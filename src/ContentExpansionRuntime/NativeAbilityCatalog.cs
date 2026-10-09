using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>
/// Separate native common/action records for REGISTERED actions. Builders
/// only: no allocation, installation, ID reservation, learning or battle activation.
/// New consumables must use a separate full-width item link: native kind=1 stores
/// an item ID in one byte and therefore cannot represent item 261.
/// </summary>
public static class NativeAbilityCatalog
{
    public const int FirstExtraId = 513; // 512 is a native special action, never allocated
    public const int IdCapacity = 1024;
    public const int RegisteredOffset = 16;
    public const int CommonPointersOffset = RegisteredOffset + IdCapacity;
    public const int ActionPointersOffset = CommonPointersOffset + IdCapacity * sizeof(long);
    public const int StateLength = ActionPointersOffset + IdCapacity * sizeof(long);

    public static byte[] BuildAddress(long original, long state) => Build(original, state, true);
    public static byte[] BuildSecondary(long original, long state) => Build(original, state, false);

    /// <summary>Only the eleven registered consumable actions are not RSM.
    /// The original effect classifier indexes its legacy table without a bound.</summary>
    public static byte[] BuildConsumableEffectClassification(long original)
    {
        if(original<=0)throw new ArgumentOutOfRangeException(nameof(original));
        var c=new Code();
        c.Emit("8D 81 FF FD FF FF 83 F8 0A"); // unsigned (id-513) <= 10
        c.Branch(0x87,"original");
        c.Emit("31 C0 C3");
        c.Label("original");c.Emit("FF 25 00 00 00 00");c.Pointer(original);
        return c.Finish();
    }

    private static byte[] Build(long original, long state, bool address)
    {
        if (original <= 0 || state <= 0 || original == state)
            throw new ArgumentOutOfRangeException(nameof(original));
        _ = checked(state + StateLength);
        var c = new Code();
        // Only scratch RAX and R9 before the fallback. get_ability_address
        // recreates R9; GetAbilitySecondaryData does NOT, so use RAX alone in
        // its raw-ID path and defer R9 initialization to the successful route.
        if (address)
        {
            c.Emit("89 C8 25 00 FC FF FF"); // flags/high bits outside the ten-bit identity
            c.Emit("85 C0"); c.Branch(0x84, "flags-ok");
            foreach (int flags in new[] { 0x1000, 0x4000, 0x6000 })
            { c.Emit("3D"); c.Int(flags); c.Branch(0x84, "flags-ok"); }
            c.Jump("original");
            c.Label("flags-ok");
            // A rewritten command may explicitly retain an original item action
            // as a full-width consumable action. Only its registered pointer is
            // overridden; every other original ability remains native.
            c.Emit("44 8B C9 41 81 E1 FF 03 00 00 41 81 F9 00 02 00 00"); c.Branch(0x84, "original");
            c.Emit("48 B8"); c.Pointer(state);
            c.Emit("80 38 01"); c.Branch(0x85, "original");
            c.Emit("42 80 BC 08"); c.Int(RegisteredOffset); c.Emit("01"); c.Branch(0x85, "original");
            // Check BOTH pointers before touching either caller output. RCX
            // must remain the raw entry on every fallback, hence first test
            // pointers in memory instead of loading into RCX prematurely.
            c.Emit("4A 83 BC C8"); c.Int(CommonPointersOffset); c.Emit("00"); c.Branch(0x84, "original");
            c.Emit("4A 83 BC C8"); c.Int(ActionPointersOffset); c.Emit("00"); c.Branch(0x84, "original");
            c.Emit("4A 8B 8C C8"); c.Int(CommonPointersOffset);
            c.Emit("4A 8B 84 C8"); c.Int(ActionPointersOffset);
            c.Emit("48 89 0A 49 89 00"); // *commonOut=RCX, *secondaryOut=RAX
            c.Emit("4B 8D 0C 89 31 C0 C3"); // RCX=id*5, R9=id; native action kind=0
        }
        else
        {
            // This helper accepts raw uint, not UI flags; no lossy mask.
            c.Emit("81 F9 00 02 00 00"); c.Branch(0x84, "original");
            c.Emit("81 F9 00 04 00 00"); c.Branch(0x83, "original");
            c.Emit("48 B8"); c.Pointer(state);
            c.Emit("80 38 01"); c.Branch(0x85, "original");
            c.Emit("80 BC 08"); c.Int(RegisteredOffset); c.Emit("01"); c.Branch(0x85, "original");
            c.Emit("48 83 BC C8"); c.Int(CommonPointersOffset); c.Emit("00"); c.Branch(0x84, "original");
            c.Emit("48 8B 84 C8"); c.Int(ActionPointersOffset);
            c.Emit("48 85 C0"); c.Branch(0x84, "original");
            c.Emit("48 8D 0C 89 C3"); // RCX=id*5, RAX=20-byte action; R9 unchanged
        }
        c.Label("original"); c.Emit("FF 25 00 00 00 00"); c.Pointer(original);
        return c.Finish();
    }

    private sealed class Code
    {
        private readonly List<byte> _bytes = new();
        private readonly Dictionary<string, int> _labels = new();
        private readonly List<(int Offset, string Label)> _branches = new();
        public void Emit(string hex) => _bytes.AddRange(Convert.FromHexString(hex.Replace(" ", "")));
        public void Pointer(long value) => _bytes.AddRange(BitConverter.GetBytes(value));
        public void Int(int value) => _bytes.AddRange(BitConverter.GetBytes(value));
        public void Label(string name) => _labels.Add(name, _bytes.Count);
        public void Branch(byte condition, string name) { _bytes.AddRange([0x0F, condition]); _branches.Add((_bytes.Count,name)); Int(0); }
        public void Jump(string name) { _bytes.Add(0xE9); _branches.Add((_bytes.Count,name)); Int(0); }
        public byte[] Finish()
        {
            byte[] code = _bytes.ToArray();
            foreach (var (offset,label) in _branches)
                BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(offset), checked(_labels[label]-offset-4));
            return code;
        }
    }
}
