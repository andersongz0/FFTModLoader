using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion;

public sealed record NativeJpCost(int AbilityId, ushort Cost);

/// <summary>
/// The JP-only integration test never changes a command, learned bit, item,
/// formula or ability classification. All eight live bytes are guarded, but
/// only the two JP bytes are written. This avoids the utility loader's Ability
/// XML writer, which rewrites flags even when only JPCost is specified.
/// </summary>
public static class NativeJpCosts
{
    public const uint AbilityTableRva = 0x787F80;
    public const int NativeCount = 512;
    public const int RecordSize = 8;

    public static IReadOnlyList<GuardedNativeWrite> Plan(long imageBase,
        IReadOnlyList<NativeJpCost> costs, Func<long, int, byte[]> read)
    {
        if (imageBase <= 0 || costs.Count == 0) throw new ArgumentOutOfRangeException(nameof(imageBase));
        var seen = new HashSet<int>();
        var writes = new List<GuardedNativeWrite>();
        foreach (var cost in costs)
        {
            if (cost.AbilityId < 0 || cost.AbilityId >= NativeCount || !seen.Add(cost.AbilityId))
                throw new InvalidDataException("Invalid or duplicate native ability in JP-only test.");
            long address = checked(imageBase + AbilityTableRva + cost.AbilityId * RecordSize);
            byte[] live = read(address, RecordSize);
            if (live.Length != RecordSize) throw new InvalidDataException("Incomplete ability record read.");
            byte[] after = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(after, cost.Cost);
            writes.Add(new(address, live[..2], after, address, live.ToArray()));
        }
        return writes;
    }
}
