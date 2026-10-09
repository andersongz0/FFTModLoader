using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GenericJobs;

// These routes run on the game's own threads/fibers. They must not enter the
// CLR, allocate, log, take a managed lock or invoke a managed original delegate.
internal static class NativeJobRoutes
{
    public static byte[] BuildLearn(long original, long jobList)
    {
        var c = new Code();
        // Only AX is scratch, but preserve even the private volatile contract.
        c.Bytes("50");
        c.Bytes("48B8"); c.Long(jobList);
        c.Bytes("4885C0"); c.Jump("0F84", "restore");
        c.Bytes("6683385E"); c.Jump("0F84", "restore");
        c.Bytes("6683FA5E"); c.Jump("0F85", "restore");
        c.Bytes("66BAA000"); // Requested job DX; other four arguments untouched.
        c.Label("restore"); c.Bytes("58");
        c.Tail(original);
        return c.Finish();
    }

    public static byte[] BuildSparse(long original)
    {
        var c = new Code();
        c.Bytes("5051524150415141524153");
        c.Bytes("4885C9"); c.Jump("0F84", "fallback");
        c.Bytes("4883793801"); c.Jump("0F85", "fallback");
        c.Bytes("80794100"); c.Jump("0F84", "fallback");
        c.Bytes("4C8B4928"); // R9 = count, bounded before row addressing.
        c.Bytes("4983F901"); c.Jump("0F8C", "fallback");
        c.Bytes("4981F940420F00"); c.Jump("0F8F", "fallback");
        c.Bytes("4C8B4110"); // R8 = existing row unions (16-byte stride).
        c.Bytes("4D85C0"); c.Jump("0F84", "fallback");
        c.Bytes("498B00480FBAF03F4883F801"); c.Jump("0F85", "fallback");
        c.Bytes("498B40084885C0"); c.Jump("0F84", "fallback");
        c.Bytes("448B10"); // First key, zero-extended.
        c.Bytes("498BC148FFC848C1E0044C01C0"); // Last row.
        c.Bytes("4C8B18490FBAF33F4983FB01"); c.Jump("0F85", "fallback");
        c.Bytes("488B40084885C0"); c.Jump("0F84", "fallback");
        c.Bytes("448B18"); // Last key, zero-extended.
        c.Bytes("453BDA"); c.Jump("0F82", "fallback");
        c.Bytes("443B5144"); c.Jump("0F85", "sparse");
        c.Bytes("418BC24C01C848FFC84C39D8"); c.Jump("0F84", "fallback");
        c.Label("sparse");
        c.Bytes("4531D249FFC9"); // R10 = low, R9 = high.
        c.Label("search");
        c.Bytes("453BD1"); c.Jump("0F8F", "absent");
        c.Bytes("458BD94529D341D1EB4501D3"); // middle = low + (high-low)/2.
        c.Bytes("498BC348C1E0044C01C0"); // RAX = candidate row union.
        c.Bytes("488B08480FBAF13F4883F901"); c.Jump("0F85", "fallback");
        c.Bytes("488B48084885C9"); c.Jump("0F84", "fallback");
        c.Bytes("3911"); c.Jump("0F84", "result");
        c.Jump("0F82", "higher");
        c.Bytes("458BCB41FFC9"); c.Jump("E9", "search");
        c.Label("higher"); c.Bytes("458BD341FFC2"); c.Jump("E9", "search");
        c.Label("absent"); c.Bytes("31C0");
        c.Label("result"); c.Bytes("4889442430"); // Replace only saved RAX.
        c.Bytes("415B415A415941585A5958C3");
        c.Label("fallback"); c.Bytes("415B415A415941585A5958");
        c.Tail(original);
        return c.Finish();
    }

    private sealed class Code
    {
        private readonly List<byte> _bytes = new();
        private readonly Dictionary<string, int> _labels = new();
        private readonly List<(int Offset, string Label)> _jumps = new();
        public void Bytes(string hex) => _bytes.AddRange(Convert.FromHexString(hex));
        public void Long(long value) => _bytes.AddRange(BitConverter.GetBytes(value));
        public void Label(string label) => _labels.Add(label, _bytes.Count);
        public void Jump(string opcode, string label)
        {
            Bytes(opcode); _jumps.Add((_bytes.Count, label)); _bytes.AddRange(new byte[4]);
        }
        // RIP-relative indirect jump consumes no register or stack argument.
        public void Tail(long address) { Bytes("FF2500000000"); Long(address); }
        public byte[] Finish()
        {
            byte[] result = _bytes.ToArray();
            foreach (var jump in _jumps)
                BitConverter.GetBytes(_labels[jump.Label] - jump.Offset - 4).CopyTo(result, jump.Offset);
            return result;
        }
    }
}

// Both code and unwind table remain allocated until the process exits. Hooks
// may remain in a suspended game fiber; freeing either on unload is unsafe.
internal sealed class NativeJobRouteMemory
{
    public long Address { get; }
    public NativeJobRouteMemory()
    {
        Address = (long)VirtualAlloc(0, 4096, 0x3000, 0x04);
        if (Address == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Complete(byte[] code, bool sparse)
    {
        if (code.Length < 1 || code.Length > 4000) throw new InvalidDataException("Invalid native job route size.");
        byte[] prefix = Convert.FromHexString(sparse ? "5051524150415141524153" : "50");
        if (!code.Take(prefix.Length).SequenceEqual(prefix)) throw new InvalidDataException("Unknown native job route frame.");
        Marshal.Copy(code, 0, (nint)Address, code.Length);
        byte[] table = [..BitConverter.GetBytes(0), ..BitConverter.GetBytes(code.Length), ..BitConverter.GetBytes(4048)];
        byte[] unwind = sparse ? [1,11,7,0,11,2,9,2,7,2,5,2,3,2,2,2,1,2,0,0] : [1,1,1,0,1,2,0,0];
        Marshal.Copy(table, 0, (nint)(Address + 4032), table.Length);
        Marshal.Copy(unwind, 0, (nint)(Address + 4048), unwind.Length);
        if (!VirtualProtect((nint)Address,4096,0x20,out _) ||
            !FlushInstructionCache(GetCurrentProcess(),(nint)Address,4096) ||
            !RtlAddFunctionTable((nint)(Address+4032),1,(ulong)Address))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern nint VirtualAlloc(nint address,nuint size,uint type,uint protection);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool VirtualProtect(nint address,nuint size,uint protection,out uint old);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool FlushInstructionCache(nint process,nint address,nuint size);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool RtlAddFunctionTable(nint table,uint count,ulong imageBase);
}
