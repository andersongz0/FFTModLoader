using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using FFTModLoader.ContentExpansion.Runtime;
using Iced.Intel;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

static class NativeItemDataRouteTests
{
    [Function(CallingConventions.Microsoft)]
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint Common(ushort id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint Capture(nint target, ushort id, nint output);

    public static void Run(byte[] image, PEReader pe)
    {
        const int rva = 0x2B8C44, length = 46;
        var section = pe.PEHeaders.SectionHeaders.Single(s => s.VirtualAddress <= rva && rva + length <= s.VirtualAddress + s.SizeOfRawData);
        byte[] body = image.AsSpan(section.PointerToRawData + rva - section.VirtualAddress, length).ToArray();
        Require(Convert.ToHexString(body) == "0FB7C1BA00010000663BCA488D0DAA73D4FF488D0440730A488D048590EA8000EB08488D048510F967004803C1C3", "Native item getter changed");
        using var original = new OwnedNativeMemory(4096);
        using var route = new OwnedNativeMemory(4096);
        using var state = new OwnedNativeMemory(12288);
        using var records = new OwnedNativeMemory(4096);
        using var snapshot = new OwnedNativeMemory(4096);
        using var caller = new OwnedNativeMemory(4096);
        // Relocate only the original image-base LEA into this OWN fixture.
        // No record dereference is performed for original/invalid IDs.
        long fixtureBase = (long)original.Address;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(14), -18);
        original.Write(0, body); original.ExecutablePage();
        caller.Write(0, CaptureCaller()); caller.ExecutablePage();
        var capture = Marshal.GetDelegateForFunctionPointer<Capture>(caller.Address);
        byte[] seed = Enumerable.Range(0, 16).Select(i => (byte)(0x41 + i)).ToArray();
        snapshot.Write(320, seed);
        int[] extraIds = Enumerable.Range(261, 11).Append(1023).ToArray();
        for (int index = 0; index < extraIds.Length; index++)
        {
            int item = extraIds[index];
            byte[] record = [13, 162, 1, 2, 0, 34, 0, 0, 140, 0, 1, 0];
            records.Write(index * 12, record);
            state.Write(NativeItemCommonLeaf.RecordsOffset + item * 8,
                BitConverter.GetBytes((long)records.Address + index * 12));
        }
        state.Write(0, [1]);
        IHook<Common> hook;
        unsafe { hook = ReloadedHooks.Instance.CreateHook<Common>((void*)route.Address, fixtureBase); }
        byte[] code = NativeItemCommonLeaf.Build((long)hook.OriginalFunctionAddress, (long)state.Address, fixtureBase);
        Audit(code);
        route.Write(0, code); route.ExecutablePage(); hook.Activate();
        try
        {
            byte[] before = state.Read(0, 12288);
            byte[] recordBefore = records.Read(0, 4096);
            for (int id = 0; id <= ushort.MaxValue; id++)
            {
                nint result = capture(original.Address, (ushort)id, snapshot.Address);
                int index = Array.IndexOf(extraIds, id);
                long expected = index >= 0 ? (long)records.Address + index * 12 :
                    fixtureBase + (id < 256 ? 0x80EA90 : 0x67F910) + id * 12L;
                Require((long)result == expected, $"Raw item ID {id} returned wrong record/aliased ID");
                Check();
            }
            Require(state.Read(0, 12288).SequenceEqual(before), "Getter wrote catalog/guard memory");
            Require(records.Read(0, 4096).SequenceEqual(recordBefore), "Getter wrote native record/guard memory");
            nint venom = capture(original.Address, 261, snapshot.Address);
            Require(Marshal.ReadInt16(venom, 8) == 140, "Venom record price is not 140 gil");
            foreach (byte enabled in new byte[] { 0, 2, 255 })
            {
                state.Write(0, [enabled]);
                Require((long)capture(original.Address, 261, snapshot.Address) == fixtureBase + 0x67F910 + 261 * 12, "Disabled catalog exposed extra record");
                Check();
            }
            state.Write(0, [1]); hook.Disable();
            Require((long)capture(original.Address, 261, snapshot.Address) == fixtureBase + 0x67F910 + 261 * 12, "Hook disable failed");
            Check(); hook.Enable();
            Require(capture(original.Address, 261, snapshot.Address) == venom, "Re-enable lost registered record");
            Check();
            Console.WriteLine("PASS: expanded common item records via actual Reloaded hook; all 65536 RAW ushort IDs, no aliases; original RCX/RDX post-state, R8-R11 and XMM0-15 preserved; separate 12-byte Venom record/140 gil. OWN process only, NOT a gameplay test.");
        }
        finally { if (hook.IsHookEnabled) hook.Disable(); GC.KeepAlive(hook); GC.KeepAlive(capture); }

        void Check()
        {
            byte[] actual = snapshot.Read(0, 320);
            long[] expected = [fixtureBase, 256, 0x11223344, 0x22334455, 0x33445566, 0x44556677];
            for (int i = 0; i < expected.Length; i++)
                Require(BinaryPrimitives.ReadInt64LittleEndian(actual.AsSpan(i * 8)) == expected[i], $"Common route internal GP contract lost at index {i}");
            for (int i = 0; i < 16; i++)
                Require(actual.AsSpan(64 + i * 16, 16).SequenceEqual(seed), $"Common route changed XMM{i}");
        }
    }

