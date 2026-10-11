namespace FFTModLoader.ContentExpansion.Runtime;

public sealed unsafe partial class BestialPlayableHost
{
    private readonly object _formGate=new();
    private sealed record PendingForm(long Battle,int Worker,bool ToWolf,long Animation,byte Kind,byte PartySlot,byte Job,byte Faction);
    private readonly Dictionary<int,PendingForm> _pendingForms=[];
    private void InstallForms(List<GuardedNativeWrite> writes)
    {
        Helper(0x26A358);
        Helper(0x2607C0); // audited current-animation lookup
        _routes.Guard(0x0103E0);
        long stub=(long)_bridge.ReserveAddress();
        _bridge.Bind((nint)(_image+0x0103E0),0,f=>
        {
            if(f->Phase!=0)return;f->Skip(0);
            if(!_active||_blocked)return;
            int ability=U16(_image+0x7B0778);
            long caster=Ptr(_image+0x186AF78),target=Ptr(_image+0x186AF68),result=Ptr(_image+0x186AF70);
            if(!OwnAbility(ability)||caster<=0||caster!=target||result<=0)return;
            int index=Read(caster+1,1)[0];
            bool ready=ability==524?CanTransform(caster):CanReturn(caster);
            if(!ready||!SpriteAvailable(ability==524?((Read(caster+6,1)[0]&64)!=0?(byte)177:(byte)176):Read(caster+6,1)[0]%128>=64?(byte)175:(byte)174))
                Write(result,[0]);
            // Preview runs the exact same eligibility check but never changes
            // the worker, sprite cache, journal, HP or party data.
            _=Worker(index);
        },Block);
        _routes.Tables.Formulas.Write(108*8,BitConverter.GetBytes(stub));
        writes.AddRange(_routes.Tables.PlanFormulaDispatch(108));
        Bind(0x30A488,0,f=>
        {
            if(f->Phase!=1||I32(_image+0x186AF80)!=0||!OwnAbility(U16(_image+0x7B0778)))return;
            int index=(int)f->Arguments[0];long worker=Worker(index);
            if(Ptr(_image+0x186AF78)!=worker||Read(worker+0x1BE,1)[0]==0||(int)f->OriginalResult<0)return;
            lock(_formGate)
            {
                // Effect evaluation still runs inside the action animation.
                // Changing SHP/SEQ and requesting idle here strands that
                // pipeline waiting for its old sequence to finish. Preserve
                // native result/render fields until afterCommandInit returns.
                byte[] identity=Read(worker,6);
                _pendingForms[index]=new(_battle,index,U16(_image+0x7B0778)==524,
                    OwnedAnimation(worker),identity[0],identity[2],identity[3],identity[5]);
                _log($"[Form] worker={index}; transition queued until native action completion.");
            }
        });
        Bind(0x203FDC,0,f=>
        {
            if(f->Phase!=1)return;
            lock(_formGate)
            {
                if(_pendingForms.Count==0)return;
                long completedAnimation=Call(0x2607C0);
                foreach(var pending in _pendingForms.Values.ToArray())
                {
                    if(pending.Animation!=completedAnimation)continue;
                    _pendingForms.Remove(pending.Worker);
                    long worker=Worker(pending.Worker);byte[] identity=Read(worker,6);
                    if(pending.Battle!=_battle||identity[0]!=pending.Kind||identity[2]!=pending.PartySlot||
                        identity[3]!=pending.Job||identity[5]!=pending.Faction||
                        OwnedAnimation(worker)!=pending.Animation)
                    {
                        _log($"[Warning] Form transition discarded for a changed owner: {pending.Worker}.");
                        continue;
                    }
                    ChangeForm(pending.Worker,pending.ToWolf,true);
                }
            }
        });
        foreach(int rva in new[]{0x227458,0x2C60E4})Bind(rva,0,f=>
        {
            if(f->Phase!=0)return;
            lock(_formGate)
            {
                _pendingForms.Clear();
                for(int i=0;i<21;i++)if(_forms.HasTemporaryForm(_battle,i))ChangeForm(i,false,false);
            }
        });
    }
    private bool SpriteAvailable(byte shape)
    {
        for(int i=0;i<15;i++){byte[] slot=Read(_image+0xD005A0+i*0x32D6,2);if(slot[1]==shape||slot[0]==0)return true;}
        return false;
    }
    private void ChangeForm(int index,bool toWolf,bool render)
    {
        long worker=Worker(index);byte[] before=Read(worker,512),calculated=[];
        if(toWolf){calculated=MonsterFormAttributes.Calculate(before,Read(_routes.Jobs.Address+175*49,49));}
        byte[] next=toWolf?_forms.PlanTransform(_battle,index,before,calculated):_forms.PlanReturn(_battle,index,before);
        byte shape=next[0x18F];
        if(render)
        {
            long animation=PreflightSprite(worker,shape);
            // The cached worker AnimationID is not authoritative during an
            // action. Keep the identity of the live, worker-owned animation.
            before[0x191]=next[0x191]=Read(animation+8,1)[0];
        }
        Write(worker,next);
        try
        {
            if(render)Paint(worker,next);
            if(toWolf)_forms.AcknowledgeTransform(_battle,index,before,next,calculated);
            else _forms.AcknowledgeReturn(_battle,index,before,next);
        }
        catch
        {
            // The journal remains in its prior state until every native draw
            // and command refresh succeeds. Put this same worker back only.
            Write(worker,before);if(render)Paint(worker,before);throw;
        }
        _log($"[Form] worker={index}; {(toWolf?"Wolf":"Bestial Warrior")}; HP={U16(worker+0x30)}/{U16(worker+0x32)}; MP={U16(worker+0x34)}/{U16(worker+0x36)}.");
    }
    private long OwnedAnimation(long worker)
    {
        int index=Read(worker+0x1BC,1)[0];
        if(index>20||Worker(index)!=worker)throw new InvalidDataException("Invalid animation owner identity.");
        // 260A98 searches by animation ID; 260ABC searches by battle worker.
        // A stale cached ID can resolve another object (or no object at all).
        long animation=Call(0x260ABC,index);
        if(animation<=0||Ptr(animation+0x148)!=worker)
            throw new InvalidDataException("No live animation belongs to the transforming worker.");
        _=Read(animation,0x228);
        return animation;
    }
    private long PreflightSprite(long worker,byte shape)
    {
        if(!_sprites.ContainsKey(shape)||!SpriteAvailable(shape))throw new InvalidDataException("No verified sprite slot is available for this form.");
        long animation=OwnedAnimation(worker);
        _=_visuals.GetRuntimeResources(shape);
        return animation;
    }
    private void Paint(long worker,byte[] form)
    {
        byte shape=form[0x18F],palette=form[0x190];var resources=_shapeResources[shape];
        long animation=OwnedAnimation(worker);
        int font=checked((int)Call(0x268FF4,shape));if(font is <0 or >=15)throw new InvalidDataException("Sprite cache became full before commit.");
        long shp=_bridge.CallOnNativeOwner(resources.ShapeHelper,shape),seq=_bridge.CallOnNativeOwner(resources.SequenceHelper,shape);
        if(shp<=0||seq<=0)throw new InvalidDataException("Missing form shape or sequence.");
        Write(animation+9,[(byte)font,shape,palette]);Word(animation+0x14,font+20);
        Write(animation+0x218,BitConverter.GetBytes(shp));Write(animation+0x220,BitConverter.GetBytes(seq));
        Int(_image+0x2E7FE6C+(font+20)*4,resources.PageId);
        Call(0x26E838,_sprites[shape].Address,font,animation);
        Call(0x26A358,BitConverter.ToUInt16(form,0x20),animation,0);
        Call(0x26947C,animation,0,1);Call(0x268E04,3,(short)U16(animation+0x7C),animation);
        // Rebuild the native action cache in place; no status clearing or new
        // animation object, position, CT, equipment inventory or party writes.
        Call(0x3167E4,form[1]);Call(0x272874,form[1]);
        Int(_image+0x822D58+form[1]*4,font);Int(_image+0x822DAC+form[1]*4,form[0x191]);
    }
}
