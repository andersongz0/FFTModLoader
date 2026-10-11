using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FFTModLoader.ContentExpansion.Runtime;

if(!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture!=Architecture.X64)
    throw new PlatformNotSupportedException("Windows x64 fixture only.");
using var original=new Owned(4096);original.Write(0,[0xB8,0x78,0x56,0x34,0x12,0xC3]);original.Executable();
using var state=new Owned(4096);
using var rows=new Owned(4096);
using var scratch=new Owned(4096);
using var output=new Owned(4096);
using var slot=new Owned(4096);
using var array=new Owned(4096);
using var caller=new Owned(4096);
caller.Write(0,Convert.FromHexString("534883EC204C89CB4889C889D14489C249B8050000000000000049B9060000000000000049BA070000000000000049BB0800000000000000FFD048890B488953084C8943104C894B184C8953204C895B284883C4205BC3"));caller.Executable();
var capture=Marshal.GetDelegateForFunctionPointer<Capture>(caller.Address);
state.Write(0,[1]);state.Write(NativeExpandedCommandCatalog.ScratchPointerOffset,BitConverter.GetBytes((long)scratch.Address));
ushort[][] actions=[[524],[532,280,281,283,282],[1023]];
int[] commands=[227,228,255];
for(int index=0;index<commands.Length;index++)
{
    int command=commands[index];
    state.Write(NativeExpandedCommandCatalog.RegisteredOffset+command,[1]);
    state.Write(NativeExpandedCommandCatalog.RowsOffset+command*8,BitConverter.GetBytes((long)rows.Address+index*48));
    state.Write(NativeExpandedCommandCatalog.CountsOffset+command,[(byte)actions[index].Length]);
    state.Write(NativeExpandedCommandCatalog.MonsterOffset+command,[(byte)(index==1?1:0)]);
    for(int a=0;a<actions[index].Length;a++)rows.Write(index*48+a*2,BitConverter.GetBytes(actions[index][a]));
}
slot.Write(0,NativeExpandedCommandCatalog.BuildSlot((long)original.Address,(long)state.Address));slot.Executable();
array.Write(0,NativeExpandedCommandCatalog.BuildActionArray((long)original.Address,(long)state.Address));array.Executable();
for(int id=0;id<=65535;id++)
{
    int at=Array.IndexOf(commands,id);
    for(int a=0;a<25;a++)
    {
        long result=capture(slot.Address,id,a,output.Address);Preserved(id,a);
        long expected=at>=0&&a<24 ? a<actions[at].Length?actions[at][a]:0 : 0x12345678;
        Check(result==expected,$"Slot mismatch {id}:{a}");
    }
}
foreach(int id in commands)
    foreach(int flags in new[]{0,1,2,3,31,255})
    {
        scratch.Write(0,Enumerable.Repeat((byte)0xA5,64).ToArray());
        Check(capture(array.Address,id,flags,output.Address)==(long)scratch.Address,"Wrong action scratch");Preserved(id,flags);
        byte[] data=scratch.Read(0,64);var expected=actions[Array.IndexOf(commands,id)];
        for(int a=0;a<24;a++)Check(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(a*2))==
            ((flags&1)!=0&&a<expected.Length?expected[a]:0),"Narrowed, hidden fifth action or leaked passive");
        Check(data.AsSpan(48).IndexOfAnyExcept((byte)0xA5)<0,"Wrote past 24-word scratch");
    }
foreach(int id in new[]{0,6,175,176,223,224,226,229,256,65535,-1,int.MaxValue})
{
    Check(capture(array.Address,id,1,output.Address)==0x12345678,"Native/unregistered command intercepted");Preserved(id,1);
}
foreach(int a in new[]{-1,int.MinValue,24,int.MaxValue})Check(capture(slot.Address,227,a,output.Address)==0x12345678,"Unbounded slot accepted");
state.Write(0,[0]);Check(capture(slot.Address,227,0,output.Address)==0x12345678,"Disabled state activated");
state.Write(0,[1]);state.Write(NativeExpandedCommandCatalog.RowsOffset+227*8,new byte[8]);
Check(capture(slot.Address,227,0,output.Address)==0x12345678,"Missing row accepted");
Console.WriteLine("PASS: isolated executable x64 routes, 65,536 command inputs x 25 slots, full-width actions including 1023, five-action Wolf, original fallback, 24-word boundaries and RCX/RDX/R8-R11 preservation. No game hooks installed.");

void Preserved(int id,int argument)
{
    var bytes=output.Read(0,48);
    Check(BinaryPrimitives.ReadInt64LittleEndian(bytes)==(uint)id,"RCX changed");
    Check(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8))==(uint)argument,"RDX changed");
    for(int n=0;n<4;n++)Check(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16+n*8))==5+n,"Private scratch GP changed");
}
static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
[UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate long Capture(nint target,int id,int argument,nint output);
sealed class Owned : IDisposable
{
    public nint Address {get;private set;}
    private readonly int _size;
    public Owned(int size) {_size=size;Address=VirtualAlloc(0,(nuint)size,0x3000,4);if(Address==0)throw new Exception("Allocation failed");}
    public void Write(int offset,byte[] bytes){if(offset<0||offset+bytes.Length>_size)throw new Exception("Fixture overflow");Marshal.Copy(bytes,0,Address+offset,bytes.Length);}
    public byte[] Read(int offset,int length){var b=new byte[length];Marshal.Copy(Address+offset,b,0,length);return b;}
    public void Executable(){if(!VirtualProtect(Address,(nuint)_size,0x20,out _)||!FlushInstructionCache(GetCurrentProcess(),Address,(nuint)_size))throw new Exception("RX failed");}
    public void Dispose(){if(Address!=0){VirtualFree(Address,0,0x8000);Address=0;}}
    [DllImport("kernel32.dll")]static extern nint VirtualAlloc(nint a,nuint s,uint t,uint p);
    [DllImport("kernel32.dll")]static extern bool VirtualProtect(nint a,nuint s,uint p,out uint old);
    [DllImport("kernel32.dll")]static extern bool VirtualFree(nint a,nuint s,uint t);
    [DllImport("kernel32.dll")]static extern bool FlushInstructionCache(nint p,nint a,nuint s);
    [DllImport("kernel32.dll")]static extern nint GetCurrentProcess();
}
