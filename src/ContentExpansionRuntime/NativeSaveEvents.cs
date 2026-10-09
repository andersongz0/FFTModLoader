using System.Buffers.Binary;
using FFTModLoader.ContentExpansion;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>
/// The native host must supply a VERIFIED profile identity and bindings from
/// serialized records. Menu/battle indices and the CURRENT inventory at async
/// completion are not substitutes. No guessing or name-only unit matching.
/// </summary>
public interface IExpandedNativeSaveSession
{
    string ProfileKey { get; }
    bool ReadyForNativeSaveHooks { get; }
    bool TryCaptureSerializedSnapshot(SaveSlotIdentity slot, byte[] queuedPacket, out ExpandedSaveState snapshot);
    bool TryBindLoadedRecords(SaveSlotIdentity slot, byte[] loadedPacket, out IReadOnlyDictionary<int, string> recordHashes);
    // Host restores/reset only at its audited native party-load boundary, not
    // when a preview thumbnail/other slot is read. null means a native-only save.
    void StageLoadedState(SaveSlotIdentity slot, string packetHash, ExpandedSaveState? state);
    void BlockExpandedMutations(string reason);
}

/// <summary>Audited offsets from queue entry/callback ABI; no native writes.</summary>
public interface INativeSaveObserver
{
    void RequireReadyBindings();
    bool BeforeQueue(long slotObject, long nativeName, long packetPointer, long packetLength);
    void CancelQueue(long slotObject);
    NativeWriteCompletion? CaptureWriteCompletion(long entryAddress, int nativeResult);
    void AfterWriteCompletion(NativeWriteCompletion completion, int originalResult);
    NativeReadCompletion? CaptureReadCompletion(long name, int result, long pointer, long length);
    void AfterReadCompletion(NativeReadCompletion completion, int originalResult);
    void ReportFailure(Exception exception);
}

public sealed class NativeSaveEvents : INativeSaveObserver
{
    public const int QueueEntrySize = 0x148;
    private readonly IExpandedNativeSaveSession _session;
    private readonly ExpandedSaveCommit _commit;
    private readonly ExpandedSaveStore _store;
    private readonly Func<long, int, byte[]> _read;
    private readonly Action<string> _log;

    public NativeSaveEvents(IExpandedNativeSaveSession session, ExpandedSaveCommit commit,
        ExpandedSaveStore store, Func<long, int, byte[]> read, Action<string> log)
    {
        _session = session;
        _commit = commit;
        _store = store;
        _read = read;
        _log = log;
    }

    public void RequireReadyBindings()
    {
        if (!_session.ReadyForNativeSaveHooks)
            throw new InvalidOperationException("Profile/serialized-unit save bindings are not ready; no detours installed.");
        _ = new SaveSlotIdentity(_session.ProfileKey, "binding-validation").StorageKey();
    }

    // Called before original PrepareSave so a fast async callback cannot beat
    // capture. Original returns native queue result; negative -> CancelQueue.
    public bool BeforeQueue(long slotObject, long nativeName, long packetPointer, long packetLength)
    {
        if (slotObject <= 0 || !TryReadPacket(packetPointer, packetLength, out var packet)) return false;
        var slot = Slot(ReadName(nativeName));
        if (!_session.TryCaptureSerializedSnapshot(slot, packet, out var state)) return false;
        _commit.Enqueue(slotObject, slot, packet, state);
        _log($"Applying expanded save capture: slot={slot.StorageKey()[..12]}, payload={ExpandedSaveRegistry.Hash(packet)[..12]}; pending native completion.");
        return true;
    }

    public void CancelQueue(long slotObject) => _commit.Cancel(slotObject);

