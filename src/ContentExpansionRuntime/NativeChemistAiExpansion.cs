using System.Buffers.Binary;
using System.Text.Json;
using FFTModLoader.ContentExpansion;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>Relocate all native candidate-bank users, including biased Tmp_UA
/// accesses. Human action-list buffers stay unchanged. Native movement, target
/// scoring and resumable/yielding AI controllers remain the original bodies.</summary>
public static class NativeChemistAiExpansion
{
    public const int SourceRva=0x1872598,OriginalCapacity=34;
    public static IReadOnlyList<GuardedNativeWrite> Plan(long image,long destination,string path,
        Func<long,int,byte[]> read)
    {
        using var doc=JsonDocument.Parse(File.ReadAllBytes(path));var root=doc.RootElement;
        if(root.GetProperty("ExecutableSha256").GetString()!=ChemistPlayableHost.ImageHash ||
           root.GetProperty("SourceRva").GetInt32()!=SourceRva || root.GetProperty("Capacity").GetInt32()!=64 ||
           root.GetProperty("OriginalCapacity").GetInt32()!=34 || root.GetProperty("OriginalRows").GetInt32()!=16)
            throw new InvalidDataException("Unsupported AI candidate-bank review.");
        var operands=root.GetProperty("Operands").EnumerateArray().Select(p=>new NativeTableOperand(
            (uint)p.GetProperty("InstructionRva").GetInt32(),Convert.FromHexString(p.GetProperty("ExpectedInstruction").GetString()!),
            p.GetProperty("DisplacementOffset").GetInt32(),p.GetProperty("FieldOffset").GetInt32(),
            Enum.Parse<NativeOperandEncoding>(p.GetProperty("Encoding").GetString()!))).ToArray();
        if(operands.Length!=30)throw new InvalidDataException("Incomplete AI bank operand review.");
        var writes=NativeTableRelocation.Plan(image,SourceRva,destination,64*16*4,operands,read).ToList();
        var biased=root.GetProperty("BiasedOperands").EnumerateArray().ToArray();
        if(biased.Length!=9)throw new InvalidDataException("Incomplete Tmp_UA biased references.");
        foreach(var p in biased)
        {
            long instruction=image+p.GetProperty("InstructionRva").GetInt32();
            byte[] expected=Convert.FromHexString(p.GetProperty("ExpectedInstruction").GetString()!);
            int offset=p.GetProperty("DisplacementOffset").GetInt32(),field=p.GetProperty("FieldOffset").GetInt32();
            if(p.GetProperty("SourceBaseRva").GetInt32()!=0x18716A0 || field is <0 or >3 ||
               !read(instruction,expected.Length).SequenceEqual(expected) ||
               BinaryPrimitives.ReadInt32LittleEndian(expected.AsSpan(offset))!=0xEF8+field)
                throw new InvalidDataException("Changed biased AI operand.");
            byte[] before=expected.AsSpan(offset,4).ToArray();
            byte[] after=BitConverter.GetBytes(checked((int)(destination+field-image-0x18716A0)));
            writes.Add(new(instruction+offset,before,after,instruction,expected));
        }
        var bounds=root.GetProperty("Bounds").EnumerateArray().ToArray();
        if(bounds.Length!=25)throw new InvalidDataException("Incomplete native AI stride/bounds review.");
        foreach(var p in bounds)
        {
            long address=image+p.GetProperty("InstructionRva").GetInt32();
            byte[] before=Convert.FromHexString(p.GetProperty("Before").GetString()!),after=Convert.FromHexString(p.GetProperty("After").GetString()!);
            if(before.Length!=after.Length || !read(address,before.Length).SequenceEqual(before))
                throw new InvalidDataException("Changed native AI capacity instruction.");
            writes.Add(new(address,before,after,address,before));
        }
        return writes;
    }
}
