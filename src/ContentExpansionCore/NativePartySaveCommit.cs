namespace FFTModLoader.ContentExpansion;

/// <summary>
/// Binds a frozen expanded snapshot to the exact serialized SaveWork inside a
/// native packet. A manual file contains fifty SaveWorks: its whole-file hash
/// is not a slot identity. Unchanged slots retain their own immutable sidecars
/// when a different slot is overwritten. No native save bytes are modified.
/// </summary>
public sealed class NativePartySaveCommit
{
    public const int WorkSize = 0x9CDC;
    public const int RecordsOffset = 0x518;
    public const int RecordsSize = 54 * 600;
    public const int BattleRecordsOffset = 0x154 + (0x89BC68 - 0x8167A0);
    public static bool IsBattlePayload(ReadOnlySpan<byte> inner)=>inner.Length>=0x154+0xA31D0 && inner.Length<=0x180000 &&
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(inner)==0x49544646 &&
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(inner[4..])==0x7A &&
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(inner[12..])==1;
    private sealed record Pending(SaveSlotIdentity Slot, string PacketHash, string WorkHash,
        ExpandedSaveState State);
    private sealed record Loaded(SaveSlotIdentity Slot, byte[] Packet);
    private readonly ExpandedSaveRegistry _registry;
    private readonly ExpandedSaveStore _store;
    private readonly Dictionary<long, Pending> _pending = [];
    private readonly List<Loaded> _loaded = [];
    private readonly object _gate = new();

    public NativePartySaveCommit(ExpandedSaveRegistry registry, ExpandedSaveStore store)
    { _registry = registry; _store = store; }

    public bool Enqueue(long slotObject, SaveSlotIdentity slot, byte[] packet,
        ReadOnlySpan<byte> frozenWork, ExpandedSaveState snapshot)
    {
        if (slotObject <= 0) throw new ArgumentOutOfRangeException(nameof(slotObject));
        string packetHash = NativeSavePacket.ValidateAndHash(packet);
        ValidateWork(frozenWork);
        if (FindUniqueWork(packet, frozenWork) < 0) return false;
        var copy = _registry.ValidateUnitBindings(snapshot,
            ChemistActionBindings.RecordHashes(frozenWork.Slice(RecordsOffset, RecordsSize)));
        _ = slot.StorageKey();
        lock (_gate)
        {
            if (_pending.ContainsKey(slotObject)) throw new InvalidDataException("A native save request is already outstanding.");
            _pending.Add(slotObject, new(slot, packetHash, ExpandedSaveRegistry.Hash(frozenWork), copy));
        }
        return true;
    }

