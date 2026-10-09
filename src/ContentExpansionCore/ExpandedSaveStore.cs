using System.Text.Json;

namespace FFTModLoader.ContentExpansion;

public sealed record SaveSlotIdentity(string ProfileKey, string NativeSlotName)
{
    // The native slot name is ONLY an identity, never a path to write to.
    public string StorageKey()
    {
        if (string.IsNullOrWhiteSpace(ProfileKey) || ProfileKey.Length > 256 || ProfileKey.Contains('\0') ||
            string.IsNullOrWhiteSpace(NativeSlotName) || NativeSlotName.Length > 512 || NativeSlotName.Contains('\0'))
            throw new InvalidDataException("An actual save profile and native slot identity are required.");
        return ExpandedSaveRegistry.Hash(ExpandedSaveJson.SlotBytes(this));
    }
}

/// <summary>
/// Content-addressed sidecars with recoverable confirmed replacement history.
/// The native save is never opened for
/// writing. Caller may Publish only after the native write completion reports
/// success. Old payload generations remain available when restoring backups.
/// </summary>
public sealed class ExpandedSaveStore
{
    private const int MaxDocumentBytes = 1024 * 1024;
    private readonly string _directory;
    private readonly ExpandedSaveRegistry _registry;
    private readonly object _gate = new();

    public ExpandedSaveStore(string directory, ExpandedSaveRegistry registry)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("An absolute sidecar directory is required.");
        _directory = Path.GetFullPath(directory);
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public void Publish(SaveSlotIdentity slot, string nativePayloadSha256, ExpandedSaveState state)
        => Publish(slot,nativePayloadSha256,state,confirmedReplacement:false);

    // Extras are deliberately absent from native bytes. A completed save can
    // therefore replace the extras of an identical native payload (e.g. using
    // a flask without changing the serialized field party). Only the native
    // transaction adapter may supply this confirmation; ordinary Publish stays
    // strict. Keep the previous document as an explicit recoverable archive.
    internal void PublishConfirmed(SaveSlotIdentity slot,string nativePayloadSha256,ExpandedSaveState state)
        => Publish(slot,nativePayloadSha256,state,confirmedReplacement:true);

