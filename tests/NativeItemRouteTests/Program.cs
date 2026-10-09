using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FFTModLoader.ContentExpansion.Runtime;
using Iced.Intel;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
    throw new PlatformNotSupportedException("Windows x64 only.");
string executable = args.Length == 1 ? args[0] :
    @"C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY TACTICS - The Ivalice Chronicles\FFT_enhanced.exe";
byte[] image = File.ReadAllBytes(executable); // read-only; NEVER run/inject the game
Require(Convert.ToHexString(SHA256.HashData(image)) == "937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5", "Unsupported native fixture image");
using var pe = new PEReader(new MemoryStream(image, writable: false));
const int entryRva = 0x2847F8, bodyLength = 0x36;
var section = pe.PEHeaders.SectionHeaders.Single(s => s.VirtualAddress <= entryRva && entryRva + bodyLength <= s.VirtualAddress + s.SizeOfRawData);
byte[] leafBody = image.AsSpan(section.PointerToRawData + entryRva - section.VirtualAddress, bodyLength).ToArray();
Require(leafBody.AsSpan(0, 16).SequenceEqual(Convert.FromHexString("B8FF03000041B8030100006623C88D41")), "Unexpected native ItemChg prologue");
Require(leafBody.AsSpan(27, 3).SequenceEqual(Convert.FromHexString("4C8D0D")), "Native stock LEA changed");

using var original = new OwnedNativeMemory(8192); // code page followed by OWN fake PartyItem
BinaryPrimitives.WriteInt32LittleEndian(leafBody.AsSpan(30), 4096 - 34); // ONLY relocate PartyItem LEA
original.Write(0, leafBody); original.ExecutablePage();
using var state = new OwnedNativeMemory(4096);
using var route = new OwnedNativeMemory(4096);
using var output = new OwnedNativeMemory(4096);
using var caller = new OwnedNativeMemory(4096);
caller.Write(0, CaptureCaller()); caller.ExecutablePage();
byte[] seed = Enumerable.Range(0, 16).Select(i => (byte)(0x51 + i)).ToArray();
output.Write(288, seed);
var capture = Marshal.GetDelegateForFunctionPointer<Capture>(caller.Address);

