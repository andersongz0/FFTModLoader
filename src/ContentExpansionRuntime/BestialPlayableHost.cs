using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FFTModLoader.ContentExpansion.Api;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>Loader-owned adapter for new jobs/actions; no original ID is replaced.</summary>
public sealed unsafe partial class BestialPlayableHost
{
    private readonly long _image;
    private readonly NativeContextBridge _bridge;
    private readonly LoaderNativeRoutes _routes;
    private readonly IExpandedJobVisuals _visuals;
    private readonly Action<string> _log;
    private readonly List<NativeLifetimeMemory> _memory=[];
    private readonly Dictionary<int,nint> _helpers=[];
    private readonly Dictionary<byte,NativeLifetimeMemory> _sprites=[];
    private readonly Dictionary<byte,ExpandedShapeResources> _shapeResources=[];
    private readonly NativeActionMenu _menu=new([new("transform",524,0)]);
    private readonly ReversibleBattleForms _forms=new(new(174,175,227,228,176,177,0,1,4,true));
    private ExpandedJobProgress? _progress;
    private NativeLifetimeMemory _abilityState=null!,_commandState=null!,_humanRow=null!,_wolfRow=null!;
    private volatile bool _active,_blocked;
    private bool _attempted;
    private long _battle=1;
    private string _directory="";
    private static readonly ushort[] WolfActions=[532,280,281,283,282];
    public BestialPlayableHost(long image,NativeContextBridge bridge,LoaderNativeRoutes routes,
        IExpandedJobVisuals visuals,Action<string> log)
    { _image=image;_bridge=bridge;_routes=routes;_visuals=visuals;_log=log; }
    private static bool OwnCommand(int command)=>command is 227 or 228;
    private static bool OwnAbility(int ability)=>ability is 524 or 532;
    private static byte[] Read(long p,int count)=>CheckedNativeRead.Read(p,count);
    private static void Write(long p,byte[] bytes)=>NativeLifetimeMemory.WriteProtected(p,bytes);
    private static ushort U16(long p)=>BinaryPrimitives.ReadUInt16LittleEndian(Read(p,2));
    private static int I32(long p)=>BinaryPrimitives.ReadInt32LittleEndian(Read(p,4));
    private static long Ptr(long p)=>BinaryPrimitives.ReadInt64LittleEndian(Read(p,8));
    private static void Word(long p,int value)=>Write(p,BitConverter.GetBytes(checked((ushort)value)));
    private static void Int(long p,int value)=>Write(p,BitConverter.GetBytes(value));
    private NativeLifetimeMemory Own(int bytes){var memory=new NativeLifetimeMemory(bytes);_memory.Add(memory);return memory;}
    private long Worker(int index)
    {
        if(index is <0 or >20)throw new InvalidDataException("Invalid Bestial battle worker.");
        long worker=_image+0x1853CE0+index*512;
        if(Read(worker+1,1)[0]!=index)throw new InvalidDataException("Inactive or recycled Bestial worker.");
        return worker;
    }
    private long Unit(int index)
    {
        if(index is <0 or >=54)throw new InvalidDataException("Invalid Bestial menu unit.");
        long unit=Ptr(_image+0x1800F50+index*8);
        if(unit<=0)throw new InvalidDataException("Null Bestial menu unit.");
        return unit;
    }
    private void Helper(int rva){_routes.Guard(rva);_helpers.TryAdd(rva,(nint)(_image+rva));}
    private long Call(int rva,long a=0,long b=0,long c=0,long d=0)
    {
        if(!_helpers.TryGetValue(rva,out nint helper))throw new InvalidDataException("Unverified Bestial native helper.");
        return _bridge.CallOnNativeOwner(helper,a,b,c,d);
    }
    private void Block(Exception failure)
    {
        _blocked=true;
        _log("[Error] Bestial Warrior action blocked: "+failure.Message);
    }
    private void Bind(int rva,uint stack,NativeContextBridge.Observer callback,bool save=false)
    {
        _routes.Observe(rva,stack,f=>
        {
            if(!_active)return;
            try{callback(f);}catch(Exception ex)
            {
                Block(ex);
                // The callback owns its explicit skip/result. Never change an
                // unrelated native call's return because an optional save/UI
                // observer failed. Subsequent Bestial actions fail closed.
            }
        },Block,save);
    }
    private ExpandedJobProgress Progress()
    {
        if(_progress is not null)return _progress;
        // Read all eight thresholds from the game's current Knight curve,
        // after NXD initialization. No invented curve and no legacy JP proxy.
        ushort[] thresholds=Enumerable.Range(1,8).Select(level=>checked((ushort)Call(0x2CC008,2,level))).ToArray();
        _progress=new(174,thresholds,[new("transform",524,0)]);
        if(_deferredProgress is {} saved){_progress.Restore(saved.Progress);_deferredProgress=null;}
        return _progress;
    }
    private string UnitKey(int slot)
    {
        if(slot is <0 or >=54)throw new InvalidDataException("No persistent party identity for Bestial progress.");
        byte[] record=Read(_image+0x11A7D10+slot*600,600);
        if(record[0] is 0 or 255 || record[1]!=slot)throw new InvalidDataException("Persistent party identity differs.");
        return Convert.ToHexString(SHA256.HashData(new byte[]{record[0],record[1],record[4],record[5],record[6],record[0x11C],record[0x11D],record[0x124]}));
    }
    private string MenuKey(long unit)=>UnitKey(ChemistActionBindings.SerializedSlot(Read(unit,0x2E)));
    private bool Known(long worker)=>!_blocked && ((Read(worker+6,1)[0]&0x20)!=0 ||
        Progress().Read(UnitKey(Read(worker+2,1)[0])).Learned.Contains((ushort)524));

