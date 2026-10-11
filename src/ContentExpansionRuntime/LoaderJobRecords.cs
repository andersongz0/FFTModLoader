using System.Text.Json;
using FFTModLoader.ContentExpansion;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>Owns the expanded physical JOB_DATA array and every audited native reader.
/// The old 174 x 49 byte array ends two bytes before AbilityData. It MUST NOT be
/// extended in place by the legacy XML writer, even if that writer accepts 176 rows.</summary>
internal sealed class LoaderJobRecords
{
    internal const int RecordSize=49, OriginalCount=174, Capacity=256;
    private readonly long _image;
    private readonly NativeLifetimeMemory _records;
    private readonly Dictionary<int,(string Owner,byte[] Bytes)> _registered=[];
    private bool _relocated;
    private int _bound=OriginalCount;
    internal long Address=>_records.Address;
    internal LoaderJobRecords(long image)
    {
        _image=image;
        _records=new(Capacity*RecordSize,image);
        // Preserve original records AFTER ordinary mods have applied their edits.
        _records.Write(0,CheckedNativeRead.Read(image+0x785E30,OriginalCount*RecordSize));
    }
    internal List<GuardedNativeWrite> Plan(string path,ExpandedContentDefinition content)
    {
        using var json=JsonDocument.Parse(File.ReadAllBytes(path));
        var root=json.RootElement;
        if(root.GetProperty("SchemaVersion").GetInt32()!=1 || root.GetProperty("ModId").GetString()!=content.ModId)
            throw new InvalidDataException("Expanded job-record owner differs.");
        var pending=new Dictionary<int,byte[]>();
        foreach(var entry in root.GetProperty("Entries").EnumerateArray())
        {
            int id=entry.GetProperty("JobId").GetInt32();
            byte[] bytes=Convert.FromHexString(entry.GetProperty("NativeRecordHex").GetString()!);
            // The currently audited byte-sized exclusive bounds support through 254.
            // Job 255 needs separately widened compares; never wrap 256 to zero.
            if(id is <OriginalCount or >=255 || bytes.Length!=RecordSize || !pending.TryAdd(id,bytes) ||
                !content.Ids.Any(i=>i.Table==ExpandedTable.Job&&i.Id==id&&!i.Reserved) ||
                !content.Commands.Any(c=>c.Id==bytes[0]))
                throw new InvalidDataException("Invalid, original or unregistered expanded job record.");
            if(_registered.TryGetValue(id,out var prior) && (prior.Owner!=content.ModId||!prior.Bytes.SequenceEqual(bytes)))
                throw new InvalidDataException("Expanded job record already owned.");
        }
        if(pending.Count==0 || content.Ids.Count(i=>i.Table==ExpandedTable.Job&&!i.Reserved)!=pending.Count)
            throw new InvalidDataException("Incomplete expanded job records.");
        int bound=Math.Max(_bound,pending.Keys.Max()+1);
        var writes=new List<GuardedNativeWrite>();
        if(!_relocated)
            writes.AddRange(NativeTableRelocation.Plan(_image,0x785E30,Address,Capacity*RecordSize,Operands,CheckedNativeRead.Read));
        foreach(var(rva,hex,offset,width) in Bounds)
        {
            byte[] expected=Convert.FromHexString(hex);
            if(width==4)BitConverter.GetBytes(_bound).CopyTo(expected,offset);else expected[offset]=(byte)_bound;
            if(!CheckedNativeRead.Read(_image+rva,expected.Length).SequenceEqual(expected))
                throw new InvalidDataException($"Job table bound changed at {rva:X}.");
            if(bound!=_bound)
                writes.Add(new(_image+rva+offset,expected.AsSpan(offset,width).ToArray(),
                    width==4?BitConverter.GetBytes(bound):[(byte)bound],_image+rva,expected));
        }
        // Only owned, unpublished storage changes here. Code changes are guarded
        // and applied together by the host before activation on the startup gate.
        foreach(var(id,bytes) in pending)
        { _records.Write(id*RecordSize,bytes);_registered[id]=(content.ModId,bytes); }
        return writes;
    }
    internal void Acknowledge()=> (_relocated,_bound)=(true,Math.Max(OriginalCount,_registered.Keys.Max()+1));

