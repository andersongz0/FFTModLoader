using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion;

/// <summary>The enhanced UI consumes eight-byte job/ability pairs, not the legacy word list.</summary>
public static class ChemistUiPairs
{
    public static bool IsChemistRow(ReadOnlySpan<ushort> row) => row.Length==24 &&
        row[..ChemistActionBindings.ActionCount].SequenceEqual(ChemistActionBindings.TestActions.Select(a=>a.AbilityId).ToArray());

    public static (int Count,byte[] Bytes) Build(ReadOnlySpan<ushort> row,int mode,uint learned,
        ReadOnlySpan<byte> nativeLearning,int jp,bool autoLearned)
    {
        if(!IsChemistRow(row)||nativeLearning.Length!=3||mode is <0 or >8||jp<0)
            throw new ArgumentException("Unreviewed enhanced Chemist list.");
        var entries=new List<ushort>();int count=0;
        for(int i=0;i<24;i++)
        {
            ushort id=row[i];if(id==0)continue;
            bool known=autoLearned || (i<ChemistActionBindings.ActionCount?(learned&(1u<<i))!=0:
                (nativeLearning[i/8]&(0x80>>(i%8)))!=0);
            // Only registered actions are rebuilt; the host leaves passive-only
            // and unrelated rows to the game's formatter.
            ushort flags=0;
            if(mode==3){if(!known)count++;continue;}
            if(mode==8){if(known)count++;continue;}
            if(mode is 1 or 5){if(!known)continue;flags=mode==5?(ushort)0x1000:(ushort)0;}
            else if(mode==6 && known)continue;
            else if(mode==0){if(!known)flags=0x4000;}
            else if(known)flags=0x1000;
            else
            {
                // Passive costs are formatted by the native path; this helper
                // is used only for the action category by the installed host.
                if(i>=ChemistActionBindings.ActionCount)throw new ArgumentException("Mixed action/passive cost list is not owned.");
                if(jp<ChemistActionBindings.TestActions[i].JpCost)flags=0x4000;
            }
            entries.Add((ushort)(id|flags));
        }
        if(mode is 3 or 8)return(count,[]);
        byte[] bytes=new byte[(entries.Count+1)*8];
        for(int i=0;i<entries.Count;i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i*8),75);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i*8+2),entries[i]);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(entries.Count*8+2),65535);
        return(entries.Count,bytes);
    }
}
