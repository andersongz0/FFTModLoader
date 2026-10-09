using System.Runtime.InteropServices;

namespace GenericJobs;

// Enhanced 1.0.4.0, guarded by the runtime's executable SHA256.
// CurChrChgDisp passes selected-unit flags to FUN_14012ea68, which stores
// them at menu+4A58 before populating the grid. Native generic previews use
// entryData2 bit 6 (0 = male, 1 = female), including for named characters.
internal static class MenuPreviewPolicy
{
    internal const int FlagsOffset = 0x4A58;
    internal const int JobOffset = 0x48;
    internal const int ShapeOffset = 0x60;
    internal const int PaletteOffset = 0x64;

    internal static byte SexFromFlags(byte flags) => (byte)(0x80 + ((flags >> 6) & 1));

    internal static bool TryRewrite(nint menu, nint descriptor, Func<byte, byte, int?> registeredShape)
    {
        if (menu == 0 || descriptor == 0) return false;
        int job = Marshal.ReadInt32(descriptor, JobOffset);
        if (job is < 0 or > byte.MaxValue) return false;
        byte sex = SexFromFlags(Marshal.ReadByte(menu, FlagsOffset));
        int? shape = registeredShape((byte)job, sex);
        if (!shape.HasValue) return false;
        // Do not touch unit/save data, portrait IDs, job stats or other fields.
        Marshal.WriteInt32(descriptor, ShapeOffset, shape.Value);
        Marshal.WriteInt16(descriptor, PaletteOffset, 0);
        return true;
    }
}
