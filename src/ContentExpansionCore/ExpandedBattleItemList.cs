using System.Collections.ObjectModel;

namespace FFTModLoader.ContentExpansion;

public sealed record ItemActionIdentity(string Key, ushort AbilityId, ushort ItemId);

/// <summary>
/// The expanded battle selection is keyed by ability and uses full ushort item
/// identities. It must NOT be copied into the game's byte-sized set_itemitem2
/// buffers. Native caller/selection/prediction/dispatch adapters are required.
/// This class performs no consumption and is not an installed native hook.
/// </summary>
public sealed class ExpandedBattleItemList
{
    private readonly ItemActionIdentity[] _actions;

    public ExpandedBattleItemList(IEnumerable<ItemActionIdentity> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        _actions = actions.ToArray();
        if (_actions.Length is < 1 or > 16 || _actions.Any(a => a is null || string.IsNullOrWhiteSpace(a.Key) ||
                a.AbilityId is 0 or 512 || a.AbilityId > 1023 || a.ItemId is 0 or > 1023) ||
            _actions.Select(a => a.Key).Distinct(StringComparer.Ordinal).Count() != _actions.Length ||
            _actions.Select(a => a.AbilityId).Distinct().Count() != _actions.Length ||
            _actions.Select(a => a.ItemId).Distinct().Count() != _actions.Length)
            throw new InvalidDataException("Item actions require unique registered keys and ten-bit UI identities.");
    }

    public IReadOnlyList<ItemActionIdentity> Select(uint learnedActionSlots, IReadOnlyDictionary<string, int> stock)
    {
        ArgumentNullException.ThrowIfNull(stock);
        if ((learnedActionSlots & 0xFF000000) != 0) throw new ArgumentOutOfRangeException(nameof(learnedActionSlots));
        var selected = new List<ItemActionIdentity>();
        for (int slot = 0; slot < _actions.Length; slot++)
        {
            var action = _actions[slot];
            // Missing native stock is not silently equivalent to empty stock:
            // the host must query all native and expanded inventory entries.
            if (!stock.TryGetValue(action.Key, out int amount) || amount is < 0 or > 99)
                throw new InvalidDataException($"Missing/invalid inventory count for {action.Key}.");
            if (amount > 0 && (learnedActionSlots & (1u << slot)) != 0) selected.Add(action);
        }
        return new ReadOnlyCollection<ItemActionIdentity>(selected);
    }

    public static int WriteWideItems(IReadOnlyList<ItemActionIdentity> selected, Span<ushort> output)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (selected.Count > 16 || output.Length < selected.Count + 1 ||
            selected.Any(a => a is null || a.ItemId is 0 or > 1023))
            throw new ArgumentException("Invalid or insufficient expanded item output buffer.");
        for (int i = 0; i < selected.Count; i++) output[i] = selected[i].ItemId;
        output[selected.Count] = 0xFFFF;
        return selected.Count;
    }

    public static int WriteLegacyItems(IReadOnlyList<ItemActionIdentity> selected, Span<byte> output)
    {
        ArgumentNullException.ThrowIfNull(selected);
        // Fail BEFORE writes if any selected item would truncate or equal the
        // native FF terminator. Existing callers are never silently widened.
        if (selected.Count > 16 || output.Length < selected.Count + 1 ||
            selected.Any(a => a is null || a.ItemId is 0 or >= 255))
            throw new ArgumentException("Expanded item IDs require an audited ushort transport; legacy buffer unchanged.");
        for (int i = 0; i < selected.Count; i++) output[i] = checked((byte)selected[i].ItemId);
        output[selected.Count] = 0xFF;
        return selected.Count;
    }
}