    public bool EnqueueManual(long slotObject,SaveSlotIdentity slot,byte[] packet,int selectedSlot,
        ReadOnlySpan<byte> preparedWork,ExpandedSaveState snapshot)
    {
        if(slotObject<=0 || selectedSlot is <0 or >=50)throw new ArgumentOutOfRangeException(nameof(selectedSlot));
        string packetHash=NativeSavePacket.ValidateAndHash(packet);ValidateWork(preparedWork);
        int offset=NativeSavePacket.HeaderSize+selectedSlot*(WorkSize+8);
        if(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8))!=0xE ||
           packet.Length<NativeSavePacket.HeaderSize+50*(WorkSize+8) ||
           !packet.AsSpan(offset,WorkSize).SequenceEqual(preparedWork))return false;
        var validated=_registry.ValidateUnitBindings(snapshot,ChemistActionBindings.RecordHashes(preparedWork.Slice(RecordsOffset,RecordsSize)));
        _=slot.StorageKey();
        lock(_gate)
        {
            if(_pending.ContainsKey(slotObject))throw new InvalidDataException("A native save request is already outstanding.");
            _pending.Add(slotObject,new(slot,packetHash,ExpandedSaveRegistry.Hash(preparedWork),validated));
        }
        return true;
    }

    public SaveCommitResult Complete(long slotObject, SaveSlotIdentity slot, byte[] packet, int result)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(slotObject, out var pending)) return SaveCommitResult.NoMatchingRequest;
            try
            {
            if (pending.Slot != slot) throw new InvalidDataException("Native save completion changed its slot identity.");
            if (result < 0) { _pending.Remove(slotObject); return SaveCommitResult.NativeWriteFailed; }
            if (NativeSavePacket.ValidateAndHash(packet) != pending.PacketHash)
                throw new InvalidDataException("Native packet differs from the frozen queue capture.");
            _store.PublishConfirmed(slot, pending.WorkHash, pending.State);
            // The game can resume its cached save without another disk read.
            // Only a successful, exact queued write supplies this provenance.
            Stage(slot, packet);
            _pending.Remove(slotObject);
            return SaveCommitResult.Published;
            }
            finally
            {
                // An invalid/failed completion must not poison the next native
                // request when the game reuses this slot object's address.
                _pending.Remove(slotObject);
            }
        }
    }
    public void Cancel(long slotObject) { lock (_gate) _pending.Remove(slotObject); }
    public void ResetReadEvidence() { lock(_gate)_loaded.Clear(); }

    public bool EnqueueBattle(long slotObject,SaveSlotIdentity slot,byte[] packet,ExpandedSaveState state)
    {
        if(slotObject<=0)throw new ArgumentOutOfRangeException(nameof(slotObject));
        string packetHash=NativeSavePacket.ValidateAndHash(packet);
        ReadOnlySpan<byte> inner=packet.AsSpan(NativeSavePacket.HeaderSize);
        if(!IsBattlePayload(inner))return false;
        var validated=_registry.ValidateUnitBindings(state,ChemistActionBindings.RecordHashes(inner.Slice(BattleRecordsOffset,RecordsSize)));
        _=slot.StorageKey();
        lock(_gate)
        {
            if(_pending.ContainsKey(slotObject))throw new InvalidDataException("A native save request is already outstanding.");
            _pending.Add(slotObject,new(slot,packetHash,ExpandedSaveRegistry.Hash(inner),validated));
        }
        return true;
    }
    public ExpandedSaveState? RestoreConsumedBattle(string profile,ReadOnlySpan<byte> inner)
    {
        if(!IsBattlePayload(inner))throw new InvalidDataException("Invalid consumed native battle payload.");
        return _store.LoadConsumedWork(profile,ExpandedSaveRegistry.Hash(inner),ChemistActionBindings.RecordHashes(inner.Slice(BattleRecordsOffset,RecordsSize)));
    }

    public ExpandedSaveState? RestoreConsumedWork(string profile,ReadOnlySpan<byte> work)
    {
        ValidateWork(work);
        return _store.LoadConsumedWork(profile,ExpandedSaveRegistry.Hash(work),
            ChemistActionBindings.RecordHashes(work.Slice(RecordsOffset,RecordsSize)));
    }

    // Read completion is not the party-load boundary. A thumbnail may read a
    // completely different slot; stage only, never restore here.
    public void Stage(SaveSlotIdentity slot, byte[] packet)
    {
        string hash = NativeSavePacket.ValidateAndHash(packet);
        _ = slot.StorageKey();
        lock (_gate)
        {
            if (_loaded.Any(x => x.Slot == slot && ExpandedSaveRegistry.Hash(x.Packet) == hash)) return;
            if (_loaded.Where(x => x.Slot != slot).Sum(x => (long)x.Packet.Length) + packet.Length > 32 * 1024 * 1024)
                throw new InvalidDataException("Loaded save evidence exceeded the bounded cache.");
            // Keep only the latest verified packet for each file identity.
            _loaded.RemoveAll(x => x.Slot == slot);
            _loaded.Add(new(slot, (byte[])packet.Clone()));
        }
    }

    public bool TryRestore(ReadOnlySpan<byte> actuallyLoadedWork, out ExpandedSaveState? state)
    {
        ValidateWork(actuallyLoadedWork);
        string hash = ExpandedSaveRegistry.Hash(actuallyLoadedWork);
        var records = ChemistActionBindings.RecordHashes(actuallyLoadedWork.Slice(RecordsOffset, RecordsSize));
        state = null;
        bool matched = false;
        lock (_gate)
        {
            foreach (var loaded in _loaded)
            {
                if (FindUniqueWork(loaded.Packet, actuallyLoadedWork) < 0) continue;
                var candidate = _store.Load(loaded.Slot, hash, records);
                if (matched && !Equivalent(state, candidate))
                    throw new InvalidDataException("Multiple native read sources disagree on the expanded state.");
                matched = true;
                state = candidate;
            }
            // Native cached reloads do not necessarily issue another file read.
            // Keep bounded verified evidence; every resume still requires an
            // exact whole-SaveWork match and consistent sidecar/unit bindings.
            // New-game initialization explicitly clears this cache.
        }
        return matched;
    }

    private static bool Equivalent(ExpandedSaveState? a, ExpandedSaveState? b) =>
        a is null ? b is null : b is not null && ExpandedSaveJson.StateBytes(a).AsSpan().SequenceEqual(ExpandedSaveJson.StateBytes(b));
    private static void ValidateWork(ReadOnlySpan<byte> work)
    {
        if (work.Length != WorkSize) throw new InvalidDataException("The complete serialized SaveWork is required.");
    }
    public static int FindUniqueWork(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> work)
    {
        ValidateWork(work);
        int first = packet[NativeSavePacket.HeaderSize..].IndexOf(work);
        if (first < 0) return -1;
        first += NativeSavePacket.HeaderSize;
        if (packet[(first + 1)..].IndexOf(work) >= 0)
            throw new InvalidDataException("Serialized party matches more than one native save position.");
        return first;
    }
}
