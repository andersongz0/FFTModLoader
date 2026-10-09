using System.Buffers.Binary;
using FFTModLoader.ContentExpansion;

internal static class NativeRelocationTests
{
    private const long ImageBase = 0x140000000;

    public static void Run(string auditPath)
    {
        using var audit = System.Text.Json.JsonDocument.Parse(File.ReadAllText(auditPath));
        var table = audit.RootElement.GetProperty("Tables")[0];
        var rows = table.GetProperty("VerifiedOperands").EnumerateArray().ToArray();
        var instructions = new Dictionary<long, byte[]>();
        var operands = rows.Select(row =>
        {
            uint rva = Convert.ToUInt32(row.GetProperty("InstructionRva").GetString(), 16);
            byte[] bytes = Convert.FromHexString(row.GetProperty("ExpectedInstruction").GetString()!);
            instructions.Add(ImageBase+rva, bytes.ToArray());
            uint dispRva = Convert.ToUInt32(row.GetProperty("OperandRva").GetString(), 16);
            return new NativeTableOperand(rva, bytes, checked((int)(dispRva-rva)),
                row.GetProperty("FieldOffset").GetInt32(),
                Enum.Parse<NativeOperandEncoding>(row.GetProperty("Encoding").GetString()!));
        }).ToArray();
        byte[] Read(long address, int count)
        {
            var entry = instructions.First(kv => kv.Key <= address && address+count <= kv.Key+kv.Value.Length);
            return entry.Value.AsSpan((int)(address-entry.Key), count).ToArray();
        }
        void Write(long address, byte[] bytes)
        {
            var entry = instructions.First(kv => kv.Key <= address && address+bytes.Length <= kv.Key+kv.Value.Length);
            bytes.CopyTo(entry.Value, (int)(address-entry.Key));
        }
        var baseline = instructions.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        long near = ImageBase+0x5000000;
        var plan = NativeTableRelocation.Plan(ImageBase, 0x787F80, near, 524*8, operands, Read);
        Check(plan.Count == 29, "Reviewed ability operand count changed; re-audit before using plan");
        NativeTableRelocation.Apply(plan, Read, Write);
        foreach (var (patch, operand) in plan.Zip(operands))
        {
            long origin = operand.Encoding == NativeOperandEncoding.RipRelative
                ? ImageBase+operand.InstructionRva+operand.ExpectedInstruction.Length : ImageBase;
            Check(origin+BinaryPrimitives.ReadInt32LittleEndian(Read(patch.Address,4)) == near+operand.FieldOffset,
                "Relocation points at wrong field");
        }
        foreach (var kv in baseline) instructions[kv.Key] = kv.Value.ToArray();
        int calls = 0;
        try
        {
            NativeTableRelocation.Apply(plan, Read, (address, bytes) =>
            {
                if (++calls == 3) { Write(address, bytes[..2]); throw new IOException("Simulated partial write"); }
                Write(address,bytes);
            });
            throw new Exception("Simulated write failure was accepted");
        }
        catch (IOException) { }
        Check(baseline.All(kv => kv.Value.SequenceEqual(instructions[kv.Key])), "Rollback changed original bytes");
        Reject(() => NativeTableRelocation.Plan(ImageBase, 0x787F80, near+(1L<<33), 524*8, operands, Read));
        Reject(() => NativeTableRelocation.Plan(ImageBase, 0x787F80, near, 1, operands, Read));
        Reject(() => NativeTableRelocation.Plan(ImageBase, 0x787F80, near, 524*8, operands.Concat(new[]{operands[0]}).ToArray(), Read));
        Reject(() => NativeTableRelocation.Plan(ImageBase, 0x787F81, near, 524*8, operands, Read));
        instructions.First().Value[0] ^= 1;
        int attempted = 0;
        Reject(() => NativeTableRelocation.Apply(plan, Read, (_,_) => attempted++));
        Check(attempted == 0, "Changed operand triggered a partial transaction");
        Console.WriteLine("PASS: 29 decoded ability table operands, signed near-address resolution, whole-plan preflight and rollback after partial write. Tested in a simulated image only; no native runtime activation.");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Invalid relocation accepted");
    }
}
