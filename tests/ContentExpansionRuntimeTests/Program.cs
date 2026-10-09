using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using FFTModLoader.ContentExpansion;
using FFTModLoader.ContentExpansion.Runtime;

var root = Path.Combine(Path.GetTempPath(), "FFTModLoader-native-save-tests-" + Guid.NewGuid().ToString("N"));
var registry = new ExpandedSaveRegistry(["fixture.venom"], ["fixture.venom"]);
var store = new ExpandedSaveStore(root, registry);
var commit = new ExpandedSaveCommit(registry, store);
var memory = new FakeMemory();
byte[] body = Encoding.ASCII.GetBytes("synthetic serialized data, NOT a live game save");
byte[] packet = new byte[16 + body.Length];
BinaryPrimitives.WriteInt32LittleEndian(packet, 16);
BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), NativeSavePacket.Crc32(body));
BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), 0x11);
body.CopyTo(packet.AsSpan(16));
memory.Add(0x100000, packet);
memory.Add(0x200000, Encoding.ASCII.GetBytes("slot001\0")); // Only eight bytes readable: no 256-byte overread.
var session = new FakeSession();
var logs = new List<string>();
var events = new NativeSaveEvents(session, commit, store, memory.Read, logs.Add);
Check(events.BeforeQueue(0x300000, 0x200000, 0x100000, packet.Length), "PrepareSave capture failed");
Check(!Directory.Exists(root), "PrepareSave published before native completion");
var entry = new byte[NativeSaveEvents.QueueEntrySize];
BinaryPrimitives.WriteInt64LittleEndian(entry, packet.Length);
BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(8), 0x100000);
BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(0x18), 0x300000);
Encoding.ASCII.GetBytes("slot001\0").CopyTo(entry.AsSpan(0x20));
memory.Add(0x400000, entry);
var completion = events.CaptureWriteCompletion(0x400000, 0)!;
// Native 032E60 frees its buffer; after-observer MUST use the copied packet.
memory.Remove(0x100000);
session.Snapshot.Items["fixture.venom"] = 80;
events.AfterWriteCompletion(completion, 0);
var slot = new SaveSlotIdentity(session.ProfileKey, Convert.ToHexString(Encoding.ASCII.GetBytes("slot001")));
string hash = NativeSavePacket.ValidateAndHash(packet);
Check(store.Load(slot, hash, session.Bindings)!.Items["fixture.venom"] == 2, "Async completion saved the current inventory instead of the queued snapshot");
memory.Add(0x100000, packet);
var read = events.CaptureReadCompletion(0x200000, 0, 0x100000, packet.Length)!;
Check(session.Staged is null && session.StageCount == 0, "Observer restored before native read succeeded");
events.AfterReadCompletion(read, -1);
Check(session.StageCount == 0, "Failed original read restored expanded state");
events.AfterReadCompletion(read, 0);
Check(session.StageCount == 1 && session.Staged!.Items["fixture.venom"] == 2, "Verified native read did not stage correct state");
Check(events.CaptureReadCompletion(0x200000, -1, 0x100000, packet.Length) is null, "Negative native input was ignored");
session.AcceptBinding = false;
Check(events.CaptureReadCompletion(0x200000, 0, 0x100000, packet.Length) is null, "Unbound thumbnail/system read staged inventory");
session.AcceptBinding = true;

// A native-only save must request resetting extras, not keep the prior session.
var nativeOnly = packet.ToArray(); nativeOnly[^1] ^= 7;
BinaryPrimitives.WriteUInt32LittleEndian(nativeOnly.AsSpan(4), NativeSavePacket.Crc32(nativeOnly.AsSpan(16)));
memory.Add(0x500000, nativeOnly);
var oldRead = events.CaptureReadCompletion(0x200000, 0, 0x500000, nativeOnly.Length)!;
events.AfterReadCompletion(oldRead, 0);
Check(session.StageCount == 2 && session.Staged is null, "Native-only save kept previous expanded stock");

// Queue entry's negative per-item result takes precedence over manager success.
events.BeforeQueue(0x300001, 0x200000, 0x500000, nativeOnly.Length);
var errorEntry = entry.ToArray();
BinaryPrimitives.WriteInt64LittleEndian(errorEntry.AsSpan(0x18), 0x300001);
BinaryPrimitives.WriteInt64LittleEndian(errorEntry.AsSpan(8), 0); // Failed writes need no readable packet.
BinaryPrimitives.WriteInt32LittleEndian(errorEntry.AsSpan(0x140), -123);
memory.Add(0x600000, errorEntry);
events.AfterWriteCompletion(events.CaptureWriteCompletion(0x600000, 0)!, 0);
Check(store.Load(slot, NativeSavePacket.ValidateAndHash(nativeOnly), session.Bindings) is null, "Per-slot native error still published a sidecar");

