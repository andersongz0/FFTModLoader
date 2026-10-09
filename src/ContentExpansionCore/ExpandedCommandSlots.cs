namespace FFTModLoader.ContentExpansion;

/// <summary>
/// A registered command has 24 ushort slots. The original command row has only
/// 22 packed nine-bit IDs in 25 bytes. Slots 22/23 are zero, not the next row.
/// Never serialize expanded IDs back to that packed row.
/// </summary>
public sealed class ExpandedCommandSlots
{
    public const int SlotCount = 24;
    private readonly ushort[] _slots;
    private readonly HashSet<ushort> _registeredActions;

    public ExpandedCommandSlots(ReadOnlySpan<byte> originalRow, IReadOnlyList<MenuAction> actions)
    {
        // Reuse the same key, ID, reserved-ID, count and uniqueness contract as
        // the menu formatter; this performs no native allocation/registration.
        _ = new NativeActionMenu(actions);
        _slots = DecodeOriginal(originalRow);
        _registeredActions = actions.Select(a => a.AbilityId).ToHashSet();
        if (_slots.Skip(16).Any(id => id != 0 && _registeredActions.Contains(id)))
            throw new InvalidDataException("An action identity collides with an original passive identity.");
        Array.Clear(_slots, 0, 16);
        for (int i = 0; i < actions.Count; i++) _slots[i] = actions[i].AbilityId;
    }

    public static ushort[] DecodeOriginal(ReadOnlySpan<byte> packedRow)
    {
        if (packedRow.Length != 25) throw new ArgumentException("Expected a single 25-byte native command row.");
        var result = new ushort[SlotCount];
        for (int slot = 0; slot < 22; slot++)
            result[slot] = (ushort)(packedRow[3 + slot] | (((packedRow[slot / 8] >> (7 - slot % 8)) & 1) << 8));
        return result;
    }

    public ushort GetSlot(int slot)
    {
        if (slot < 0) throw new ArgumentOutOfRangeException(nameof(slot));
        return slot < SlotCount ? _slots[slot] : (ushort)0;
    }

    public bool IsRegisteredAction(ushort identity) => _registeredActions.Contains(identity);

    public void CopyTo(Span<ushort> output)
    {
        if (output.Length < SlotCount) throw new ArgumentException("Command buffer lacks its 24 ushort slots.");
        _slots.CopyTo(output);
    }
}
