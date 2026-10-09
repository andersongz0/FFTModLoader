using System.Buffers.Binary;
using FFTModLoader.ContentExpansion;

static class ChemistActionBindingsTests
{
    public static void Run()
    {
        Require(ChemistActionBindings.TestActions.Select(a=>a.AbilityId).SequenceEqual(new ushort[]{368,371,380,381}.Concat(Enumerable.Range(513,11).Select(x=>(ushort)x))), "Playable action identities differ");
        for(uint mask=0;mask<=ChemistActionBindings.ExtraMask;mask++)
            Require(ChemistActionBindings.Learned(new byte[3],mask)==(mask<<4),"Independent extra learning bits aliased.");
        for (uint bits=0;bits<0x1000000;bits+=37)
        {
            byte[] original=[(byte)(bits>>16),(byte)(bits>>8),(byte)bits];
            byte[] copy=original.ToArray();
            uint got=ChemistActionBindings.Learned(original,false);
            int[] native=[0,3,12,13];
            for (int i=0;i<4;i++)
            {
                bool expected=(original[native[i]/8]&(0x80>>(native[i]%8)))!=0;
                Require(((got&(1u<<i))!=0)==expected, "Retained native learning remapped incorrectly");
            }
            Require(ChemistActionBindings.Learned(original,true)==(got|16),"Extra learning aliased a native bit");
            Require(original.SequenceEqual(copy),"Learning projection mutated native bits");
        }
        for (int slot=0;slot<4;slot++)
        {
            byte[] native=[0x21,0x05,0xB3]; byte[] before=native.ToArray();
            ChemistActionBindings.LearnRetained(native,slot);
            int[] old=[0,3,12,13]; before[old[slot]/8]|=(byte)(0x80>>(old[slot]%8));
            Require(native.SequenceEqual(before),"Retained learn changed a removed/passive ability bit");
        }
        for (uint flags=0;flags<256;flags++)for(int modified=0;modified<256;modified++)for(int state=0;state<3;state++)
            Require(ChemistActionBindings.Consumes(flags,(byte)modified,state)==((flags&1)!=0&&(modified&0x30)==0&&state==0),"Preview/reaction consumed stock");
        byte[] unit=new byte[0x2E];
        for(int slot=0;slot<54;slot++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(unit.AsSpan(0x2C),(ushort)slot);
            Require(ChemistActionBindings.SerializedSlot(unit)==slot,"Menu index substituted for persistent save slot");
        }
        BinaryPrimitives.WriteUInt16LittleEndian(unit.AsSpan(0x2C),54);
        try {ChemistActionBindings.SerializedSlot(unit);throw new Exception("Invalid binding accepted");}catch(InvalidDataException){}
        Require(ChemistActionBindings.RecordHashes(new byte[54*600]).Count==54,"Incomplete serialized native array");
        Require(ChemistMedicineFamilies.Ether.SequenceEqual(new ushort[]{371,372})&&ChemistMedicineFamilies.Remedy.SequenceEqual(new ushort[]{374,375,376,377,378,379,380}),"Native medicine families/order changed");
        Require(ChemistMedicineFamilies.All.Length==13&&ChemistMedicineFamilies.All.Distinct().Count()==13,"Native medicine family aliases");
        foreach(int group in new[]{0,1,2})
        {
            ushort[] family=ChemistMedicineFamilies.ForSlot(group);
            foreach(ushort ability in family)
            {
                Require(ChemistMedicineFamilies.CombatSlot(ability)==group&&ChemistMedicineFamilies.Item(ability)==ability-128,"Medicine group/item identity differs");
                Require(ChemistMedicineFamilies.HasStock(group,item=>item==ability-128?1:0),"Variant stock does not enable its group");
            }
            Require(!ChemistMedicineFamilies.HasStock(group,_=>0),"Empty medicine family enabled");
        }
        foreach(ushort ability in new ushort[]{367,381,393,513,523,524,1024,65535})
            Require(ChemistMedicineFamilies.Group(ability)==-1&&ChemistMedicineFamilies.CombatSlot(ability)==ChemistActionBindings.Slot(ability),"Medicine group widened unrelated identity");
        Console.WriteLine("PASS: playable Chemist identities, independent extra learning, retained/passive native bits, all commitment/preview/reaction conditions and 54 serialized slot bindings.");
    }
    private static void Require(bool ok,string message){if(!ok)throw new Exception(message);}
}