byte[] otherKind = packet.ToArray(); otherKind[8] = 0x12;
memory.Add(0x700000, otherKind);
Check(!events.BeforeQueue(0x300002, 0x200000, 0x700000, otherKind.Length), "Unrelated native slot kind was intercepted");
var corrupt = packet.ToArray(); corrupt[^1] ^= 1;
memory.Add(0x800000, corrupt);
Reject(() => events.BeforeQueue(0x300003, 0x200000, 0x800000, corrupt.Length));
session.AcceptCapture = false;
Check(!events.BeforeQueue(0x300003, 0x200000, 0x100000, packet.Length), "Unbound save snapshot was captured");
session.AcceptCapture = true;

// Install is forbidden until the actual profile and serialized-unit adapter exists.
var hooks = new NativeSaveHooks(events);
try { hooks.Install(null!, 0, "not-read-before-readiness"); throw new Exception("Incomplete native save bindings activated hooks"); }
catch (InvalidOperationException) { }
events.ReportFailure(new InvalidDataException("fixture failure"));
Check(session.Blocked && logs.Any(line => line.Contains("[Error]")), "Save failure did not block further expanded mutations/log an error");
session.ThrowOnBlock = true;
events.ReportFailure(new InvalidDataException("no exception through native ABI"));

// Real read-only Win32 page checks, using ONLY our allocated test memory.
nint testAddress = Marshal.AllocHGlobal(packet.Length);
try
{
    Marshal.Copy(packet, 0, testAddress, packet.Length);
    Check(CheckedNativeRead.Read((long)testAddress, packet.Length).SequenceEqual(packet), "Checked heap read failed");
}
finally { Marshal.FreeHGlobal(testAddress); }
Reject(() => CheckedNativeRead.Read(0, 16));
Reject(() => CheckedNativeRead.Read(0x10000, -1));
Reject(() => CheckedNativeRead.Read(0x10000, int.MaxValue));
Console.WriteLine("PASS: audited queue-entry offsets, raw slot identity, no name overread, pre-free capture, original-result gating, per-slot errors, separate profile/unit binding, native-only reset, safe heap reads and activation veto. Synthetic callbacks only; no game hooks installed.");
IntegrationProbeTests.Run();
NativeLeafAbiTests.Run();
string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
if (Path.GetDirectoryName(Path.GetFullPath(root)) != parent || !Path.GetFileName(root).StartsWith("FFTModLoader-native-save-tests-", StringComparison.Ordinal))
    throw new Exception("Unexpected owned test cleanup target");
Directory.Delete(root, recursive: true);

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Reject(Action action)
{
    try { action(); }
    catch (InvalidDataException) { return; }
    throw new Exception("Invalid native callback was accepted");
}

sealed class FakeMemory
{
    private readonly Dictionary<long, byte[]> _regions = new();
    public void Add(long address, byte[] bytes) => _regions[address] = bytes.ToArray();
    public void Remove(long address) => _regions.Remove(address);
    public byte[] Read(long address, int length)
    {
        foreach (var (start, bytes) in _regions)
            if (address >= start && checked(address + length) <= checked(start + bytes.Length))
                return bytes.AsSpan(checked((int)(address - start)), length).ToArray();
        throw new InvalidDataException("Read outside synthetic native allocation");
    }
}
sealed class FakeSession : IExpandedNativeSaveSession
{
    public string ProfileKey => "fixture-profile-not-a-Steam-account";
    public bool ReadyForNativeSaveHooks => false;
    public bool AcceptCapture = true, AcceptBinding = true, Blocked, ThrowOnBlock;
    public int StageCount;
    public ExpandedSaveState? Staged;
    public Dictionary<int, string> Bindings = new() { [2] = ExpandedSaveRegistry.Hash([1, 2, 3]) };
    public ExpandedSaveState Snapshot;
    public FakeSession() => Snapshot = new(new() { ["fixture.venom"] = 2 }, [new(2, Bindings[2], ["fixture.venom"])]);
    public bool TryCaptureSerializedSnapshot(SaveSlotIdentity slot, byte[] packet, out ExpandedSaveState snapshot)
    { snapshot = Snapshot; return AcceptCapture; }
    public bool TryBindLoadedRecords(SaveSlotIdentity slot, byte[] packet, out IReadOnlyDictionary<int, string> hashes)
    { hashes = Bindings; return AcceptBinding; }
    public void StageLoadedState(SaveSlotIdentity slot, string hash, ExpandedSaveState? state)
    { StageCount++; Staged = state; }
    public void BlockExpandedMutations(string reason)
    { Blocked = true; if (ThrowOnBlock) throw new Exception("test host violation"); }
}
