using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion;

/// <summary>
/// Calculated on a copy only. Mirrors the audited 49-byte JobData and native
/// UnitSetup/SetUnitRSMFlags arithmetic, without calling helpers that refill
/// resources, generate new bases, reset statuses or touch the party save.
/// Uses this person's existing 24-bit raw stat bases with the monster class's
/// multipliers, not a freshly randomized/recruited monster's bases.
/// </summary>
public static class MonsterFormAttributes
{
    public const int JobRecordSize=49;
    public static byte[] Calculate(ReadOnlySpan<byte> worker,ReadOnlySpan<byte> job)
    {
        if(worker.Length!=ReversibleBattleForms.WorkerSize || job.Length!=JobRecordSize ||
            worker[0] is not (0x80 or 0x81))
            throw new InvalidDataException("Human worker and a complete monster job record required.");
        var result=worker.ToArray();
        job.Slice(1,8).CopyTo(result.AsSpan(0x0A));
        job.Slice(9,5).CopyTo(result.AsSpan(0x52));
        job.Slice(14,10).CopyTo(result.AsSpan(0x8A));
        job.Slice(27,5).CopyTo(result.AsSpan(0x57));
        job.Slice(32,5).CopyTo(result.AsSpan(0x5C));
        job.Slice(42,4).CopyTo(result.AsSpan(0x76));result[0x7A]=0;
        result.AsSpan(0x1A,14).Clear(); // no active weapon/armor bonuses in beast form
        result.AsSpan(0x3B,3).Clear();
        result.AsSpan(0x44,11).Clear();result[0x4B]=job[26];
        result.AsSpan(0x94,11).Clear();
        for(int offset=0x0A;offset<=0x10;offset+=2)AddPassive(result,Read16(result,offset));
        foreach(int offset in new[]{0x14,0x16,0x18})AddPassive(result,Read16(result,offset));
        bool specialCap=(worker[6]&4)!=0;
        for(int stat=0;stat<5;stat++)
        {
            int at=0x7B+stat*3;
            uint raw=(uint)(worker[at]|worker[at+1]<<8|worker[at+2]<<16);
            uint multiplier=job[15+stat*2];
            uint denominator=(uint)(stat<2&&specialCap?10:100);
            int value=(int)(((ulong)raw*multiplier/denominator)>>14);
            value=Math.Max(value,1);
            if(stat<2)
            {
                value=Math.Min(value,specialCap?ushort.MaxValue:999);
                // Native Max HP Boost is support flag 0x9B bit 2.
                if(stat==0 && (result[0x9B]&2)!=0)value=value*6/5;
                Write16(result,stat==0?0x32:0x36,value);
            }
            else
            {
                value=Math.Min(value,stat==2?50:99);
                int baseOffset=stat==2?0x3A:stat==3?0x38:0x39;
                result[baseOffset]=(byte)value;result[baseOffset+6]=(byte)value;
            }
        }
        byte movement=result[0x9C];
        result[0x42]=(byte)Math.Min(252,job[24]+((movement&0x80)!=0?1:0)+
            ((movement&0x40)!=0?2:0)+((movement&0x20)!=0?3:0));
        result[0x43]=(byte)Math.Min(7,(job[25]&0x7F)+((movement&0x10)!=0?1:0)+
            ((movement&8)!=0?2:0)+((movement&4)!=0?3:0));
        return result;
    }
    private static void AddPassive(byte[] result,int ability)
    {
        // Native passive identity -> eleven-byte MSB-first R/S/M bitset.
        int bit=ability-422;
        if(bit is <0 or >=88)return;
        if(ability<454)Write16(result,0x14,ability);
        result[0x94+bit/8]|=(byte)(0x80>>(bit%8));
    }
    private static int Read16(ReadOnlySpan<byte> bytes,int at)=>BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);
    private static void Write16(Span<byte> bytes,int at,int value)=>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[at..],(ushort)Math.Min(value,ushort.MaxValue));
}
