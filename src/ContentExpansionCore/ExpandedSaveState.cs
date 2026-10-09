using System.Security.Cryptography;

namespace FFTModLoader.ContentExpansion;

// SerializedSlot is an audited save-record position, NOT a menu index or a
// battle worker number. NativeRecordSha256 binds learning to those exact bytes.
public sealed record SavedUnitLearning(int SerializedSlot, string NativeRecordSha256, string[] LearnedKeys);
public sealed record ExpandedSaveState(Dictionary<string, int> Items, SavedUnitLearning[] Units);

/// <summary>Validates/copies state before any save publication or live restoration.</summary>
public sealed class ExpandedSaveRegistry
{
    private readonly HashSet<string> _items;
    private readonly HashSet<string> _actions;
    public ExpandedSaveRegistry(IEnumerable<string> itemKeys, IEnumerable<string> newActionKeys)
    {
        _items = Keys(itemKeys);
        _actions = Keys(newActionKeys);
    }

    public ExpandedSaveState CopyValidated(ExpandedSaveState state)
    {
        if (state is null || state.Items is null || state.Units is null)
            throw new InvalidDataException("Missing expanded save state.");
        var items = state.Items.ToArray();
        var units = state.Units.ToArray();
        if (items.Any(p => !_items.Contains(p.Key) || p.Value is < 0 or > InventoryLedger.StackLimit))
            throw new InvalidDataException("Unregistered item or invalid saved quantity.");
        if (units.Length > 54 || units.Any(u => u is null || u.SerializedSlot is < 0 or >= 54) ||
            units.Select(u => u.SerializedSlot).Distinct().Count() != units.Length)
            throw new InvalidDataException("Invalid or repeated serialized unit slot.");
        var copy = new List<SavedUnitLearning>();
        foreach (var unit in units.OrderBy(u => u.SerializedSlot))
        {
            RequireHash(unit.NativeRecordSha256);
            if (unit.LearnedKeys is null) throw new InvalidDataException("Missing learned action keys.");
            var learned = unit.LearnedKeys.ToArray();
            if (learned.Any(k => k is null || !_actions.Contains(k)) ||
                learned.Distinct(StringComparer.Ordinal).Count() != learned.Length)
                throw new InvalidDataException("Unregistered or repeated learned action key.");
            copy.Add(new(unit.SerializedSlot, unit.NativeRecordSha256.ToUpperInvariant(),
                learned.Order(StringComparer.Ordinal).ToArray()));
        }
        return new(items.OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), copy.ToArray());
    }

    public ExpandedSaveState ValidateUnitBindings(ExpandedSaveState state,
        IReadOnlyDictionary<int, string> loadedNativeRecordHashes)
    {
        ArgumentNullException.ThrowIfNull(loadedNativeRecordHashes);
        var copy = CopyValidated(state);
        foreach (var unit in copy.Units)
            if (!loadedNativeRecordHashes.TryGetValue(unit.SerializedSlot, out var actual) ||
                !unit.NativeRecordSha256.Equals(actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Loaded unit does not match the stored serialized record; no state restored.");
        return copy;
    }

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static void RequireHash(string hash)
    {
        if (hash is null || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Invalid SHA256 identity.");
    }
    private static HashSet<string> Keys(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var array = keys.ToArray();
        if (array.Any(string.IsNullOrWhiteSpace) || array.Distinct(StringComparer.Ordinal).Count() != array.Length)
            throw new InvalidDataException("Registered save keys must be nonempty and unique.");
        return array.ToHashSet(StringComparer.Ordinal);
    }
}
