using System.Buffers.Binary;
using FFTModLoader.ContentExpansion;

static class NativePartySaveCommitTests
{
    public static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"FFTModLoader-party-save-tests-"+Guid.NewGuid().ToString("N"));
        var registry=new ExpandedSaveRegistry(["venom"],["venom"]);
        var store=new ExpandedSaveStore(root,registry);
        var commit=new NativePartySaveCommit(registry,store);
        var identity=new SaveSlotIdentity("fixture-profile","opaque-manual-name");
        byte[] a=Work(3),b=Work(7),c=Work(11);
        var first=State(a,2);
        byte[] initial=Packet([a,b]);
        Check(commit.Enqueue(101,identity,initial,a,first),"Serialized party not found");
        first.Items["venom"]=98;
        Check(!Directory.Exists(root),"Sidecar published before completion");
        Check(commit.Complete(101,identity,initial,0)==SaveCommitResult.Published,"Successful completion not published");
        Check(store.Load(identity,ExpandedSaveRegistry.Hash(a),Hashes(a))!.Items["venom"]==2,"Snapshot was not frozen");
        Check(commit.TryRestore(a,out var written) && written!.Items["venom"]==2,"Confirmed native write cannot resume from its cache");
        // Overwriting another position changes the whole native file, but not
        // position A's sidecar or learning binding.
        byte[] updated=Packet([a,c]);
        Check(commit.Enqueue(102,identity,updated,c,State(c,9)),"New manual position not captured");
        Check(commit.Complete(102,identity,updated,0)==SaveCommitResult.Published,"New manual position not published");
        commit.Stage(identity,updated);
        Check(commit.TryRestore(a,out var restored) && restored!.Items["venom"]==2,"Another overwritten slot lost position A");
        commit.Stage(identity,updated);
        Check(commit.TryRestore(c,out restored) && restored!.Items["venom"]==9,"Wrong manual position restored");
        Check(commit.TryRestore(a,out restored) && restored!.Items["venom"]==2,"Cached second load lost verified provenance");
        commit.ResetReadEvidence();
        Check(!commit.TryRestore(a,out _),"New game retained verified save provenance");
        commit.Stage(identity,initial);
        Check(commit.TryRestore(b,out restored) && restored is null,"Native-only slot retained the prior stock");
        // A read for a thumbnail cannot substitute another SaveWork.
        commit.Stage(identity,initial);
        Check(!commit.TryRestore(c,out _),"Unrelated thumbnail read accepted");
        byte[] duplicate=Packet([a,a]);
        Reject(()=>commit.Enqueue(103,identity,duplicate,a,State(a,2)));
        Reject(()=>commit.Enqueue(103,identity,initial,a,State(b,2)));
        Check(!commit.Enqueue(103,identity,initial,c,State(c,2)),"Unserialized live inventory queued");
        Check(commit.Enqueue(104,identity,initial,a,State(a,2)),"Native-failure fixture not queued");
        Check(commit.Complete(104,identity,[], -1)==SaveCommitResult.NativeWriteFailed,"Native failure not respected");
        Check(commit.Enqueue(105,identity,initial,a,State(a,2)),"Integrity fixture not queued");
        Reject(()=>commit.Complete(105,identity,updated,0));
        Check(commit.Enqueue(105,identity,initial,a,State(a,2)),"Rejected completion poisoned a reused native slot object");
        commit.Cancel(105);
        Check(commit.Complete(105,identity,initial,0)==SaveCommitResult.NoMatchingRequest,"Cancelled snapshot committed");
        // Native bytes do not contain expanded inventory. An EXACT successful
        // new transaction supplies the provenance for replacing current extras.
        // Cancel/failure still cannot change them; old document is archived.
        Check(commit.Enqueue(106,identity,initial,a,State(a,3)),"Ambiguity fixture not queued");
        Check(commit.Complete(106,identity,initial,0)==SaveCommitResult.Published,"Confirmed unchanged native payload could not advance expanded state");
        Check(commit.TryRestore(a,out restored)&&restored!.Items["venom"]==3,"Latest confirmed extras were not restored");
        string archived=Directory.GetFiles(Path.Combine(root,identity.StorageKey(),"history"),"*.json").Single();
        Check(System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(archived)).RootElement.GetProperty("State").GetProperty("Items").GetProperty("venom").GetInt32()==2,"Previous identical-native generation was not retained");
        Check(commit.Enqueue(107,identity,initial,a,State(a,4)),"Failed replacement not queued");
        Check(commit.Complete(107,identity,[], -1)==SaveCommitResult.NativeWriteFailed && store.Load(identity,ExpandedSaveRegistry.Hash(a),Hashes(a))!.Items["venom"]==3,"Failed replacement changed current extras");
        Check(commit.Enqueue(108,identity,initial,a,State(a,4)),"Identity fixture not queued");
        Reject(()=>commit.Complete(108,identity with {NativeSlotName="wrong"},initial,0));
        Check(commit.Enqueue(108,identity,initial,a,State(a,3)),"Identity refusal poisoned another native transaction");
        Check(commit.Complete(108,identity,initial,0)==SaveCommitResult.Published,"Native transaction could not recover after refusal");
        Console.WriteLine("PASS: serialized SaveWork association, independent manual positions, frozen queue stock/learning, confirmed identical-native generation replacement with recoverable history, failed/cancelled write isolation, reused native slot recovery after integrity/identity refusal, cached reload and new-game reset, native-only reset, thumbnail and duplicate bindings rejected. Owned fixture only.");
        byte[] Work(int salt)=>Enumerable.Range(0,NativePartySaveCommit.WorkSize).Select(i=>(byte)((i*73+salt)%251)).ToArray();
        Dictionary<int,string> Hashes(byte[] work)=>ChemistActionBindings.RecordHashes(work.AsSpan(NativePartySaveCommit.RecordsOffset,NativePartySaveCommit.RecordsSize));
        ExpandedSaveState State(byte[] work,int count)=>new(new(){{"venom",count}},[new(4,Hashes(work)[4],["venom"])]);
        byte[] Packet(byte[][] works)
        {
            var bytes=new byte[16+works.Length*(NativePartySaveCommit.WorkSize+8)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes,16);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8),NativeSavePacket.ManualBinaryKind);
            for(int i=0;i<works.Length;i++)works[i].CopyTo(bytes,16+i*(NativePartySaveCommit.WorkSize+8));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),NativeSavePacket.Crc32(bytes.AsSpan(16)));
            return bytes;
        }
    }
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    static void Reject(Action action){try{action();throw new Exception("Expected strict refusal");}catch(InvalidDataException){}}
}
