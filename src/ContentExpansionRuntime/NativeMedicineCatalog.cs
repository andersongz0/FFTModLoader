using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>
/// Native-only medicine classification and secondary-record routes. No hook is
/// installed here. The host must validate all registrations and retain memory;
/// secondary records being readable does NOT mean new battle formulas are ready.
/// </summary>
public static class NativeMedicineCatalog
{
    public const int NativeItemCount = 261;
    public const int IdCapacity = 1024;
    public const int EnabledOffset = 0;
    public const int RegisteredOffset = 16;
    public const int SecondaryPointersOffset = RegisteredOffset + IdCapacity;
    public const int StateLength = SecondaryPointersOffset + IdCapacity * sizeof(long);
    public const int NativeMedicineRangeIndex = 6; // NOT secondary-data type 4 or UI category 5
    public const int NativeMedicineFirstId = 240;
    public const int NativeRangeEndRva = 0x6804FC;

    public static byte[] BuildRangeIndex(long original, long state, long imageBase) =>
        Build(original, state, imageBase, 0, Kind.Range);
    public static byte[] BuildRangeMinimum(long original, long state, long imageBase) =>
        Build(original, state, imageBase, 0, Kind.Minimum);
    public static byte[] BuildSecondaryRecord(long original, long state, long imageBase, long tableBase) =>
        Build(original, state, imageBase, tableBase, Kind.Secondary);

    private enum Kind { Range, Minimum, Secondary }
    private static byte[] Build(long original, long state, long imageBase, long tableBase, Kind kind)
    {
        if (original <= 0 || state <= 0 || imageBase <= 0 || original == state ||
            (kind == Kind.Secondary && tableBase <= 0))
            throw new ArgumentOutOfRangeException(nameof(original));
        _ = checked(state + StateLength);
        _ = checked(imageBase + NativeRangeEndRva);
        var b = new Code();
        // Test only with RAX/RDX. The native fallback overwrites EDX itself,
        // including invalid raw IDs; R8/R9/R10/R11 are still intact on entry.
        b.Emit("0F B7 C1 3D 05 01 00 00"); b.Branch(0x82, "original");
        b.Emit("3D 00 04 00 00"); b.Branch(0x83, "original");
        b.Emit("48 BA"); b.Pointer(state);
        b.Emit("80 3A 01"); b.Branch(0x85, "original");
        b.Emit("80 BC 02"); b.Int(RegisteredOffset); b.Emit("01"); b.Branch(0x85, "original");
        b.Emit("48 8B 84 C2"); b.Int(SecondaryPointersOffset);
        b.Emit("48 85 C0"); b.Branch(0x84, "original");
        // Recreate native successful-medicine post-state, not just the return.
        b.Emit("49 B8"); b.Pointer(checked(imageBase + NativeRangeEndRva));
        if (kind == Kind.Range)
            b.Emit("B8 06 00 00 00 BA 06 00 00 00 C3"); // RCX unchanged, R9..R11 preserved
        else if (kind == Kind.Minimum)
        {
            b.Emit("B8 F0 00 00 00 B9 06 00 00 00 48 BA"); b.Pointer(imageBase); b.Emit("C3");
        }
        else
        {
            b.Emit("44 0F B7 C9 45 89 CA 41 81 EA F0 00 00 00 BA 06 00 00 00 48 B9");
            b.Pointer(tableBase); b.Emit("C3"); // RAX remains the registered secondary record
        }
        b.Label("original"); b.Emit("FF 25 00 00 00 00"); b.Pointer(original);
        return b.Finish();
    }

    private sealed class Code
    {
        private readonly List<byte> _bytes = new();
        private readonly Dictionary<string, int> _labels = new();
        private readonly List<(int Offset, string Label)> _branches = new();
        public void Emit(string hex) => _bytes.AddRange(Convert.FromHexString(hex.Replace(" ", "")));
        public void Pointer(long v) => _bytes.AddRange(BitConverter.GetBytes(v));
        public void Int(int v) => _bytes.AddRange(BitConverter.GetBytes(v));
        public void Label(string label) => _labels.Add(label, _bytes.Count);
        public void Branch(byte c, string label) { _bytes.AddRange([0x0F,c]); _branches.Add((_bytes.Count,label)); Int(0); }
        public byte[] Finish()
        {
            byte[] result = _bytes.ToArray();
            foreach (var (offset, label) in _branches)
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), checked(_labels[label] - offset - 4));
            return result;
        }
    }
}
