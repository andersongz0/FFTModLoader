using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion;

public sealed record BattleFormDefinition(byte HumanJob, byte MonsterJob, byte HumanCommand,
    byte MonsterCommand, byte MaleShape, byte FemaleShape, byte MalePalette, byte FemalePalette,
    byte MonsterGraphic, bool UseComputedAttributes = false);
public sealed record SavedBattleForm(int Worker,byte[] Before,byte[] Form);

/// <summary>
/// Pure journal/planner for temporary forms, owned by ContentExpansion. Never
/// runs native formulas, draws sprites or writes game memory. The native host
/// must preflight the sprite/AI routes, apply the plan transactionally and only
/// then acknowledge it. No full worker snapshot is restored: that would rewind
/// damage, status timers, CT, coordinates and actions performed as a monster.
/// </summary>
public sealed class ReversibleBattleForms
{
    public const int WorkerSize = 512;
    private readonly BattleFormDefinition _form;
    private readonly Dictionary<(long Battle, int Worker), Origin> _origins = [];
    private readonly object _gate = new();
    // Identity and calculated class attributes are separate from mutable battle
    // state. Never restore a full snapshot of a worker after it has acted.
    private static readonly int[] IdentityOffsets = [0,3,4,0x12,0x13,0x18E,0x18F,0x190,0x192];
    private static readonly int[] AttributeOffsets = Enumerable.Range(0x0A,8)
        .Concat(Enumerable.Range(0x14,6)) // calculated R/S/M slots (innate reaction can change it)
        .Concat(Enumerable.Range(0x1A,14)) // monster has no active equipment
        .Concat(new[]{0x32,0x33,0x36,0x37}) // MAX resources only; current is proportional
        .Concat(Enumerable.Range(0x38,9)) // PA/MA/SP bases, bonuses and effective stats
        .Concat(Enumerable.Range(0x42,13)) // Move, Jump, weapon power and evasion
        .Concat(Enumerable.Range(0x52,15)) // equip policy, innate statuses and immunity
        .Concat(Enumerable.Range(0x76,5)) // elemental affinities
        .Concat(Enumerable.Range(0x8A,10)) // growth/multipliers
        .Concat(Enumerable.Range(0x94,11)) // R/S/M flags
        .ToArray();
    private static readonly int[] PersistentStatOffsets = [0x38,0x39,0x3A,0x3E,0x3F,0x40];
    private sealed record Origin(byte PartySlot, byte Faction, byte[] Before, byte[] Form);

    public ReversibleBattleForms(BattleFormDefinition form)
    {
        if (form.HumanJob < 174 || form.MonsterJob < 174 || form.HumanJob == form.MonsterJob ||
            form.HumanCommand < 227 || form.MonsterCommand < 227 || form.HumanCommand == form.MonsterCommand ||
            form.MaleShape < 170 || form.FemaleShape < 170 || form.MaleShape == form.FemaleShape ||
            form.MalePalette >=16 || form.FemalePalette >=16)
            throw new InvalidDataException("Form must use separate new job, command and shape identities.");
        _form=form;
    }

    public byte[] PlanTransform(long battle,int worker,ReadOnlySpan<byte> current,
        ReadOnlySpan<byte> computedAttributes = default)
    {
        Validate(battle,worker,current);
        lock(_gate)
        {
            if (_origins.ContainsKey((battle,worker)) || current[3]!=_form.HumanJob ||
                current[0] is not (0x80 or 0x81) || current[0x12]!=_form.HumanCommand)
                throw new InvalidDataException("Worker is not an untransformed owner of this form.");
            byte[] next=current.ToArray();
            if(_form.UseComputedAttributes)
            {
                // Calculator input is a separate native scratch worker, never
                // CopyJobEffectsToUnit on the live worker (it clears statuses).
                if(computedAttributes.Length!=WorkerSize || U16(computedAttributes,0x32)==0)
                    throw new InvalidDataException("Calculated monster attributes are required before transformation.");
                foreach(int offset in AttributeOffsets)next[offset]=computedAttributes[offset];
                ScaleResource(current,next,0x30,0x32);
                ScaleResource(current,next,0x34,0x36);
            }
            bool female=current[0]==0x81 || (current[6]&0x40)!=0;
            byte shape=female?_form.FemaleShape:_form.MaleShape;
            byte palette=female?_form.FemalePalette:_form.MalePalette;
            next[0]=0x82;next[3]=_form.MonsterJob;next[4]=palette;
            // Audited native sex/kind encoding: 0x40 female, 0x80 monster.
            // 0x10 is NOT a monster-kind flag. Preserve every unrelated bit.
            next[6]|=0x80;next[0x12]=_form.MonsterCommand;next[0x13]=0;
            next[0x18E]=_form.MonsterGraphic;next[0x18F]=shape;next[0x190]=palette;next[0x192]=_form.MonsterCommand;
            return next;
        }
    }

    public void AcknowledgeTransform(long battle,int worker,ReadOnlySpan<byte> before,ReadOnlySpan<byte> applied,
        ReadOnlySpan<byte> computedAttributes = default)
    {
        lock(_gate)
        {
            if(!PlanTransform(battle,worker,before,computedAttributes).AsSpan().SequenceEqual(applied))
                throw new InvalidDataException("Native form commit did not match the prepared plan.");
            _origins.Add((battle,worker),new Origin(before[2],before[5],before.ToArray(),applied.ToArray()));
        }
    }

