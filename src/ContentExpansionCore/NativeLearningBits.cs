namespace FFTModLoader.ContentExpansion;

/// <summary>
/// The audited unit learning stream is three bytes, MSB-first within each byte.
/// ActionLearning uses a logical LSB-first slot mask, NOT native memory order.
/// This codec deliberately has no pointer or save access.
/// </summary>
public static class NativeLearningBits
{
    public const int ByteCount = 3;
    public const int SlotCount = 24;

    public static uint Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteCount) throw new ArgumentException("Expected exactly three native learning bytes.");
        uint result = 0;
        for (int slot = 0; slot < SlotCount; slot++)
            if ((bytes[slot / 8] & (0x80 >> (slot % 8))) != 0) result |= 1u << slot;
        return result;
    }

    public static byte[] Encode(uint logicalSlots)
    {
        if ((logicalSlots & 0xFF000000) != 0) throw new ArgumentOutOfRangeException(nameof(logicalSlots));
        var result = new byte[ByteCount];
        for (int slot = 0; slot < SlotCount; slot++)
            if ((logicalSlots & (1u << slot)) != 0) result[slot / 8] |= (byte)(0x80 >> (slot % 8));
        return result;
    }

    public static byte[] SetLearned(ReadOnlySpan<byte> bytes, int slot)
    {
        if (slot is < 0 or >= SlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
        return Encode(Decode(bytes) | (1u << slot));
    }
}
