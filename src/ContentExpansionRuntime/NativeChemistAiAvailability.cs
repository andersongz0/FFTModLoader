using System.Buffers.Binary;
using FFTModLoader.ContentExpansion;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>Read-only eligibility leaf on the AI's per-target hot path. No CLR
/// transition, helper call, allocation or mutation; original scoring stays native.</summary>
public static class NativeChemistAiAvailability
{
    public const int LearningSize=16+54*16, CatalogSize=1024*16;
    public static byte[] Catalog(Func<ushort,long> common)
    {
        byte[] table=new byte[CatalogSize];
        for(int i=0;i<1024;i++)table[i*16]=255;
        foreach(ushort ability in ChemistAiActions.All)
        {
            int p=ability*16,slot=ChemistMedicineFamilies.CombatSlot(ability);
            table[p]=(byte)slot;
            int bit=slot switch {0=>0,1=>3,2=>12,3=>13,_=>0};
            table[p+1]=(byte)(0x80>>(bit%8));table[p+2]=(byte)(bit/8);
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(p+4),ChemistAiActions.Item(ability));
            BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(p+8),common(ChemistAiActions.Item(ability)));
        }
        return table;
    }
    public static byte[] Build(long original,long image,long catalog,long stock,long learning)
    {
        if(new[]{original,image,catalog,stock,learning}.Any(p=>p<=0))throw new ArgumentOutOfRangeException(nameof(original));
        var c=new Code();
        // Preserve even private volatile GP contracts, and every SIMD
        // register by never touching it. No CALL and no managed callback here.
        c.Emit("50 51 52 41 50 41 51 41 52 41 53");
        c.Emit("49 BA");c.Pointer(image);
        c.Emit("41 80 BA");c.Int(0x18716A0);c.Emit("06");c.Branch(0x85,"original");
        c.Emit("41 0F B7 82");c.Int(0x18716A2);
        c.Emit("3D FF 03 00 00");c.Branch(0x87,"original");
        c.Emit("48 C1 E0 04 49 B9");c.Pointer(catalog);c.Emit("49 01 C1 41 0F B6 09 80 F9 FF");c.Branch(0x84,"original");
        c.Emit("49 BB");c.Pointer(learning);c.Emit("41 80 3B 01");c.Branch(0x85,"reject");
        c.Emit("41 0F B6 82");c.Int(0x18724CE);c.Emit("83 F8 14");c.Branch(0x87,"reject");
        c.Emit("48 C1 E0 09 4D 8D 82");c.Int(0x1853CE0);c.Emit("49 01 C0 41 80 78 01 FF");c.Branch(0x84,"reject");
        c.Emit("41 0F B7 51 04 41 F6 40 05 30");c.Branch(0x85,"npc");
        c.Emit("81 FA 05 01 00 00");c.Branch(0x83,"extraStock");
        c.Emit("41 80 BC 12");c.Int(0x11A7C00);c.Emit("00");c.Branch(0x84,"reject");c.Jump("learned");
        c.Label("extraStock");c.Emit("48 B8");c.Pointer(stock+NativeItemStockLeaf.CountsOffset);
        c.Emit("80 3C 10 00");c.Branch(0x84,"reject");c.Jump("learned");
        c.Label("npc");c.Emit("49 8B 41 08 0F B6 40 0B 41 3A 40 29");c.Branch(0x87,"reject");
        c.Label("learned");c.Emit("41 F6 40 06 20");c.Branch(0x85,"original");
        c.Emit("83 F9 04");c.Branch(0x83,"extraLearned");
        c.Emit("41 0F B6 51 02 41 0F B6 84 10 A5 00 00 00 41 84 41 01");c.Branch(0x84,"reject");c.Jump("original");
        c.Label("extraLearned");c.Emit("41 0F B6 50 02 83 FA 36");c.Branch(0x83,"reject");
        // Mirror identity must still match the current serialized party. This
        // prevents stale learning after dismissal/replacement or a slot move.
        c.Emit("48 69 C2 58 02 00 00 49 8D 94 02");c.Int(0x11A7D10);
        c.Emit("80 3A 00");c.Branch(0x84,"reject");c.Emit("80 3A FF");c.Branch(0x84,"reject");
        c.Emit("41 0F B6 40 02 48 C1 E0 04 4D 8D 5C 03 10");
        foreach(var pair in new[]{(0,4),(1,5),(4,6),(5,7),(6,8),(0x11C,9),(0x11D,10),(0x124,11)})
        {
            c.Emit("0F B6 82");c.Int(pair.Item1);c.Emit("41 3A 43");c.Byte((byte)pair.Item2);c.Branch(0x85,"reject");
        }
        c.Emit("83 E9 04 41 8B 03 0F A3 C8");c.Branch(0x83,"reject");
        c.Label("original");c.Restore();c.Emit("FF 25 00 00 00 00");c.Pointer(original);
        // Replace only saved RAX; use the same OS-recognizable pop epilogue.
        c.Label("reject");c.Emit("48 C7 44 24 30 00 00 00 00");c.Restore();c.Emit("C3");
        return c.Finish();
    }
    /// <summary>The other per-candidate hot path: CheckLearned(index,command,
    /// slot). No inventory test here; preserve the native learning-only contract.</summary>
    public static byte[] BuildLearned(long original,long image,long learning)
    {
        if(new[]{original,image,learning}.Any(p=>p<=0))throw new ArgumentOutOfRangeException(nameof(original));
        var c=new Code();c.Emit("50 51 52 41 50 41 51 41 52 41 53");
        c.Emit("83 FA 06");c.Branch(0x85,"original");
        c.Emit("41 83 F8 10");c.Branch(0x83,"original");
        c.Emit("83 F9 14");c.Branch(0x87,"reject");
        c.Emit("49 BB");c.Pointer(learning);c.Emit("41 80 3B 01");c.Branch(0x85,"reject");
        c.Emit("49 BA");c.Pointer(image);
        c.Emit("89 C8 48 C1 E0 09 49 8D 94 02");c.Int(0x1853CE0);
        c.Emit("80 7A 01 FF");c.Branch(0x84,"reject");
        c.Emit("41 83 F8 0F");c.Branch(0x83,"reject");
        c.Emit("F6 42 06 20");c.Branch(0x85,"accept");
        c.Emit("41 83 F8 04");c.Branch(0x83,"extra");
        // Potion/Ether/Remedy/PhoenixDown retained bits0,3,12,13.
        c.Emit("44 89 C1 48 B8");c.Pointer(0x000000000D0C0300);
        c.Emit("C1 E1 03 48 D3 E8 0F B6 C8 83 E1 07 B8 80 00 00 00 D3 E8 0F B6 8A A5 00 00 00");
        // The byte offset depends on bit12/13; read it after forming the mask.
        c.Emit("41 83 F8 02");c.Branch(0x82,"retainedByte");
        c.Emit("0F B6 8A A6 00 00 00");
        c.Label("retainedByte");c.Emit("85 C8");c.Branch(0x84,"reject");c.Jump("accept");
        c.Label("extra");c.Emit("0F B6 42 02 83 F8 36");c.Branch(0x83,"reject");
        c.Emit("89 C1 48 69 C0 58 02 00 00 49 8D 94 02");c.Int(0x11A7D10);
        c.Emit("80 3A 00");c.Branch(0x84,"reject");c.Emit("80 3A FF");c.Branch(0x84,"reject");
        c.Emit("48 C1 E1 04 4D 8D 5C 0B 10");
        foreach(var pair in new[]{(0,4),(1,5),(4,6),(5,7),(6,8),(0x11C,9),(0x11D,10),(0x124,11)})
        {c.Emit("0F B6 82");c.Int(pair.Item1);c.Emit("41 3A 43");c.Byte((byte)pair.Item2);c.Branch(0x85,"reject");}
        c.Emit("44 89 C1 83 E9 04 41 8B 03 0F A3 C8");c.Branch(0x83,"reject");
        c.Label("accept");c.Emit("48 C7 44 24 30 01 00 00 00");c.Restore();c.Emit("C3");
        c.Label("reject");c.Emit("48 C7 44 24 30 00 00 00 00");c.Restore();c.Emit("C3");
        c.Label("original");c.Restore();c.Emit("FF 25 00 00 00 00");c.Pointer(original);
        return c.Finish();
    }
    public static byte[] BuildSelectionGate(long original,long observer)
    {
        if(original<=0||observer<=0)throw new ArgumentOutOfRangeException(nameof(original));
        var c=new Code();c.Emit("50 51 52 41 50 41 51 41 52 41 53");
        c.Emit("48 85 C9");c.Branch(0x84,"original");
        c.Emit("80 79 02 06");c.Branch(0x85,"original");
        c.Restore();c.Emit("FF 25 00 00 00 00");c.Pointer(observer);
        c.Label("original");c.Restore();c.Emit("FF 25 00 00 00 00");c.Pointer(original);
        return c.Finish();
    }
    private sealed class Code
    {
        readonly List<byte> bytes=[];
        readonly Dictionary<string,int> labels=[];
        readonly List<(int,string)> branches=[];
        public void Emit(string hex)=>bytes.AddRange(Convert.FromHexString(hex.Replace(" ","")));
        public void Byte(byte b)=>bytes.Add(b);
        public void Pointer(long p)=>bytes.AddRange(BitConverter.GetBytes(p));
        public void Int(int n)=>bytes.AddRange(BitConverter.GetBytes(n));
        public void Label(string name)=>labels.Add(name,bytes.Count);
        public void Branch(byte op,string name){Emit("0F");Byte(op);branches.Add((bytes.Count,name));Int(0);}
        public void Jump(string name){Byte(0xE9);branches.Add((bytes.Count,name));Int(0);}
        public void Restore()=>Emit("41 5B 41 5A 41 59 41 58 5A 59 58");
        public byte[] Finish(){var result=bytes.ToArray();foreach(var (p,n) in branches)BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(p),labels[n]-p-4);return result;}
    }
}