// Match the actual Reloaded.Hooks 4.3.2 shipped with this installation.
IHook<ItemChange>? hook = null;
unsafe { hook = ReloadedHooks.Instance.CreateHook<ItemChange>((void*)route.Address, (long)original.Address); }
byte[] routeBody = NativeItemStockLeaf.Build((long)hook.OriginalFunctionAddress, (long)state.Address);
AuditLeaf(routeBody);
route.Write(0, routeBody); route.ExecutablePage();
var baseline = Marshal.GetDelegateForFunctionPointer<ItemChange>(hook.OriginalFunctionAddress);
hook.Activate();
try
{
    state.Write(NativeItemStockLeaf.EnabledOffset, [1]);
    int[] extraIds = Enumerable.Range(261, 11).Append(1023).ToArray(); // FIXTURES, not live reserved IDs
    foreach (int id in extraIds)
    {
        state.Write(NativeItemStockLeaf.RegisteredOffset + id, [1]);
        state.Write(NativeItemStockLeaf.CountsOffset + id, [17]);
    }
    // Every ushort identity/flag combination; native IDs must retain original
    // semantics. Compare normalized native/unregistered IDs with real leaf.
    for (int id = 0; id <= ushort.MaxValue; id++)
    {
        int normalized = id & 1023;
        int result = capture(original.Address, (ushort)id, 0, output.Address);
        AssertPreserved(0, result);
        int expected = extraIds.Contains(normalized) ? 17 : baseline((ushort)id, 0);
        Require(result == expected, $"ID normalization/query changed: {id}");
    }
    Console.WriteLine("PASS: actual Reloaded hook + copied native leaf, all 65536 ushort queries; RDX/R10/R11 and XMM0-15 preserved; no managed reverse callback.");

    foreach (int id in extraIds)
        foreach (int count in new[] { 0, 1, 45, 98, 99 })
            foreach (int delta in new[] { int.MinValue, -1000, -99, -1, 0, 1, 99, 1000, int.MaxValue })
            {
                state.Write(NativeItemStockLeaf.CountsOffset + id, [(byte)count]);
                byte[] nativeBefore = original.Read(4096, 4096);
                byte[] stateBefore = state.Read(0, 4096);
                int expected = (int)Math.Clamp((long)count + delta, 0, 99);
                int result = capture(original.Address, (ushort)id, delta, output.Address);
                AssertPreserved(delta, result);
                Require(result == expected && state.Read(NativeItemStockLeaf.CountsOffset + id, 1)[0] == expected,
                    "Expanded stack clamp/return mismatch");
                Require(original.Read(4096, 4096).SequenceEqual(nativeBefore), "Expanded stock touched native/save-adjacent memory");
                stateBefore[NativeItemStockLeaf.CountsOffset + id] = (byte)expected;
                Require(state.Read(0, 4096).SequenceEqual(stateBefore), "Stock touched an unregistered/guard byte");
            }
    Console.WriteLine("PASS: separate extra inventory, exact single-byte mutation, read-only queries, clamp 0..99, int32 extrema, highest normalized ID and guard memory untouched.");

    foreach (int id in new[] { 0, 1, 19, 240, 243, 252, 253, 260, 272, 1022 })
        foreach (int delta in new[] { -2, -1, 0, 1, 98, 150 })
        {
            original.Write(4096, new byte[261]);
            int expected = baseline((ushort)id, delta);
            byte[] expectedNative = original.Read(4096, 261);
            original.Write(4096, new byte[261]);
            byte[] stateBefore = state.Read(0, 4096);
            int result = capture(original.Address, (ushort)id, delta, output.Address);
            AssertPreserved(delta, result);
            Require(result == expected && original.Read(4096, 261).SequenceEqual(expectedNative), "Native item behavior changed");
            Require(state.Read(0, 4096).SequenceEqual(stateBefore), "Native item touched extra stock");
        }
    // Reproduce the five equipment-slot calls that crashed the old diagnostic.
    for (int slot = 0; slot < 5; slot++)
    {
        int result = capture(original.Address, 19, 1, output.Address);
        AssertPreserved(1, result);
    }
    Console.WriteLine("PASS: native item mutations match original including its legacy overflow behavior; five equipment-style stock calls preserve internal caller registers.");

    foreach (byte enabled in new byte[] { 0, 2, 255 })
    {
        state.Write(0, [enabled]); byte[] before = state.Read(0, 4096);
        Require(capture(original.Address, 261, 1, output.Address) == 0, "Unbound session exposed extra stock");
        AssertPreserved(1, 0); Require(state.Read(0, 4096).SequenceEqual(before), "Disabled session mutated extra stock");
    }
    state.Write(0, [1]); state.Write(NativeItemStockLeaf.RegisteredOffset + 261, [2]);
    byte[] malformedBefore = state.Read(0, 4096);
    Require(capture(original.Address, 261, 1, output.Address) == 0, "Malformed registration accepted");
    Require(state.Read(0, 4096).SequenceEqual(malformedBefore), "Malformed registration changed stock");
    state.Write(NativeItemStockLeaf.RegisteredOffset + 261, [1]);
    hook.Disable();
    Require(capture(original.Address, 261, 1, output.Address) == 0, "Disabled hook still routes extra stock");
    AssertPreserved(1, 0);
    hook.Enable();
    int enabledResult = capture(original.Address, 261, 0, output.Address);
    Require(enabledResult == state.Read(NativeItemStockLeaf.CountsOffset + 261, 1)[0], "Re-enabled hook lost state");
    AssertPreserved(0, enabledResult);
    Console.WriteLine("PASS: disabled/unbound/malformed-registration paths fail closed; actual hook disable/enable preserves leaf ABI. OWN test process only; game integration, shops, saves and abilities are NOT activated.");
}
finally
{
    if (hook.IsHookEnabled) hook.Disable();
    GC.KeepAlive(hook); GC.KeepAlive(baseline); GC.KeepAlive(capture);
}
NativeShopRouteTests.Run();
NativeItemDataRouteTests.Run(image, pe);
NativeMedicineRouteTests.Run(image, pe);
NativeAbilityRouteTests.Run(image, pe);
NativeCommandRouteTests.Run(image, pe);
NativeActionListRouteTests.Run(image, pe);
NativeActionPurchaseRouteTests.Run(image, pe);
NativeContextBridgeTests.Run();

