using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

// Isolated MACHINE-CODE fixture, not a game hook or a proposed production
// adapter. Shows why a Microsoft-compliant replacement is not equivalent to
// ItemChg's stronger leaf ABI. Touches only memory allocated by this test.
static class NativeLeafAbiTests
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Capture(nint target, nint output);

    public static void Run()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Native ABI fixture requires Windows x64.");
        nint output = Marshal.AllocHGlobal(160);
        try
        {
            byte[] initial = new byte[160];
            for (int i = 0; i < 16; i++) initial[128 + i] = (byte)(0x31 + i);
            Marshal.Copy(initial, 0, output, initial.Length);
            using var caller = new Executable(Caller());
            using var leaf = new Executable([0xB8, 1, 0, 0, 0, 0xC3]); // return 1; preserves registers
            using var volatileCallee = new Executable(Clobber(output + 144));
            using var preservingFixture = new Executable(Preserve(volatileCallee.Address));
            var capture = Marshal.GetDelegateForFunctionPointer<Capture>(caller.Address);

            Check(capture(leaf.Address, output) == 1, "Leaf return changed");
            Check(Preserved(Read(output), output), "Native leaf fixture did not preserve its contract");
            Check(capture(volatileCallee.Address, output) == 1, "Volatile callee return changed");
            byte[] corrupted = Read(output);
            Check(!Preserved(corrupted, output), "ABI mismatch was not reproduced");
            Check(Get64(corrupted, 0) == 0 && Get64(corrupted, 8) == 0 && Get64(corrupted, 16) == 0,
                "Fixture did not deterministically clobber RDX/R10/R11");
            Check(corrupted.AsSpan(32, 96).IndexOfAnyExcept((byte)0) < 0, "SIMD clobber not observed");
            for (int iteration = 0; iteration < 5; iteration++)
            {
                Check(capture(preservingFixture.Address, output) == 1, "Preserving fixture return changed");
                Check(Preserved(Read(output), output), "Preserving fixture lost GP/SIMD state");
            }
            Check(Get64(Read(output), 144) == 6, "Callee invocation count changed");
            GC.KeepAlive(capture);
            Console.WriteLine("PASS: isolated x64 leaf ABI reproduces RDX/R10/R11 and XMM0-5 loss despite identical return; test-only preservation passes five calls with no retries. No game process, detours or production bridge tested.");
        }
        finally { Marshal.FreeHGlobal(output); }
    }

    private static byte[] Caller()
    {
        var code = new List<byte>();
        Add(code, "53 48 83 EC 20 48 89 D3 48 89 C8"); // save RBX, shadow; RBX=output,RAX=target
        Add(code, "BA 01 00 00 00 49 89 DA 41 BB 05 00 00 00"); // RDX=1,R10=output,R11=5
        for (int i = 0; i < 6; i++)
        {
            code.AddRange([0xF3, 0x0F, 0x6F, (byte)(0x83 + i * 8)]); // movdqu xmm,[rbx+128]
            code.AddRange(BitConverter.GetBytes(128));
        }
        Add(code, "FF D0 48 89 13 4C 89 53 08 4C 89 5B 10 89 43 18"); // call, snapshot GP/return
        for (int i = 0; i < 6; i++)
            code.AddRange([0xF3, 0x0F, 0x7F, (byte)(0x43 + i * 8), (byte)(32 + i * 16)]);
        Add(code, "48 83 C4 20 5B C3");
        return code.ToArray();
    }

    private static byte[] Clobber(nint count)
    {
        var code = new List<byte>();
        Add(code, "48 B8"); code.AddRange(BitConverter.GetBytes((long)count));
        Add(code, "48 FF 00 31 D2 45 31 D2 45 31 DB"); // count++, zero permitted volatile GP registers
        for (int i = 0; i < 6; i++) code.AddRange([0x66, 0x0F, 0xEF, (byte)(0xC0 + i * 9)]);
        Add(code, "B8 01 00 00 00 C3");
        return code.ToArray();
    }

    private static byte[] Preserve(nint target)
    {
        var code = new List<byte>();
        Add(code, "52 41 52 41 53 48 81 EC 80 00 00 00"); // GP saves, alignment,32 shadow +96 SIMD
        for (int i = 0; i < 6; i++)
            code.AddRange([0xF3, 0x0F, 0x7F, (byte)(0x44 + i * 8), 0x24, (byte)(32 + i * 16)]);
        Add(code, "48 B8"); code.AddRange(BitConverter.GetBytes((long)target)); Add(code, "FF D0");
        for (int i = 0; i < 6; i++)
            code.AddRange([0xF3, 0x0F, 0x6F, (byte)(0x44 + i * 8), 0x24, (byte)(32 + i * 16)]);
        Add(code, "48 81 C4 80 00 00 00 41 5B 41 5A 5A C3");
        return code.ToArray();
    }

    private static bool Preserved(byte[] bytes, nint output)
    {
        if (Get64(bytes, 0) != 1 || Get64(bytes, 8) != (long)output || Get64(bytes, 16) != 5 ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)) != 1) return false;
        for (int i = 0; i < 6; i++)
            if (!bytes.AsSpan(32 + i * 16, 16).SequenceEqual(bytes.AsSpan(128, 16))) return false;
        return true;
    }
    private static byte[] Read(nint pointer) { byte[] result = new byte[160]; Marshal.Copy(pointer, result, 0, 160); return result; }
    private static long Get64(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
    private static void Add(List<byte> bytes, string hex) => bytes.AddRange(Convert.FromHexString(hex.Replace(" ", "")));
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    private sealed class Executable : IDisposable
    {
        public nint Address { get; private set; }
        public Executable(byte[] bytes)
        {
            // RW -> RX, never a permanently writable/executable allocation.
            Address = VirtualAlloc(0, (nuint)bytes.Length, 0x3000, 0x04);
            if (Address == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                Marshal.Copy(bytes, 0, Address, bytes.Length);
                if (!VirtualProtect(Address, (nuint)bytes.Length, 0x20, out _) ||
                    !FlushInstructionCache(GetCurrentProcess(), Address, (nuint)bytes.Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (Address == 0) return;
            if (!VirtualFree(Address, 0, 0x8000)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Address = 0;
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint old);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFree(nint address, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
}
