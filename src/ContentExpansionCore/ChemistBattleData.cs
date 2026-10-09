using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion;

/// <summary>Full-width transport; no native item-byte or item+128 alias.</summary>
public static class ChemistBattleData
{
    public const byte Formula = 107;
    public static byte[] Action(byte status)
    {
        // Native 20-byte action schema: range, area, vertical, flags[4],
        // element, formula, Z, unused, status index, CT, MP, unused[2],
        // four target restrictions. Immediate medicine, not a Faith spell.
        byte[] result = new byte[20];
        // Native AOE is a radius: zero means exactly one tile. One would
        // include the four neighbours, unlike medicine's single target.
        result[0]=4; result[1]=0; result[2]=2; result[5]=4;
        result[8]=Formula; result[11]=status;
        result.AsSpan(16,4).Fill(255);
        return result;
    }
    public static byte[] Commitment(ReadOnlySpan<byte> input)
    {
        if(input.Length!=20 || input[0]>20 || input[1]!=6)
            throw new InvalidDataException("Not a bound Chemist reaction packet.");
        ushort ability=BinaryPrimitives.ReadUInt16LittleEndian(input[2..]);
        int slot=ChemistActionBindings.Slot(ability);
        if(slot<0)throw new InvalidDataException("Unregistered Chemist action.");
        byte[] result=input.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(8),ChemistActionBindings.Item(slot));
        return result;
    }
    public static byte[] Selection(ReadOnlySpan<byte> nativePacket, int slot, byte status,
        ReadOnlySpan<byte> nativeStatus, ReadOnlySpan<byte> commonAi)
    {
        if(nativePacket.Length!=20 || nativeStatus.Length!=5 || commonAi.Length!=4 || slot<0 || slot>=ChemistActionBindings.ActionCount)
            throw new InvalidDataException("Incomplete native selection packet.");
        byte[] result=nativePacket.ToArray();
        result[0]=6;result[1]=0;result[4]=4;result[5]=0;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6),ChemistActionBindings.Item(slot));
        nativeStatus.CopyTo(result.AsSpan(8,5));commonAi[..3].CopyTo(result.AsSpan(13,3));
        // Selection byte 16 is a request flag, NOT actionCommon.ai4. Preserve
        // the native request and mark medicine exactly like command-kind 1.
        result[15]|=0x40;
        result[17]=0;result[18]=0;
        return result;
    }
}
