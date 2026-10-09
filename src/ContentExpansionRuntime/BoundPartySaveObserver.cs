using System.Buffers.Binary;
using FFTModLoader.ContentExpansion;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>Actual queue/read wire ABI; restoration is deferred to ResumeFromSave.</summary>
public sealed class BoundPartySaveObserver : INativeSaveObserver
{
    private readonly Func<string> _profile;
    private readonly NativePartySaveCommit _commit;
    private readonly Func<(byte[] Work, ExpandedSaveState State)?> _frozen;
    private readonly Action<string> _failure;
    private readonly Action<string> _log;
    private readonly Func<long,int,byte[]> _read;
    private readonly Func<byte[],ExpandedSaveState?>? _battleSnapshot;
    private readonly Func<(int Slot,byte[] Work,ExpandedSaveState State)?>? _manual;
    private int _unrelatedReported;

    public BoundPartySaveObserver(string profile, NativePartySaveCommit commit,
        Func<(byte[] Work, ExpandedSaveState State)?> frozen, Action<string> failure,
        Action<string> log, Func<long,int,byte[]>? read = null)
        :this(()=>profile,commit,frozen,failure,log,read)
    { _=new SaveSlotIdentity(profile,"binding-validation").StorageKey(); }
    public BoundPartySaveObserver(Func<string> profile, NativePartySaveCommit commit,
        Func<(byte[] Work, ExpandedSaveState State)?> frozen, Action<string> failure,
        Action<string> log, Func<long,int,byte[]>? read = null,Func<byte[],ExpandedSaveState?>? battleSnapshot=null,
        Func<(int Slot,byte[] Work,ExpandedSaveState State)?>? manual=null)
    {
        _profile=profile;_commit=commit;_frozen=frozen;_failure=failure;_log=log;
        _read=read ?? CheckedNativeRead.Read;
        _battleSnapshot=battleSnapshot;
        _manual=manual;
    }
    // Steam initializes after the injected runtime: resolve the actual profile
    // lazily at the native save/read boundary, never invent a default account.
    public void RequireReadyBindings() => ArgumentNullException.ThrowIfNull(_profile);
    public bool BeforeQueue(long slotObject,long name,long pointer,long length)
    {
        if(slotObject<=0 || !Packet(pointer,length,out var packet))return false;
        if(BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8))==0xE && _manual?.Invoke() is { } manual)
        {
            bool staged=_commit.EnqueueManual(slotObject,Slot(name),packet,manual.Slot,manual.Work,manual.State);
            _log(staged?$"[Save] Slot manual {manual.Slot+1}: pacote exato vinculado; aguardando gravação.":
                "[Save] Pacote manual não corresponde ao slot preparado; nenhum extra publicado.");
            return staged;
        }
        var battle=_battleSnapshot?.Invoke(packet);
        if(battle is not null)
        {
            bool staged=_commit.EnqueueBattle(slotObject,Slot(name),packet,battle);
            if(staged)_log("[Save] Autosave de batalha: estoque/aprendizado congelados com o pacote integral selecionado.");
            return staged;
        }
        var frozen=_frozen();
        if(frozen is null)return false;
        bool captured=_commit.Enqueue(slotObject,Slot(name),packet,frozen.Value.Work,frozen.Value.State);
        // The same queue also writes other positions/metadata. A valid packet
        // without this frozen SaveWork is not our transaction: leave it native,
        // without publishing extras or disabling the current party's abilities.
        if(!captured && Interlocked.Exchange(ref _unrelatedReported,1)==0)
            _log("[Save] Pacote de outra posição preservado pelo jogo; sem publicar extras e sem bloquear aprendizado/combate.");
        if(captured)_log("[Save] Conteúdo extra congelado com a posição nativa; aguardando confirmação da gravação.");
        return captured;
    }
    public void CancelQueue(long slotObject)=>_commit.Cancel(slotObject);
    public NativeWriteCompletion? CaptureWriteCompletion(long entryAddress,int result)
    {
        byte[] entry=_read(entryAddress,NativeSaveEvents.QueueEntrySize);
        long slotObject=BinaryPrimitives.ReadInt64LittleEndian(entry.AsSpan(0x18));
        if(slotObject<=0)return null;
        var slot=new SaveSlotIdentity(_profile(),Name(entry.AsSpan(0x20,0x100)));
        int error=BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(0x140));
        int effective=error<0?error:result;
        if(effective<0)return new(slotObject,slot,[],effective);
        if(!Packet(BinaryPrimitives.ReadInt64LittleEndian(entry.AsSpan(8)),
            BinaryPrimitives.ReadInt64LittleEndian(entry),out var packet))return null;
        return new(slotObject,slot,packet,effective);
    }
    public void AfterWriteCompletion(NativeWriteCompletion completion,int originalResult)
    {
        try
        {
        var result=_commit.Complete(completion.SlotObject,completion.Slot,completion.Packet,
            completion.NativeResult<0?completion.NativeResult:originalResult);
        if(result==SaveCommitResult.Published)_log("[Save] Estoque e aprendizado extras gravados; save original preservado.");
        else if(result==SaveCommitResult.NativeWriteFailed)_log("[Save] Gravação nativa falhou; conteúdo extra não foi publicado.");
        }
        catch(Exception exception)
        {
            // A failed disk publication does not invalidate already-paid live
            // learning or current stock. Preserve gameplay; NEVER claim saved,
            // and keep load-time integrity validation strict.
            _log("[Aviso] Extras deste save não foram gravados; aprendizado/estoque da sessão atual preservados: "+exception.Message);
        }
    }
    public NativeReadCompletion? CaptureReadCompletion(long name,int result,long pointer,long length)
    {
        if(result<0 || !Packet(pointer,length,out var packet))return null;
        return new(Slot(name),NativeSavePacket.ValidateAndHash(packet),null,packet);
    }
    public void AfterReadCompletion(NativeReadCompletion completion,int originalResult)
    {
        if(originalResult>=0 && completion.Packet is not null)_commit.Stage(completion.Slot,completion.Packet);
    }
    public void ReportFailure(Exception exception)
    {
        try{_failure(exception.Message);}catch{}
        try{_log("[Erro] Integração do save bloqueada: "+exception.Message);}catch{}
    }
    private bool Packet(long pointer,long length,out byte[] packet)
    {
        packet=[];
        if(pointer<=0 || length<=16 || length>NativeSavePacket.MaxBytes)return false;
        byte[] header=_read(pointer,16);
        if(BinaryPrimitives.ReadUInt32LittleEndian(header)!=16 ||
           BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) is not (0xE or 0x11))return false;
        packet=_read(pointer,checked((int)length));
        NativeSavePacket.ValidateAndHash(packet);
        return true;
    }
    private SaveSlotIdentity Slot(long pointer)
    {
        byte[] bytes=new byte[256];
        for(int i=0;i<bytes.Length;i++)
        {
            bytes[i]=_read(checked(pointer+i),1)[0];
            if(bytes[i]==0)return new(_profile(),Name(bytes.AsSpan(0,i+1)));
        }
        throw new InvalidDataException("Unterminated native save name.");
    }
    private static string Name(ReadOnlySpan<byte> bytes)
    {
        int end=bytes.IndexOf((byte)0);
        if(end<=0)throw new InvalidDataException("Empty/unbounded native save name.");
        return Convert.ToHexString(bytes[..end]);
    }
}
