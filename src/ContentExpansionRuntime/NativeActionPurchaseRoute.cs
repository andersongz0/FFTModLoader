using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>
/// Native-only replacement for the WHOLE Chemist purchase confirmation,
/// not UnitLearnAbility (whose caller subtracts JP again). LAB ONLY.
/// No installer or save-session host exists here. A production host must
/// durably reconcile the pending transaction, publish a fresh immutable frame,
/// and implement native refresh/mastery/sound notifications before enabling.
/// Publishing/reconciliation must be on the audited game UI thread, never
/// mutate or free a published frame. An atomic pending claim blocks duplicates.
/// Never call the original new-action confirmation on stale/duplicate input:
/// its native search may be unbounded. Retained actions use their ORIGINAL
/// learning bits, not their reordered menu slots. No managed callback.
/// </summary>
public static class NativeActionPurchaseRoute
{
    public const int StateLength = 56;
    public const int UnitIdOffset = 2;
    public const int PartyPointerOffset = 8;
    public const int ExpectedJpOffset = 16;
    public const int ExpectedNativeBitsOffset = 20;
    public const int ExpandedBitsPointerOffset = 24;
    public const int ExpectedExpandedBitsOffset = 32;
    public const int CostsPointerOffset = 40;
    public const int PendingOffset = 48;
    public const int SelectedUnitRva = 0x1811428;

    public static byte[] Build(long original,long state,long imageBase)
    {
        if(original<=0||state<=0||imageBase<=0||original==state)
            throw new ArgumentOutOfRangeException(nameof(original));
        _=checked(state+StateLength);_=checked(imageBase+SelectedUnitRva+2);
        var c=new Code();
        c.Emit("83 FA 4B");c.Branch(0x85,"original");
        ushort[] retained=[368,371,380,381];
        for(int i=0;i<4;i++){c.Emit("81 F9");c.Int(retained[i]);c.Branch(0x84,"retained-"+i);}
        c.Emit("89 C8 2D 01 02 00 00 83 F8 0A");c.Branch(0x87,"original");
        c.Emit("83 C0 04");c.Jump("mapped");
        for(int i=0;i<4;i++){c.Label("retained-"+i);c.Emit("B8");c.Int(i);c.Jump("mapped");}
        c.Label("mapped");
        c.Emit("51 52 41 50 41 51 41 52 41 53 50 41 89 C2 49 BB");c.Pointer(state);
        c.Emit("41 80 3B 01");c.Branch(0x85,"restore-original");
        c.Emit("41 83 7B 30 00");c.Branch(0x85,"refuse");
        c.Emit("48 B8");c.Pointer(imageBase+SelectedUnitRva);
        c.Emit("0F B7 08 83 F9 36");c.Branch(0x83,"refuse");
        c.Emit("66 41 3B 4B 02");c.Branch(0x85,"refuse");
        c.Emit("48 B8");c.Pointer(imageBase+NativeActionListRoute.PartyPointersRva);
        c.Emit("4C 8B 04 C8 4D 85 C0");c.Branch(0x84,"refuse");
        c.Emit("4D 3B 43 08");c.Branch(0x85,"refuse");
        c.Emit("41 0F B7 88 CC 00 00 00 66 41 3B 4B 10");c.Branch(0x85,"refuse");
        c.Emit("66 85 C9");c.Branch(0x88,"refuse"); // native JP is signed short
        c.Emit("41 0F B7 40 7E 41 0F B6 90 80 00 00 00 C1 E2 10 09 D0 41 3B 43 14");c.Branch(0x85,"refuse");
        c.Emit("4D 8B 4B 18 4D 85 C9");c.Branch(0x84,"refuse");
        c.Emit("41 8B 11 41 3B 53 20");c.Branch(0x85,"refuse");
        c.Emit("F7 C2 00 00 00 FF");c.Branch(0x85,"refuse");
        c.Emit("4C 89 D1 0F A3 CA");c.Branch(0x82,"refuse"); // already learned projected bit
        c.Emit("49 8B 43 28 48 85 C0");c.Branch(0x84,"refuse");
        c.Emit("42 0F B7 14 50 85 D2");c.Branch(0x84,"refuse");
        c.Emit("41 0F B7 88 CC 00 00 00 39 D1");c.Branch(0x82,"refuse");
        c.Emit("41 83 FA 04");c.Branch(0x83,"extra");
        // Retained identities write their legacy bits 0,3,12,13. Removed
        // actions and every passive bit remain byte-for-byte unchanged.
        for(int i=0;i<4;i++){c.Emit("41 83 FA");c.Byte((byte)i);c.Branch(0x84,"native-bit-"+i);}
        c.Jump("refuse");
        int[] old=[0,3,12,13];
        for(int i=0;i<4;i++)
        {
            c.Label("native-bit-"+i);
            c.Emit("41 F6 40");c.Byte((byte)(0x7E+old[i]/8));c.Byte((byte)(0x80>>(old[i]%8)));
            c.Branch(0x85,"refuse");
            c.Emit("49 8D 40");c.Byte((byte)(0x7E+old[i]/8));
            c.Emit("B9");c.Int(0x80>>(old[i]%8));
            c.Jump("commit");
        }
        c.Label("extra");c.Emit("31 C0 31 C9");
        c.Label("commit");
        // One JP subtraction and one bit update; pending blocks repetition
        // until the host durably reconciles and refreshes the frame.
        c.Emit("50 51 31 C0 B9 01 00 00 00 F0 41 0F B1 4B 30");c.Branch(0x85,"claim-failed");
        c.Emit("59 58 48 85 C0");c.Branch(0x84,"subtract");
        c.Emit("08 08"); // optional original native learning bit
        c.Label("subtract");
        c.Emit("66 41 29 90 CC 00 00 00");
        c.Emit("4C 89 D1 41 0F AB 09 B8 01 00 00 00");c.Jump("restore");
        c.Label("claim-failed");c.Emit("59 58");
        c.Label("refuse");c.Emit("31 C0");
        c.Label("restore");c.Emit("48 83 C4 08 41 5B 41 5A 41 59 41 58 5A 59 C3");
        c.Label("restore-original");c.Emit("58 41 5B 41 5A 41 59 41 58 5A 59");
        c.Label("original");c.Emit("FF 25 00 00 00 00");c.Pointer(original);
        return c.Finish();
    }
    private sealed class Code
    {
        private readonly List<byte> _bytes=new();
        private readonly Dictionary<string,int> _labels=new();
        private readonly List<(int Offset,string Label)> _branches=new();
        public void Emit(string hex)=>_bytes.AddRange(Convert.FromHexString(hex.Replace(" ","")));
        public void Byte(byte value)=>_bytes.Add(value);
        public void Pointer(long value)=>_bytes.AddRange(BitConverter.GetBytes(value));
        public void Int(int value)=>_bytes.AddRange(BitConverter.GetBytes(value));
        public void Label(string name)=>_labels.Add(name,_bytes.Count);
        public void Branch(byte condition,string name){_bytes.AddRange([0x0F,condition]);_branches.Add((_bytes.Count,name));Int(0);}
        public void Jump(string name){_bytes.Add(0xE9);_branches.Add((_bytes.Count,name));Int(0);}
        public byte[] Finish()
        {
            byte[] result=_bytes.ToArray();
            foreach(var(offset,label)in _branches)BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset),checked(_labels[label]-offset-4));
            return result;
        }
    }
}
