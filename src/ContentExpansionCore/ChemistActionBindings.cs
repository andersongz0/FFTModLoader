using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion;

/// <summary>
/// Shared identities for the first playable expansion, never a native item-byte
/// alias. Native learning bits for the four retained actions remain unchanged.
/// </summary>
public static class ChemistActionBindings
{
    public const int Command = 6;
    public const int Job = 75;
    public const int FirstExtraItem = 261;
    public const int FirstExtraAbility = 513;
    public const int SerializedSlots = 54;
    public const int SerializedRecordSize = 600;
    public const int NativeRecordSlotOffset = 0x2C;
    public const int NativeLearningOffset = 0x7E;
    public const int NativeJpOffset = 0xCC;
    public const int WorkerSize = 512;
    public const int WorkerLearningOffset = 0xA5;
    public static readonly MenuAction[] TestActions =
    [
        new("potion", 368, 50), new("ether", 371, 150),
        new("remedy", 380, 300), new("phoenix-down", 381, 90),
        new("venom-flask", FirstExtraAbility, 70),
        new("oil-flask", 514, 150), new("fire-flask", 515, 250),
        new("ice-flask", 516, 250), new("thunder-flask", 517, 250),
        new("essence-of-silence", 518, 700), new("essence-of-blindness", 519, 700),
        new("essence-of-slowness", 520, 700), new("essence-of-acceleration", 521, 1000),
        new("essence-of-regeneration", 522, 1000), new("essence-of-flight", 523, 1000),
    ];
    public static int ActionCount => TestActions.Length;
    public static int ExtraCount => ActionCount-4;
    public static uint ExtraMask => (1u<<ExtraCount)-1;
    public static uint AllMask => (1u<<ActionCount)-1;
    public static IEnumerable<int> ExtraSlots => Enumerable.Range(4,ExtraCount);
    public static IEnumerable<string> ExtraKeys => TestActions.Skip(4).Select(a=>a.Key);
    public static bool IsExtraItem(ushort item) => item>=FirstExtraItem && item<FirstExtraItem+ExtraCount;
    private static readonly int[] NativeBits = [0, 3, 12, 13];
    private static readonly ushort[] NativeItems = [240, 243, 252, 253];

    public static int Slot(ushort ability) => Array.FindIndex(TestActions, a => a.AbilityId == ability);
    public static ushort Item(int slot) => slot is >= 0 and < 4 ? NativeItems[slot] :
        slot >=4 && slot<ActionCount ? (ushort)(FirstExtraItem+slot-4) : throw new ArgumentOutOfRangeException(nameof(slot));
    public static uint Learned(ReadOnlySpan<byte> nativeBits, bool venomLearned)
        => Learned(nativeBits,venomLearned?1u:0u);
    public static uint Learned(ReadOnlySpan<byte> nativeBits, uint extraLearned)
    {
        if (nativeBits.Length != 3) throw new InvalidDataException("Exactly three native Chemist learning bytes required.");
        if((extraLearned&~ExtraMask)!=0)throw new InvalidDataException("Unregistered extra learning bits.");
        uint result = extraLearned<<4;
        for (int slot = 0; slot < NativeBits.Length; slot++)
            if ((nativeBits[NativeBits[slot] / 8] & (0x80 >> (NativeBits[slot] % 8))) != 0)
                result |= 1u << slot;
        return result;
    }
    public static void LearnRetained(Span<byte> nativeBits, int slot)
    {
        if (nativeBits.Length != 3 || slot is < 0 or >= 4) throw new ArgumentOutOfRangeException(nameof(slot));
        nativeBits[NativeBits[slot] / 8] |= (byte)(0x80 >> (NativeBits[slot] % 8));
    }
    public static int SerializedSlot(ReadOnlySpan<byte> menuUnit)
    {
        if (menuUnit.Length < NativeRecordSlotOffset + 2) throw new InvalidDataException("Truncated menu unit.");
        int slot = BinaryPrimitives.ReadUInt16LittleEndian(menuUnit[NativeRecordSlotOffset..]);
        if (slot >= SerializedSlots) throw new InvalidDataException("Menu position is not a serialized-unit binding.");
        return slot;
    }

    /// <summary>
    /// Audited setskilldata_common commitment. Preview and target evaluations
    /// never consume; reflected/reaction paths carry native modified-event flags.
    /// </summary>
    public static bool Consumes(uint commitFlags, byte modifiedEvent, int battleActionState) =>
        (commitFlags & 1) != 0 && (modifiedEvent & 0x30) == 0 && battleActionState == 0;

    public static Dictionary<int, string> RecordHashes(ReadOnlySpan<byte> serializedRecords)
    {
        if (serializedRecords.Length != SerializedSlots * SerializedRecordSize)
            throw new InvalidDataException("Expected the complete native serialized-unit array.");
        var result = new Dictionary<int, string>();
        for (int slot = 0; slot < SerializedSlots; slot++)
            result.Add(slot, ExpandedSaveRegistry.Hash(serializedRecords.Slice(slot * SerializedRecordSize, SerializedRecordSize)));
        return result;
    }
}
