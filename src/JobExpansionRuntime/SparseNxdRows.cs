using System.Runtime.InteropServices;

namespace GenericJobs;

// Enhanced 1.0.4.0's single-key NEX row-view ABI (3D3AE8 / 3D3BE4).
// Return the game's existing row union, not an invented row or a cloned job.
internal static class SparseNxdRows
{
    public static bool TryLookup(nint table, uint key, out nint row, out int count)
    {
        row = 0; count = 0;
        if (table == 0 || Marshal.ReadInt64(table, 0x38) != 1 || Marshal.ReadByte(table, 0x41) == 0)
            return false;
        long length = Marshal.ReadInt64(table, 0x28);
        nint rows = Marshal.ReadIntPtr(table, 0x10);
        if (rows == 0 || length is < 1 or > 1000000) return false;
        count = (int)length;
        if (!ReadKey(rows, out uint first) || !ReadKey(rows + (count - 1) * 16, out uint last)) return false;
        uint baseId = unchecked((uint)Marshal.ReadInt32(table, 0x44));
        if (first == baseId && (ulong)last == (ulong)first + (uint)count - 1) return false;
        if (last < first) return false;
        // NEX single-key records are sorted by the builder. UsesBaseRowId's
        // direct ordinal lookup is invalid when keys contain holes.
        int low = 0, high = count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            nint candidate = rows + middle * 16;
            if (!ReadKey(candidate, out uint actual)) return false;
            if (actual == key) { row = candidate; return true; }
            if (actual < key) low = middle + 1; else high = middle - 1;
        }
        return true; // Absent key remains absent; never substitute another row.
    }

    private static bool ReadKey(nint row, out uint key)
    {
        key = 0;
        if ((Marshal.ReadInt64(row) & long.MaxValue) != 1) return false;
        nint record = Marshal.ReadIntPtr(row, 8);
        if (record == 0) return false;
        key = unchecked((uint)Marshal.ReadInt32(record));
        return true;
    }
}
