using System.Runtime.InteropServices;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>Read only committed, readable pages; reject null/overflow/guard pages.</summary>
public static unsafe class CheckedNativeRead
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
    public static byte[] Read(long address, int length)
    {
        if (address < 0x10000 || length < 0 || length > 16 * 1024 * 1024)
            throw new InvalidDataException("Invalid native read range.");
        long end = checked(address + length);
        long current = address;
        while (current < end)
        {
            // Blittable native ABI: no Marshal.SizeOf reflection/internal-call
            // work on gameplay observers (seen in PID 1376's JIT stack).
            if (VirtualQuery((nint)current, out var information, (nuint)sizeof(MemoryInformation)) == 0 ||
                information.State != 0x1000 || (information.Protect & (0x100 | 0x01)) != 0 ||
                (information.Protect & 0xFF) is not (0x02 or 0x04 or 0x08 or 0x20 or 0x40 or 0x80))
                throw new InvalidDataException("Native save memory is not committed/readable.");
            long regionEnd = checked((long)information.BaseAddress + (long)information.RegionSize);
            if (regionEnd <= current) throw new InvalidDataException("Invalid native memory region.");
            current = Math.Min(end, regionEnd);
        }
        var bytes = new byte[length];
        Marshal.Copy((nint)address, bytes, 0, length);
        return bytes;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(nint address, out MemoryInformation information, nuint length);
}
