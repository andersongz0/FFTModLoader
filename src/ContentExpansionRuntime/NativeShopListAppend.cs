using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>
/// Native-only postprocessor for the SIX-argument GetShopItemList route.
/// Restricted to one host-audited output allocation, ordinary shops, category
/// 7 (medicine tab), no equipment filter and native sort=0. Other calls tail-jump unchanged.
/// Does not install hooks, allocate/register IDs, charge gil or mutate stock.
/// </summary>
public static class NativeShopListAppend
{
    public const short MedicineShopTab = 7;
    public static byte[] Build(long originalTrampoline, long stockState, long auditedOutput,
        int outputWordCapacity, IReadOnlyList<ushort> extraItems,
        long storyProgressGetter = 0, IReadOnlyList<byte>? requiredProgress = null)
    {
        ushort[] ids = extraItems.ToArray();
        byte[]? thresholds = requiredProgress?.ToArray();
        if ((storyProgressGetter == 0) != (thresholds is null) || storyProgressGetter < 0 ||
            thresholds is not null && (thresholds.Length != ids.Length || thresholds.Any(t => t is < 1 or > 20)))
            throw new ArgumentException("Unvalidated native story progression policy.");
        if (originalTrampoline <= 0 || stockState <= 0 || auditedOutput <= 0 ||
            outputWordCapacity is < 2 or > 1024 || ids.Length == 0 || ids.Length >= outputWordCapacity ||
            ids.Distinct().Count() != ids.Length || ids.Any(id => id < NativeItemStockLeaf.NativeItemCount || id >= NativeItemStockLeaf.IdCapacity))
            throw new ArgumentException("Unvalidated shop output/extra-item registration.");
        _ = checked(stockState + NativeItemStockLeaf.StateLength);
        _ = checked(auditedOutput + outputWordCapacity * 2L);
        var c = new Code();
        c.Emit("66 83 FA 0F"); c.Branch(0x83, "original"); // unsigned DX>=15 includes negatives
        c.Emit("66 41 83 F8 07"); c.Branch(0x85, "original");
        c.Emit("83 7C 24 28 00"); c.Branch(0x85, "original"); // arg5: equipment filter
        c.Emit("80 7C 24 30 00"); c.Branch(0x85, "original"); // arg6: native sort
        c.Emit("48 B8"); c.Pointer(auditedOutput);
        c.Emit("49 39 C1"); c.Branch(0x85, "original"); // never append to an unknown caller buffer
        c.Emit("48 B8"); c.Pointer(stockState);
        c.Emit("80 38 01"); c.Branch(0x85, "original");

        // Three nonvolatile pushes align RSP; 64 bytes hold shadow + two stack
        // arguments. Source arg5/6 remain at new RSP+128/+136 respectively.
        c.Emit("53 56 57 48 83 EC 40 4C 89 CB"); // RBX=output
        c.Emit("8B 84 24 80 00 00 00 89 44 24 20");
        c.Emit("0F B6 84 24 88 00 00 00 89 44 24 28");
        if (thresholds is not null)
        {
            // Read the same flag as the original shop, BEFORE its call, so its
            // post-call state is preserved. Save all four integer args and six
            // volatile SIMD registers; aligned 128-byte frame includes shadow.
            // ESI is nonvolatile across the original and holds this request's
            // progress, with no cached unlock state surviving a save load.
            c.Emit("51 52 41 50 41 51 48 81 EC 80 00 00 00");
            for (int i = 0; i < 6; i++) { c.Emit("F3 0F 7F"); c.Emit(Convert.ToHexString([(byte)(0x44 + 8 * i),0x24,(byte)(0x20 + 16 * i)])); }
            c.Emit("B9 6F 00 00 00 FF 15 02 00 00 00 EB 08"); c.Pointer(storyProgressGetter);
            c.Emit("89 C6");
            for (int i = 0; i < 6; i++) { c.Emit("F3 0F 6F"); c.Emit(Convert.ToHexString([(byte)(0x44 + 8 * i),0x24,(byte)(0x20 + 16 * i)])); }
            c.Emit("48 81 C4 80 00 00 00 41 59 41 58 5A 59");
        }
        // RIP-indirect CALL, skip embedded pointer without a scratch register.
        c.Emit("FF 15 02 00 00 00 EB 08"); c.Pointer(originalTrampoline);

        // Preserve the ORIGINAL's post-call volatile GP state, not merely the
        // Microsoft minimum. No SIMD/x87/MXCSR/AVX instruction is emitted.
        c.Emit("51 52 41 50 41 51 41 52 41 53 89 C7"); // EDI=original count
        c.Emit("85 C0"); c.Branch(0x88, "unwind"); // negative native failure
        c.Emit("3D"); c.Integer(outputWordCapacity - ids.Length - 1); c.Branch(0x87, "unwind");
        c.Emit("66 83 3C 43 FF"); c.Branch(0x85, "unwind"); // verify native terminator BEFORE mutation
        c.Emit("49 B8"); c.Pointer(checked(stockState + NativeItemStockLeaf.RegisteredOffset));
        for (int n = 0; n < ids.Length; n++)
        {
            string skip = $"skip-{n}", scan = $"scan-{n}", append = $"append-{n}";
            if (thresholds is not null) { c.Emit("81 FE"); c.Integer(thresholds[n]); c.Branch(0x8C, skip); }
            c.Emit("41 80 B8"); c.Integer(ids[n]); c.Emit("01"); c.Branch(0x85, skip);
            c.Emit("45 31 C9"); // R9D=scan index; original/previous IDs must not be duplicated
            c.Label(scan); c.Emit("41 39 F9"); c.Branch(0x83, append);
            c.Emit("66 42 81 3C 4B"); c.Word(ids[n]); c.Branch(0x84, skip);
            c.Emit("41 FF C1"); c.Jump(scan);
            c.Label(append); c.Emit("66 C7 04 7B"); c.Word(ids[n]); c.Emit("FF C7");
            c.Label(skip);
        }
        c.Emit("66 C7 04 7B FF FF 89 F8"); // terminator + updated native result
        c.Label("unwind");
        c.Emit("41 5B 41 5A 41 59 41 58 5A 59 48 83 C4 40 5F 5E 5B C3");
        c.Label("original"); c.Emit("FF 25 00 00 00 00"); c.Pointer(originalTrampoline);
        return c.Finish();
    }

    private sealed class Code
    {
        private readonly List<byte> _bytes = new();
        private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);
        private readonly List<(int Offset, string Label)> _branches = new();
        public void Emit(string hex) => _bytes.AddRange(Convert.FromHexString(hex.Replace(" ", "")));
        public void Pointer(long value) => _bytes.AddRange(BitConverter.GetBytes(value));
        public void Integer(int value) => _bytes.AddRange(BitConverter.GetBytes(value));
        public void Word(ushort value) => _bytes.AddRange(BitConverter.GetBytes(value));
        public void Label(string name) => _labels.Add(name, _bytes.Count);
        public void Branch(byte condition, string label)
        { _bytes.AddRange([0x0F, condition]); _branches.Add((_bytes.Count, label)); Integer(0); }
        public void Jump(string label) { _bytes.Add(0xE9); _branches.Add((_bytes.Count, label)); Integer(0); }
        public byte[] Finish()
        {
            byte[] bytes = _bytes.ToArray();
            foreach (var (offset, label) in _branches)
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), checked(_labels[label] - offset - 4));
            return bytes;
        }
    }
}
