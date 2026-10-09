using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FFTModLoader.ContentExpansion;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>Version-locked fifteen-action/eleven-new-item playable integration.</summary>
public sealed unsafe class ChemistPlayableHost
{
    public const string ImageHash="937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5";
    [Function(CallingConventions.Microsoft)] private delegate long Native(long a,long b,long c,long d);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate long Helper(long a,long b,long c,long d);
    private readonly long _image;
    private readonly Action<string> _log;
    private readonly IReloadedHooks _hooksApi;
    private readonly NativeContextBridge _bridge;
    private readonly List<IHook<Native>> _hooks=[];
    private readonly List<NativeLifetimeMemory> _memory=[];
    private readonly Dictionary<int,Helper> _helpers=[];
    private readonly ThreadLocal<Dictionary<long,byte[]>> _loads=new(()=>[]);
    private readonly ConcurrentDictionary<long,HashSet<ushort>> _sortExtras=new();
    private readonly ConcurrentDictionary<long,(long Ui,int Worker,long Task,int Index)> _potionWindows=new();
    private NativeLifetimeMemory _potionScopes=null!,_medicineInput=null!;
    private NativeMedicineFourthMenu _medicineFourth=null!;
    private int _menuReported,_shopReported,_rangeReported,_areaReported;
    private int _throwReports,_emptyBattleReports,_receiverReports,_aiReports;
    private NativeLifetimeMemory _aiCandidates=null!;
    private readonly NativeActionMenu _menu=new(ChemistActionBindings.TestActions);
    private NativeLifetimeMemory _stock=null!,_common=null!,_shared=null!,_actions=null!,_visual=null!;
    private long _effectCallAddress;
    private NativeLifetimeMemory _mapNameDiagnostic=null!;
    private ChemistLiveSession _session=null!;
    private CompiledNativeSaveHooks? _save;
    private volatile bool _active;
    private byte[] _exe=[];
    private PEReader? _pe;

    public ChemistPlayableHost(IReloadedHooks hooks,long image,NativeContextBridge bridge,Action<string> log)
    {_hooksApi=hooks;_image=image;_bridge=bridge;_log=log;}
    private NativeLifetimeMemory Own(int size,bool near=false)
    {var m=new NativeLifetimeMemory(size,near?_image:0);_memory.Add(m);return m;}
    private byte[] Read(long address,int length)=>CheckedNativeRead.Read(address,length);
    private void Write(long address,byte[] bytes)=>NativeLifetimeMemory.WriteProtected(address,bytes);
    private ushort U16(long address)=>BinaryPrimitives.ReadUInt16LittleEndian(Read(address,2));
    private long Ptr(long address)=>BinaryPrimitives.ReadInt64LittleEndian(Read(address,8));
    private void Word(long address,int value)=>Write(address,BitConverter.GetBytes(checked((ushort)value)));
    private long Worker(int index)
    {
        if(index is <0 or >20)throw new InvalidDataException("Invalid battle worker.");
        long p=_image+0x1853CE0+index*512;
        if(Read(p+1,1)[0]==255)throw new InvalidDataException("Inactive battle worker.");
        return p;
    }
    private (long Pointer,int Slot) Unit(int index)
    {
        if(index is <0 or >=54)throw new InvalidDataException("Invalid selected menu unit.");
        long p=Ptr(_image+0x1800F50+index*8);
        if(p<=0)throw new InvalidDataException("Selected menu unit is null.");
        return (p,ChemistActionBindings.SerializedSlot(Read(p,0x2E)));
    }
    private uint Learned(long p,int slot,bool battle=false)
        =>ChemistActionBindings.Learned(Read(p+(battle?0xA5:0x7E),3),_session.ExtraLearning(slot));
    private uint BattleLearned(long p)
    {
        if((Read(p+6,1)[0]&0x20)!=0)return ChemistActionBindings.AllMask; // native NPC bypass
        return Learned(p,Read(p+2,1)[0],true);
    }
    private long Call(int rva,long a=0,long b=0,long c=0,long d=0)
    {
        if(!_helpers.TryGetValue(rva,out var f))throw new InvalidOperationException("Unpreflighted helper.");
        return _bridge.IsGameplayObserver?_bridge.CallOnNativeOwner((nint)(_image+rva),a,b,c,d):f(a,b,c,d);
    }
    private void HelperAt(int rva)
    {Guard(rva);_helpers.Add(rva,Marshal.GetDelegateForFunctionPointer<Helper>((nint)(_image+rva)));}
    private long CallGuiHelper(int rva,long a,long b,long c,long d)
    {
        // Native component initialization uses its original virtual method.
        // Preflight that executable RVA before using the same owner transport.
        if(!_helpers.ContainsKey(rva))HelperAt(rva);
        return Call(rva,a,b,c,d);
    }
    private void Guard(int rva)
    {
        var section=_pe!.PEHeaders.SectionHeaders.Single(s=>s.VirtualAddress<=rva && rva+14<=s.VirtualAddress+s.SizeOfRawData);
        byte[] bytes=_exe.AsSpan(section.PointerToRawData+rva-section.VirtualAddress,14).ToArray();
        if(!Read(_image+rva,14).SequenceEqual(bytes))throw new InvalidDataException($"Conflicting native hook at {rva:X}; integration refused.");
    }
    private void Bind(int rva,uint stack,NativeContextBridge.Observer observer,bool isolateSave=false,bool selectionGate=false)
    {
        Guard(rva);
        long observerAddress=(long)_bridge.ReserveAddress();
        var gate=selectionGate?Own(4096):null;
        var h=_hooksApi.CreateHook<Native>((void*)(gate?.Address??observerAddress),_image+rva);
        // Own routes catch before the generic bridge guard: extra IDs must
        // never fall back to an unbounded native inventory/formula branch.
        NativeContextBridge.Observer safe=f=>
        {
            if(!_active)return;
            try{observer(f);}
            catch(Exception ex){_session.Block($"{rva:X}: {ex.Message}");if(f->Phase==0)f->Skip(-1);else f->ReplaceResult(-1);}
        };
        _bridge.Bind(h.OriginalFunctionAddress,stack,safe,ex=>_session.Block(ex.Message),isolateSave);_hooks.Add(h);
        if(gate is not null){byte[] code=NativeChemistAiAvailability.BuildSelectionGate(h.OriginalFunctionAddress,observerAddress);gate.Write(0,code);gate.ExecutableWithVolatilePushes(code.Length);}
    }
    private void Leaf(int rva,Func<long,byte[]> build,bool volatileFrame=false)
    {
        Guard(rva);var code=Own(4096);
        var hook=_hooksApi.CreateHook<Native>((void*)code.Address,_image+rva);
        byte[] bytes=build(hook.OriginalFunctionAddress);code.Write(0,bytes);
        if(volatileFrame)code.ExecutableWithVolatilePushes(bytes.Length);else code.Executable();_hooks.Add(hook);
    }

