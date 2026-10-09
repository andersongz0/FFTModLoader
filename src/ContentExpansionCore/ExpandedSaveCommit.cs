namespace FFTModLoader.ContentExpansion;

public enum SaveCommitResult { NoMatchingRequest, NativeWriteFailed, Published }

/// <summary>
/// Bridges queue -> native write completion -> sidecar publication. Never
/// captures live inventory at completion: it could already be another session.
/// </summary>
public sealed class ExpandedSaveCommit
{
    private sealed record Pending(SaveSlotIdentity Slot, string PayloadHash, ExpandedSaveState State);
    private readonly Dictionary<long, Pending> _pending = new();
    private readonly ExpandedSaveRegistry _registry;
    private readonly ExpandedSaveStore _store;
    private readonly object _gate = new();
    public ExpandedSaveCommit(ExpandedSaveRegistry registry, ExpandedSaveStore store)
    {
        _registry = registry;
        _store = store;
    }

    public void Enqueue(long nativeSlotObject, SaveSlotIdentity slot, ReadOnlySpan<byte> packet, ExpandedSaveState snapshot)
    {
        if (nativeSlotObject <= 0) throw new ArgumentOutOfRangeException(nameof(nativeSlotObject));
        _ = slot.StorageKey();
        string hash = NativeSavePacket.ValidateAndHash(packet);
        var state = _registry.CopyValidated(snapshot);
        lock (_gate)
        {
            // The native slot object's buffer is reused. A second outstanding
            // request would be ambiguous; do not overwrite the first snapshot.
            if (_pending.ContainsKey(nativeSlotObject)) throw new InvalidDataException("Save slot object already has a pending request.");
            _pending.Add(nativeSlotObject, new(slot, hash, state));
        }
    }

    public SaveCommitResult Complete(long nativeSlotObject, SaveSlotIdentity slot, ReadOnlySpan<byte> packet, int nativeResult)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(nativeSlotObject, out var pending)) return SaveCommitResult.NoMatchingRequest;
            if (pending.Slot != slot) throw new InvalidDataException("Save completion belongs to a different native slot.");
            if (nativeResult < 0)
            {
                _pending.Remove(nativeSlotObject);
                return SaveCommitResult.NativeWriteFailed;
            }
            if (NativeSavePacket.ValidateAndHash(packet) != pending.PayloadHash)
                throw new InvalidDataException("Queued payload changed before save completion; publication refused.");
            // Retain pending on a sidecar IO failure, so a host can explicitly
            // retry the same captured snapshot. Never treat IO failure as success.
            _store.Publish(slot, pending.PayloadHash, pending.State);
            _pending.Remove(nativeSlotObject);
            return SaveCommitResult.Published;
        }
    }

    public void Cancel(long nativeSlotObject) { lock (_gate) _pending.Remove(nativeSlotObject); }
}