    public NativeWriteCompletion? CaptureWriteCompletion(long entryAddress, int nativeResult)
    {
        byte[] entry = ReadExact(entryAddress, QueueEntrySize);
        long slotObject = BinaryPrimitives.ReadInt64LittleEndian(entry.AsSpan(0x18));
        if (slotObject <= 0) return null;
        int entryError = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(0x140));
        int effectiveResult = entryError < 0 ? entryError : nativeResult;
        string name = DecodeName(entry.AsSpan(0x20, 0x100));
        if (effectiveResult < 0) return new(slotObject, Slot(name), [], effectiveResult);
        long length = BinaryPrimitives.ReadInt64LittleEndian(entry);
        long pointer = BinaryPrimitives.ReadInt64LittleEndian(entry.AsSpan(8));
        if (!TryReadPacket(pointer, length, out var packet)) return null;
        // Capture BEFORE original 032E60: it calls 033AD4 and frees the buffer.
        return new(slotObject, Slot(name), packet, effectiveResult);
    }

    public void AfterWriteCompletion(NativeWriteCompletion completion, int originalResult)
    {
        int result = completion.NativeResult < 0 ? completion.NativeResult : originalResult;
        var outcome = _commit.Complete(completion.SlotObject, completion.Slot, completion.Packet, result);
        if (outcome == SaveCommitResult.Published)
            _log("Applying expanded save publication: native completion succeeded; exact queued stock/learning stored separately.");
        else if (outcome == SaveCommitResult.NativeWriteFailed)
            _log("[Warning] Expanded save not published: native save write failed.");
    }

    public NativeReadCompletion? CaptureReadCompletion(long nativeName, int nativeResult, long packetPointer, long packetLength)
    {
        if (nativeResult < 0 || !TryReadPacket(packetPointer, packetLength, out var packet)) return null;
        var slot = Slot(ReadName(nativeName));
        if (!_session.TryBindLoadedRecords(slot, packet, out var recordHashes)) return null;
        string hash = NativeSavePacket.ValidateAndHash(packet);
        // Fully validated before anything may be staged for live restoration.
        var state = _store.Load(slot, hash, recordHashes);
        return new(slot, hash, state);
    }

    public void AfterReadCompletion(NativeReadCompletion completion, int originalResult)
    {
        if (originalResult < 0) return;
        _session.StageLoadedState(completion.Slot, completion.PacketHash, completion.State);
        _log(completion.State is null
            ? "Applying native-only save load: expanded session reset requested at the party-load boundary."
            : "Applying expanded save load: profile, slot, payload integrity and serialized unit bindings verified; state staged.");
    }

    public void ReportFailure(Exception exception)
    {
        // Observational hook failures must never escape through a native ABI.
        // Implementations must make BlockExpandedMutations noexcept; continue
        // protecting the original native callback even if a host violates that.
        try { _session.BlockExpandedMutations(exception.Message); } catch { }
        try { _log($"[Error] Content expansion save bridge blocked: {exception.Message}. Native save result is not changed."); } catch { }
    }

    private SaveSlotIdentity Slot(string rawName) => new(_session.ProfileKey, rawName);
    private bool TryReadPacket(long pointer, long length, out byte[] packet)
    {
        packet = [];
        if (pointer <= 0 || length <= NativeSavePacket.HeaderSize || length > NativeSavePacket.MaxBytes) return false;
        var header = ReadExact(pointer, NativeSavePacket.HeaderSize);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != NativeSavePacket.HeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) is not (NativeSavePacket.BinaryKind or NativeSavePacket.ManualBinaryKind)) return false;
        packet = ReadExact(pointer, checked((int)length));
        NativeSavePacket.ValidateAndHash(packet);
        return true;
    }
    private string ReadName(long address)
    {
        var bytes = new byte[0x100];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = ReadExact(checked(address + i), 1)[0];
            if (bytes[i] == 0) return DecodeName(bytes.AsSpan(0, i + 1));
        }
        throw new InvalidDataException("Unterminated native slot name.");
    }
    private static string DecodeName(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        if (end <= 0) throw new InvalidDataException("Unterminated or empty native slot name.");
        // Raw ANSI bytes are the identity. Hex encoding avoids locale-dependent
        // decoding, traversal, control characters and account/path exposure in logs.
        return Convert.ToHexString(bytes[..end]);
    }
    private byte[] ReadExact(long address, int length)
    {
        byte[] bytes = _read(address, length);
        if (bytes.Length != length) throw new InvalidDataException("Truncated native save memory read.");
        return bytes;
    }
}

public sealed record NativeWriteCompletion(long SlotObject, SaveSlotIdentity Slot, byte[] Packet, int NativeResult);
public sealed record NativeReadCompletion(SaveSlotIdentity Slot, string PacketHash, ExpandedSaveState? State,
    byte[]? Packet = null);
