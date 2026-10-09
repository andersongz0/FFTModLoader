using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion;

/// <summary>
/// Supported binary slot envelope observed at PrepareSave (032B94) and the
/// read completion (033DF0). This is an entry BEFORE PNG/UMIF compression, not
/// the whole save container, and not the 9CE4 legacy party working buffer.
/// XML/other slot kinds must go through the native code, never this decoder.
/// </summary>
public static class NativeSavePacket
{
    public const int HeaderSize = 16;
    public const int BinaryKind = 0x11;
    public const int ManualBinaryKind = 0x0E;
    public const int MaxBytes = 16 * 1024 * 1024;

    public static string ValidateAndHash(ReadOnlySpan<byte> entry)
    {
        if (entry.Length <= HeaderSize || entry.Length > MaxBytes ||
            BinaryPrimitives.ReadUInt32LittleEndian(entry) != HeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]) is not (BinaryKind or ManualBinaryKind))
            throw new InvalidDataException("Not the audited binary save slot envelope.");
        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
        if (expected != Crc32(entry[HeaderSize..]))
            throw new InvalidDataException("Native save CRC mismatch; no expanded state may be associated.");
        return ExpandedSaveRegistry.Hash(entry);
    }

    // Standard complemented CRC32, independently checked against FUN_025D74.
    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
        }
        return ~crc;
    }
}
