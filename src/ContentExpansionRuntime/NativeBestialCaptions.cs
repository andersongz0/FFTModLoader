namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>The legacy timeline stores only the low byte. Resolve names from
/// the same displayed worker/animation, never from AI's reused Tmp_UA.</summary>
public static class NativeBestialCaptions
{
    // The large action heading is a different route from timeline bubbles.
    // DrawUnitStatusIconGrid reads the old packet at animation+17A, while
    // animation+142 retains the full-width action. Rewrite only a matching
    // owned command/action on the animation of the displayed battle worker.
    public static byte[] BuildActionCaption(long original,long image,bool currentAction=false)
    {
        var c=new Code();
        c.Emit("8D 82 00 90 FF FF"); // EAX = EDX - 0x7000
        foreach(int alias in new[]{12,20,268,276,524,532})
        {
            c.Emit("3D");c.Int(alias);c.Branch(0x84,"scan");
        }
        c.JumpLabel("fallback");
        c.Label("scan");c.Emit("41 50 41 51 49 BB");c.Address(image+0xD3A410);
        c.Emit("4D 8B 1B 49 B8");c.Address(image+(currentAction?0xCF873C:0x7DCF9A));
        c.Emit(currentAction?"45 8B 00":"45 0F B7 00");c.Emit("41 B9 80 00 00 00");
        c.Label("loop");c.Emit("4D 85 DB");c.Branch(0x84,"restore");
        c.Emit("49 8B 83 48 01 00 00 48 85 C0");c.Branch(0x84,"next");
        if(currentAction)c.Emit("41 0F B6 43 08 44 39 C0");
        else c.Emit("0F B6 80 BC 01 00 00 66 44 39 C0");
        c.Branch(0x84,"owner");
        c.Label("next");c.Emit("4D 8B 1B 41 FF C9");c.Branch(0x85,"loop");c.JumpLabel("restore");
        c.Label("owner");
        c.Emit("41 0F B6 83 79 01 00 00 3D E3 00 00 00");c.Branch(0x84,"human");
        c.Emit("3D E4 00 00 00");c.Branch(0x85,"restore");
        c.Emit("41 0F B7 83 42 01 00 00 3D 14 02 00 00");c.Branch(0x85,"restore");c.JumpLabel("verify");
        c.Label("human");c.Emit("41 0F B7 83 42 01 00 00 3D 0C 02 00 00");c.Branch(0x85,"restore");
        c.Label("verify");c.Emit("44 8D 90 00 70 00 00 44 39 D2");c.Branch(0x84,"resolved");
        c.Emit("41 89 C2 41 81 E2 FF 01 00 00 41 81 C2 00 70 00 00 44 39 D2");c.Branch(0x84,"resolved");
        c.Emit("41 89 C2 41 81 E2 FF 00 00 00 41 81 C2 00 71 00 00 44 39 D2");c.Branch(0x85,"restore");
        c.Label("resolved");c.Emit("8D 90 00 70 00 00");
        c.Label("restore");c.Emit("41 59 41 58");
        c.Label("fallback");c.Emit("FF 25 00 00 00 00");c.Address(original);
        return c.Finish();
    }
    public static byte[] Build(long original,long image)
    {
        var c=new Code();
        c.Emit("49 BA");c.Address(image+0x2151EA);
        c.Emit("4C 39 14 24");c.Branch(0x84,"caller");
        c.Emit("49 BA");c.Address(image+0x215263);
        c.Emit("4C 39 14 24");c.Branch(0x85,"fallback");
        c.Label("caller");
        c.Emit("49 BA");c.Address(image+0xC6B1CC);
        c.Emit("41 8B 02 83 E8 17 A9 FD FF FF FF");c.Branch(0x85,"animation");
        c.Emit("49 BA");c.Address(image+0x2FD35A4);
        c.Emit("41 0F B6 02 83 F8 14");c.Branch(0x87,"animation");
        c.Emit("C1 E0 09 49 BB");c.Address(image+0x1853CE0);
        c.Emit("49 01 C3 4D 39 DF");c.Branch(0x85,"animation");
        c.Emit("45 0F B6 5A 01 41 0F B7 42 02");
        c.JumpLabel("verify-preview");
        c.Label("animation");c.Emit("48 85 ED");c.Branch(0x84,"fallback");
        c.Emit("44 0F B6 9D 79 01 00 00 0F B7 85 42 01 00 00");
        c.Label("verify-preview");
        c.Emit("41 81 FB E3 00 00 00");c.Branch(0x84,"human");
        c.Emit("41 81 FB E4 00 00 00");c.Branch(0x85,"restore");
        c.Emit("3D 14 02 00 00");c.Branch(0x85,"restore");c.JumpLabel("match");
        c.Label("human");c.Emit("3D 0C 02 00 00");c.Branch(0x85,"restore");
        c.Label("match");
        c.Emit("39 C1");c.Branch(0x84,"resolved");
        c.Emit("41 89 C2 41 81 E2 FF 01 00 00 44 39 D1");c.Branch(0x84,"resolved");
        c.Emit("41 89 C2 41 81 E2 FF 00 00 00 41 81 C2 00 01 00 00 44 39 D1");c.Branch(0x85,"restore");
        c.Label("resolved");c.Emit("89 C1");
        c.Label("restore");
        c.Label("fallback");c.Emit("FF 25 00 00 00 00");c.Address(original);
        return c.Finish();
    }
    private sealed class Code
    {
        readonly List<byte> bytes=[];readonly Dictionary<string,int> labels=[];
        readonly List<(int,string)> branches=[];
        public void Emit(string hex)=>bytes.AddRange(Convert.FromHexString(hex.Replace(" ","")));
        public void Address(long address)=>bytes.AddRange(BitConverter.GetBytes(address));
        public void Int(int value)=>bytes.AddRange(BitConverter.GetBytes(value));
        public void Label(string name)=>labels.Add(name,bytes.Count);
        public void Branch(byte condition,string name){Emit("0F");bytes.Add(condition);branches.Add((bytes.Count,name));bytes.AddRange(new byte[4]);}
        public void JumpLabel(string name){bytes.Add(0xE9);branches.Add((bytes.Count,name));bytes.AddRange(new byte[4]);}
        public byte[] Finish(){byte[] code=bytes.ToArray();foreach(var(offset,label) in branches)System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(offset),labels[label]-offset-4);return code;}
    }
}
