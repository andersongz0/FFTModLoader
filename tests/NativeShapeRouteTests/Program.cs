using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using GenericJobs;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

// Read only the user's supported executable. Never launch/inject a game or
// publish its bytes. All executable fixtures and writes belong to THIS process.
if (args.Length is < 1 or > 2) throw new ArgumentException("Pass the supported FFT_enhanced.exe and optional packaged JobExpansion DLL.");
byte[] image = File.ReadAllBytes(args[0]);
Require(Convert.ToHexString(SHA256.HashData(image)) == "937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5", "Unsupported native fixture image");
using var pe = new PEReader(new MemoryStream(image, writable: false));
const int rva = 0x278EDC, length = 0xCC;
var section = pe.PEHeaders.SectionHeaders.Single(s => s.VirtualAddress <= rva && rva + length <= s.VirtualAddress + s.SizeOfRawData);
byte[] body = image.AsSpan(section.PointerToRawData + rva - section.VirtualAddress, length).ToArray();
Require(body.AsSpan(0, 14).SequenceEqual(Convert.FromHexString("450FB6C84C8BD1C602004183E95C")) && body[^1] == 0xC3, "Native shape leaf changed");
var assembly = args.Length == 2
    ? new AssemblyLoadContext("PackagedShapeHook", isCollectible: true).LoadFromAssemblyPath(Path.GetFullPath(args[1]))
    : typeof(NativeJobRoutes).Assembly;
var routes = assembly.GetType("GenericJobs.NativeJobRoutes", true)!;
var memory = assembly.GetType("GenericJobs.NativeJobRouteMemory", true)!;
using var original = new OwnedNativeMemory(4096);
using var unit = new OwnedNativeMemory(4096);
using var palette = new OwnedNativeMemory(4096);
using var registrations = new OwnedNativeMemory(4096);
using var caller = new OwnedNativeMemory(4096);
using var expected = new OwnedNativeMemory(4096);
using var actual = new OwnedNativeMemory(4096);
original.Write(0, body); original.ExecutablePage();
caller.Write(0, CaptureCaller()); caller.ExecutablePage();
byte[] seed = Enumerable.Range(0, 16).Select(x => (byte)(0x53 + x)).ToArray();
expected.Write(320, seed); actual.Write(320, seed);
unit.Write(0x18F, [0x9A, 0x3C]);
var capture = Marshal.GetDelegateForFunctionPointer<Capture>(caller.Address);
var allocation = Activator.CreateInstance(memory)!;
long routeAddress = (long)memory.GetProperty("Address")!.GetValue(allocation)!;
IHook<Shape> hook;
unsafe { hook = ReloadedHooks.Instance.CreateHook<Shape>((void*)routeAddress, (long)original.Address); }
var code = (byte[])routes.GetMethod("BuildShape")!.Invoke(null,
    new object[] { (long)hook.OriginalFunctionAddress, (long)registrations.Address })!;
memory.GetMethod("CompleteShape")!.Invoke(allocation, new object[] { code });
Require(Native.RtlLookupFunctionEntry((ulong)routeAddress, out ulong unwindImage, 0) != 0 && unwindImage == (ulong)routeAddress, "Shape frame lacks Windows unwind metadata");
TestUnwind(routeAddress, code.Length);
hook.Activate();
int calls = 0;
try
{
    foreach (bool populated in new[] { false, true })
    {
        byte[] lookup = new byte[512];
        if (populated)
        {
            lookup[0xA2 * 2] = 170; lookup[0xA2 * 2 + 1] = 171;
            lookup[0xA3 * 2] = 172; lookup[0xA3 * 2 + 1] = 173;
            // Highest-byte keys/shape: test the bounds without live registrations.
            lookup[0xFF * 2] = 254; lookup[0xFF * 2 + 1] = 255;
        }
        registrations.Write(0, lookup);
        for (int identity = 0; identity < 256; identity++)
        for (int job = 0; job < 256; job++)
        {
            unit.Write(0, [(byte)identity]);
            palette.Write(0, [0xCC]);
            capture(hook.OriginalFunctionAddress, unit.Address, palette.Address, (byte)job, expected.Address);
            byte expectedPalette = palette.Read(0, 1)[0];
            byte[] baseline = expected.Read(0, 320);
            if (identity is 0x80 or 0x81 && lookup[job * 2 + identity - 0x80] != 0)
                baseline[0] = lookup[job * 2 + identity - 0x80];
            palette.Write(0, [0xCC]);
            capture(original.Address, unit.Address, palette.Address, (byte)job, actual.Address);
            Require(actual.Read(0, 320).SequenceEqual(baseline), $"Private native GP/flags/XMM contract changed: identity={identity:X2}, job={job:X2}");
            Require(palette.Read(0, 1)[0] == expectedPalette, "Native palette changed");
            Require(BinaryPrimitives.ReadInt64LittleEndian(actual.Read(48, 8)) == (long)unit.Address, "Battle's R11 unit pointer was destroyed");
            calls++;
            if (calls % 8192 == 0) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        }
        Require(registrations.Read(0, 512).SequenceEqual(lookup), "Immutable registration table changed");
    }
    Require(unit.Read(0x18F, 2).SequenceEqual(new byte[] { 0x9A, 0x3C }), "Unit's shape/palette fields were modified by resolver");
    hook.Disable();
    capture(original.Address, unit.Address, palette.Address, 0xA3, actual.Address);
    capture(hook.OriginalFunctionAddress, unit.Address, palette.Address, 0xA3, expected.Address);
    Require(actual.Read(0, 320).SequenceEqual(expected.Read(0, 320)), "Disabled shape route changed native behavior");
    hook.Enable();
    unit.Write(0, [0x80]);
    capture(original.Address, unit.Address, palette.Address, 0xA2, actual.Address);
    Require(actual.Read(0, 1)[0] == 170, "Re-enabled route lost registered visual");
    // Deterministic reproduction of the observed crash contract, without an AV:
    // FCAF8 cannot safely write [R11+190] if a callback returns R11=130.
    using var clobber = new OwnedNativeMemory(4096);
    clobber.Write(0, [..Convert.FromHexString("41BB30010000FF2500000000"), ..BitConverter.GetBytes(hook.OriginalFunctionAddress)]);
    clobber.ExecutablePage();
    capture(clobber.Address, unit.Address, palette.Address, 0xA2, actual.Address);
    Require(BinaryPrimitives.ReadInt64LittleEndian(actual.Read(48, 8)) == 0x130, "Private-R11 failure fixture did not reproduce");
    Console.WriteLine($"PASS: {calls} production shape calls through actual Reloaded detour; every identity/job byte with empty/populated maps, original GP/flags/XMM0-15/palette and battle R11 preserved. Original leaf runs natively; no CLR callback.");
    Console.WriteLine("PASS: highest keys/shapes, named identities, Windows unwind registration, GC interleaving, disable/enable and deterministic old R11=130 failure fixture. Game and saves untouched.");
}
finally
{
    if (hook.IsHookEnabled) hook.Disable();
    GC.KeepAlive(hook); GC.KeepAlive(capture); GC.KeepAlive(allocation);
}

