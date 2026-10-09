using System.Buffers.Binary;
using FFTModLoader.ContentExpansion;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>Game-thread session, with immutable async-save snapshots.</summary>
public sealed class ChemistLiveSession
{
    private readonly long _image, _stock, _nativeLearning;
    private readonly Func<long,int,byte[]> _read;
    private readonly Action<long,byte[]> _write;
    private readonly Action<string> _log;
    private readonly Func<string>? _profile;
    private readonly uint[] _extraLearning = new uint[54];
    private readonly string?[] _unitKeys = new string?[54];
    private readonly object _gate = new();
    private (byte[] Work, ExpandedSaveState State)? _frozen;
    private (int Slot,byte[] Work,ExpandedSaveState State)? _manualFrozen;
    private (byte[] Records,ExpandedSaveState? State,bool Exact)? _pendingBattle;
    private volatile bool _mutable = true;
    public NativePartySaveCommit Saves { get; }
    public bool CanMutate => _mutable;

    public ChemistLiveSession(long image,long stock,NativePartySaveCommit saves,
        Func<long,int,byte[]> read,Action<long,byte[]> write,Action<string> log,Func<string>? profile=null,long nativeLearning=0)
    { _image=image;_stock=stock;Saves=saves;_read=read;_write=write;_log=log;_profile=profile;_nativeLearning=nativeLearning;PublishEnabled(true); }

    private void PublishEnabled(bool enabled)
    {if(_nativeLearning!=0)_write(_nativeLearning,[(byte)(enabled?1:0)]);}
    private void PublishMask(int slot)
    {if(_nativeLearning!=0)_write(_nativeLearning+16+slot*16,BitConverter.GetBytes(_extraLearning[slot]));}

    public void Block(string reason)
    {
        PublishEnabled(false);_mutable=false;
        lock(_gate){_frozen=null;_manualFrozen=null;}
        _log("[Erro] Ações extras bloqueadas para proteger o estoque/save: "+reason);
    }
    public int Stock(int slot)
    {
        int item=ChemistActionBindings.Item(slot);
        return _read(slot>=4?_stock+NativeItemStockLeaf.CountsOffset+item:_image+0x11A7C00+item,1)[0];
    }
    public bool Learned(int slot)
        => (ExtraLearning(slot)&1)!=0; // compatibility: Venom is extra bit0
    public uint ExtraLearning(int slot)
    {
        if(slot is <0 or >=54)return 0;
        lock(_gate){BindUnit(slot);return _extraLearning[slot];}
    }
    public void LearnVenom(int slot)
        => LearnExtra(slot,4);
    public void LearnExtra(int slot,int actionSlot)
    {
        if(!CanMutate || slot is <0 or >=54 || actionSlot<4 || actionSlot>=ChemistActionBindings.ActionCount)throw new InvalidOperationException("Extra learning is blocked.");
        lock(_gate){BindUnit(slot);_extraLearning[slot]|=1u<<(actionSlot-4);PublishMask(slot);}
    }
    internal void RestoreLearning(int slot,bool learned)
        => RestoreExtraLearning(slot,learned?1u:0u);
    internal void RestoreExtraLearning(int slot,uint learned)
    {if((learned&~ChemistActionBindings.ExtraMask)!=0)throw new InvalidDataException("Unknown learning bit.");lock(_gate){BindUnit(slot);_extraLearning[slot]=learned;PublishMask(slot);}}
    public void Consume(int slot)
    {
        if(!CanMutate)throw new InvalidOperationException("Extra commitment is blocked.");
        int count=Stock(slot);
        if(count<=0)throw new InvalidOperationException("Cannot consume an empty item stack.");
        int item=ChemistActionBindings.Item(slot);
        _write(slot>=4?_stock+NativeItemStockLeaf.CountsOffset+item:_image+0x11A7C00+item,[(byte)(count-1)]);
        _log($"[Combate] Consumido 1 {ChemistActionBindings.TestActions[slot].Key}; estoque={count-1}.");
    }
    public void CaptureSerializedWork()
    {
        if(!CanMutate)return;
        byte[] work=_read(_image+0x2E80450,NativePartySaveCommit.WorkSize);
        var state=SnapshotWork(work);
        lock(_gate)_frozen=(work,state);
    }
    public void CaptureManualWork(int slot)
    {
        if(!CanMutate)return;
        if(slot is <0 or >=50)throw new InvalidDataException("Invalid prepared manual save position.");
        // PrepareManualSave(0C4420) completes the selected slot AFTER adding
        // manual-only flags. The autosave global is a different SaveWork.
        byte[] work=_read(_image+0x2C8B970+slot*(NativePartySaveCommit.WorkSize+8L),NativePartySaveCommit.WorkSize);
        var state=SnapshotWork(work);
        lock(_gate)_manualFrozen=(slot,work,state);
        _log($"[Save] Slot manual {slot+1}: habilidades/estoques capturados após a preparação nativa.");
    }
    private ExpandedSaveState SnapshotWork(byte[] work)
    {
        byte[] actual=_read(_image+0x11A7D10,NativePartySaveCommit.RecordsSize);
        if(!work.AsSpan(NativePartySaveCommit.RecordsOffset,NativePartySaveCommit.RecordsSize).SequenceEqual(actual))
            throw new InvalidDataException("SaveWork does not contain the serialized live party.");
        var hashes=ChemistActionBindings.RecordHashes(actual);
        lock(_gate)
        {
            var units=new List<SavedUnitLearning>();
            for(int slot=0;slot<54;slot++)
            {
                BindUnit(slot);
                if(_extraLearning[slot]!=0)units.Add(new(slot,hashes[slot],ChemistActionBindings.ExtraSlots
                    .Where(s=>(_extraLearning[slot]&(1u<<(s-4)))!=0).Select(s=>ChemistActionBindings.TestActions[s].Key).ToArray()));
            }
            return new(ChemistActionBindings.ExtraSlots.ToDictionary(s=>ChemistActionBindings.TestActions[s].Key,Stock),units.ToArray());
        }
    }
    public (byte[] Work, ExpandedSaveState State)? Frozen()
    { lock(_gate)return _frozen; }
    public (int Slot,byte[] Work,ExpandedSaveState State)? ManualFrozen()
    {lock(_gate)return _manualFrozen;}