    private void Publish(SaveSlotIdentity slot,string nativePayloadSha256,ExpandedSaveState state,bool confirmedReplacement)
    {
        ExpandedSaveRegistry.RequireHash(nativePayloadSha256);
        string payloadHash = nativePayloadSha256.ToUpperInvariant();
        var copy = _registry.CopyValidated(state);
        string stateHash = StateHash(copy);
        byte[] bytes = ExpandedSaveJson.DocumentBytes(new(1, slot, payloadHash, stateHash, copy));
        if (bytes.Length > MaxDocumentBytes) throw new InvalidDataException("Expanded save exceeds the allowed size.");
        string path = GetPath(slot, payloadHash);
        lock (_gate)
        {
            string? archive = null;
            if (File.Exists(path))
            {
                var previous = ReadDocument(path, slot, payloadHash);
                // Two distinct expanded states with IDENTICAL native save bytes
                // cannot be distinguished on load. Never silently choose one.
                if (previous.StateSha256 == stateHash) return;
                if (!confirmedReplacement)
                    throw new InvalidDataException("Ambiguous save generation: identical native payload has different expanded state.");
                archive=Path.Combine(Path.GetDirectoryName(path)!,"history",payloadHash+"-"+previous.StateSha256+".json");
                Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
                if(File.Exists(archive) && ReadDocument(archive,slot,payloadHash).StateSha256!=previous.StateSha256)
                    throw new InvalidDataException("Expanded save history integrity mismatch.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".pending";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(bytes);
                    output.Flush(flushToDisk: true);
                }
                // Same-directory publication keeps either the old or new
                // complete document; confirmed replacement archives the old one.
                try
                {
                    if(archive is null) File.Move(temporary, path, overwrite: false);
                    else File.Replace(temporary,path,archive); // atomic current-state + old-state backup
                }
                catch (IOException) when (File.Exists(path))
                {
                    var previous = ReadDocument(path, slot, payloadHash);
                    if (previous.StateSha256 != stateHash)
                        throw new InvalidDataException("Another writer published a conflicting expanded save generation.");
                }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary); // Only this owned temporary file.
            }
        }
    }

    public ExpandedSaveState? Load(SaveSlotIdentity slot, string nativePayloadSha256,
        IReadOnlyDictionary<int, string> loadedNativeRecordHashes)
    {
        ExpandedSaveRegistry.RequireHash(nativePayloadSha256);
        string hash = nativePayloadSha256.ToUpperInvariant();
        string path = GetPath(slot, hash);
        lock (_gate)
        {
            if (!File.Exists(path)) return null; // Native-only save; host must reset, never carry another session's stock.
            var document = ReadDocument(path, slot, hash);
            return _registry.ValidateUnitBindings(document.State, loadedNativeRecordHashes);
        }
    }

    public string GetPath(SaveSlotIdentity slot, string payloadHash)
    {
        ExpandedSaveRegistry.RequireHash(payloadHash);
        return Path.Combine(_directory, slot.StorageKey(), payloadHash.ToUpperInvariant() + ".json");
    }

    // Enhanced loading consumes a complete in-memory SaveWork, bypassing the
    // legacy read callback. Match exact full payload, profile and unit hashes;
    // never select a latest document or match only roster/name fields.
    public ExpandedSaveState? LoadConsumedWork(string profile,string payloadHash,IReadOnlyDictionary<int,string> records)
    {
        ExpandedSaveRegistry.RequireHash(payloadHash);
        string hash=payloadHash.ToUpperInvariant();
        ExpandedSaveState? found=null;
        lock(_gate)
        {
            if(!Directory.Exists(_directory))return null;
            foreach(string directory in Directory.EnumerateDirectories(_directory))
            {
                string path=Path.Combine(directory,hash+".json");
                if(!File.Exists(path))continue;
                if(new FileInfo(path).Length>MaxDocumentBytes)throw new InvalidDataException("Oversized expanded save.");
                using var json=JsonDocument.Parse(File.ReadAllBytes(path),new JsonDocumentOptions { MaxDepth=16 });
                RejectDuplicateProperties(json.RootElement);
                var slot=ExpandedSaveJson.ReadDocument(json.RootElement).Slot;
                if(slot.ProfileKey!=profile)continue;
                if(Path.GetFullPath(path)!=GetPath(slot,hash))throw new InvalidDataException("Misplaced expanded save.");
                var candidate=_registry.ValidateUnitBindings(ReadDocument(path,slot,hash).State,records);
                if(found is not null && StateHash(found)!=StateHash(candidate))
                    throw new InvalidDataException("Identical loaded native save has conflicting expanded states.");
                found=candidate;
            }
        }
        return found;
    }

    private ExpandedSaveJson.Document ReadDocument(string path, SaveSlotIdentity slot, string hash)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxDocumentBytes) throw new InvalidDataException("Oversized expanded save document.");
        byte[] bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        ExpandedSaveJson.Document document;
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateProperties(json.RootElement);
            document = ExpandedSaveJson.ReadDocument(json.RootElement);
        }
        catch (JsonException ex) { throw new InvalidDataException("Malformed expanded save document.", ex); }
        if (document.SchemaVersion != 1 || document.Slot != slot || document.NativePayloadSha256 != hash)
            throw new InvalidDataException("Expanded save schema/slot/payload identity mismatch.");
        var copy = _registry.CopyValidated(document.State);
        if (document.StateSha256 != StateHash(copy)) throw new InvalidDataException("Expanded save integrity check failed.");
        return document with { State = copy };
    }

    private static string StateHash(ExpandedSaveState state) =>
        ExpandedSaveRegistry.Hash(ExpandedSaveJson.StateBytes(state));

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property in expanded save.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) RejectDuplicateProperties(child);
    }
}