static byte[] CaptureCaller()
{
    var bytes = new List<byte>();
    void Emit(string hex) => bytes.AddRange(Convert.FromHexString(hex));
    void Simd(byte opcode, int reg, bool stack, int offset)
    {
        bytes.Add(0xF3); if (reg >= 8) bytes.Add(0x44);
        bytes.AddRange([0x0F, opcode, (byte)((stack ? 0x84 : 0x83) + reg % 8 * 8)]);
        if (stack) bytes.Add(0x24);
        bytes.AddRange(BitConverter.GetBytes(offset));
    }
    // Capture(target, unit, palette, job, output); protect this managed caller's
    // nonvolatiles while setting private volatile input values deterministically.
    Emit("534881ECC0000000488B9C24F00000004889C84889D14C89C249B80077665544332211458AC14C8BD149B9EFCDAB8967452301");
    for (int reg = 6; reg < 16; reg++) Simd(0x7F, reg, true, 32 + (reg - 6) * 16);
    for (int reg = 0; reg < 16; reg++) Simd(0x6F, reg, false, 320);
    Emit("4989CBFFD048890348894B08488953104C8943184C894B204C8953284C895B309C5848894338");
    for (int reg = 0; reg < 16; reg++) Simd(0x7F, reg, false, 64 + reg * 16);
    for (int reg = 6; reg < 16; reg++) Simd(0x6F, reg, true, 32 + (reg - 6) * 16);
    Emit("4881C4C00000005BC3");
    return bytes.ToArray();
}
static void Require(bool value, string message) { if (!value) throw new Exception(message); }
static void TestUnwind(long address, int codeLength)
{
    using var stack = new OwnedNativeMemory(4096);
    using var context = new OwnedNativeMemory(4096);
    long entry = (long)stack.Address + 512;
    const long previousRbp = 0x12345678, returnAddress = 0x11223344;
    stack.Write(504, BitConverter.GetBytes(previousRbp));
    stack.Write(512, BitConverter.GetBytes(returnAddress));
    // Body and every epilogue instruction, including after LEA and after POP.
    foreach (var (offset, rsp, rbp) in new[] {
        (16, entry - 184, entry - 8), (codeLength - 6, entry - 184, entry - 8),
        (codeLength - 2, entry - 8, entry - 8), (codeLength - 1, entry, previousRbp) })
    {
        context.Write(0, new byte[1232]);
        context.Write(48, BitConverter.GetBytes(0x10000B));
        context.Write(152, BitConverter.GetBytes(rsp));
        context.Write(160, BitConverter.GetBytes(rbp));
        context.Write(248, BitConverter.GetBytes(address + offset));
        nint function = Native.RtlLookupFunctionEntry((ulong)(address + offset), out ulong imageBase, 0);
        Native.RtlVirtualUnwind(0, imageBase, (ulong)(address + offset), function, context.Address, out _, out _, 0);
        Require(BinaryPrimitives.ReadInt64LittleEndian(context.Read(152, 8)) == entry + 8 &&
            BinaryPrimitives.ReadInt64LittleEndian(context.Read(160, 8)) == previousRbp &&
            BinaryPrimitives.ReadInt64LittleEndian(context.Read(248, 8)) == returnAddress,
            "Windows failed to unwind the shape frame/epilogue");
    }
    Console.WriteLine("PASS: Windows RtlVirtualUnwind restores caller RSP/RBP/RIP from body and every shape epilogue instruction.");
}
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
delegate void Capture(nint target, nint unit, nint palette, byte job, nint output);
[Function(Reloaded.Hooks.Definitions.X64.CallingConventions.Microsoft)]
delegate byte Shape(nint unit, nint palette, byte job);
static class Native
{
    [DllImport("kernel32.dll")] public static extern nint RtlLookupFunctionEntry(ulong pc, out ulong image, nint history);
    [DllImport("kernel32.dll")] public static extern nint RtlVirtualUnwind(uint handlerType, ulong imageBase,
        ulong pc, nint function, nint context, out nint handlerData, out ulong frame, nint contextPointers);
}
