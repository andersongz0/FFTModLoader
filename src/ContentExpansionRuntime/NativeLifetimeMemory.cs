using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>In-process allocations retained until exit; never remote injection.</summary>
internal sealed class NativeLifetimeMemory
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void WriteAudit(nuint address,uint length,uint phase);
    private static WriteAudit? _audit;
    internal static void SetWriteAudit(WriteAudit audit)=>Volatile.Write(ref _audit,audit);
    public long Address { get; }
    public int Length { get; }
    public NativeLifetimeMemory(int length,long nearImage=0)
    {
        if(length<=0)throw new ArgumentOutOfRangeException(nameof(length));
        Length=(length+4095)&~4095;
        if(nearImage!=0)
        {
            // All encoded operands must remain within signed disp32 of both
            // the image base and its code. Never assume a random heap is near.
            long origin=nearImage&~0xFFFFL;
            for(long distance=0x4000000;distance<0x70000000 && Address==0;distance+=0x10000)
                Address=(long)VirtualAlloc((nint)(origin+distance),(nuint)Length,0x3000,0x04);
        }
        else Address=(long)VirtualAlloc(0,(nuint)Length,0x3000,0x04);
        if(Address==0)throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot allocate native lifetime storage.");
    }
    public void Write(int offset,byte[] data)
    {
        if(offset<0 || (long)offset+data.Length>Length)throw new InvalidDataException("Outside owned native memory.");
        Marshal.Copy(data,0,(nint)(Address+offset),data.Length);
    }
    public void Executable()
    {
        if(!VirtualProtect((nint)Address,(nuint)Length,0x20,out _) ||
           !FlushInstructionCache(GetCurrentProcess(),(nint)Address,(nuint)Length))throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    // Registered for the whole process lifetime, like this allocation. The
    // decoder entry chain may call another mod's managed code, so its caller
    // needs real OS unwind data, not a non-leaf anonymous executable frame.
    public void ExecutableWithSavedFrame(int begin,int end)
    {
        if(begin<0 || end<=begin+7 || end>Length-64 ||
           !CheckedNativeRead.Read(Address+begin,7).SequenceEqual(new byte[]{0x53,0x56,0x57,0x48,0x83,0xEC,0x20}))
            throw new InvalidDataException("Unsupported native saved-frame prologue.");
        int table=Length-64,info=Length-48;
        Write(table,BitConverter.GetBytes(begin).Concat(BitConverter.GetBytes(end)).Concat(BitConverter.GetBytes(info)).ToArray());
        // version1,prologue7,count4; alloc32,pushRDI,pushRSI,pushRBX.
        Write(info,[1,7,4,0,7,0x32,3,0x70,2,0x60,1,0x30]);
        Executable();
        if(!RtlAddFunctionTable((nint)(Address+table),1,(ulong)Address))
            throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot register native decoder unwind frame.");
    }
    // Seven volatile pushes still require unwind metadata for a native stack
    // walker. Describe their stack allocation; no nonvolatile GP is modified.
    public void ExecutableWithVolatilePushes(int end)
    {
        if(end<=11 || end>Length-64 || !CheckedNativeRead.Read(Address,11)
            .SequenceEqual(Convert.FromHexString("5051524150415141524153")))
            throw new InvalidDataException("Unsupported AI eligibility prologue.");
        int table=Length-64,info=Length-48;
        Write(table,[..BitConverter.GetBytes(0),..BitConverter.GetBytes(end),..BitConverter.GetBytes(info)]);
        // UWOP_ALLOC_SMALL8 for each volatile push (reverse instruction order).
        Write(info,[1,11,7,0,11,2,9,2,7,2,5,2,3,2,2,2,1,2,0,0]);
        Executable();
        if(!RtlAddFunctionTable((nint)(Address+table),1,(ulong)Address))
            throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot register AI eligibility unwind frame.");
    }
    public static void WriteProtected(long address,byte[] data)
    {
        _=CheckedNativeRead.Read(address,data.Length);
        // No legitimate Chemist destination belongs to a managed runtime image.
        // Reject BEFORE changing page protection or bytes, including cross-page
        // writes. This does not classify arbitrary private CLR/native heaps.
        RejectRuntimeImage(address,data.Length);
        Volatile.Read(ref _audit)?.Invoke((nuint)address,(uint)data.Length,0);
        if(!VirtualProtect((nint)address,(nuint)data.Length,0x40,out uint old))throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            Marshal.Copy(data,0,(nint)address,data.Length);
            Volatile.Read(ref _audit)?.Invoke((nuint)address,(uint)data.Length,1);
            if(!FlushInstructionCache(GetCurrentProcess(),(nint)address,(nuint)data.Length))throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally{if(!VirtualProtect((nint)address,(nuint)data.Length,old,out _))throw new Win32Exception(Marshal.GetLastWin32Error());}
    }
    private static void RejectRuntimeImage(long address,int length)
    {
        long end=checked(address+length);
        for(long page=address;page<end;page=Math.Min(end,(page&~4095L)+4096))
        {
            if(!GetModuleHandleEx(0x6,(nint)page,out nint module))continue;
            var name=new System.Text.StringBuilder(1024);
            if(GetModuleFileName(module,name,name.Capacity)==0)throw new InvalidDataException("Cannot identify native write destination module.");
            string file=Path.GetFileName(name.ToString());
            if(file.Equals("coreclr.dll",StringComparison.OrdinalIgnoreCase)||
               file.Equals("clrjit.dll",StringComparison.OrdinalIgnoreCase)||
               file.Equals("System.Private.CoreLib.dll",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Chemist write into managed runtime module refused: "+file);
        }
    }
    [DllImport("kernel32.dll",EntryPoint="GetModuleHandleExW",SetLastError=true)]
    private static extern bool GetModuleHandleEx(uint flags,nint address,out nint module);
    [DllImport("kernel32.dll",EntryPoint="GetModuleFileNameW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern uint GetModuleFileName(nint module,System.Text.StringBuilder name,int capacity);
    [DllImport("kernel32.dll",SetLastError=true)]static extern nint VirtualAlloc(nint address,nuint size,uint type,uint protection);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool VirtualProtect(nint address,nuint size,uint protection,out uint old);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool FlushInstructionCache(nint process,nint address,nuint size);
    [DllImport("kernel32.dll")]static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool RtlAddFunctionTable(nint table,uint count,ulong imageBase);
}
