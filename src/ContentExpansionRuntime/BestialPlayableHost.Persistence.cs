using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;

namespace FFTModLoader.ContentExpansion.Runtime;

public sealed unsafe partial class BestialPlayableHost
{
    private sealed record SavedState(int Version,ExpandedJobProgressState[] Progress,SavedBattleForm[] Forms);
    private sealed record SavedDocument(int Version,string NativeHash,string StateHash,SavedState State);
    private readonly object _saveGate=new();
    private readonly ConcurrentDictionary<long,(string PacketHash,string Profile,Dictionary<string,SavedState> States)> _queued=[];
    private (byte[] Work,SavedState State)? _frozen;
    private (int Slot,byte[] Work,SavedState State)? _manual;
    private SavedState? _pendingBattleState;
    private SavedState? _deferredProgress;
    private readonly ThreadLocal<Dictionary<long,long>> _completions=new(()=>[]);
    private string _saveDirectory="";
    private Func<string> _profile=null!;
    private ThreadLocal<Dictionary<long,byte[]>> _loaded=new(()=>[]);
    private SavedState SnapshotState(bool battle)=>new(1,_progress?.Snapshot()??[],battle?_forms.Snapshot(_battle):[]);
    private static string Hash(byte[] bytes)=>ExpandedSaveRegistry.Hash(bytes);
    private string SavePath(string profile,string nativeHash)
    {
        if(profile.Length!=64||nativeHash.Length!=64||profile.Concat(nativeHash).Any(c=>!Uri.IsHexDigit(c)))
            throw new InvalidDataException("Invalid expanded save identity.");
        return Path.Combine(_saveDirectory,profile,nativeHash+".json");
    }
    private void PublishState(string profile,string nativeHash,SavedState state)
    {
        string path=SavePath(profile,nativeHash);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] stateBytes=JsonSerializer.SerializeToUtf8Bytes(state);
        byte[] document=JsonSerializer.SerializeToUtf8Bytes(new SavedDocument(1,nativeHash,Hash(stateBytes),state));
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(document);file.Flush(true);}File.Move(temporary,path,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private SavedState ReadState(byte[] native)
    {
        string hash=Hash(native),path=SavePath(_profile(),hash);
        if(!File.Exists(path))return new(1,[],[]);
        if(new FileInfo(path).Length>256*1024)throw new InvalidDataException("Expanded save is too large.");
        var document=JsonSerializer.Deserialize<SavedDocument>(File.ReadAllBytes(path))??throw new InvalidDataException("Empty expanded save.");
        if(document.Version!=1||document.NativeHash!=hash||document.State.Version!=1||document.State.Progress is null||document.State.Forms is null||
            document.State.Progress.Length>54||document.State.Forms.Length>21||Hash(JsonSerializer.SerializeToUtf8Bytes(document.State))!=document.StateHash)
            throw new InvalidDataException("Expanded save integrity check failed.");
        return document.State;
    }
    private void RestoreState(byte[] work,bool battle)
    {
        SavedState state=ReadState(work);
        lock(_formGate)_pendingForms.Clear();
        lock(_saveGate)
        {
            if(_progress is not null)_progress.Restore(state.Progress);else _deferredProgress=state;
            _frozen=null;_manual=null;
            if(battle)_pendingBattleState=state;
            else{_pendingBattleState=null;_forms.Restore(++_battle,[]);}
        }
        _log("[Save] Bestial progress restored from the exact consumed native position.");
    }
    private void InstallPersistence(string executable,Func<string> profile)
    {
        _profile=profile;_saveDirectory=Path.Combine(Path.GetDirectoryName(executable)!,"FFTModLoader.Runtime","ExpandedSaves","BestialWarrior");
        Bind(0x2CEEE8,0,f=>{if(f->Phase==1){lock(_saveGate)_frozen=(Read(_image+0x2E80450,NativePartySaveCommit.WorkSize),SnapshotState(false));}},true);
        Bind(0x0C4420,0,f=>
        {if(f->Phase==1){int slot=(int)f->Arguments[1];if(slot is <0 or >=50)throw new InvalidDataException("Invalid manual save slot.");
            lock(_saveGate)_manual=(slot,Read(_image+0x2C8B970+slot*(NativePartySaveCommit.WorkSize+8L),NativePartySaveCommit.WorkSize),SnapshotState(false));}},true);
        Bind(0x32B94,1,f=>
        {
            long slot=f->Arguments[1];if(f->Phase==1){if((int)f->OriginalResult<0)_queued.TryRemove(slot,out _);return;}
            long size=f->Arguments[4],pointer=f->Arguments[3];if(pointer<=0||size<=16||size>NativeSavePacket.MaxBytes)return;
            byte[] header=Read(pointer,16);if(BinaryPrimitives.ReadUInt32LittleEndian(header)!=16||BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) is not (14 or 17))return;
            byte[] packet=Read(pointer,checked((int)size));string packetHash=NativeSavePacket.ValidateAndHash(packet);
            var states=new Dictionary<string,SavedState>();
            lock(_saveGate)
            {
                if(BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8))==14&&_manual is {} manual)
                {
                    int at=16+manual.Slot*(NativePartySaveCommit.WorkSize+8);
                    if(at+manual.Work.Length<=packet.Length&&packet.AsSpan(at,manual.Work.Length).SequenceEqual(manual.Work))states.Add(Hash(manual.Work),manual.State);
                }
                else if(NativePartySaveCommit.IsBattlePayload(packet.AsSpan(16)))states.Add(Hash(packet[16..]),SnapshotState(true));
                else if(_frozen is {} frozen&&packet.AsSpan(16).IndexOf(frozen.Work)>=0)states.Add(Hash(frozen.Work),frozen.State);
            }
            if(states.Count!=0&&!_queued.TryAdd(slot,(packetHash,_profile(),states)))throw new InvalidDataException("Outstanding expanded save transaction.");
        },true);
        Bind(0x32E60,0,f=>
        {
            if(f->Phase==0)
            {
                long entry=f->Arguments[2],slot=Ptr(entry+0x18);if(!_queued.TryGetValue(slot,out var queued))return;
                if((int)f->Arguments[1]<0||I32(entry+0x140)<0){_queued.TryRemove(slot,out _);return;}
                long size=Ptr(entry),pointer=Ptr(entry+8);if(size is <=16 or >NativeSavePacket.MaxBytes||pointer<=0)throw new InvalidDataException("Save completion packet missing.");
                if(NativeSavePacket.ValidateAndHash(Read(pointer,(int)size))!=queued.PacketHash)throw new InvalidDataException("Save completion packet changed.");
                _completions.Value![f->EntryStack]=slot;return;
            }
            if(!_completions.Value!.Remove(f->EntryStack,out long completedSlot)||!_queued.TryRemove(completedSlot,out var pending)||(int)f->OriginalResult<0)return;
            try{foreach(var pair in pending.States)PublishState(pending.Profile,pair.Key,pair.Value);}
            catch(Exception ex){_log("[Warning] Native save completed but Bestial sidecar was not written: "+ex.Message);}
        },true);
        foreach(int rva in new[]{0x2CF768,0x21B0E8})Bind(rva,0,f=>
        {
            if(f->Phase==0){long p=rva==0x21B0E8?Ptr(_image+0xD407A0):_image+0x2E80450;_loaded.Value![f->EntryStack]=Read(p,NativePartySaveCommit.WorkSize);}
            else if(_loaded.Value!.Remove(f->EntryStack,out var work))RestoreState(work,false);
        },true);
        Bind(0x2181EC,0,f=>
        {
            if(f->Phase==0)
            {
                long controller=Ptr(_image+0x3CD9EE0);byte[] inner=[];
                if(controller>0){int offset=(int)f->Arguments[1] switch{2=>0x3D5410,3=>0x3D5698,4=>0x3D5920,_=>0x3D5188};
                    long pointer=Ptr(controller+offset),size=Ptr(controller+offset+8);if(pointer>0&&size is >=0xA3324 and <=0x180000)inner=Read(pointer,(int)size);}
                _loaded.Value![f->EntryStack]=inner;
            }
            else if(_loaded.Value!.Remove(f->EntryStack,out var inner))
            {
                if((int)f->OriginalResult==1){if(!NativePartySaveCommit.IsBattlePayload(inner))throw new InvalidDataException("Battle load payload differs.");RestoreState(inner,true);}
                else if((int)f->OriginalResult==0)RestoreState(Read(_image+0x2E80450,NativePartySaveCommit.WorkSize),false);
            }
        },true);
        Bind(0x2CE5C8,0,f=>
        {
            if(f->Phase!=1||_pendingBattleState is not {} state)return;
            lock(_formGate)
            {
                _pendingForms.Clear();
                _forms.Restore(++_battle,state.Forms);
                foreach(var form in state.Forms){long worker=Worker(form.Worker);byte[] current=Read(worker,512);
                    // A consumed autosave may differ in HP/status/CT from the
                    // transition snapshot, but its temporary owner must match.
                    _=_forms.PlanReturn(_battle,form.Worker,current);}
                for(int i=0;i<21;i++)if(Read(_image+0x1853CE0+i*512+3,1)[0]==175&&!_forms.HasTemporaryForm(_battle,i))
                    throw new InvalidDataException("Wolf autosave has no matching reversible origin.");
                _pendingBattleState=null;
            }
        });
        Bind(0x284500,0,f=>{if(f->Phase==1&&(int)f->Arguments[0]==0){lock(_formGate)_pendingForms.Clear();_progress?.Restore([]);_deferredProgress=null;_forms.Restore(++_battle,[]);_pendingBattleState=null;}},true);
        InstallProgressGain();
    }
}
