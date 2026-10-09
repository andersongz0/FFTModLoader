using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion;

public enum NativeOperandEncoding { RipRelative, ImageRelativeIndexed }

/// <summary>Independently decoded operand, never an inferred Ghidra reference.</summary>
public sealed record NativeTableOperand(uint InstructionRva, byte[] ExpectedInstruction,
    int DisplacementOffset, int FieldOffset, NativeOperandEncoding Encoding);

public sealed record GuardedNativeWrite(long Address, byte[] Before, byte[] After,
    long InstructionAddress, byte[] ExpectedInstruction);

/// <summary>
/// Builds all operand writes before any mutation. The host must also guard the
/// executable hash, use a near allocation and validate every pending capability.
/// A relocation is not an item/ability expansion on its own.
/// </summary>
public static class NativeTableRelocation
{
    public static IReadOnlyList<GuardedNativeWrite> Plan(long imageBase, uint sourceTableRva, long destination,
        int tableLength, IReadOnlyList<NativeTableOperand> operands,
        Func<long, int, byte[]> read)
    {
        if (imageBase <= 0 || destination <= 0 || tableLength <= 0 || operands.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(destination));
        var writes = new List<GuardedNativeWrite>();
        var bytesClaimed = new HashSet<long>();
        foreach (var operand in operands)
        {
            if (!Enum.IsDefined(operand.Encoding) || operand.ExpectedInstruction.Length is < 1 or > 15 ||
                operand.DisplacementOffset < 0 || operand.DisplacementOffset + 4 > operand.ExpectedInstruction.Length ||
                operand.FieldOffset < 0 || operand.FieldOffset >= tableLength)
                throw new InvalidDataException("Invalid independently decoded native operand.");
            long instruction = checked(imageBase + operand.InstructionRva);
            if (!read(instruction, operand.ExpectedInstruction.Length).SequenceEqual(operand.ExpectedInstruction))
                throw new InvalidDataException($"Instruction changed at RVA {operand.InstructionRva:X}; no writes planned.");
            long origin = operand.Encoding == NativeOperandEncoding.RipRelative
                ? checked(instruction + operand.ExpectedInstruction.Length) : imageBase;
            long expectedSource = checked(imageBase + sourceTableRva + operand.FieldOffset - origin);
            if (expectedSource != BinaryPrimitives.ReadInt32LittleEndian(
                operand.ExpectedInstruction.AsSpan(operand.DisplacementOffset, 4)))
                throw new InvalidDataException("Audited operand does not point at the requested source table field.");
            long displacement = checked(destination + operand.FieldOffset - origin);
            if (displacement < int.MinValue || displacement > int.MaxValue)
                throw new InvalidDataException("Expanded table is outside signed displacement range; near allocation required.");
            long address = checked(instruction + operand.DisplacementOffset);
            for (int b = 0; b < 4; b++)
                if (!bytesClaimed.Add(checked(address + b)))
                    throw new InvalidDataException("Overlapping or duplicate native operand writes.");
            byte[] before = operand.ExpectedInstruction.AsSpan(operand.DisplacementOffset, 4).ToArray();
            byte[] after = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(after, (int)displacement);
            writes.Add(new GuardedNativeWrite(address, before, after, instruction,
                operand.ExpectedInstruction.ToArray()));
        }
        return writes;
    }

    // This method does not call WriteProcessMemory/VirtualProtect. It requires a
    // host-supplied protected native writer and is tested in a fake image only.
    // Do NOT call with an incomplete plan or run on a live game thread yet.
    public static void Apply(IReadOnlyList<GuardedNativeWrite> writes,
        Func<long, int, byte[]> read, Action<long, byte[]> write)
    {
        foreach (var patch in writes)
            if (!read(patch.InstructionAddress, patch.ExpectedInstruction.Length).SequenceEqual(patch.ExpectedInstruction) ||
                !read(patch.Address, patch.Before.Length).SequenceEqual(patch.Before))
                throw new InvalidDataException("Native operands changed since planning; transaction not started.");
        var attempted = new List<GuardedNativeWrite>();
        try
        {
            foreach (var patch in writes)
            {
                // A native writer might fail after a partial copy. Roll back
                // the attempted write too, not only completed writes.
                attempted.Add(patch);
                write(patch.Address, patch.After);
                if (!read(patch.Address, patch.After.Length).SequenceEqual(patch.After))
                    throw new IOException("Native write verification failed.");
            }
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            foreach (var patch in attempted.AsEnumerable().Reverse())
            {
                try
                {
                    write(patch.Address, patch.Before);
                    if (!read(patch.Address, patch.Before.Length).SequenceEqual(patch.Before))
                        throw new IOException("Native rollback verification failed.");
                }
                catch (Exception rollbackFailure) { failures.Add(rollbackFailure); }
            }
            if (failures.Count > 1)
                throw new AggregateException("Native relocation failed and rollback was incomplete; host must stop initialization.", failures);
            throw;
        }
    }
}