void AssertPreserved(int delta, int result)
{
    byte[] snapshot = output.Read(0, 288);
    Require(BinaryPrimitives.ReadUInt64LittleEndian(snapshot) == unchecked((uint)delta), "RDX lost across stock route");
    Require(BinaryPrimitives.ReadInt64LittleEndian(snapshot.AsSpan(8)) == (long)output.Address, "R10 equipment pointer lost");
    Require(BinaryPrimitives.ReadInt64LittleEndian(snapshot.AsSpan(16)) == 5, "R11 equipment count lost");
    Require(BinaryPrimitives.ReadInt32LittleEndian(snapshot.AsSpan(24)) == result, "RAX result changed");
    for (int i = 0; i < 16; i++) Require(snapshot.AsSpan(32 + i * 16, 16).SequenceEqual(seed), $"XMM{i} lost");
}

static void AuditLeaf(byte[] bytes)
{
    var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes.AsSpan(0, bytes.Length - 8).ToArray()));
    while (decoder.IP < (ulong)bytes.Length - 8)
    {
        Instruction instruction = decoder.Decode();
        // Installed Iced is the lean decoder build (without InstructionInfo).
        // Explicit instruction whitelist excludes implicit GP/SIMD writes,
        // CALLs, stack manipulation and every AVX/x87/MXCSR instruction.
        Require(instruction.Code != Code.INVALID && instruction.Mnemonic is
            Mnemonic.Mov or Mnemonic.Movzx or Mnemonic.Movsxd or Mnemonic.And or
            Mnemonic.Cmp or Mnemonic.Test or Mnemonic.Jb or Mnemonic.Jne or
            Mnemonic.Je or Mnemonic.Jmp or Mnemonic.Ret or Mnemonic.Add or
            Mnemonic.Xor or Mnemonic.Cmovs or Mnemonic.Cmovg, "Unexpected/non-leaf instruction");
        if (instruction.OpCount > 0 && instruction.Op0Kind == OpKind.Register && instruction.Mnemonic is not (Mnemonic.Cmp or Mnemonic.Test))
            Require(instruction.Op0Register is Register.RAX or Register.EAX or Register.RCX or Register.ECX or
                Register.R8 or Register.R8D or Register.R9, $"Native route writes preserved register: {instruction.Op0Register} / {instruction.Mnemonic}");
    }
}

static byte[] CaptureCaller()
{
    var bytes = new List<byte>();
    void Emit(string hex) => bytes.AddRange(Convert.FromHexString(hex.Replace(" ", "")));
    void Simd(byte opcode, int reg, bool stack, int offset)
    {
        bytes.Add(0xF3); if (reg >= 8) bytes.Add(0x44);
        bytes.AddRange([0x0F, opcode, (byte)((stack ? 0x84 : 0x83) + (reg % 8) * 8)]);
        if (stack) bytes.Add(0x24);
        bytes.AddRange(BitConverter.GetBytes(offset));
    }
    Emit("53 48 81 EC C0 00 00 00 4C 89 CB 48 89 C8 89 D1 44 89 C2 49 89 DA 41 BB 05 00 00 00");
    for (int reg = 6; reg < 16; reg++) Simd(0x7F, reg, true, 32 + (reg - 6) * 16);
    for (int reg = 0; reg < 16; reg++) Simd(0x6F, reg, false, 288);
    Emit("FF D0 48 89 13 4C 89 53 08 4C 89 5B 10 89 43 18");
    for (int reg = 0; reg < 16; reg++) Simd(0x7F, reg, false, 32 + reg * 16);
    for (int reg = 6; reg < 16; reg++) Simd(0x6F, reg, true, 32 + (reg - 6) * 16);
    Emit("48 81 C4 C0 00 00 00 5B C3");
    return bytes.ToArray();
}

static void Require(bool value, string message) { if (!value) throw new Exception(message); }
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
delegate int Capture(nint target, ushort item, int delta, nint output);
[Function(CallingConventions.Microsoft)]
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
delegate int ItemChange(ushort item, int delta);
