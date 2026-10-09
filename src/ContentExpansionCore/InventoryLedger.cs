namespace FFTModLoader.ContentExpansion;

/// <summary>
/// Loader-owned logical inventory for future expanded items. No native hooks,
/// gil writes or save-file writes are performed by this pure transaction layer.
/// </summary>
public sealed class InventoryLedger
{
    public const int StackLimit = 99;
    private readonly HashSet<string> _allowed;
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _committedUses = new();
    private readonly object _gate = new();

    public InventoryLedger(IEnumerable<string> registeredKeys)
    {
        var keys = registeredKeys.ToArray();
        if (keys.Any(string.IsNullOrWhiteSpace) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
            throw new InvalidDataException("Inventory keys must be nonempty and unique.");
        _allowed = keys.ToHashSet(StringComparer.Ordinal);
    }

    public int Count(string key)
    {
        lock (_gate) { RequireKey(key); return _counts.GetValueOrDefault(key); }
    }

    public bool TryBuy(string key, int quantity, int unitPrice, int gil, out int remainingGil)
    {
        lock (_gate)
        {
            RequireKey(key);
            remainingGil = gil;
            if (quantity <= 0 || quantity > StackLimit || unitPrice < 0 || gil < 0)
                throw new ArgumentOutOfRangeException(nameof(quantity), "Invalid purchase values.");
            long cost = (long)quantity * unitPrice;
            int current = _counts.GetValueOrDefault(key);
            if (cost > gil || current + quantity > StackLimit) return false;
            _counts[key] = current + quantity;
            remainingGil = gil - (int)cost;
            return true;
        }
    }

    // Call only after execution is committed, never for UI prediction/cancel.
    // Use IDs are scoped to a battle/session; ClearCommittedUses starts a new scope.
    public bool TryCommitUse(string key, Guid executionId)
    {
        lock (_gate)
        {
            RequireKey(key);
            if (executionId == Guid.Empty) throw new ArgumentException("Execution identity is required.");
            if (_committedUses.Contains(executionId)) return false;
            int current = _counts.GetValueOrDefault(key);
            if (current == 0) return false;
            _counts[key] = current - 1;
            _committedUses.Add(executionId);
            return true;
        }
    }

    public IReadOnlyDictionary<string, int> Snapshot()
    {
        lock (_gate) return new Dictionary<string, int>(_counts, StringComparer.Ordinal);
    }

    public void Restore(IReadOnlyDictionary<string, int> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // Copy once: callers may provide a mutable dictionary. Validate and
        // commit the same records, not two independent enumerations.
        var records = snapshot.ToArray();
        lock (_gate)
        {
            // Validate every record before replacing any live state.
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, count) in records)
            {
                RequireKey(key);
                if (!unique.Add(key)) throw new InvalidDataException("Duplicate stored item key.");
                if (count < 0 || count > StackLimit) throw new InvalidDataException("Invalid stored stack count.");
            }
            _counts.Clear();
            foreach (var (key, count) in records) _counts.Add(key, count);
            _committedUses.Clear();
        }
    }

    public void ClearCommittedUses() { lock (_gate) _committedUses.Clear(); }

    private void RequireKey(string key)
    {
        if (!_allowed.Contains(key)) throw new InvalidDataException($"Unregistered expanded item: {key}");
    }
}
