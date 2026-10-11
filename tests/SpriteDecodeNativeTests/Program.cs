using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FFTModLoader.ContentExpansion;

if(args.Length!=2 || !OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture!=Architecture.X64)
    throw new ArgumentException("Pass supported executable and Bestial Warrior Visuals directory.");
byte[] image=File.ReadAllBytes(args[0]);
Check(Convert.ToHexString(SHA256.HashData(image))=="937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5","Unsupported executable");
using var pe=new PEReader(new MemoryStream(image));
const int rva=0xCB5B4;
var section=pe.PEHeaders.SectionHeaders.Single(s=>rva>=s.VirtualAddress && rva+457<=s.VirtualAddress+s.SizeOfRawData);
byte[] helper=image.AsSpan(section.PointerToRawData+rva-section.VirtualAddress,457).ToArray();
// Audited self-contained function; refuses changed code before isolated execution.
Check(Convert.ToHexString(SHA256.HashData(helper))=="8E5DE5A4D106713569B38E80171734D8EACF7EF43CF0D4FD7B86EE9C2FC14C4A","Native SPR helper differs");
using var code=new Owned(4096); code.Write(helper); code.Executable();
using var state=new Owned(4096); using var source=new Owned(65536); using var target=new Owned(65536);
var native=Marshal.GetDelegateForFunctionPointer<Defrost>(code.Address);
int tested=0;
foreach(string name in new[]{"bestial_m.spr","bestial_f.spr","wolf_m.spr","wolf_f.spr"})
{
    byte[] packed=File.ReadAllBytes(Path.Combine(args[1],name));
    byte[] decoded=NativeSpriteSheet.Unpack(packed);
    source.Write(packed.AsSpan(NativeSpriteSheet.RawPrefix).ToArray());
    state.Write(new byte[16]); target.Write(new byte[NativeSpriteSheet.TailBytes]);
    native(state.Address,source.Address,target.Address);
    Check(target.Read(NativeSpriteSheet.TailBytes).SequenceEqual(decoded.AsSpan(NativeSpriteSheet.RawPrefix).ToArray()),"Native SPR comparison differs: "+name);
    Check(decoded.AsSpan(0,NativeSpriteSheet.RawPrefix).SequenceEqual(packed.AsSpan(0,NativeSpriteSheet.RawPrefix)),"Uncompressed SPR prefix changed");
    tested++;
}
foreach(byte[] invalid in new[]{new byte[NativeSpriteSheet.RawPrefix],new byte[45057],new byte[NativeSpriteSheet.RawPrefix+1]})
{
    bool rejected=false;
    try{NativeSpriteSheet.Unpack(invalid);}catch(InvalidDataException){rejected=true;}
    Check(rejected,"Malformed SPR accepted");
}
Console.WriteLine($"PASS: {tested} full SPR tails match the original native decoder; malformed inputs rejected. Isolated process only, no game files or saves modified.");
static void Check(bool valid,string message){if(!valid)throw new InvalidDataException(message);}
[UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate void Defrost(nint state,nint source,nint output);
sealed class Owned:IDisposable
{
    public nint Address{get;private set;}
    private readonly int _length;
    public Owned(int length){_length=length;Address=VirtualAlloc(0,(nuint)length,0x3000,4);if(Address==0)throw new Exception("Allocation failed");}
    public void Write(byte[] data){if(data.Length>_length)throw new Exception("Fixture overflow");Marshal.Copy(data,0,Address,data.Length);}
    public byte[] Read(int length){if(length>_length)throw new Exception("Fixture overflow");var bytes=new byte[length];Marshal.Copy(Address,bytes,0,length);return bytes;}
    public void Executable(){if(!VirtualProtect(Address,(nuint)_length,0x20,out _)||!FlushInstructionCache(GetCurrentProcess(),Address,(nuint)_length))throw new Exception("RX allocation failed");}
    public void Dispose(){if(Address!=0){VirtualFree(Address,0,0x8000);Address=0;}}
    [DllImport("kernel32.dll")]static extern nint VirtualAlloc(nint a,nuint size,uint type,uint protection);
    [DllImport("kernel32.dll")]static extern bool VirtualProtect(nint a,nuint size,uint p,out uint old);
    [DllImport("kernel32.dll")]static extern bool VirtualFree(nint a,nuint s,uint type);
    [DllImport("kernel32.dll")]static extern bool FlushInstructionCache(nint process,nint a,nuint size);
    [DllImport("kernel32.dll")]static extern nint GetCurrentProcess();
}
