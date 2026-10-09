using System.Buffers.Binary;
using System.Text;
using FFTModLoader.ContentExpansion;

internal static class ExpandedSaveTests
{
    public static void Run(string[] newKeys)
    {
        var registry = new ExpandedSaveRegistry(newKeys, newKeys);
        string directory = Path.Combine(Path.GetTempPath(), "FFTModLoader-save-tests-" + Guid.NewGuid().ToString("N"));
        var store = new ExpandedSaveStore(directory, registry);
        var commit = new ExpandedSaveCommit(registry, store);
        var slot = new SaveSlotIdentity("fixture-profile-A", "enhanced/slot01");
        var otherSlot = slot with { NativeSlotName = "enhanced/slot02" };
        string anchor = ExpandedSaveRegistry.Hash(Encoding.UTF8.GetBytes("fixture serialized unit record; not a live offset"));
        var bindings = new Dictionary<int, string> { [3] = anchor };
        var state = new ExpandedSaveState(new() { [newKeys[0]] = 5 }, [new(3, anchor, [newKeys[1]])]);
        byte[] packet = Packet("fixture native save generation A");
        string hash = NativeSavePacket.ValidateAndHash(packet);
        Check(NativeSavePacket.Crc32(Encoding.ASCII.GetBytes("123456789")) == 0xCBF43926, "CRC32 known vector failed");
        Check(!Directory.Exists(directory), "Constructing a store writes files");

        // Schema 1 compatibility is byte-level, not just equivalent JSON: old
        // slot folders and StateSha256 must remain valid, including escaping.
        foreach (string text in new[] { "plain", "Açúcar日本語😀", "\"<>&'\\/\n\t\r\b\f", "\u2028\u2029\u007f" })
        {
            var escapedSlot = new SaveSlotIdentity(text, "native/" + text);
            byte[] oldSlot = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new[] { escapedSlot.ProfileKey, escapedSlot.NativeSlotName });
            Check(oldSlot.SequenceEqual(ExpandedSaveJson.SlotBytes(escapedSlot)), "Legacy slot escaping/hash changed");
            var escapedState = new ExpandedSaveState(new() { [text] = 99 }, [new(53, anchor, [text])]);
            Check(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(escapedState).SequenceEqual(ExpandedSaveJson.StateBytes(escapedState)), "Legacy state bytes/hash changed");
        }
        var legacyCopy = registry.CopyValidated(state);
        string legacyStateHash = ExpandedSaveRegistry.Hash(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(legacyCopy));
        byte[] legacyDocument = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            { SchemaVersion = 1, Slot = slot, NativePayloadSha256 = hash, StateSha256 = legacyStateHash, State = legacyCopy });
        string legacyPath = store.GetPath(slot, hash);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllBytes(legacyPath, legacyDocument);
        Check(store.Load(slot, hash, bindings)!.Items[newKeys[0]] == 5, "Old reflection-serialized save no longer loads");
        // A new publication must have precisely the old canonical document.
        store.Publish(otherSlot, hash, legacyCopy);
        byte[] expectedDocument = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            { SchemaVersion = 1, Slot = otherSlot, NativePayloadSha256 = hash, StateSha256 = legacyStateHash, State = legacyCopy });
        Check(File.ReadAllBytes(store.GetPath(otherSlot, hash)).SequenceEqual(expectedDocument), "New document breaks schema 1 byte compatibility");
        File.Delete(store.GetPath(otherSlot, hash));
        File.Delete(legacyPath);

        commit.Enqueue(1000, slot, packet, state);
        Check(!File.Exists(legacyPath), "Queueing a save prematurely publishes state");
        Reject(() => commit.Enqueue(1000, slot, packet, state));
        Reject(() => commit.Complete(1000, otherSlot, packet, 0));
        var changed = Packet("different queued native bytes");
        Reject(() => commit.Complete(1000, slot, changed, 0));
        // Mutation after enqueue must not change the captured generation.
        state.Items[newKeys[0]] = 99;
        state.Units[0].LearnedKeys[0] = newKeys[2];
        Check(commit.Complete(1000, slot, packet, 0) == SaveCommitResult.Published, "Success did not publish");
        Check(commit.Complete(1000, slot, packet, 0) == SaveCommitResult.NoMatchingRequest, "Duplicate completion published twice");
        var loaded = store.Load(slot, hash, bindings)!;
        Check(loaded.Items[newKeys[0]] == 5 && loaded.Units[0].LearnedKeys.Single() == newKeys[1], "Late mutation changed save snapshot");
        loaded.Items[newKeys[0]] = 0;
        Check(store.Load(slot, hash, bindings)!.Items[newKeys[0]] == 5, "Loaded state shares mutable storage");
        Check(store.Load(otherSlot, hash, bindings) is null, "State leaked into another slot");
        Check(store.Load(slot with { ProfileKey = "fixture-profile-B" }, hash, bindings) is null, "State leaked into another profile");
        Reject(() => store.Load(slot, hash, new Dictionary<int, string> { [3] = new string('A', 64) }));
        Reject(() => store.Load(slot, hash, new Dictionary<int, string> { [4] = anchor }));
        Reject(() => store.Publish(slot, hash, state)); // Same native bytes, different extras: ambiguous.
        Check(store.Load(slot, hash, bindings)!.Items[newKeys[0]] == 5, "Ambiguous generation overwrote the old one");
        store.Publish(slot, hash, store.Load(slot, hash, bindings)!); // Identical publication is idempotent.

        byte[] next = Packet("fixture native save generation B");
        string nextHash = NativeSavePacket.ValidateAndHash(next);
        commit.Enqueue(1001, slot, next, state);
        Check(commit.Complete(1001, slot, ReadOnlySpan<byte>.Empty, -1) == SaveCommitResult.NativeWriteFailed, "Failed native save was published");
        Check(store.Load(slot, nextHash, bindings) is null, "Failed save created a generation");
        commit.Enqueue(1002, slot, next, state);
        commit.Cancel(1002);
        Check(commit.Complete(1002, slot, next, 0) == SaveCommitResult.NoMatchingRequest, "Canceled native save was published");
        commit.Enqueue(1003, slot, next, state);
        Check(commit.Complete(1003, slot, next, 0) == SaveCommitResult.Published, "Second generation failed");
        Check(store.Load(slot, nextHash, bindings)!.Items[newKeys[0]] == 99, "New generation not restored");
        Check(store.Load(slot, hash, bindings)!.Items[newKeys[0]] == 5, "Restoring an older native backup loses its extras");

        byte[] corrupt = packet.ToArray();
        corrupt[^1] ^= 1;
        Reject(() => NativeSavePacket.ValidateAndHash(corrupt));
        Reject(() => NativeSavePacket.ValidateAndHash(new byte[16]));
        byte[] wrongKind = packet.ToArray(); wrongKind[8] = 0x12;
        Reject(() => NativeSavePacket.ValidateAndHash(wrongKind));
        Reject(() => registry.CopyValidated(state with { Items = new() { [newKeys[0]] = 100 } }));
        Reject(() => registry.CopyValidated(state with { Items = new() { ["unregistered"] = 1 } }));
        Reject(() => registry.CopyValidated(state with { Units = [new(3, anchor, ["native-retained-action"])] }));
        Reject(() => registry.CopyValidated(state with { Units = [state.Units[0], state.Units[0]] }));

        // Native names including traversal-like text are opaque hashed identities.
        var unusual = slot with { NativeSlotName = "../../not-a-writable-target" };
        Check(Path.GetFullPath(store.GetPath(unusual, hash)).StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Slot identity escapes sidecar directory");
        Reject(() => store.GetPath(slot, "../../not-a-hash"));

        // Corrupted files are rejected before live inventory/learning restoration.
        string documentPath = store.GetPath(slot, nextHash);
        string json = File.ReadAllText(documentPath);
        File.WriteAllText(documentPath, json.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":1,\"SchemaVersion\":1"));
        Reject(() => store.Load(slot, nextHash, bindings));
        File.WriteAllText(documentPath, json.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2"));
        Reject(() => store.Load(slot, nextHash, bindings));
        File.WriteAllText(documentPath, json.Replace("\"StateSha256\":\"", "\"StateSha256\":\"BAD"));
        Reject(() => store.Load(slot, nextHash, bindings));
        foreach (string invalid in new[] {
            json.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":1,\"Unknown\":0"),
            json.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":\"1\""),
            json.Replace("\"ProfileKey\":", "\"UnknownSlotKey\":"),
            json.Replace("\"LearnedKeys\":", "\"UnknownUnitKey\":"),
            json.Replace("\"Items\":", "\"UnknownStateKey\":"),
            json.Replace("\"Units\":[", "\"Units\":null,\"Duplicate\":[") })
        {
            File.WriteAllText(documentPath, invalid);
            Reject(() => store.Load(slot, nextHash, bindings));
        }
        File.WriteAllText(documentPath, json);

        var failureSlot = slot with { NativeSlotName = "fixture-IO-failure" };
        Directory.CreateDirectory(store.GetPath(failureSlot, hash)); // A directory occupies the exact final filename.
        commit.Enqueue(1004, failureSlot, packet, state);
        try { commit.Complete(1004, failureSlot, packet, 0); throw new Exception("IO failure accepted"); }
        catch (IOException) { }
        Check(!Directory.EnumerateFiles(directory, "*.pending", SearchOption.AllDirectories).Any(), "Failed write left a temporary publication");
        Check(store.Load(slot, hash, bindings)!.Items[newKeys[0]] == 5, "IO failure changed another saved generation");
        // Test artifacts only. The unique, fully resolved child was created here;
        // no game/native save path is ever passed to this recursive cleanup.
        string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (Path.GetDirectoryName(Path.GetFullPath(directory)) != expectedParent ||
            !Path.GetFileName(directory).StartsWith("FFTModLoader-save-tests-", StringComparison.Ordinal))
            throw new Exception("Unexpected test cleanup target");
        Directory.Delete(directory, recursive: true);
        Console.WriteLine("PASS: queued vs committed saves, CRC32, snapshot isolation, native failure/cancel, profile/slot/unit binding, backup generations, ambiguity refusal, strict JSON/integrity, atomic publication and IO failure. Native hooks are not activated by this test.");
    }

    private static byte[] Packet(string payload)
    {
        byte[] data = Encoding.UTF8.GetBytes(payload);
        byte[] packet = new byte[16 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), NativeSavePacket.Crc32(data));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 0x11);
        data.CopyTo(packet.AsSpan(16));
        return packet;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action operation)
    {
        try { operation(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Invalid expanded save accepted");
    }
}