    public byte[] PlanReturn(long battle,int worker,ReadOnlySpan<byte> current)
    {
        Validate(battle,worker,current);
        lock(_gate)
        {
            if(!_origins.TryGetValue((battle,worker),out var origin) || current[2]!=origin.PartySlot ||
                current[5]!=origin.Faction || current[3]!=_form.MonsterJob || current[0x12]!=_form.MonsterCommand ||
                current[0]!=0x82 || (current[6]&0x80)==0 ||
                (current[0x18F]!=_form.MaleShape && current[0x18F]!=_form.FemaleShape))
                throw new InvalidDataException("Missing form origin or recycled worker; restoration refused.");
            var next=current.ToArray();
            foreach(int offset in IdentityOffsets)next[offset]=origin.Before[offset];
            next[6]=(byte)((next[6]&~0x80)|(origin.Before[6]&0x80));
            if(_form.UseComputedAttributes)
            {
                foreach(int offset in AttributeOffsets)next[offset]=origin.Before[offset];
                // Permanent stat changes incurred in Wolf form are not erased
                // by restoring the original class's attribute scale.
                foreach(int offset in PersistentStatOffsets)
                    next[offset]=(byte)Math.Clamp(origin.Before[offset]+current[offset]-origin.Form[offset],0,255);
                RestoreResource(origin,current,next,0x30,0x32);
                RestoreResource(origin,current,next,0x34,0x36);
            }
            return next;
        }
    }

    public void AcknowledgeReturn(long battle,int worker,ReadOnlySpan<byte> before,ReadOnlySpan<byte> applied)
    {
        lock(_gate)
        {
            if(!PlanReturn(battle,worker,before).AsSpan().SequenceEqual(applied))
                throw new InvalidDataException("Native return commit did not match the prepared plan.");
            _origins.Remove((battle,worker));
        }
    }

    public IReadOnlyDictionary<int,byte[]> PlanBattleEnd(long battle,IReadOnlyDictionary<int,byte[]> workers)
    {
        lock(_gate)
        {
            var plans=new Dictionary<int,byte[]>();
            foreach(var identity in _origins.Keys.Where(k=>k.Battle==battle))
            {
                if(!workers.TryGetValue(identity.Worker,out var current))
                    throw new InvalidDataException("Battle end cannot serialize an un-restored form.");
                plans.Add(identity.Worker,PlanReturn(battle,identity.Worker,current));
            }
            return plans;
        }
    }

    public bool HasTemporaryForm(long battle,int worker) { lock(_gate)return _origins.ContainsKey((battle,worker)); }
    public SavedBattleForm[] Snapshot(long battle)
    {
        lock(_gate)return _origins.Where(p=>p.Key.Battle==battle).OrderBy(p=>p.Key.Worker)
            .Select(p=>new SavedBattleForm(p.Key.Worker,p.Value.Before.ToArray(),p.Value.Form.ToArray())).ToArray();
    }
    public void Restore(long battle,IEnumerable<SavedBattleForm> forms)
    {
        var rows=forms.ToArray();if(rows.Length>21||rows.Select(f=>f.Worker).Distinct().Count()!=rows.Length)
            throw new InvalidDataException("Duplicate or oversized form journal.");
        var checker=new ReversibleBattleForms(_form);
        foreach(var row in rows)
        {
            if(row.Before is null||row.Form is null)throw new InvalidDataException("Incomplete form journal.");
            checker.AcknowledgeTransform(battle,row.Worker,row.Before,row.Form,_form.UseComputedAttributes?row.Form:default);
        }
        lock(_gate)
        {
            _origins.Clear();foreach(var pair in checker._origins)_origins.Add(pair.Key,pair.Value);
        }
    }
    private static ushort U16(ReadOnlySpan<byte> bytes,int offset)=>BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static void ScaleResource(ReadOnlySpan<byte> source,Span<byte> target,int valueOffset,int maxOffset)
    {
        int value=U16(source,valueOffset),oldMax=U16(source,maxOffset),newMax=U16(target,maxOffset);
        int next=oldMax==0||newMax==0?0:Math.Min(newMax,(int)((long)value*newMax/oldMax));
        if(valueOffset==0x30 && value>0 && newMax>0)next=Math.Max(next,1); // conversion alone must not cause KO
        BinaryPrimitives.WriteUInt16LittleEndian(target[valueOffset..],(ushort)next);
    }
    private static void RestoreResource(Origin origin,ReadOnlySpan<byte> current,Span<byte> target,int valueOffset,int maxOffset)
    {
        // An unchanged percentage must round-trip exactly: toggling forms is
        // neither a source of healing nor a slow loss through integer rounding.
        if(U16(current,valueOffset)==U16(origin.Form,valueOffset) && U16(current,maxOffset)==U16(origin.Form,maxOffset))
            BinaryPrimitives.WriteUInt16LittleEndian(target[valueOffset..],U16(origin.Before,valueOffset));
        else ScaleResource(current,target,valueOffset,maxOffset);
    }
    private static void Validate(long battle,int worker,ReadOnlySpan<byte> bytes)
    {
        if(battle<=0 || worker is <0 or >20 || bytes.Length!=WorkerSize || bytes[1]!=worker)
            throw new InvalidDataException("Invalid active battle identity or worker buffer.");
    }
}