    // Independently decoded from the supported executable (SHA guarded by host).
    // Propagated loads aren't patched: they follow the relocated base operand.
    private static NativeTableOperand Rip(uint rva,string hex,int field=0)
    { byte[] bytes=Convert.FromHexString(hex);return new(rva,bytes,bytes.Length-4,field,NativeOperandEncoding.RipRelative); }
    private static NativeTableOperand Indexed(uint rva,string hex)
    { byte[] bytes=Convert.FromHexString(hex);return new(rva,bytes,bytes.Length-4,0,NativeOperandEncoding.ImageRelativeIndexed); }
    private static readonly NativeTableOperand[] Operands=
    [
        Rip(0x21E043,"488B05177E5600",49),Rip(0x21E04F,"488B153C7E5600",98),Rip(0x21E05B,"488B15617E5600",147),
        Rip(0x21E071,"488D05B87D5600"),Rip(0x21E136,"488D05F37C5600"),
        Indexed(0x27213C,"0FB6B401305E7800"),Rip(0x275DE3,"488D0554005100",14),
        Rip(0x2761F7,"3A0D64FC5000",49),Rip(0x2761FF,"3A0D8DFC5000",98),Rip(0x276207,"3A0DB6FC5000",147),
        Indexed(0x276216,"428A8401305E7800"),Indexed(0x276240,"460FB6AC01305E7800"),Indexed(0x2762FA,"440FB6AC11305E7800"),
        Indexed(0x276B43,"428A8429305E7800"),Rip(0x276C7E,"488D15ABF15000"),
        Rip(0x277219,"4C8D1D10EC5000"),Rip(0x2774A7,"4C8D1D82E95000"),Rip(0x27790E,"488D0D35E55000",26),
        Rip(0x278265,"488D05C4DB5000"),Rip(0x278448,"488D05E1D95000"),Rip(0x278713,"4C8D1516D75000"),
        Rip(0x278958,"4C8D15D1D45000"),Rip(0x278B61,"488D05D6D25000",14),
        Rip(0x28676B,"488D05BEF64F00"),Rip(0x286809,"4C8D3520F64F00"),Rip(0x2893AF,"488D057ACA4F00"),
        Rip(0x28A40B,"488D3D1EBA4F00"),Rip(0x28A84E,"488D05DBB54F00"),Rip(0x28AC98,"488D0591B14F00"),
        Rip(0x29FEAF,"488D35A46D4E00",3626),Rip(0x2A7777,"488D05B3E64D00",1),Rip(0x2B56C9,"488D0560074D00"),
        Rip(0x2BA300,"488D0529BB4C00"),Rip(0x2DB402,"488D0527AA4A00"),Rip(0x300959,"488D05D0544800"),
        Rip(0x300F0D,"488D051C4F4800"),Rip(0x32ABC4,"488D0565B24500"),Rip(0x348796,"488D0593D64300"),
        Rip(0x34887B,"488D05AED54300"),Rip(0x39265A,"4C8D15CF373F00")
    ];
    private static readonly (uint Rva,string Instruction,int Offset,int Width)[] Bounds=
    [
        (0x286759,"B9AE000000",1,4),(0x286803,"41BBAE000000",2,4),(0x289399,"B8AE000000",1,4),
        (0x28A406,"BBAE000000",1,4),(0x28A839,"B9AE000000",1,4),(0x28AC88,"B9AE000000",1,4),
        (0x2A7765,"41817E48AE000000",4,4),(0x2B56BF,"4180F9AE",3,1),(0x2DB3F8,"4180F8AE",3,1),
        (0x30094F,"4180F8AE",3,1),(0x300F03,"4180F8AE",3,1),(0x32ABBB,"80FAAE",2,1),
        (0x348787,"813EAE000000",2,4),(0x34886C,"81FBAE000000",2,4),(0x392654,"41B9AE000000",2,4)
    ];
}
