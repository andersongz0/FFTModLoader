using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>
/// Native command/action decoding bridges. No allocation, hooks or registration
/// happens here. A host must preflight and retain a 24-ushort row, original
/// passive slots and the original 24-word get_ability scratch allocation.
/// Does NOT implement learning confirmation, menus, battle transport or saves.
/// </summary>
public static class NativeCommandCatalog
{
    public const int SlotsPointerOffset = 8;
    public const int AuditedOutputOffset = 16;
    public const int StateLength = 24;
    public const int SlotCount = 24;
    public const int ActionSlotCount = 16;

    public static byte[] BuildSlot(long original, long state, int command = 6) => Build(original,state,command,false);
    public static byte[] BuildActionArray(long original, long state, int command = 6) => Build(original,state,command,true);

    private static byte[] Build(long original,long state,int command,bool array)
    {
        if(original<=0 || state<=0 || original==state || command is <0 or >=176)
            throw new ArgumentOutOfRangeException(nameof(original));
        _=checked(state+StateLength);
        var c=new Code();
        c.Emit("81 F9"); c.Int(command); c.Branch(0x85,"original");
        if(!array) { c.Emit("83 FA 18"); c.Branch(0x83,"original"); } // unsigned also rejects negative slot
        c.Emit("48 B8"); c.Pointer(state);
        c.Emit("80 38 01"); c.Branch(0x85,"original");
        c.Emit("48 83 78 08 00"); c.Branch(0x84,"original");
        if(array) { c.Emit("48 83 78 10 00"); c.Branch(0x84,"original"); }
        if(!array)
        {
            // Call the original leaf once and preserve its full post-call GP
            // contract. Its 22/23 slots return zero; the new row has explicit
            // zero slots, never reads the next native packed row.
            c.Emit("48 83 EC 28 89 54 24 20");
            c.Call(original);
            c.Emit("89 44 24 24 51 48 B8"); c.Pointer(state);
            c.Emit("80 38 01"); c.Branch(0x85,"slot-original-result");
            c.Emit("48 8B 40 08 48 85 C0"); c.Branch(0x84,"slot-original-result");
            c.Emit("8B 4C 24 28 0F B7 04 48"); c.Jump("slot-return");
            c.Label("slot-original-result"); c.Emit("8B 44 24 2C");
            c.Label("slot-return"); c.Emit("59 48 83 C4 28 C3");
        }
        else
        {
            // RBX retains original output, restored before returning. Original
            // getter fills its own 24-word buffer and native passive behavior.
            c.Emit("53 48 83 EC 30 89 54 24 20"); c.Call(original);
            c.Emit("48 89 C3 51 52 41 50 41 51 41 52 41 53");
            c.Emit("48 B8"); c.Pointer(state);
            c.Emit("48 3B 58 10"); c.Branch(0x85,"restore");
            c.Emit("80 38 01"); c.Branch(0x85,"restore");
            c.Emit("4C 8B 40 08 4D 85 C0"); c.Branch(0x84,"restore");
            c.Emit("4D 31 C9 0F B6 4C 24 50");
            c.Label("slot");
            c.Emit("43 0F B7 14 48");
            c.Emit("81 FA 00 04 00 00"); c.Branch(0x83,"next");
            c.Emit("81 FA 00 02 00 00"); c.Branch(0x84,"next");
            // Every registered slot in this command is an ACTION, including
            // the retained medicines in their new order. Numeric native
            // classification must not turn IDs 368..381 into passive skills.
            // Native slots 16..23 are never rewritten; ID 512 is reserved.
            c.Emit("F6 C1 01"); c.Branch(0x85,"write"); c.Emit("31 D2");
            c.Label("write"); c.Emit("66 42 89 14 4B");
            c.Label("next"); c.Emit("41 FF C1 41 83 F9 10"); c.Branch(0x82,"slot");
            c.Label("restore");
            c.Emit("48 89 D8 41 5B 41 5A 41 59 41 58 5A 59 48 83 C4 30 5B C3");
        }
        c.Label("original"); c.Emit("FF 25 00 00 00 00"); c.Pointer(original);
        return c.Finish();
    }
    private sealed class Code
    {
        private readonly List<byte> _bytes=new();
        private readonly Dictionary<string,int> _labels=new();
        private readonly List<(int Offset,string Label)> _branches=new();
        public void Emit(string hex)=>_bytes.AddRange(Convert.FromHexString(hex.Replace(" ","")));
        public void Pointer(long value)=>_bytes.AddRange(BitConverter.GetBytes(value));
        public void Int(int value)=>_bytes.AddRange(BitConverter.GetBytes(value));
        public void Label(string name)=>_labels.Add(name,_bytes.Count);
        public void Branch(byte condition,string name){_bytes.AddRange([0x0F,condition]);_branches.Add((_bytes.Count,name));Int(0);}
        public void Jump(string name){_bytes.Add(0xE9);_branches.Add((_bytes.Count,name));Int(0);}
        public void Call(long original){Emit("FF 15 02 00 00 00 EB 08");Pointer(original);}
        public byte[] Finish()
        {
            byte[] result=_bytes.ToArray();
            foreach(var(offset,label)in _branches)BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset),checked(_labels[label]-offset-4));
            return result;
        }
    }
}
