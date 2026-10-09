using System.Buffers.Binary;
using FFTModLoader.ContentExpansion;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>
/// Read-only observers, NOT the expanded-content backend. Does not reserve IDs,
/// grant items, replace actions or save state. Native results always pass through.
/// No names, raw unit records, save packets or account identifiers are logged.
/// </summary>
public sealed class NativeIntegrationProbe
{
    private readonly long _image;
    private readonly Func<long, int, byte[]> _read;
    private readonly Action<string> _emit;
    private readonly object _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private int _messages;
    private const int MessageLimit = 300;

    public NativeIntegrationProbe(long image, Func<long, int, byte[]> read, Action<string> emit)
    {
        if (image <= 0) throw new ArgumentOutOfRangeException(nameof(image));
        _image = image;
        _read = read;
        _emit = emit;
    }

    public void AbilityList(short unit, ushort job, int category, int mode, long output, long nativeResult)
    {
        if (job != 75 || category != 0 || mode is not (0 or 2 or 3)) return;
        int count = checked((int)nativeResult);
        if (count is < 0 or > 24) throw new InvalidDataException("Unexpected native ability count.");
        string list = mode == 3 ? "count-only; output not read" : ReadList(output, count, 24);
        string learning = "unit-not-bound";
        if (unit is >= 0 and < 54)
        {
            long pointer = BinaryPrimitives.ReadInt64LittleEndian(Read(_image + 0x1800F50 + unit * 8, 8));
            if (pointer != 0)
            {
                byte[] bits = Read(pointer + ChemistNativeLearning.PartyLearningOffset, 3);
                int jp = BinaryPrimitives.ReadInt16LittleEndian(Read(pointer + ChemistNativeLearning.PartyJpOffset, 2));
                learning = $"jp={jp}; learned-original-slots={NativeLearningBits.Decode(bits):X6}";
            }
        }
        Once($"ability:{unit}:{mode}:{list}:{learning}",
            $"ABILITY job=75 unit-index={unit} mode={mode} count={count}; {learning}; entries={list}");
    }

    public void ShopList(short shop, short category, int filterEquip, byte sort, long output, int count)
    {
        if (shop is < 0 or >= 15 || category != 3 || filterEquip != 0) return;
        string list = ReadList(output, count, 256);
        Once($"shop:{shop}:{category}:{sort}:{list}",
            $"SHOP ordinary={shop} consumables category={category} filter={filterEquip} sort={sort} count={count}; entries={list}");
    }

    public int? StockBefore(ushort rawId, int delta)
    {
        int id = rawId & 0x3FF;
        if (delta == 0 || id is < 1 or > 260) return null;
        return Read(_image + 0x11A7C00 + id, 1)[0];
    }

    public void StockAfter(ushort rawId, int delta, int? before, int nativeResult)
    {
        if (before is null) return;
        int id = rawId & 0x3FF;
        int after = Read(_image + 0x11A7C00 + id, 1)[0];
        // Report what happened, not an invented transaction source. ItemChg is
        // shared by shops/equipment/battle, so it alone does NOT prove purchase.
        Emit($"STOCK item={id} delta={delta} before={before} after={after} native-result={nativeResult}; source=shared-native-route");
    }

    public string? Packet(long address, long size)
    {
        if (address == 0 || size <= 16 || size > NativeSavePacket.MaxBytes) return null;
        byte[] header = Read(address, 16);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 16 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) != NativeSavePacket.BinaryKind) return null;
        byte[] packet = Read(address, checked((int)size));
        return NativeSavePacket.ValidateAndHash(packet);
    }

    public void Queued(string? hash, long size, int nativeResult)
    {
        if (hash is not null) Emit($"SAVE queued type=11h size={size} crc=valid packet={hash} native-result={nativeResult}; observation-only, no sidecar");
    }

    public (string? Hash, long Size, int EntryError) WriteBefore(long queueEntry, int nativeResult)
    {
        // Snapshot BEFORE the original completion releases slot/buffer memory.
        byte[] entry = Read(queueEntry, 0x148);
        int error = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(0x140));
        long size = BinaryPrimitives.ReadInt64LittleEndian(entry);
        long pointer = BinaryPrimitives.ReadInt64LittleEndian(entry.AsSpan(8));
        return (error < 0 || nativeResult < 0 ? null : Packet(pointer, size), size, error);
    }

    public void Completed((string? Hash, long Size, int EntryError)? captured, int nativeInput, int nativeResult)
    {
        if (captured is not { } value) return;
        Emit($"SAVE write-complete size={value.Size} entry-error={value.EntryError} input={nativeInput} result={nativeResult} packet={value.Hash ?? "not-captured"}; original-result-preserved");
    }

    public void Loaded(string? hash, long size, int nativeInput, int nativeResult)
    {
        if (hash is not null) Emit($"SAVE read-complete type=11h size={size} crc=valid input={nativeInput} result={nativeResult} packet={hash}; observation-only, no restore");
    }

    public void Failure(Exception exception)
    {
        // No exception text containing native data or filenames in the log.
        try { Once($"error:{exception.GetType().Name}", $"OBSERVER skipped: {exception.GetType().Name}; native call unchanged"); }
        catch (Exception) { }
    }

    private byte[] Read(long address, int size)
    {
        byte[] bytes = _read(address, size);
        if (bytes.Length != size) throw new InvalidDataException("Truncated observation read.");
        return bytes;
    }

    private string ReadList(long address, int count, int maximum)
    {
        if (count < 0 || count > maximum) throw new InvalidDataException("Unexpected native list size.");
        // Read only the returned count and the terminator, not a guessed caller
        // capacity. Count-only mastery mode must never enter this method.
        byte[] bytes = Read(address, checked((count + 1) * 2));
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(count * 2)) != 0xFFFF)
            throw new InvalidDataException("Unexpected native list terminator.");
        return string.Join(",", Enumerable.Range(0, count).Select(i =>
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2)).ToString("X4")));
    }

    private void Once(string identity, string message)
    {
        lock (_gate)
        {
            if (_messages >= MessageLimit || !_seen.Add(identity)) return;
            EmitLocked(message);
        }
    }

    private void Emit(string message) { lock (_gate) EmitLocked(message); }
    private void EmitLocked(string message)
    {
        if (_messages >= MessageLimit) return;
        _messages++;
        _emit(message);
        if (_messages == MessageLimit) _emit("Observation limit reached; native routes still pass through unchanged.");
    }
}
