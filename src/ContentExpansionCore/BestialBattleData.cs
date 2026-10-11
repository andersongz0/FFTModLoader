using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion;

/// <summary>Selection/AI and reaction packets have different layouts. Keep
/// the native layouts and the new ten-bit identities explicit.</summary>
public static class BestialBattleData
{
    public static byte[] Action()
    {
        byte[] action=new byte[20];
        // Zero range + flag 0x02 means native automatic self-use, not a
        // selectable zero-radius area. Immediate, no weapon/item requirement.
        action[3]=2;action[5]=4;action[8]=108;
        action.AsSpan(16,4).Fill(255);
        return action;
    }
    public static byte[] Selection(ReadOnlySpan<byte> packet,ushort ability,byte command)
    {
        if(packet.Length!=20 || !Own(command,ability))throw new InvalidDataException("Unregistered Bestial selection.");
        byte[] output=packet.ToArray();
        // Tmp_UA: command, CT, ability, range, area, item, status[5], AI[3],
        // request, element, MP, unused. Its first byte is NOT a worker ID.
        output[0]=command;output[1]=0;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(2),ability);
        output.AsSpan(4,12).Clear();
        // Preserve the registered action's AI target flags (ai1=0x41),
        // rather than turning its selection into a neutral/untyped action.
        output[13]=0x41;output[17]=0;output[18]=0;
        return output;
    }
    public static bool Own(byte command,int ability)=>command==227&&ability==524 || command==228&&ability==532;
    public static byte[] Commitment(ReadOnlySpan<byte> packet,ReadOnlySpan<byte> worker)
    {
        if(packet.Length!=20||worker.Length!=512||packet[0]!=worker[1]||
            !Own(packet[1],BinaryPrimitives.ReadUInt16LittleEndian(packet[2..])))
            throw new InvalidDataException("Unregistered Bestial reaction packet.");
        byte[] output=packet.ToArray();
        output.AsSpan(4,6).Clear();output[10]=6;output[11]=worker[1];
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(12),worker[0x4F]);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(14),(ushort)(worker[0x51]>>7));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(16),worker[0x50]);
        return output;
    }
}
