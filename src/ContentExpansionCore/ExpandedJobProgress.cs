namespace FFTModLoader.ContentExpansion;

public sealed record ExpandedJobProgressState(int SchemaVersion,string UnitKey,ushort Job,
    ushort UnspentJp,ushort TotalJp,ushort[] Learned);

/// <summary>New jobs own progress; never index or repurpose a legacy worker JP/learning cell.</summary>
public sealed class ExpandedJobProgress
{
    private readonly ushort _job;
    private readonly ushort[] _thresholds;
    private readonly Dictionary<ushort,ushort> _costs;
    private readonly Dictionary<string,ExpandedJobProgressState> _units=new(StringComparer.Ordinal);
    private readonly object _gate=new();
    public ExpandedJobProgress(ushort job,IEnumerable<ushort> thresholds,IEnumerable<MenuAction> actions)
    {
        if(job is <174 or >255)throw new InvalidDataException("Progress belongs to a new job ID.");
        _thresholds=thresholds.ToArray();
        if(_thresholds.Length!=8 || !_thresholds.SequenceEqual(_thresholds.Order()) ||
            _thresholds.Distinct().Count()!=8)throw new InvalidDataException("Eight increasing native level thresholds required.");
        var rows=actions.ToArray();_ = new NativeActionMenu(rows);
        if(rows.Any(a=>a.AbilityId<513))throw new InvalidDataException("New job learning cannot claim an original action.");
        _costs=rows.Where(a=>a.Learnable).ToDictionary(a=>a.AbilityId,a=>a.JpCost);_job=job;
    }
    private static void Key(string key)
    {
        if(key.Length!=64 || key.Any(c=>!Uri.IsHexDigit(c)))throw new InvalidDataException("Hashed persistent unit identity required.");
    }
    public ExpandedJobProgressState Read(string unitKey)
    {
        Key(unitKey);
        lock(_gate)
        {
            var state=_units.GetValueOrDefault(unitKey)??new(1,unitKey,_job,0,0,[]);
            return state with {Learned=state.Learned.ToArray()};
        }
    }
    public int Level(string unitKey)
    {var state=Read(unitKey);return _thresholds.Count(t=>t<=state.TotalJp);}
    public int Next(string unitKey)
    {var state=Read(unitKey);int level=Level(unitKey);return level==8?0:Math.Max(0,_thresholds[level]-state.TotalJp);}
    public void Gain(string unitKey,int jp)
    {
        if(jp<0)throw new ArgumentOutOfRangeException(nameof(jp));
        lock(_gate)
        {
            var prior=Read(unitKey);
            _units[unitKey]=prior with {UnspentJp=(ushort)Math.Min(9999,(long)prior.UnspentJp+jp),
                TotalJp=(ushort)Math.Min(9999,(long)prior.TotalJp+jp)};
        }
    }
    public bool Learn(string unitKey,ushort ability)
    {
        lock(_gate)
        {
            var prior=Read(unitKey);
            if(!_costs.TryGetValue(ability,out ushort cost)||prior.Learned.Contains(ability)||prior.UnspentJp<cost)return false;
            _units[unitKey]=prior with {UnspentJp=(ushort)(prior.UnspentJp-cost),Learned=prior.Learned.Append(ability).Order().ToArray()};
            return true;
        }
    }
    public void Restore(IEnumerable<ExpandedJobProgressState> states)
    {
        var incoming=states.ToArray();var validated=new Dictionary<string,ExpandedJobProgressState>(StringComparer.Ordinal);
        foreach(var state in incoming)
        {
            Key(state.UnitKey);
            if(state.SchemaVersion!=1||state.Job!=_job||state.UnspentJp>state.TotalJp||state.TotalJp>9999||
                state.Learned is null||state.Learned.Distinct().Count()!=state.Learned.Length||state.Learned.Any(a=>!_costs.ContainsKey(a))||
                !validated.TryAdd(state.UnitKey,state with {Learned=state.Learned.ToArray()}))
                throw new InvalidDataException("Invalid or duplicate expanded job progress.");
        }
        lock(_gate){_units.Clear();foreach(var pair in validated)_units.Add(pair.Key,pair.Value);}
    }
    public ExpandedJobProgressState[] Snapshot()
    {lock(_gate)return _units.Keys.Order(StringComparer.Ordinal).Select(Read).ToArray();}
    public static bool MeetsBestialRequirements(ReadOnlySpan<byte> nativeLevels)
    {
        if(nativeLevels.Length!=12)throw new InvalidDataException("Native packed level array must contain twelve bytes.");
        return NativeLevel(nativeLevels,0)>=4&&NativeLevel(nativeLevels,3)>=4&&NativeLevel(nativeLevels,11)>=4;
    }
    private static int NativeLevel(ReadOnlySpan<byte> levels,int index)
        =>(index&1)==0?levels[index/2]>>4:levels[index/2]&15;
}
