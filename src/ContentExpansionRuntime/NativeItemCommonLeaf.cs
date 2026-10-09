using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>
/// Native get_itemcommon extension. No managed callback, calls, stack writes,
/// ID masking or changes to the original 261 records. The host owns the state
/// and record allocations and must publish them before setting Enabled to 1.
/// This emitter alone neither installs hooks nor makes a playable mod.
/// </summary>
public static class NativeItemCommonLeaf
{
    public const int NativeItemCount = 261;
    public const int IdCapacity = 1024;
    public const int RecordSize = 12;
    public const int EnabledOffset = 0;
    public const int RecordsOffset = 16; // aligned array of native pointers
    public const int StateLength = RecordsOffset + IdCapacity * sizeof(long);

    // Audited native post-call GP contract: RCX=image base, EDX=256,
    // RAX=record; R8/R9/R10/R11 and all SIMD/FP/nonvolatile state untouched.
    // Only RAX/RDX/flags are used while testing the extension. The fallback
    // original overwrites EDX itself, so the extra RDX scratch is safe there.
    // This getter accepts a RAW ushort, unlike ItemChg's 10-bit normalization.
    public static byte[] Build(long originalTrampoline, long stateAddress, long imageBase)
    {
        if (originalTrampoline <= 0 || stateAddress <= 0 || imageBase <= 0 ||
            originalTrampoline == stateAddress || stateAddress == imageBase)
            throw new ArgumentOutOfRangeException(nameof(originalTrampoline));
        _ = checked(stateAddress + StateLength);
        var code = new Code();
        code.Emit("0F B7 C1 3D 05 01 00 00"); // EAX=raw ushort(CX), originals bypass
        code.Branch(0x82, "original");
        code.Emit("3D 00 04 00 00"); // out-of-catalog raw IDs do NOT alias valid IDs
        code.Branch(0x83, "original");
        code.Emit("48 BA"); code.Pointer(stateAddress);
        code.Emit("80 3A 01");
        code.Branch(0x85, "original");
        code.Emit("48 8B 44 C2 10 48 85 C0"); // RAX=records[id]; null is unregistered
        code.Branch(0x84, "original");
        code.Emit("48 B9"); code.Pointer(imageBase);
        code.Emit("BA 00 01 00 00 C3");
        code.Label("original");
        code.Emit("FF 25 00 00 00 00"); code.Pointer(originalTrampoline);
        return code.Finish();
    }

    private sealed class Code
    {
        private readonly List<byte> _bytes = new();
        private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);
        private readonly List<(int Operand, string Label)> _branches = new();
        public void Emit(string hex) => _bytes.AddRange(Convert.FromHexString(hex.Replace(" ", "")));
        public void Pointer(long value) => _bytes.AddRange(BitConverter.GetBytes(value));
        public void Label(string name) => _labels.Add(name, _bytes.Count);
        public void Branch(byte condition, string label)
        {
            _bytes.AddRange([0x0F, condition]);
            _branches.Add((_bytes.Count, label)); _bytes.AddRange(new byte[4]);
        }
        public byte[] Finish()
        {
            byte[] result = _bytes.ToArray();
            foreach (var (operand, label) in _branches)
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(operand, 4), checked(_labels[label] - operand - 4));
            return result;
        }
    }
}