    public void Install(string executable,string modDirectory,Func<string> profileKey,string? ownedFixtureSaveRoot=null)
    {
        if(_pe is not null)throw new InvalidOperationException("Host already attempted installation.");
        _exe=File.ReadAllBytes(executable);
        if(Convert.ToHexString(SHA256.HashData(_exe))!=ImageHash)throw new NotSupportedException("Unsupported game version.");
        ChemistRuntimePreparation.Prepare(_log);
        _pe=new PEReader(new MemoryStream(_exe,false));
        _stock=Own(NativeItemStockLeaf.StateLength);
        _common=Own(8192,true);_common.Write(0,Read(_image+0x787F80,4096));
        _shared=Own(2048,true);_shared.Write(0,Read(_image+0x1811470,512));
        _actions=Own(ChemistActionBindings.ActionCount*20);
        // Relocate only the bounded visual triplets; adjacent 6732F4 stat
        // growth and 673300 shape metadata remain owned by the original game.
        _visual=Own(3072,true);_visual.Write(0,Read(_image+0x672DA0,0x554));
        var itemState=Own(NativeItemCommonLeaf.StateLength);
        var medState=Own(NativeMedicineCatalog.StateLength);
        HelperAt(0x2B8C44);
        foreach(int s in ChemistActionBindings.ExtraSlots)
        {
            var a=ChemistActionBindings.TestActions[s];int id=ChemistActionBindings.Item(s);
            // Keep the item throw/end bytes, but never request the Potion's
            // preparation/casting pose. Selector 19 is a verified native no-op.
            _visual.Write(a.AbilityId*3,[ChemistExtraEffects.NoCastingVisualSelector,1,0]);
            var item=Own(12);item.Write(0,File.ReadAllBytes(Path.Combine(modDirectory,"native",a.Key+".item-common.bin")));
            // The world projectile uses the original sixteen-pixel atlas,
            // independently of the expanded enhanced UI textures.
            item.Write(0,Read(Call(0x2B8C44,ChemistExtraEffects.BottleModel(s)),2));
            item.Write(10,[ChemistExtraEffects.ShopProgress(s)]);
            var medicine=Own(3);medicine.Write(0,ChemistExtraEffects.Elemental(s)?[107,5,0]:[56,0,ChemistExtraEffects.Status(s)]);
            if(U16(item.Address+8)!=ChemistExtraEffects.Price(s) || Read(item.Address+5,1)[0]!=0x22)throw new InvalidDataException("Unexpected extra item record: "+a.Key);
            itemState.Write(16+id*8,BitConverter.GetBytes(item.Address));
            medState.Write(16+id,[1]);medState.Write(NativeMedicineCatalog.SecondaryPointersOffset+id*8,BitConverter.GetBytes(medicine.Address));
            _stock.Write(16+id,[1]);
        }
        var abilityState=Own(NativeAbilityCatalog.StateLength);
        foreach(int rva in new[]{0x285744,0x2BD05C,0xF4BCC,0x30FFC4,0x30E2F0,0x2B8E04,0x2CBED4,0x27F974,0x280B40,0x282A7C,0x312ED0})HelperAt(rva);
        for(int slot=0;slot<ChemistActionBindings.ActionCount;slot++)
        {
            var a=ChemistActionBindings.TestActions[slot];
            // Venom targets/status AI derives from Poison, not Potion's ally-only AI.
            byte[] common=Read(_image+0x787F80+(slot>=4?ChemistExtraEffects.Template(slot):a.AbilityId)*8,8);
            BinaryPrimitives.WriteUInt16LittleEndian(common,a.JpCost);common[3]=0x41;
            _common.Write(a.AbilityId*8,common);
            byte status=slot>=4?ChemistExtraEffects.Status(slot):Read(Call(0x2B8E04,ChemistActionBindings.Item(slot))+2,1)[0];
            byte[] action=ChemistBattleData.Action(status);
            if(slot>=4)action[7]=ChemistExtraEffects.Element(slot);
            _actions.Write(slot*20,action);
            abilityState.Write(16+a.AbilityId,[1]);
            abilityState.Write(NativeAbilityCatalog.CommonPointersOffset+a.AbilityId*8,BitConverter.GetBytes(_common.Address+a.AbilityId*8));
            abilityState.Write(NativeAbilityCatalog.ActionPointersOffset+a.AbilityId*8,BitConverter.GetBytes(_actions.Address+slot*20));
        }
        // Preserve native medicine/common AI/visual identities. Each variant
        // needs its own status record (Remedy is not every antidote); only
        // paid learning is shared with its group. No save bits are allocated.
        _medicineInput=Own(160);
        foreach(ushort ability in ChemistMedicineFamilies.All)
        {
            int group=ChemistMedicineFamilies.Group(ability);
            if(ability==ChemistActionBindings.TestActions[group].AbilityId)continue;
            var variant=Own(20);
            byte status=Read(Call(0x2B8E04,ChemistMedicineFamilies.Item(ability))+2,1)[0];
            variant.Write(0,ChemistBattleData.Action(status));
            abilityState.Write(16+ability,[1]);
            abilityState.Write(NativeAbilityCatalog.CommonPointersOffset+ability*8,BitConverter.GetBytes(_common.Address+ability*8));
            abilityState.Write(NativeAbilityCatalog.ActionPointersOffset+ability*8,BitConverter.GetBytes(variant.Address));
        }
        var commandState=Own(24);var row=Own(48);
        // Original passive slots use the native decoder before any detour.
        HelperAt(0x275860);
        for(int s=0;s<24;s++)row.Write(s*2,BitConverter.GetBytes((ushort)(s<ChemistActionBindings.ActionCount?ChemistActionBindings.TestActions[s].AbilityId:s<16?0:Call(0x275860,6,s))));
        commandState.Write(8,BitConverter.GetBytes(row.Address));commandState.Write(16,BitConverter.GetBytes(_image+0x3D1A1C0));
        var registry=new ExpandedSaveRegistry(ChemistActionBindings.ExtraKeys,ChemistActionBindings.ExtraKeys);
        var store=new ExpandedSaveStore(ownedFixtureSaveRoot??Path.Combine(Path.GetDirectoryName(executable)!,"FFTModLoader.Runtime","ExpandedSaves","ReworkedChemist"),registry);
        var commit=new NativePartySaveCommit(registry,store);
        var aiLearning=Own(NativeChemistAiAvailability.LearningSize);
        _session=new ChemistLiveSession(_image,_stock.Address,commit,Read,Write,_log,profileKey,aiLearning.Address);
        var saveEvents=new BoundPartySaveObserver(profileKey,commit,_session.Frozen,_session.Block,_log,battleSnapshot:_session.SnapshotBattlePacket,manual:_session.ManualFrozen);

        var writes=Relocations(Path.Combine(modDirectory,"native","playable-relocation-plan.json"));
        // Command6 is a full-width consumable action, never an item-byte path.
        AddWrite(writes,0x67E016,[1],[0]);
        AddWrite(writes,0x309B85,BitConverter.GetBytes(368),BitConverter.GetBytes(1024));
        AddWrite(writes,0x309CFB,BitConverter.GetBytes(368),BitConverter.GetBytes(1024));
        AddWrite(writes,0x309DAD,[105],[106]);

        Leaf(0x2847F8,p=>NativeItemStockLeaf.Build(p,_stock.Address));
        Leaf(0x2B8C44,p=>NativeItemCommonLeaf.Build(p,itemState.Address,_image));
        Leaf(0x2B8BCC,p=>NativeMedicineCatalog.BuildRangeIndex(p,medState.Address,_image));
        Leaf(0x2B8E04,p=>NativeMedicineCatalog.BuildSecondaryRecord(p,medState.Address,_image,_image+0x80FB70));
        Leaf(0x2B8C0C,p=>NativeMedicineCatalog.BuildRangeMinimum(p,medState.Address,_image));
        Leaf(0x275A78,p=>NativeAbilityCatalog.BuildAddress(p,abilityState.Address));
        Leaf(0x2BB060,p=>NativeAbilityCatalog.BuildSecondary(p,abilityState.Address));
        Leaf(0x275860,p=>NativeCommandCatalog.BuildSlot(p,commandState.Address));
        Leaf(0x275980,p=>NativeCommandCatalog.BuildActionArray(p,commandState.Address));
        Leaf(0x288E54,p=>NativeShopListAppend.Build(p,_stock.Address,_shared.Address,1024,ChemistActionBindings.ExtraSlots.Select(ChemistActionBindings.Item).ToArray(),
            _image+0x2E9898,ChemistActionBindings.ExtraSlots.Select(ChemistExtraEffects.ShopProgress).ToArray()));

        Bind(0x2867BC,1,Menu);
        Bind(0x2866E4,0,Commands);
        Bind(0x2B9AD0,0,Purchase);
        Bind(0x288B94,2,Inventory);
        Bind(0x36B71C,0,f=>{if(f->Phase==0&&!_session.CanMutate)f->Skip(0);});
        Bind(0x30E368,4,f=>BattleList(f,true));
        Bind(0x30DD8C,4,f=>BattleList(f,false));
        Leaf(0x3170CC,p=>NativeChemistAiAvailability.BuildLearned(p,_image,aiLearning.Address),volatileFrame:true);
        Bind(0x281488,0,CommitAction);
        Bind(0x320504,0,SelectAction,selectionGate:true);
        Bind(0x2CEEE8,0,f=>{if(f->Phase==1)_session.CaptureSerializedWork();},isolateSave:true);
        Bind(0x2CF768,0,Load,isolateSave:true);
        Bind(0x284500,0,f=>{if(f->Phase==1)_session.ResetParty((int)f->Arguments[0]==0);},isolateSave:true);
        // Enhanced status uses a separate numeric filter and eight-byte pair
        // formatter. The legacy GetAbilityList hook is not on that path.
        Bind(0x28A370,0,EnhancedActionFilter);
        Bind(0x28A474,2,EnhancedActionPairs);
        // Both preset and user ordering can discard IDs outside 1..260.
        // Preserve only the registered extra already present in this owned list.
        Bind(0x285DF0,0,PreserveSortedExtra);
        Bind(0x286228,0,PreserveSortedExtra);
        Bind(0x2B8EBC,0,f=>{if(f->Phase==0 && ChemistActionBindings.IsExtraItem((ushort)f->Arguments[0]))f->Skip(1);});
        // Human target selection does not use SetTmp_UA. Both native wrappers
        // reject action IDs >=368 before reaching the already extended getter.
        // Route only our fifteen registered command6 actions to their original
        // native map builders, preserving the wrappers' return conventions.
        Bind(0x27FE20,0,f=>TargetMap(f,false));
        Bind(0x27FFE8,0,f=>TargetMap(f,true));
        // Enhanced manual/field-autosave load consumes the selected SaveWork.
        Bind(0x21B0E8,0,EnhancedLoad,isolateSave:true);
        Bind(0x2181EC,0,f=>
        {
            if(f->Phase==0)
            {
                long controller=Ptr(_image+0x3CD9EE0);byte[] inner=[];
                if(controller>0)
                {
                    int offset=(int)f->Arguments[1] switch {2=>0x3D5410,3=>0x3D5698,4=>0x3D5920,_=>0x3D5188};
                    long pointer=Ptr(controller+offset),size=Ptr(controller+offset+8);
                    if(pointer>0 && size>=0x154+0xA31D0 && size<=0x180000)inner=Read(pointer,(int)size);
                }
                _loads.Value![f->EntryStack]=inner;
            }
            else
            {
                if(!_loads.Value!.Remove(f->EntryStack,out var inner))throw new InvalidDataException("Missing selected autosave snapshot.");
                if((int)f->OriginalResult==0)_session.AfterAutosaveLoad(_session.BeforePartyLoad());
                else if((int)f->OriginalResult==1)_session.AfterBattleAutosaveLoad(inner,_session.BeforePartyLoad());
            }
        },isolateSave:true);
        // In-battle Continue copies the serialized party through this route.
        Bind(0x279BF4,0,f=>
        {
            if(f->Phase==1 && (int)f->Arguments[1]==1)
            {
                byte[] actual=Read(f->Arguments[0],NativePartySaveCommit.RecordsSize);
                if(!actual.SequenceEqual(Read(_image+0x11A7D10,actual.Length)))throw new InvalidDataException("Continue did not copy its selected party.");
                if(_session.AfterBattlePartyLoad(actual))return;
                byte[] work=_session.BeforePartyLoad();
                if(work.AsSpan(NativePartySaveCommit.RecordsOffset,NativePartySaveCommit.RecordsSize)
                    .SequenceEqual(actual))
                    _session.AfterPartyLoad(work);
                else _log("[Save] Continuação: bloco auxiliar não corresponde ao SaveWork; estado extra mantido.");
            }
        },isolateSave:true);
        HelperAt(0x268E04);
        Bind(0x269988,0,ThrowBottle);
        Bind(0x0C4420,0,f=>{if(f->Phase==1)_session.CaptureManualWork(checked((int)f->Arguments[1]));},isolateSave:true);
        // Completion is entirely native. Do not detour 2059AC only to log
        // endings: removes two observer transitions, including self-Haste.
        Leaf(0x319D74,NativeAbilityCatalog.BuildConsumableEffectClassification);
        var bottleCoordinates=WorldBottleAtlas(modDirectory);
        Leaf(0x22A9E4,p=>NativeConsumableVisuals.BuildBottleUv(p,_image,bottleCoordinates.Address));
        Leaf(0x2E1604,p=>NativeConsumableVisuals.BuildBottleUv(p,_image,bottleCoordinates.Address));
        Leaf(0x0E512C,p=>NativeConsumableVisuals.BuildActionCaption(p,_image));
        _mapNameDiagnostic=Own(32);
        Leaf(0x0F8474,p=>NativeConsumableVisuals.BuildMapBubbleLookup(p,_image,_mapNameDiagnostic.Address));
        var enhancedGlyphs=Own(11*512);
        using(var world=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(modDirectory,"native","world-bottle-atlas.json"))))
        {
            int i=0;
            foreach(var entry in world.RootElement.GetProperty("Entries").EnumerateArray())
                enhancedGlyphs.Write(i++*512,NativeConsumableVisuals.EnhancedGlyph(Convert.FromHexString(entry.GetProperty("GlyphHex").GetString()!)));
        }
        // JobExpansion already detours the shared decoder. Adapt only this
        // Enhanced cache callsite and call its existing entry chain unchanged.
        const int decodeSite=0x4165F5,decodeTarget=0x3D25D8;
        var enhancedDecode=Own(4096,true);
        byte[] enhancedCode=NativeConsumableVisuals.BuildEnhancedBottleSource(_image+decodeTarget,_image,enhancedGlyphs.Address);
        enhancedDecode.Write(0,enhancedCode);
        int savedFrame=enhancedCode.AsSpan().IndexOf(new byte[]{0x53,0x56,0x57,0x48,0x83,0xEC,0x20});
        enhancedDecode.ExecutableWithSavedFrame(savedFrame,enhancedCode.Length-14); // fallback tail jump has no frame
        byte[] decodeBefore=new byte[5],decodeAfter=new byte[5];decodeBefore[0]=decodeAfter[0]=0xE8;
        BinaryPrimitives.WriteInt32LittleEndian(decodeBefore.AsSpan(1),decodeTarget-decodeSite-5);
        BinaryPrimitives.WriteInt32LittleEndian(decodeAfter.AsSpan(1),checked((int)(enhancedDecode.Address-(_image+decodeSite+5))));
        AddWrite(writes,decodeSite,decodeBefore,decodeAfter);
        ConsumableVisualRouting(writes);
        // The approved Dark Knight runtime already hooks StartEffect. Adapt
        // only its audited ability caller, then call the existing entry chain.
        // Never replace or bypass that other mod's effect hook.
        VisualEffect(writes);
        // Formula binding uses a private table pointer, never detours an
        // existing formula used by unrelated jobs.
        Formula(writes);
        _aiCandidates=Own(ChemistAiActions.Rows*ChemistAiActions.Capacity*4,true);
        byte[] oldAi=Read(_image+NativeChemistAiExpansion.SourceRva,16*34*4);
        byte[] ai=new byte[16*64*4];
        for(int i=0;i<16*64;i++)ai[i*4+2]=255; // flags start zero, not spurious native valid/special bits
        for(int r=0;r<16;r++)oldAi.AsSpan(r*34*4,34*4).CopyTo(ai.AsSpan(r*64*4));
        _aiCandidates.Write(0,ai);
        writes.AddRange(NativeChemistAiExpansion.Plan(_image,_aiCandidates.Address,
            Path.Combine(modDirectory,"native","chemist-ai-expansion.json"),Read));
        HelperAt(0x22F868);
        // A real fourth native component, not a replacement or drawing strip.
        foreach(int rva in NativeMedicineFourthMenu.Helpers)HelperAt(rva);
        var medicineName=Own(320);medicineName.Write(0,"ActionList\0"u8.ToArray());
        medicineName.Write(16,"ActionMenu\0"u8.ToArray());
        medicineName.Write(32,"ChemistMedicineChild\0"u8.ToArray());
        medicineName.Write(64,"ListAction\0"u8.ToArray());
        medicineName.Write(80,"TextTitle\0"u8.ToArray());
        medicineName.Write(112,"Potions\0"u8.ToArray());
        medicineName.Write(128,"Ethers\0"u8.ToArray());
        medicineName.Write(144,"Remedys\0"u8.ToArray());
        medicineName.Write(160,"Show\0"u8.ToArray());medicineName.Write(176,"Hide\0"u8.ToArray());
        for(int i=0;i<6;i++)medicineName.Write(192+i*16,System.Text.Encoding.UTF8.GetBytes($"BattleMenu0{i+1}\0"));
        _medicineFourth=new(_image,medicineName.Address,CallGuiHelper,_log);
        _potionScopes=Own(NativePotionWindowLifecycle.Slots*NativePotionWindowLifecycle.RecordSize);
        HelperAt(0x22B9F4);
        var emptyConfirm=Own(4096,true);
        emptyConfirm.Write(0,NativePotionWindowLifecycle.BuildEmptyConfirmGuard(_image,_potionScopes.Address));emptyConfirm.Executable();
        const int emptySite=0x22BAD7,emptyEntry=0x22B9F4;
        byte[] emptyBefore=new byte[5],emptyAfter=new byte[5];emptyBefore[0]=emptyAfter[0]=0xE8;
        BinaryPrimitives.WriteInt32LittleEndian(emptyBefore.AsSpan(1),emptyEntry-emptySite-5);
        BinaryPrimitives.WriteInt32LittleEndian(emptyAfter.AsSpan(1),checked((int)(emptyConfirm.Address-(_image+emptySite+5))));
        AddWrite(writes,emptySite,emptyBefore,emptyAfter);
        var potionLifecycle=Own(4096,true);
        potionLifecycle.Write(0,NativePotionWindowLifecycle.Build(_image,_potionScopes.Address));potionLifecycle.Executable();
        const int potionKillSite=0x231C65,potionKillTarget=0x2F7EC8;
        byte[] potionBefore=new byte[5],potionAfter=new byte[5];potionBefore[0]=potionAfter[0]=0xE8;
        BinaryPrimitives.WriteInt32LittleEndian(potionBefore.AsSpan(1),potionKillTarget-potionKillSite-5);
        BinaryPrimitives.WriteInt32LittleEndian(potionAfter.AsSpan(1),checked((int)(potionLifecycle.Address-(_image+potionKillSite+5))));
        AddWrite(writes,potionKillSite,potionBefore,potionAfter);
        HelperAt(0x2F7DE4);
        HelperAt(0x2F7F4C); // wait for native validation/target before returning to the action controller
        var potionTarget=Own(4096,true);
        potionTarget.Write(0,NativePotionWindowLifecycle.BuildTargetDeferral(_image,_potionScopes.Address));potionTarget.Executable();
        const int targetSite=0x22BB5B,targetEntry=0x2F7DE4;
        byte[] targetBefore=new byte[5],targetAfter=new byte[5];targetBefore[0]=targetAfter[0]=0xE8;
        BinaryPrimitives.WriteInt32LittleEndian(targetBefore.AsSpan(1),targetEntry-targetSite-5);
        BinaryPrimitives.WriteInt32LittleEndian(targetAfter.AsSpan(1),checked((int)(potionTarget.Address-(_image+targetSite+5))));
        AddWrite(writes,targetSite,targetBefore,targetAfter);
        Bind(0x22F868,0,PotionWindow);
        foreach(var patch in writes)
            if(!Read(patch.InstructionAddress,patch.ExpectedInstruction.Length).SequenceEqual(patch.ExpectedInstruction) ||
               !Read(patch.Address,patch.Before.Length).SequenceEqual(patch.Before))throw new InvalidDataException("Native patch changed during preflight.");
        Bind(0x316D24,0,AiCommand);
        var aiEligibility=Own(NativeChemistAiAvailability.CatalogSize);
        // Extra common records are already allocated, but their detour is not
        // active yet. Use their owned pointer rather than a native out-of-range lookup.
        aiEligibility.Write(0,NativeChemistAiAvailability.Catalog(item=>ChemistActionBindings.IsExtraItem(item)
            ?Ptr(itemState.Address+16+item*8):Call(0x2B8C44,item)));
        // This is the per-action/per-tile hot path. Evaluate read-only stock,
        // paid learning and NPC supply in native code, then tail-call the exact
        // original target policy. Keep the same hook index/order as before.
        Leaf(0x38B34C,p=>NativeChemistAiAvailability.Build(p,_image,aiEligibility.Address,_stock.Address,aiLearning.Address),volatileFrame:true);
        try
        {
            _save=new CompiledNativeSaveHooks(saveEvents);_save.Install(_hooksApi,_bridge,(nint)_image,executable);
            foreach(var hook in _hooks)hook.Activate();
            NativeTableRelocation.Apply(writes,Read,Write);
            foreach(var state in new[]{_stock,itemState,medState,abilityState,commandState})state.Write(0,[1]);
            _active=true;
            _log("[Conteúdo] REWORKED CHEMIST v0.2.43: fechamento completo nativo de Items limpa seleção/cursor sem desativar a lista interna. Falha anterior reproduzida no código nativo: fechamento recursivo desativava ListAction e a reabertura após cancelar alvo não restaurava essa seleção.48 ciclos de escolher/cancelar alvo/voltar e192 saídas do submenu mais192 saídas do menu anterior verificados em fixtures. Estoque, quatro janelas, progressão, dano5 +15%HPmáximo, IA, interceptação e saves preservados; confirmação em batalha pendente.");
        }
        catch
        {
            _active=false;
            _save?.Disable();
            foreach(var hook in _hooks.AsEnumerable().Reverse())if(hook.IsHookActivated)hook.Disable();
            throw;
        }
    }

    private NativeLifetimeMemory WorldBottleAtlas(string modDirectory)
    {
        using var doc=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(modDirectory,"native","world-bottle-atlas.json")));
        var root=doc.RootElement;
        if(root.GetProperty("Version").GetInt32()!=1 || root.GetProperty("Width").GetInt32()!=256 ||
           root.GetProperty("Height").GetInt32()!=235 || root.GetProperty("BodyYOffset").GetInt32()!=21 ||
           !root.GetProperty("OriginalGlyphsPreserved").GetBoolean() || !root.GetProperty("OriginalClutsPreserved").GetBoolean())
            throw new InvalidDataException("Unsupported world bottle atlas.");
        var entries=root.GetProperty("Entries").EnumerateArray().ToArray();
        if(entries.Length!=ChemistActionBindings.ExtraCount)throw new InvalidDataException("Incomplete world bottles.");
        var coordinates=Own(64);
        for(int i=0;i<entries.Length;i++)
        {
            uint packed=NativeConsumableVisuals.BottleCoordinates(i);var e=entries[i];
            if(e.GetProperty("ItemId").GetInt32()!=261+i || e.GetProperty("Key").GetString()!=ChemistActionBindings.TestActions[4+i].Key ||
               e.GetProperty("U").GetInt32()!=(packed&255) || e.GetProperty("V").GetInt32()!=((packed>>8)&255) || e.GetProperty("Clut").GetInt32()!=(packed>>16))
                throw new InvalidDataException("World bottle identity mismatch.");
            coordinates.Write(i*4,BitConverter.GetBytes(packed));
        }
        var resources=root.GetProperty("Resources").EnumerateObject().ToArray();
        string[] expected={"en","de","fr","ja"};
        if(resources.Length!=4)throw new InvalidDataException("Incomplete localized item atlas.");
        foreach(string locale in expected)
        {
            string path="FFTIVC/data/enhanced/fftpack/tex/item/item_01."+locale+".tex";
            var record=root.GetProperty("Resources").GetProperty(path);byte[] data=File.ReadAllBytes(Path.Combine(modDirectory,path));
            if(data.Length!=0x7580 || Convert.ToHexString(SHA256.HashData(data))!=record.GetProperty("Sha256").GetString())
                throw new InvalidDataException("Changed localized world atlas: "+locale);
            for(int i=0;i<11;i++)
            {
                byte[] palette=Convert.FromHexString(entries[i].GetProperty("PaletteHex").GetString()!);
                byte[] glyph=Convert.FromHexString(entries[i].GetProperty("GlyphHex").GetString()!);
                if(palette.Length!=32 || glyph.Length!=128 || !data.AsSpan(i/3*128+i%3*32,32).SequenceEqual(palette))
                    throw new InvalidDataException("World CLUT mismatch.");
                for(int y=0;y<16;y++)if((glyph[y*8+7]&0xF0)!=0)
                    throw new InvalidDataException("Bottle edge column is not transparent; unsafe UV width.");
                for(int y=0;y<16;y++)if(!data.AsSpan((i*16+y)*128+120,8).SequenceEqual(glyph.AsSpan(y*8,8)))
                    throw new InvalidDataException("World glyph mismatch.");
            }
        }
        return coordinates;
    }

    private List<GuardedNativeWrite> Relocations(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllBytes(path));
        if(document.RootElement.GetProperty("ExecutableSha256").GetString()!=ImageHash)throw new InvalidDataException("Relocation manifest version differs.");
        if(!document.RootElement.GetProperty("Installable").GetBoolean() || document.RootElement.GetProperty("ReviewedFor").GetString()!="FullChemist15Actions")throw new InvalidDataException("Relocation plan has not been audited for this test.");
        var seen=new HashSet<string>();
        var writes=new List<GuardedNativeWrite>();
        foreach(var table in document.RootElement.GetProperty("Tables").EnumerateArray())
        {
            string name=table.GetProperty("Name").GetString()!;
            if(!seen.Add(name))throw new InvalidDataException("Duplicate relocation table.");
            long dest=name=="AbilityData"?_common.Address:name=="SharedUiList"?_shared.Address:name=="AbilityVisual"?_visual.Address:throw new InvalidDataException("Unexpected relocation table.");
            var operands=new List<NativeTableOperand>();
            foreach(var o in table.GetProperty("VerifiedOperands").EnumerateArray())
                operands.Add(new(Convert.ToUInt32(o.GetProperty("InstructionRva").GetString(),16),Convert.FromHexString(o.GetProperty("ExpectedInstruction").GetString()!),o.GetProperty("DisplacementOffset").GetInt32(),o.GetProperty("FieldOffset").GetInt32(),Enum.Parse<NativeOperandEncoding>(o.GetProperty("Encoding").GetString()!)));
            foreach(var o in table.GetProperty("PropagatedReferences").EnumerateArray())
            {
                byte[] expected=Convert.FromHexString(o.GetProperty("ExpectedInstruction").GetString()!);
                if(!Read(_image+Convert.ToUInt32(o.GetProperty("InstructionRva").GetString(),16),expected.Length).SequenceEqual(expected))throw new InvalidDataException("Propagated consumer changed.");
                if(o.GetProperty("OriginRvas").GetArrayLength()==0)throw new InvalidDataException("Missing pointer provenance.");
            }
            foreach(var o in table.GetProperty("ProvenanceInstructions").EnumerateArray())
            {
                byte[] expected=Convert.FromHexString(o.GetProperty("ExpectedInstruction").GetString()!);
                if(!Read(_image+Convert.ToUInt32(o.GetProperty("InstructionRva").GetString(),16),expected.Length).SequenceEqual(expected))throw new InvalidDataException("Pointer provenance instruction changed.");
            }
            writes.AddRange(NativeTableRelocation.Plan(_image,Convert.ToUInt32(table.GetProperty("SourceRva").GetString(),16),dest,table.GetProperty("ExpandedBytes").GetInt32(),operands,Read));
        }
        if(seen.Count!=3)throw new InvalidDataException("Incomplete relocation table set.");
        return writes;
    }
    private void AddWrite(List<GuardedNativeWrite> writes,int rva,byte[] before,byte[] after)
    {
        if(!Read(_image+rva,before.Length).SequenceEqual(before))throw new InvalidDataException($"Patch precondition differs: {rva:X}.");
        writes.Add(new(_image+rva,before,after,_image+rva,before));
    }

    private void Menu(NativeContextBridge.Frame* f)
    {
        int mode=(int)f->Arguments[4];
        if(f->Phase!=1 || !NativeActionMenu.HandlesChemistActionList((ushort)f->Arguments[1],(int)f->Arguments[2],mode))return;
        var unit=Unit((short)f->Arguments[0]);
        uint bits=Learned(unit.Pointer,unit.Slot);
        Span<ushort> result=stackalloc ushort[17];
        int count=_menu.Build(mode==0?ActionMenuView.All:mode==2?ActionMenuView.Learn:ActionMenuView.UnlearnedCount,
            bits,_session.CanMutate?U16(unit.Pointer+0xCC):0,result);
        if(mode!=3)
        {
            if(f->Arguments[3]<=0)throw new InvalidDataException("Null action-list destination.");
            byte[] output=new byte[(count+1)*2];
            for(int i=0;i<=count;i++)BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(i*2),result[i]);
            Write(f->Arguments[3],output);
        }
        f->ReplaceResult(count);
    }
    private void EnhancedActionFilter(NativeContextBridge.Frame* f)
    {
        if(f->Phase!=1 || (ushort)f->Arguments[0]!=6 || (int)f->Arguments[1]!=0)return;
        if(f->OriginalResult!=_image+0x3D1A1C0)throw new InvalidDataException("Enhanced action scratch identity differs.");
        // The native filter has just zeroed 513 because it exceeds 421.
        // Do not change its R/S/M bounds, other commands or passive cells.
        foreach(int s in ChemistActionBindings.ExtraSlots)Word(f->OriginalResult+s*2,ChemistActionBindings.TestActions[s].AbilityId);
    }
    private void EnhancedActionPairs(NativeContextBridge.Frame* f)
    {
        if(f->Phase!=0 || (ushort)f->Arguments[2]!=75)return;
        ushort[] row=new ushort[24];byte[] raw=Read(f->Arguments[0],48);
        for(int i=0;i<24;i++)row[i]=BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i*2));
        // Passive-only and category=15 mixed lists stay native. Their action
        // entry remains filtered; only the action tab uses the owned adapter.
        if(!ChemistUiPairs.IsChemistRow(row) || row.Skip(ChemistActionBindings.ActionCount).Any(id=>id!=0))return;
        int mode=(int)f->Arguments[5];if(mode is <0 or >8)return;
        var unit=Unit((short)f->Arguments[1]);
        if(U16(unit.Pointer+0x3E)!=0)return; // native monster three-action rule
        bool special=Call(0x2CBED4,U16(unit.Pointer+0x24))!=0 && (Read(unit.Pointer+0x70,1)[0]&0x20)!=0;
        if(special && U16(unit.Pointer+0x24)!=75)return;
        var result=ChemistUiPairs.Build(row,mode,Learned(unit.Pointer,unit.Slot),
            Read(unit.Pointer+0x7E,3),_session.CanMutate?U16(unit.Pointer+0xCC):0,special);
        f->Skip(result.Count);
        if(result.Bytes.Length!=0)Write(f->Arguments[4],result.Bytes);
        if(mode is 2 or 4 && Interlocked.Exchange(ref _menuReported,1)==0)
            _log("[Menu Enhanced] Chemist: 15 habilidades enviadas para aquisição; IDs e custos individuais verificados.");
    }
    private void PreserveSortedExtra(NativeContextBridge.Frame* f)
    {
        if(f->Arguments[1]!=_shared.Address)return;
        if(f->Phase==0)
        {
            var present=new HashSet<ushort>();
            for(int i=0;i<1024;i++)
            {
                ushort entry=U16(_shared.Address+i*2);
                if(entry==65535){if(!_sortExtras.TryAdd(f->EntryStack,present))throw new InvalidDataException("Duplicate owned sort frame.");return;}
                ushort id=(ushort)(entry&1023);if(ChemistActionBindings.IsExtraItem(id))present.Add(id);
            }
            throw new InvalidDataException("Unterminated owned item list before sort.");
        }
        if(!_sortExtras.TryRemove(f->EntryStack,out var hadExtra)||hadExtra.Count==0)return;
        int n=0;var retained=new HashSet<ushort>();
        for(;n<1024;n++)
        {
            ushort entry=U16(_shared.Address+n*2);if(entry==65535)break;
            retained.Add((ushort)(entry&1023));
        }
        if(n>=1023)throw new InvalidDataException("Owned item sort exceeded capacity.");
        foreach(ushort id in hadExtra.Order())if(!retained.Contains(id))
        {if(n>=1023)throw new InvalidDataException("Extra sort capacity exceeded.");Word(_shared.Address+n*2,id);n++;}
        Word(_shared.Address+n*2,65535);
        f->ReplaceResult(n);
        if(Interlocked.Exchange(ref _shopReported,1)==0)
            _log("[Lista de itens] Novos frascos/essências preservados individualmente após ordenação.");
    }
    private void Commands(NativeContextBridge.Frame* f)
    {
        if(f->Phase!=1)return;
        var unit=Unit((short)f->Arguments[0]);
        if(U16(unit.Pointer+0x24)==75 || _session.ExtraLearning(unit.Slot)==0)return;
        int count=(int)f->OriginalResult;
        if(count is <0 or >22)throw new InvalidDataException("Unknown native command-list capacity.");
        long output=f->Arguments[1];
        byte[] list=Read(output,(count+1)*2);
        if(U16(output+count*2)!=65535)throw new InvalidDataException("Command-list terminator differs.");
        for(int i=0;i<count;i++)if(BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(i*2))==6)return;
        Word(output+count*2,6);Word(output+(count+1)*2,65535);f->ReplaceResult(count+1);
    }
    private void Purchase(NativeContextBridge.Frame* f)
    {
        if(f->Phase!=0 || (ushort)f->Arguments[1]!=75)return;
        ushort entry=(ushort)f->Arguments[0];int slot=ChemistActionBindings.Slot((ushort)(entry&1023));
        if(slot<0)return;f->Skip(0);
        if((entry&~1023)!=0 || !_session.CanMutate)
        {
            _log($"[Aprendizado recusado] habilidade={entry&1023}; flags={entry>>10}; sessão habilitada={_session.CanMutate}; JP e aprendizado não alterados.");
            return;
        }
        int selected=(short)U16(_image+0x1811428);
        var unit=Unit(selected);uint bits=Learned(unit.Pointer,unit.Slot);
        int jp=U16(unit.Pointer+0xCC),cost=ChemistActionBindings.TestActions[slot].JpCost;
        if((bits&(1u<<slot))!=0 || jp<cost)
        {
            _log($"[Aprendizado recusado] unidade={unit.Slot}; habilidade={entry}; JP={jp}; custo={cost}; já aprendida={(bits&(1u<<slot))!=0}.");
            return;
        }
        byte[] previous=Read(unit.Pointer+0x7E,3),next=previous.ToArray();
        if(slot<4)ChemistActionBindings.LearnRetained(next,slot);
        Word(unit.Pointer+0xCC,jp-cost);
        try
        {
            if(slot<4)Write(unit.Pointer+0x7E,next);else _session.LearnExtra(unit.Slot,slot);
            Call(0x285744,selected);Call(0x2BD05C);Call(0xF4BCC,selected,0x81);
        }
        catch {Word(unit.Pointer+0xCC,jp);Write(unit.Pointer+0x7E,previous);if(slot>=4)_session.RestoreExtraLearning(unit.Slot,(bits>>4)&~(1u<<(slot-4)));throw;}
        _log($"[Aprendizado] unidade={unit.Slot}; {ChemistActionBindings.TestActions[slot].Key}: {cost} JP descontados uma vez; JP restante={jp-cost}; confirmado para batalha.");
    }
    private void Inventory(NativeContextBridge.Frame* f)
    {
        if(f->Phase!=1 || (byte)f->Arguments[2]!=7 || f->Arguments[3]!=_shared.Address ||
            (byte)f->Arguments[5] is not (0 or 3))return;
        int count=(int)f->OriginalResult;
        if(count is <0 or >=1023 || U16(_shared.Address+count*2)!=65535)throw new InvalidDataException("Inventory output bounds/terminator differ.");
        var present=Enumerable.Range(0,count).Select(i=>(ushort)(U16(_shared.Address+i*2)&1023)).ToHashSet();
        foreach(int s in ChemistActionBindings.ExtraSlots)if(_session.Stock(s)>0 && !present.Contains(ChemistActionBindings.Item(s)))
        {if(count>=1023)throw new InvalidDataException("Inventory extra capacity exceeded.");Word(_shared.Address+count*2,ChemistActionBindings.Item(s));count++;}
        Word(_shared.Address+count*2,65535);f->ReplaceResult(count);
    }
    private int PotionStock(ushort item)=>Read(_image+0x11A7C00+item,1)[0];
    // entryData's faction bits match native __ActingUnitDataSetup and the
    // currentAction.modified_evtStts stock-vs-reqLevel branch. NPC supplies
    // follow the original game: unlimited eligible items by level, not PartyItem.
    private bool PartyInventory(long worker)=>(Read(worker+5,1)[0]&0x30)==0;
    private bool Stocked(long worker,ushort ability)
    {
        int slot=ChemistMedicineFamilies.CombatSlot(ability);
        if(slot<0 || !_session.CanMutate)return false;
        if(!PartyInventory(worker))
            return Read(Call(0x2B8C44,ChemistAiActions.Item(ability))+11,1)[0]<=Read(worker+0x29,1)[0];
        return (slot<3?PotionStock(ChemistAiActions.Item(ability)):_session.Stock(slot))>0;
    }
    private void AiCommand(NativeContextBridge.Frame* f)
    {
        if(f->Phase!=0 || (int)f->Arguments[1]!=6)return;
        int index=(int)f->Arguments[0],start=(int)f->Arguments[2];long worker=Worker(index);
        int row=Read(_image+0x1873038+index,1)[0];
        f->Skip(start);
        if(row>=16 || start is <0 or >=62 || !_session.CanMutate)return;
        // Clear ONLY our portion; other command sets are written by native
        // SetUACommand into the same widened row. Reserve terminal action/sentinel.
        ushort[] candidates=ChemistAiActions.Available(BattleLearned(worker),a=>Stocked(worker,a));
        if(start+candidates.Length>62)throw new InvalidDataException("AI command bank overflow prevented.");
        foreach(ushort ability in candidates)
        {
            byte[] entry=new byte[4];BinaryPrimitives.WriteUInt16LittleEndian(entry,(ushort)((index<<10)|ability));entry[2]=6;
            _aiCandidates.Write((row*64+start++)*4,entry);
        }
        f->Result=start;
        if(Interlocked.Increment(ref _aiReports)<=32)
            _log($"[IA/Items] trabalhador={index}; inventário={(PartyInventory(worker)?"jogador":"NPC nativo por nível")}; opções={string.Join(",",candidates)}; capacidade=64.");
    }
    private void PotionWindow(NativeContextBridge.Frame* f)
    {
        // Only the player action controller's synchronous list call. Status,
        // help windows, AI and our nested helper calls must remain native.
        if(f->Phase==0)
        {
            if(Ptr(f->EntryStack)!=_image+0x23709D || Read(_image+0x2FD35A5,1)[0]!=6)return;
            long ui=Ptr(_image+0xD40950);int worker=U16(_image+0x7DCF9A);
            int index=BinaryPrimitives.ReadInt32LittleEndian(Read(_image+0xD40948,4));
            long tasks=Ptr(_image+0x783088);
            if(ui>0 && worker<=20 && tasks>0 && index is >=1 and <=16 &&
                _potionWindows.TryAdd(f->EntryStack,(ui,worker,tasks+index*0x800L,index)))
            {
                // ShowSkillsetAbilityList otherwise calls task_killmyself
                // BEFORE its post-observer runs. A post-hook then re-enters
                // a dead fiber while the parent already selects a target.
                long scope=_potionScopes.Address+index*NativePotionWindowLifecycle.RecordSize;
                Write(scope+8,BitConverter.GetBytes(tasks));Write(scope+16,BitConverter.GetBytes(ui));
                Write(scope+48,BitConverter.GetBytes(Ptr(tasks+index*0x800L)));
                Write(scope,BitConverter.GetBytes(1));
            }
            return;
        }
        if(!_potionWindows.TryRemove(f->EntryStack,out var owner))return;
        try
        {
            PotionChoice(owner);
            long scope=_potionScopes.Address+owner.Index*NativePotionWindowLifecycle.RecordSize;
            if(_session.CanMutate && U16(scope+4)==1 && U16(_image+0xD407B8)!=65535)
            {
                int target=BinaryPrimitives.ReadInt32LittleEndian(Read(scope+24,4));
                long function=Ptr(scope+32),window=Ptr(scope+40),tasks=Ptr(scope+8);
                if(target!=owner.Index-1 || function<=0 || (window!=owner.Ui && window!=owner.Ui+0xC60) ||
                   Ptr(window+0x30)!=function || Ptr(owner.Task)!=Ptr(scope+48) ||
                   Ptr(_image+0x783088)!=tasks || Ptr(_image+0xD40950)!=owner.Ui ||
                   BinaryPrimitives.ReadInt32LittleEndian(Read(_image+0xD40948,4))!=owner.Index)
                    throw new InvalidDataException("Deferred Potion target owner changed.");
                // Same native task creation and window association as
                // multiwindow_keyright, only after both menu choices finish.
                Write(owner.Task+0x10,new byte[8]);
                Call(0x2F7DE4,target,function);
                long task=tasks+target*0x800L;
                Write(task,BitConverter.GetBytes(window));Write(task+8,new byte[16]);
                _log($"[Potion/Alvo] escolha concluída; janela={(window-owner.Ui)/0x58}; tarefa={target}; aguardando validação/alvo nativo.");
                Call(0x2F7F4C,target);
                _log("[Potion/Alvo] validação/alvo nativo concluído; devolvendo ao controlador de ações.");
            }
        }
        finally{Write(_potionScopes.Address+owner.Index*NativePotionWindowLifecycle.RecordSize,new byte[NativePotionWindowLifecycle.RecordSize]);}
    }
    private void PotionChoice((long Ui,int Worker,long Task,int Index) owner)
    {
        if(!_session.CanMutate)return;
        long cursor=owner.Ui,buffer=Ptr(_image+0x184A9B0);
        if(Ptr(_image+0xD40950)!=cursor || U16(_image+0x7DCF9A)!=owner.Worker || buffer<=0)return;
        while(U16(_image+0xD407B8)!=65535)
        {
            int parentIndex=(short)U16(cursor+0x158);
            if(parentIndex is <0 or >=80)return;
            int encoded=U16(buffer+parentIndex*2);
            int group=encoded>=0x7000?ChemistMedicineFamilies.Group((ushort)(encoded-0x7000)):-1;
            if(group<0 || (BattleLearned(Worker(owner.Worker))&(1u<<group))==0)return;
            ushort[] choices=ChemistMedicineFamilies.ForSlot(group);
            // The native window descriptor uses the same list/columns/flags
            // as make_abilitylistmain. Scope and restore the complete data;
            // keep only the chosen full ID in the parent's selected entry.
            byte[] list=Read(buffer,0x388),descriptor=Read(_image+0x7831B0,72);
            long listWindow=Ptr(owner.Task);
            if(listWindow<=0)return;
            long rowFlags=Ptr(listWindow+0x28);
            if(rowFlags<=0)return;
            byte[] window=Read(listWindow,0x58);
            byte[] stockFlags=Read(_image+0xD492D0,160);
            byte[] preview=Read(_image+0x2FD35A4,20);
            ushort selected=0;bool cancelled=true,rejected=false;
            NativeMedicineFourthMenu.Scope? fourth=null;
            try
            {
                // Copy before replacing the modal's data. All parent GUI rows
                // and its title stay owned by the original Items component.
                using var child=fourth=_medicineFourth.Begin(group);
                for(int i=0;i<80;i++)
                {
                    Word(buffer+i*2,65535);Word(buffer+0xA4+i*2,0);Word(buffer+0x148+i*2,0);
                    Word(_image+0xD492D0+i*2,4);
                    Word(_medicineInput.Address+i*2,4);
                }
                // Private bounded table: Remedy has seven entries and must
                // not overrun the parent's native window-navigation table.
                Write(listWindow+0x28,BitConverter.GetBytes(_medicineInput.Address));
                for(int i=0;i<choices.Length;i++)
                {
                    // Enhanced renders 7000 as an ability (MP eligibility),
                    // ignoring the legacy stock/color column. 3800 is its
                    // native inventory row: actual bottle, count and missing-
                    // stock colors. Only the child uses this presentation ID;
                    // after restoring the parent we commit the full ability.
                    Word(buffer+i*2,0x3800+ChemistMedicineFamilies.Item(choices[i]));
                    int stock=PotionStock(ChemistMedicineFamilies.Item(choices[i]));
                    Word(buffer+0xA4+i*2,stock);
                    Word(_image+0xD492D0+i*2,stock>0?0:4);
                    // This is the ACTUAL descriptor's keyright table. A
                    // stocked child finishes this list (-1), without starting
                    // Enhanced validation/target until final restoration.
                    Word(_medicineInput.Address+i*2,stock>0?65535:4);
                }
                Word(_image+0x7831B0,choices.Length);Word(_image+0x7831B2,0);
                Word(_image+0x7831BC,2);Word(_image+0x7831BE,2);Word(_image+0x7831F0,0);
                Word(listWindow+0xA,0x30+Math.Max(0,6-choices.Length)*8);
                Word(listWindow+0x50,0);Word(cursor+0x158,0);
                Word(_image+0xD407B8,0);
                // multiwindow_break otherwise sees the first window's finish
                // request and closes this child immediately. Keep its parent.
                Write(owner.Task+0x10,new byte[8]);
                Write(_potionScopes.Address+owner.Index*NativePotionWindowLifecycle.RecordSize,BitConverter.GetBytes(2));
                Call(0x22F868);
                if(Ptr(_image+0xD40950)!=cursor || U16(_image+0x7DCF9A)!=owner.Worker)
                    throw new InvalidDataException("Potion submenu owner changed.");
                int choice=(short)U16(listWindow+0x50);
                cancelled=U16(_image+0xD407B8)==65535;
                if(!cancelled && choice>=0 && choice<choices.Length)
                {
                    selected=choices[choice];
                    rejected=U16(buffer+choice*2)!=0x3800+ChemistMedicineFamilies.Item(selected) || PotionStock(ChemistMedicineFamilies.Item(selected))<=0;
                    cancelled=rejected;
                }
                else cancelled=true;
            }
            finally
            {
                Write(buffer,list);Write(_image+0x7831B0,descriptor);
                Write(listWindow,window);Write(_image+0xD492D0,stockFlags);
                Word(cursor+0x158,parentIndex);
                Write(_potionScopes.Address+owner.Index*NativePotionWindowLifecycle.RecordSize,BitConverter.GetBytes(1));
                if(cancelled)Write(_image+0x2FD35A4,preview);
            }
            if(!cancelled)
            {
                Word(buffer+parentIndex*2,0x7000+selected);Word(_image+0xD407B8,0);
                _log($"[Medicine/Submenu] grupo={group}; trabalhador={owner.Worker}; habilidade={selected}; item={ChemistMedicineFamilies.Item(selected)}; estoque ainda não consumido.");
                return;
            }
            if(rejected){Word(_image+0xD407B8,0);continue;} // remain in the choice, never release an empty item
            // Back from the child reopens Items; the outer action controller
            // cannot see a commitment until this parent is confirmed.
            Word(_image+0xD407B8,0);Write(owner.Task+0x10,new byte[8]);
            using(var parent=fourth?.ReopenItems())
            {
                if(fourth is not null&&parent is null)throw new InvalidDataException("Items GUI owner changed before Back reopening.");
                _log($"[Medicine/Voltar] grupo={group}; seleção Items restaurada={parentIndex}; reabertura visual={parent is not null}; controle original ativo até confirmar/voltar.");
                Call(0x22F868);
            }
        }
    }
    private void BattleList(NativeContextBridge.Frame* f,bool filter)
    {
        if(f->Phase!=0 || (byte)f->Arguments[1]!=6)return;
        f->Skip(0);
        int index=(int)f->Arguments[0];long worker=Worker(index);uint bits=BattleLearned(worker);
        byte* events=stackalloc byte[160];
        Call(0x30FFC4,(long)events,0,0);
        byte position=(byte)Call(0x30E2F0,index,0,(long)events,0);
        int count=0;
        if(_session.CanMutate)
        for(int slot=0;slot<ChemistActionBindings.ActionCount;slot++)
        {
            if(filter&&((bits&(1u<<slot))==0 || (slot<3?!ChemistMedicineFamilies.ForSlot(slot).Any(a=>Stocked(worker,a)):!Stocked(worker,ChemistActionBindings.TestActions[slot].AbilityId))))continue;
            Word(f->Arguments[2]+count*2,ChemistActionBindings.TestActions[slot].AbilityId);
            Write(f->Arguments[3]+count,[0]);Write(f->Arguments[4]+count,[0]);
            Write(f->Arguments[6]+count,[0]);Write(f->Arguments[7]+count,[position]);count++;
        }
        Word(f->Arguments[2]+count*2,65535);f->Result=count;
        if(filter && count==0 && Interlocked.Increment(ref _emptyBattleReports)<=32)
            _log($"[Combate/Lista] Items sem ações utilizáveis: trabalhador={index}; slot={Read(worker+2,1)[0]}; aprendizado=0x{bits:X}; sessão ativa={_session.CanMutate}; estoques="+
                string.Join(",",Enumerable.Range(0,15).Select(_session.Stock))+". Requisitos/estoque não equivalem necessariamente a aprendizado perdido.");
    }
    private void CheckLearned(NativeContextBridge.Frame* f)
    {
        if(f->Phase!=0 || (int)f->Arguments[1]!=6 || (uint)f->Arguments[2]>=16)return;
        f->Skip(0);int slot=(int)f->Arguments[2];
        if(slot<ChemistActionBindings.ActionCount && _session.CanMutate && (BattleLearned(Worker((int)f->Arguments[0]))&(1u<<slot))!=0)f->Result=1;
    }
    private void CommitAction(NativeContextBridge.Frame* f)
    {
        if(f->Phase!=0)return;
        byte[] packet=Read(f->Arguments[0],20);
        if(packet[1]!=6)return;
        f->Skip(-1);
        ushort ability=BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));int slot=ChemistMedicineFamilies.CombatSlot(ability);
        if(slot<0 || !_session.CanMutate)return;
        long worker=Worker(packet[0]);
        if(packet[10]==6)_=Worker(packet[11]);
        if((BattleLearned(worker)&(1u<<slot))==0 || !Stocked(worker,ability))return;
        int stock=PartyInventory(worker)?(slot<3?PotionStock(ChemistMedicineFamilies.Item(ability)):_session.Stock(slot)):0;
        byte[] committed=slot<3?packet.ToArray():ChemistBattleData.Commitment(packet);
        if(slot<3)BinaryPrimitives.WriteUInt16LittleEndian(committed.AsSpan(8),ChemistMedicineFamilies.Item(ability));
        Write(f->Arguments[1],committed);
        uint flags=(uint)f->Arguments[2];
        if(flags!=0)Write(worker+0x18D,[0]);
        if((flags&1)!=0)
        {
            byte modified=Read(worker+0x1BE+0x30,1)[0];
            int state=BinaryPrimitives.ReadInt32LittleEndian(Read(_image+0x186AF80,4));
            if(PartyInventory(worker) && ChemistActionBindings.Consumes(flags,modified,state))
            {
                if(slot<3)Write(_image+0x11A7C00+ChemistMedicineFamilies.Item(ability),[(byte)(stock-1)]);
                else _session.Consume(slot);
            }
            Write(worker+0x1BA,[1]);
        }
        f->Result=1;
    }
    private void SelectAction(NativeContextBridge.Frame* f)
    {
        if(f->Phase!=1)return;
        byte[] request=Read(f->Arguments[0],4);
        if(request[2]!=6)return;
        ushort encoded=BinaryPrimitives.ReadUInt16LittleEndian(request);
        ushort ability=(ushort)(encoded&1023);
        int slot=ChemistMedicineFamilies.CombatSlot(ability);
        if(slot<0)throw new InvalidDataException("Unregistered selection in rewritten command.");
        _=Worker(encoded>>10);
        byte status=slot<3?Read(Call(0x2B8E04,ChemistMedicineFamilies.Item(ability))+2,1)[0]:Read(_actions.Address+slot*20+11,1)[0];
        // inflictStatus entry is {type, five status bytes}, not five flags at 0.
        byte[] selected=ChemistBattleData.Selection(Read(_image+0x18716A0,20),slot,status,
            Read(_image+0x80FBA0+status*6+1,5),Read(_common.Address+ability*8+4,4));
        if(slot<3)BinaryPrimitives.WriteUInt16LittleEndian(selected.AsSpan(6),ChemistMedicineFamilies.Item(ability));
        if(slot>=4)selected[17]=ChemistExtraEffects.Element(slot);
        Write(_image+0x18716A0,selected);
    }
    private void TargetMap(NativeContextBridge.Frame* f,bool effect)
    {
        if(f->Phase!=0)return;
        byte[] packet=Read(f->Arguments[0],20);
        if(packet[1]!=6)return;
        int slot=ChemistMedicineFamilies.CombatSlot(BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));
        if(slot<0)return; // no widened numeric bound for unrelated abilities
        f->Skip(-1);
        if(!_session.CanMutate || packet[0]>20)return;
        long worker=_image+0x1853CE0+packet[0]*512;
        if(Read(worker+1,1)[0]==255 || (effect && packet[10]==6 && packet[11]>20))return;
        int cells=(int)Call(effect?0x280B40:0x27F974,f->Arguments[0]);
        // The expanded single-tile spell map omitted medicine's original
        // getweaponeffect receiver resolution. Use that function's own collision
        // engine before make_target_table/prediction/commit, not only at launch.
        // Preserve the full action/item and original range mask. Scene geometry,
        // unit height, obstacles and first receiver remain native policy.
        if(effect && cells>0)
        {
            byte[] coords=packet.AsSpan(12,6).ToArray();
            if(packet[10]==6)
            {
                long target=Worker(packet[11]);byte[] position=Read(target+0x4F,3);
                BinaryPrimitives.WriteUInt16LittleEndian(coords,(ushort)position[0]);
                BinaryPrimitives.WriteUInt16LittleEndian(coords.AsSpan(2),(ushort)(position[2]>>7));
                BinaryPrimitives.WriteUInt16LittleEndian(coords.AsSpan(4),(ushort)position[1]);
            }
            int destination=(int)Call(0x282A7C,U16From(coords,0),U16From(coords,4),U16From(coords,2),0);
            // Original adjacent/self medicine does not test a flight collision.
            byte[] origin=Read(worker+0x4F,2);
            if(Math.Abs(origin[0]-U16From(coords,0))+Math.Abs(origin[1]-U16From(coords,4))>=2)
            {
                byte* nativeCoords=stackalloc byte[6];coords.CopyTo(new Span<byte>(nativeCoords,6));
                int receiver=(int)Call(0x312ED0,packet[0],(long)nativeCoords,destination);
                byte[] map=Read(_image+0xD8DCB0,512*8);
                for(int tile=0;tile<512;tile++)map[tile*8+5]&=0x7F;
                if(receiver is >=0 and <=20)
                {
                    byte[] position=Read(Worker(receiver)+0x4F,3);
                    int tile=position[0]+Read(_image+0xC6AD6A,1)[0]*position[1]+(position[2]>>7)*256;
                    if(tile is <0 or >=512)throw new InvalidDataException("Native medicine receiver is outside map.");
                    map[tile*8+5]|=0x80;
                }
                Write(_image+0xD8DCB0,map);
                if(Interlocked.Increment(ref _receiverReports)<=64)
                    _log($"[Trajetória/Receptor] habilidade={BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2))}; lançador={packet[0]}; destino={destination}; receptor nativo={receiver}.");
            }
        }
        int result=effect?(cells==0?1:cells==-1?-1:0):
            (cells==0?3:((Read(_actions.Address+slot*20+4,1)[0]&0x10)!=0 || (Read(worker+0x63,1)[0]&2)!=0)?1:0);
        f->Result=result;
        if(slot==4 && (effect?Interlocked.Exchange(ref _areaReported,1):Interlocked.Exchange(ref _rangeReported,1))==0)
            _log($"[Alvos] Venom Flask: {(effect?"área":"alcance")}; células nativas={cells}; retorno={result}; habilidade=513; item=261.");
    }
    private static ushort U16From(byte[] data,int offset)=>BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
    private void Load(NativeContextBridge.Frame* f)
    {
        if(f->Phase==0)_loads.Value![f->EntryStack]=_session.BeforePartyLoad();
        else if(_loads.Value!.Remove(f->EntryStack,out var work))_session.AfterPartyLoad(work);
        else throw new InvalidDataException("Missing actual party-load snapshot.");
    }
    private void EnhancedLoad(NativeContextBridge.Frame* f)
    {
        if(f->Phase==0)
        {
            long source=Ptr(_image+0xD407A0);
            if(source<=0)throw new InvalidDataException("Missing enhanced selected SaveWork.");
            _loads.Value![f->EntryStack]=Read(source,NativePartySaveCommit.WorkSize);
        }
        else if(_loads.Value!.Remove(f->EntryStack,out var work))_session.AfterPartyLoad(work);
        else throw new InvalidDataException("Missing enhanced load snapshot.");
    }
    private void ThrowBottle(NativeContextBridge.Frame* f)
    {
        if(f->Arguments[0]<=0)return;
        long actor=f->Arguments[0],target=f->Arguments[1];
        ushort ability=U16(actor+0x142);
        int slot=ChemistMedicineFamilies.CombatSlot(ability);
        if(slot<0 || (slot<4 && !(slot<3 && ability!=368)))return;
        if(Interlocked.Increment(ref _throwReports)<=64)
        {
            _log($"[Visual/Throw] {ChemistActionBindings.TestActions[slot].Key}: fase={f->Phase}; estado={U16(actor+0x10):X}; alvo={(target>0?"presente":"ausente")}; item={U16(actor+0x150)}; equipamento={U16(actor+0x1FC):X}; efeito={BinaryPrimitives.ReadInt32LittleEndian(Read(_image+0x18732C8,4))}.");
            byte[] label=Read(_mapNameDiagnostic.Address,32);
            _log($"[Visual/MapName] consulta={BinaryPrimitives.ReadInt32LittleEndian(label)}; resolvida={BinaryPrimitives.ReadInt32LittleEndian(label.AsSpan(4))}; ação exibida={BinaryPrimitives.ReadInt32LittleEndian(label.AsSpan(16))}; comando={BinaryPrimitives.ReadInt32LittleEndian(label.AsSpan(20))}; fonte={BinaryPrimitives.ReadInt32LittleEndian(label.AsSpan(24))} (1=animação,2=prévia); consultas={BinaryPrimitives.ReadUInt32LittleEndian(label.AsSpan(28))}.");
        }
        if(f->Phase!=1 || target<=0 || U16(actor+0x10)!=0x3A)return;
        byte[] from=Read(actor+0x88,2),to=Read(target+0x88,2);
        // Native self-use has no flight path. Do not create a zero-length
        // trajectory or change ordinary Potion / other class animations.
        if(from.SequenceEqual(to))return;
        Write(actor+0x360,[0]);
        Call(0x268E04,0x4C,(short)U16(actor+0x7C),actor);
        _log($"[Visual] {ChemistActionBindings.TestActions[slot].Key}: habilidade={ability}; frasco em trajetória nativa, inclusive alvo adjacente; item/modelo={(slot<4?U16(actor+0x150):ChemistExtraEffects.BottleModel(slot))}.");
    }
    private void ConsumableVisualRouting(List<GuardedNativeWrite> writes)
    {
        // Battle's two inline effect-word lookups are independent of StartEffect
        // and AbilityIsRSM. Preserve the copied legacy entries; owned consumables
        // wait for the item projectile's collision, just like Potion (bit 0x800).
        var effects=Own(2048,true);effects.Write(0,Read(_image+0x683500,2048));
        foreach(int slot in ChemistActionBindings.ExtraSlots)
            effects.Write(ChemistActionBindings.TestActions[slot].AbilityId*2,
                BitConverter.GetBytes((ushort)(0x800|(U16(_image+0x683500+ChemistExtraEffects.ImpactTemplate(slot)*2)&0x1FF))));
        byte[] operand=Convert.FromHexString("420FBF8C4F00356800");
        writes.AddRange(NativeTableRelocation.Plan(_image,0x683500,effects.Address,2048,
            [new(0x212CE9,operand,5,0,NativeOperandEncoding.ImageRelativeIndexed),
             new(0x212D18,operand,5,0,NativeOperandEncoding.ImageRelativeIndexed)],Read));
        // Native requestParmanentEffect only recognizes original 368..393 as
        // items. Extend this branch for 513..523; otherwise a sword/bow/gun
        // equipped by a Chemist can incorrectly select the weapon projectile.
        foreach(int rva in new[]{0x312ED0,0x272BD0,0x312D4C})Guard(rva);
        var selector=Own(4096,true);selector.Write(0,NativeConsumableVisuals.BuildProjectileSelector(_image));selector.Executable();
        byte[] jump=new byte[5];jump[0]=0xE9;
        BinaryPrimitives.WriteInt32LittleEndian(jump.AsSpan(1),checked((int)(selector.Address-(_image+0x1FADFF+5))));
        AddWrite(writes,0x1FADFF,[0xB9,0x7E,1,0,0],jump);
    }
    private void Formula(List<GuardedNativeWrite> writes)
    {
        foreach(int rva in new[]{0x30698C,0x308C30,0x308C70,0x306BD0,0x308C98})Guard(rva);
        HelperAt(0x305554);HelperAt(0x306224);
        var table=Own(108*8,true);table.Write(0,Read(_image+0x682BC8,107*8));
        long stub=(long)_bridge.ReserveAddress();
        NativeContextBridge.Observer observer=f=>
        {
            if(f->Phase!=0)return;
            try
            {
                ushort ability=U16(_image+0x7B0778);
                int slot=ChemistMedicineFamilies.CombatSlot(ability);
                if(slot<0 || Read(_image+0x7B0776,1)[0]!=6 || !_session.CanMutate){f->Skip(0);return;}
                int item=slot<3?ChemistMedicineFamilies.Item(ability):ChemistActionBindings.Item(slot);
                Word(_image+0x7B077A,item);
                if(slot<4)Write(_image+0x7B077E,[(byte)(item-240)]);
                if(ChemistExtraEffects.Elemental(slot))
                {
                    // Preformula owns eligibility, reactions, and initialized hit
                    // state. Only HP magnitude is replaced; Faith/PA/MA are unused.
                    long result=Ptr(_image+0x186AF70),target=Ptr(_image+0x186AF68);
                    if(result<=0 || target<=0)throw new InvalidDataException("Missing native elemental target/result.");
                    _=Read(result,0x38);_=Read(target,512);
                    if(Read(result,1)[0]==0 || Read(result+2,1)[0]==5){f->Skip(0);return;}
                    Word(result+6,ChemistExtraEffects.ElementalDamage(U16(target+0x32)));Write(result+0x27,[0x80]);
                    Call(0x305554,ChemistExtraEffects.Element(slot));
                    // Native nullification sets evade_type5; preserve that veto.
                    Call(0x306224);
                    f->Skip(0);
                    return;
                }
                int nativeFormula=slot==0?(ability==373?74:72):slot==1?73:slot==3?75:56;
                // Native reaction/immunity calculations have already run;
                // never force hit/status over an immunity or reaction result.
                Write(_image+0x7B0788,[(byte)nativeFormula]);
                f->OriginalAddress=_image+(slot==0?(ability==373?0x308C70:0x308C30):slot==1?0x306BD0:slot==3?0x308C98:0x30698C);
                if(slot==4)_log("[Combate] Venom Flask: fórmula Poison sem Faith; resultado sujeito às imunidades nativas.");
            }
            catch(Exception ex){_session.Block(ex.Message);f->Skip(0);}
        };
        _bridge.Bind((nint)(_image+0x30698C),0,observer,ex=>_session.Block(ex.Message));
        table.Write(107*8,BitConverter.GetBytes(stub));
        // Only the independently decoded indexed CALL operand is relocated.
        byte[] instruction=Read(_image+0x309F53,8);
        if(Convert.ToHexString(instruction)!="41FF94C4C82B6800")throw new InvalidDataException("Formula dispatch instruction differs.");
        writes.AddRange(NativeTableRelocation.Plan(_image,0x682BC8,table.Address,108*8,
            [new(0x309F53,instruction,4,0,NativeOperandEncoding.ImageRelativeIndexed)],Read));
    }
    private void VisualEffect(List<GuardedNativeWrite> writes)
    {
        const int site=0x206317,target=0x319B50;
        byte[] before=new byte[5];before[0]=0xE8;
        BinaryPrimitives.WriteInt32LittleEndian(before.AsSpan(1),target-site-5);
        if(!Read(_image+site,5).SequenceEqual(before))throw new InvalidDataException("Visual effect caller differs.");
        nint bridge=_bridge.Bind((nint)(_image+target),0,f=>
        {
            if(!_active || f->Phase!=0 || (ulong)f->Arguments[1]>ushort.MaxValue)return;
            int slot=ChemistActionBindings.Slot((ushort)f->Arguments[1]);
            if(slot<4)return;
            int template=ChemistExtraEffects.ImpactTemplate(slot);
            // StartEffect's low word selects a separate persistent caster FX.
            // Spell class 1 starts casting; zero selects no persistent FX.
            // The impact script is selected independently by argument 1.
            f->Arguments[0]=0;
            f->Arguments[1]=template;
            _log($"[Visual] {ChemistActionBindings.TestActions[slot].Key}: impacto nativo={template}; sem conjuração.");
        },ex=>_session.Block(ex.Message));
        var thunk=Own(4096,true);
        // RIP-indirect tail jump preserves every entry register, including RAX.
        thunk.Write(0,[0xFF,0x25,0,0,0,0]);thunk.Write(6,BitConverter.GetBytes((long)bridge));thunk.Executable();
        _effectCallAddress=thunk.Address;
        byte[] after=new byte[5];after[0]=0xE8;
        BinaryPrimitives.WriteInt32LittleEndian(after.AsSpan(1),checked((int)(thunk.Address-(_image+site+5))));
        writes.Add(new(_image+site,before,after,_image+site,before));
    }
}
