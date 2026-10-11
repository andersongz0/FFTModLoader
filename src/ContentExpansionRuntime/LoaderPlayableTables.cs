using System.Text.Json;
using FFTModLoader.ContentExpansion;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>Shared native table storage, independent of the consuming gameplay mod.</summary>
internal sealed class LoaderPlayableTables
{
    private readonly long _image;
    internal NativeLifetimeMemory Common { get; }
    internal NativeLifetimeMemory UiList { get; }
    internal NativeLifetimeMemory Visual { get; }
    internal NativeLifetimeMemory Formulas { get; }
    internal NativeLifetimeMemory CommandKinds { get; }
    private bool _commandKindsRelocated;
    private bool _relocated;
    private bool _formulaRelocated;
    private int _formulaBound=105;
    private bool _actionBoundsExpanded;
    private string? _planHash;
    internal LoaderPlayableTables(long image)
    {
        _image=image;
        Common=new(8192,image);Common.Write(0,CheckedNativeRead.Read(image+0x787F80,4096));
        UiList=new(2048,image);UiList.Write(0,CheckedNativeRead.Read(image+0x1811470,512));
        Visual=new(3072,image);Visual.Write(0,CheckedNativeRead.Read(image+0x672DA0,0x554));
        Formulas=new(256*8,image);Formulas.Write(0,CheckedNativeRead.Read(image+0x682BC8,107*8));
        for(int i=107;i<256;i++)Formulas.Write(i*8,BitConverter.GetBytes(image+0x0103E0));
        CommandKinds=new(256,image);CommandKinds.Write(0,CheckedNativeRead.Read(image+0x67E010,227));
    }
    internal List<GuardedNativeWrite> PlanCommandKinds()=>_commandKindsRelocated?[]:
        NativeCommandKindRelocation.Plan(_image,CommandKinds.Address);
    internal void AcknowledgeCommandKinds()=>_commandKindsRelocated=true;
    internal List<GuardedNativeWrite> PlanActionBounds()
    {
        var writes=new List<GuardedNativeWrite>();
        foreach(int rva in new[]{0x309B85,0x309CFB})
        {
            byte[] expected=BitConverter.GetBytes(_actionBoundsExpanded?1024:368);
            if(!CheckedNativeRead.Read(_image+rva,4).SequenceEqual(expected))
                throw new InvalidDataException("Expanded action bound changed outside its owner.");
            if(!_actionBoundsExpanded)writes.Add(new(_image+rva,expected,BitConverter.GetBytes(1024),_image+rva,expected));
        }
        return writes;
    }
    internal List<GuardedNativeWrite> PlanFormulaDispatch(int formula)
    {
        if(formula is <107 or >255)throw new ArgumentOutOfRangeException(nameof(formula));
        var writes=new List<GuardedNativeWrite>();
        byte[] expected=[(byte)_formulaBound];
        if(!CheckedNativeRead.Read(_image+0x309DAD,1).SequenceEqual(expected))
            throw new InvalidDataException("Expanded formula bound changed outside its owner.");
        int bound=Math.Max(_formulaBound,formula-1);
        if(bound!=_formulaBound)writes.Add(new(_image+0x309DAD,expected,[(byte)bound],_image+0x309DAD,expected));
        byte[] original=Convert.FromHexString("41FF94C4C82B6800");
        if(!_formulaRelocated)
            writes.AddRange(NativeTableRelocation.Plan(_image,0x682BC8,Formulas.Address,256*8,
                [new(0x309F53,original,4,0,NativeOperandEncoding.ImageRelativeIndexed)],CheckedNativeRead.Read));
        else
        {
            byte[] live=CheckedNativeRead.Read(_image+0x309F53,8);
            original.AsSpan(4,4).Clear();
            BitConverter.GetBytes(checked((int)(Formulas.Address-_image))).CopyTo(original,4);
            if(!live.SequenceEqual(original))throw new InvalidDataException("Expanded formula dispatch changed outside its owner.");
        }
        return writes;
    }
    internal void AcknowledgeFormulaDispatch(int formula)
    { _formulaRelocated=true;_formulaBound=Math.Max(_formulaBound,formula-1); }
    internal void AcknowledgeActionBounds()=>_actionBoundsExpanded=true;
    internal List<GuardedNativeWrite> Plan(string path)
    {
        byte[] data=File.ReadAllBytes(path);
        using var document=JsonDocument.Parse(data);
        // Consumers may carry release-specific notes alongside the audited
        // relocation instructions.  Only the executable guard, install flag
        // and the effective table instructions determine whether two
        // consumers can safely share this one relocated table set.
        string hash=PlanIdentity(document.RootElement);
        if(_relocated)
        {
            if(hash!=_planHash)throw new InvalidDataException("Shared table relocation plan differs between consumers.");
            return [];
        }
        if(document.RootElement.GetProperty("ExecutableSha256").GetString()!=ChemistPlayableHost.ImageHash ||
            !document.RootElement.GetProperty("Installable").GetBoolean())throw new InvalidDataException("Unaudited native table plan.");
        var seen=new HashSet<string>();var writes=new List<GuardedNativeWrite>();
        foreach(var table in document.RootElement.GetProperty("Tables").EnumerateArray())
        {
            string name=table.GetProperty("Name").GetString()!;
            if(!seen.Add(name))throw new InvalidDataException("Duplicate shared table.");
            var memory=name switch {"AbilityData"=>Common,"SharedUiList"=>UiList,"AbilityVisual"=>Visual,
                _=>throw new InvalidDataException("Unknown shared table.")};
            if(table.GetProperty("ExpandedBytes").GetInt32()>memory.Length)throw new InvalidDataException("Expanded table lacks capacity.");
            var operands=table.GetProperty("VerifiedOperands").EnumerateArray().Select(o=>new NativeTableOperand(
                Convert.ToUInt32(o.GetProperty("InstructionRva").GetString(),16),
                Convert.FromHexString(o.GetProperty("ExpectedInstruction").GetString()!),
                o.GetProperty("DisplacementOffset").GetInt32(),o.GetProperty("FieldOffset").GetInt32(),
                Enum.Parse<NativeOperandEncoding>(o.GetProperty("Encoding").GetString()!))).ToArray();
            foreach(string group in new[]{"PropagatedReferences","ProvenanceInstructions"})
            foreach(var o in table.GetProperty(group).EnumerateArray())
            {
                byte[] expected=Convert.FromHexString(o.GetProperty("ExpectedInstruction").GetString()!);
                if(!CheckedNativeRead.Read(_image+Convert.ToUInt32(o.GetProperty("InstructionRva").GetString(),16),expected.Length).SequenceEqual(expected))
                    throw new InvalidDataException("Shared pointer provenance changed.");
                if(group=="PropagatedReferences"&&o.GetProperty("OriginRvas").GetArrayLength()==0)
                    throw new InvalidDataException("Shared pointer origin missing.");
            }
            writes.AddRange(NativeTableRelocation.Plan(_image,Convert.ToUInt32(table.GetProperty("SourceRva").GetString(),16),
                memory.Address,table.GetProperty("ExpandedBytes").GetInt32(),operands,CheckedNativeRead.Read));
        }
        if(seen.Count!=3)throw new InvalidDataException("Incomplete shared table plan.");
        _planHash=hash;return writes;
    }
    private static string PlanIdentity(JsonElement root)
    {
        var identity=new
        {
            ExecutableSha256=root.GetProperty("ExecutableSha256").GetString(),
            Installable=root.GetProperty("Installable").GetBoolean(),
            Tables=root.GetProperty("Tables").GetRawText()
        };
        byte[] canonical=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(identity);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(canonical));
    }
    internal void AcknowledgeRelocation()=>_relocated=true;
}