    public void Install(string executable,string contentDirectory,string relocationPlan,Func<string> profile)
    {
        if(_attempted)throw new InvalidOperationException("Bestial activation already attempted.");
        _attempted=true;_directory=Path.GetFullPath(contentDirectory);
        if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable)))!=ChemistPlayableHost.ImageHash)
            throw new NotSupportedException("Unsupported Bestial executable.");
        var content=ExpandedContentCatalog.Read(File.ReadAllText(Path.Combine(_directory,"FFTModLoader.content.json")));
        if(content.ModId!="ffttic.jobs.bestialwarrior" || !content.Commands.Single(c=>c.Id==227).Actions.SequenceEqual(new ushort[]{524}) ||
            !content.Commands.Single(c=>c.Id==228).Actions.SequenceEqual(WolfActions))
            throw new InvalidDataException("Bestial content definition differs from its native adapter.");
        if(_visuals.VisualContractVersion!=1)throw new NotSupportedException("Expanded visuals v1 required.");
        foreach(var(shape,name) in new (byte,string)[]{(174,"bestial_m"),(175,"bestial_f"),(176,"wolf_m"),(177,"wolf_f")})
        {
            _shapeResources.Add(shape,_visuals.GetRuntimeResources(shape));
            byte[] bytes=NativeSpriteSheet.Unpack(File.ReadAllBytes(Path.Combine(_directory,"Visuals",name+".spr")));
            var sprite=Own(bytes.Length);sprite.Write(0,bytes);_sprites.Add(shape,sprite);
        }
        foreach(int rva in new[]{0x2CC008,0x285744,0x2BD05C,0xF4BCC,0x30FFC4,0x30E2F0,0x27F974,0x280B40,
            0x260ABC,0x268FF4,0x26E838,0x26947C,0x268E04,0x3167E4,0x272874,0x2BB060})Helper(rva);
        var writes=_routes.Tables.Plan(relocationPlan);
        writes.AddRange(_routes.Tables.PlanCommandKinds());
        writes.AddRange(_routes.Tables.PlanActionBounds());
        writes.AddRange(_routes.Jobs.Plan(Path.Combine(_directory,"FFTModLoader.job-records.json"),content));
        InstallRecords();
        InstallMenus();
        InstallBattle();
        InstallForms(writes);
        InstallPersistence(executable,profile);
        NativeTableRelocation.Apply(writes,Read,Write);
        _routes.Jobs.Acknowledge();
        _routes.Tables.AcknowledgeRelocation();_routes.Tables.AcknowledgeCommandKinds();
        _routes.Tables.AcknowledgeActionBounds();_routes.Tables.AcknowledgeFormulaDispatch(108);
        _active=true;
        _abilityState.Write(0,[1]);_commandState.Write(0,[1]);
        _log("[Runtime] Bestial Warrior local candidate activated; new IDs only, temporary Wolf form and five-action monster command.");
        _log("[Runtime] Native job records relocated: 40 audited readers and 15 bounds; original jobs and neighboring ability data preserved.");
    }
    private void InstallRecords()
    {
        _emptyRow=Own(48);
        _abilityState=Own(NativeAbilityCatalog.StateLength);
        foreach(int ability in new[]{524,532})
        {
            var action=Own(20);byte[] bytes=BestialBattleData.Action();action.Write(0,bytes);
            // Zero JP/MP/CT for the initial local Transform test. Undefined
            // abilities 525..531 never enter a row or become learnable.
            byte[] common=new byte[8];common[2]=100;common[3]=0x41;common[7]=0x80;
            _routes.Tables.Common.Write(ability*8,common);
            _routes.Tables.Visual.Write(ability*3,[19,0,0]); // native no-cast selector; never Infection's effect
            _abilityState.Write(NativeAbilityCatalog.RegisteredOffset+ability,[1]);
            _abilityState.Write(NativeAbilityCatalog.CommonPointersOffset+ability*8,BitConverter.GetBytes(_routes.Tables.Common.Address+ability*8));
            _abilityState.Write(NativeAbilityCatalog.ActionPointersOffset+ability*8,BitConverter.GetBytes(action.Address));
        }
        _humanRow=Own(48);_humanRow.Write(0,BitConverter.GetBytes((ushort)524));
        _wolfRow=Own(48);
        for(int i=0;i<WolfActions.Length;i++)_wolfRow.Write(i*2,BitConverter.GetBytes(WolfActions[i]));
        _commandState=Own(NativeExpandedCommandCatalog.StateLength);
        _commandState.Write(8,BitConverter.GetBytes(Own(48).Address));
        foreach(var(command,row,count,monster) in new[]{(227,_humanRow,1,0),(228,_wolfRow,5,1)})
        {
            _commandState.Write(NativeExpandedCommandCatalog.RegisteredOffset+command,[1]);
            _commandState.Write(NativeExpandedCommandCatalog.RowsOffset+command*8,BitConverter.GetBytes(row.Address));
            _commandState.Write(NativeExpandedCommandCatalog.CountsOffset+command,[(byte)count]);
            _commandState.Write(NativeExpandedCommandCatalog.MonsterOffset+command,[(byte)monster]);
            _routes.Tables.CommandKinds.Write(command,[(byte)(monster==1?6:0)]);
        }
        _routes.Leaf(0x275A78,p=>NativeAbilityCatalog.BuildAddress(p,_abilityState.Address));
        _routes.Leaf(0x2BB060,p=>NativeAbilityCatalog.BuildSecondary(p,_abilityState.Address));
        _routes.Leaf(0x319D74,p=>NativeAbilityCatalog.BuildRegisteredEffectClassification(p,_abilityState.Address));
        _routes.Leaf(0x275860,p=>NativeExpandedCommandCatalog.BuildSlot(p,_commandState.Address));
        _routes.Leaf(0x275980,p=>NativeExpandedCommandCatalog.BuildActionArray(p,_commandState.Address),2);
    }
}