    private static void Audit(byte[] bytes)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes.AsSpan(0, bytes.Length - 8).ToArray()));
        while (decoder.IP < (ulong)bytes.Length - 8)
        {
            var instruction = decoder.Decode();
            Require(instruction.Code != Code.INVALID && instruction.Mnemonic is Mnemonic.Mov or Mnemonic.Movzx or
                Mnemonic.Cmp or Mnemonic.Test or Mnemonic.Jb or Mnemonic.Jae or Mnemonic.Jne or Mnemonic.Je or Mnemonic.Jmp or Mnemonic.Ret,
                "Non-leaf/unexpected common route instruction");
            if (instruction.OpCount > 0 && instruction.Op0Kind == OpKind.Register && instruction.Mnemonic is not (Mnemonic.Cmp or Mnemonic.Test))
                Require(instruction.Op0Register is Register.RAX or Register.EAX or Register.RCX or Register.RDX or Register.EDX,
                    "Common route writes a preserved register");
        }
    }

    internal static byte[] CaptureCaller()
    {
        var code = new List<byte>();
        void Emit(string hex) => code.AddRange(Convert.FromHexString(hex.Replace(" ", "")));
        void Simd(byte opcode, int reg, bool stack, int offset)
        {
            code.Add(0xF3); if (reg >= 8) code.Add(0x44);
            code.AddRange([0x0F, opcode, (byte)((stack ? 0x84 : 0x83) + reg % 8 * 8)]);
            if (stack) code.Add(0x24);
            code.AddRange(BitConverter.GetBytes(offset));
        }
        // Capture(target=RCX,item=DX,output=R8). RBX owns output; RAX owns target.
        Emit("53 48 81 EC C0 00 00 00 4C 89 C3 48 89 C8 0F B7 CA 41 B8 44 33 22 11 41 B9 55 44 33 22 41 BA 66 55 44 33 41 BB 77 66 55 44");
        for (int reg = 6; reg < 16; reg++) Simd(0x7F, reg, true, 32 + (reg - 6) * 16);
        for (int reg = 0; reg < 16; reg++) Simd(0x6F, reg, false, 320);
        Emit("FF D0 48 89 0B 48 89 53 08 4C 89 43 10 4C 89 4B 18 4C 89 53 20 4C 89 5B 28");
        for (int reg = 0; reg < 16; reg++) Simd(0x7F, reg, false, 64 + reg * 16);
        for (int reg = 6; reg < 16; reg++) Simd(0x6F, reg, true, 32 + (reg - 6) * 16);
        Emit("48 81 C4 C0 00 00 00 5B C3");
        return code.ToArray();
    }
    private static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
}