    public byte[] BeforePartyLoad()=>_read(_image+0x2E80450,NativePartySaveCommit.WorkSize);
    public void ResetParty(bool newGame)
    {
        lock(_gate)
        {
            PublishEnabled(false);
            Array.Clear(_extraLearning);Array.Clear(_unitKeys);_frozen=null;_manualFrozen=null;
            if(_nativeLearning!=0)_write(_nativeLearning+16,new byte[54*16]);
            foreach(int s in ChemistActionBindings.ExtraSlots)_write(_stock+NativeItemStockLeaf.CountsOffset+ChemistActionBindings.Item(s),[0]);_mutable=true;
            PublishEnabled(true);
            // A load can initialize the native party AFTER its read callback.
            // Only new-game mode discards those staged read sources.
            if(newGame){Saves.ResetReadEvidence();_pendingBattle=null;}
            _log("[Sessão] Reinício nativo da partida: estoque e aprendizado extras zerados.");
        }
    }
    public void AfterPartyLoad(byte[] serializedWork)
    {
        byte[] actual=_read(_image+0x11A7D10,NativePartySaveCommit.RecordsSize);
        if(!serializedWork.AsSpan(NativePartySaveCommit.RecordsOffset,NativePartySaveCommit.RecordsSize).SequenceEqual(actual))
            throw new InvalidDataException("Loaded native party differs from the consumed serialized SaveWork.");
        RestoreConsumed(serializedWork);
    }
    // AutoSave_Load consumes this exact full SaveWork before restoring battle
    // workers. Bind keys from its saved records, not the previous live party.
    public void AfterAutosaveLoad(byte[] serializedWork)=>RestoreConsumed(serializedWork);
    public ExpandedSaveState? SnapshotBattlePacket(byte[] packet)
    {
        var inner=packet.AsSpan(NativeSavePacket.HeaderSize);
        if(!NativePartySaveCommit.IsBattlePayload(inner) || !CanMutate)return null;
        byte[] records=inner.Slice(NativePartySaveCommit.BattleRecordsOffset,NativePartySaveCommit.RecordsSize).ToArray();
        if(!records.SequenceEqual(_read(_image+0x11A7D10,records.Length)))
            throw new InvalidDataException("Queued battle autosave differs from its live serialized party.");
        var hashes=ChemistActionBindings.RecordHashes(records);
        lock(_gate)
        {
            var units=new List<SavedUnitLearning>();
            for(int slot=0;slot<54;slot++)
            {
                BindRecord(slot,records.AsSpan(slot*600,600));
                if(_extraLearning[slot]!=0)units.Add(new(slot,hashes[slot],ChemistActionBindings.ExtraSlots
                    .Where(s=>(_extraLearning[slot]&(1u<<(s-4)))!=0).Select(s=>ChemistActionBindings.TestActions[s].Key).ToArray()));
            }
            return new(ChemistActionBindings.ExtraSlots.ToDictionary(s=>ChemistActionBindings.TestActions[s].Key,Stock),units.ToArray());
        }
    }
    public void AfterBattleAutosaveLoad(byte[] inner,byte[] selectedFieldWork)
    {
        if(_profile is null)throw new InvalidDataException("Battle profile binding missing.");
        var state=Saves.RestoreConsumedBattle(_profile(),inner);
        bool exact=state is not null;
        byte[] records=inner.AsSpan(NativePartySaveCommit.BattleRecordsOffset,NativePartySaveCommit.RecordsSize).ToArray();
        if(!exact)
        {
            // Old versions did not persist battle packets. Recover learning
            // only from the exact field save actually loaded with this battle.
            state=Saves.RestoreConsumedWork(_profile(),selectedFieldWork);
            records=selectedFieldWork.AsSpan(NativePartySaveCommit.RecordsOffset,NativePartySaveCommit.RecordsSize).ToArray();
            _log("[Save] Batalha antiga sem registro próprio: recuperação do save do mapa selecionado; consumo posterior não foi registrado pela versão antiga.");
        }
        lock(_gate)_pendingBattle=(records,state,exact);
    }
    public bool AfterBattlePartyLoad(byte[] actualRecords)
    {
        lock(_gate)
        {
            if(_pendingBattle is not { } pending)return false;
            if(pending.Exact && !pending.Records.SequenceEqual(actualRecords))throw new InvalidDataException("Restored battle party differs from selected autosave.");
            if(!pending.Exact)
                for(int slot=0;slot<54;slot++)
                    if(RecordIdentity(pending.Records.AsSpan(slot*600,600))!=RecordIdentity(actualRecords.AsSpan(slot*600,600)))
                        throw new InvalidDataException("Battle/selected field autosave unit identities differ.");
            ApplyState(actualRecords,pending.State);_pendingBattle=null;
            _log("[Save] Continue da batalha: aprendizado e estoques restaurados do pacote selecionado.");return true;
        }
    }
    private void RestoreConsumed(byte[] serializedWork)
    {
        ExpandedSaveState? state;
        if(_profile is not null)state=Saves.RestoreConsumedWork(_profile(),serializedWork);
        else if(!Saves.TryRestore(serializedWork,out state))
            throw new InvalidDataException("No successful native read matches the actual loaded party.");
        lock(_gate)
        {
            _pendingBattle=null;
            ApplyState(serializedWork.AsSpan(NativePartySaveCommit.RecordsOffset,NativePartySaveCommit.RecordsSize),state);
            _log($"[Save] Posição carregada validada; 11 estoques restaurados; aprendizado extra restaurado={state?.Units.Length??0}.");
        }
    }
    private void ApplyState(ReadOnlySpan<byte> records,ExpandedSaveState? state)
    {
            PublishEnabled(false);
            Array.Clear(_extraLearning);Array.Clear(_unitKeys);
            for(int slot=0;slot<54;slot++)BindRecord(slot,records.Slice(slot*600,600));
            if(state is not null)foreach(var unit in state.Units)
                foreach(int s in ChemistActionBindings.ExtraSlots)
                    if(unit.LearnedKeys.Contains(ChemistActionBindings.TestActions[s].Key))_extraLearning[unit.SerializedSlot]|=1u<<(s-4);
            for(int slot=0;slot<54;slot++)PublishMask(slot);
            foreach(int s in ChemistActionBindings.ExtraSlots)_write(_stock+NativeItemStockLeaf.CountsOffset+ChemistActionBindings.Item(s),[(byte)(state?.Items.GetValueOrDefault(ChemistActionBindings.TestActions[s].Key)??0)]);
            _frozen=null;_manualFrozen=null;
            _mutable=true;
            PublishEnabled(true);
    }

    private void BindUnit(int slot)
    {
        byte[] record=_read(_image+0x11A7D10+slot*600,600);
        BindRecord(slot,record);
    }
    private void BindRecord(int slot,ReadOnlySpan<byte> record)
    {
        // Native immutable identity fields: character, serialized index, sex,
        // birthday/zodiac, name-number and original join ID. Job, HP, level,
        // equipment, nickname and party sorting are deliberately excluded.
        bool active=record[0] is not (0 or 255) && record[1]==slot;
        string signature=active?RecordIdentity(record):"inactive";
        if(_unitKeys[slot]!=signature)
        {
            _unitKeys[slot]=signature;_extraLearning[slot]=0;
            if(_nativeLearning!=0)
            {
                byte[] identity=[record[0],record[1],record[4],record[5],record[6],record[0x11C],record[0x11D],record[0x124]];
                PublishMask(slot);_write(_nativeLearning+16+slot*16+4,identity);
            }
        }
        if(!active)_extraLearning[slot]=0;
    }
    private static string RecordIdentity(ReadOnlySpan<byte> record)
    {
        byte[] key=[record[0],record[1],record[4],record[5],record[6],
            record[0x11C],record[0x11D],record[0x124]];
        return Convert.ToHexString(key);
    }
}
