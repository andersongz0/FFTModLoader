using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>
/// Full-width command rows for NEW commands 227..255. Native original rows
/// 0..226, including the old four-slot monster bank, are never repacked.
/// Builders only: the shared host must allocate rows, bind ONE hook per entry
/// point, install action/menu/AI routes and enable the state after preflight.
/// </summary>
public static class NativeExpandedCommandCatalog
{
    public const int Capacity = 256;
    public const int ScratchPointerOffset = 8;
    public const int RegisteredOffset = 16;
    public const int RowsOffset = RegisteredOffset + Capacity;
    public const int CountsOffset = RowsOffset + Capacity * 8;
    public const int MonsterOffset = CountsOffset + Capacity;
    public const int StateLength = MonsterOffset + Capacity;
    public const int Slots = 24;

    public static byte[] BuildSlot(long original, long state) => Build(original, state, false);
    public static byte[] BuildActionArray(long original, long state) => Build(original, state, true);

    private static byte[] Build(long original, long state, bool array)
    {
        if(original <= 0 || state <= 0 || original == state) throw new ArgumentOutOfRangeException(nameof(state));
        _ = checked(state + StateLength);
        var code = new Code();
        code.Emit("81 F9 E3 00 00 00"); code.Branch(0x82,"original");
        code.Emit("81 F9 00 01 00 00"); code.Branch(0x83,"original");
        if(!array) { code.Emit("83 FA 18"); code.Branch(0x83,"original"); }
        code.Emit("48 B8"); code.Long(state);
        code.Emit("80 38 01"); code.Branch(0x85,"original");
        code.Emit("80 BC 08"); code.Int(RegisteredOffset); code.Emit("01"); code.Branch(0x85,"original");
        code.Emit("48 83 BC C8"); code.Int(RowsOffset); code.Emit("00"); code.Branch(0x84,"original");
        if(!array)
        {
            code.Emit("48 8B 84 C8"); code.Int(RowsOffset);
            code.Emit("0F B7 04 50 C3"); // ushort[row + slot*2]; no narrowing of ability IDs
        }
        else
        {
            code.Emit("48 83 78 08 00"); code.Branch(0x84,"original");
            // Pure native leaf, no CLR or original decoding for a new command.
            // Keep every input/scratch GP except the returned RAX unchanged.
            code.Emit("41 50 41 51 41 52 41 53");
            code.Emit("4C 8B 84 C8"); code.Int(RowsOffset); // R8 = full-width row
            code.Emit("4C 8B 48 08 45 31 D2"); // R9 = owned 24-word scratch, R10=index
            code.Label("slot");
            code.Emit("45 31 DB F6 C2 01"); code.Branch(0x84,"write");
            code.Emit("41 83 FA 10"); code.Branch(0x83,"write");
            code.Emit("47 0F B7 1C 50"); // R11D = row[R10], active action section only
            code.Label("write"); code.Emit("66 47 89 1C 51 41 FF C2 41 83 FA 18"); code.Branch(0x82,"slot");
            code.Emit("4C 89 C8 41 5B 41 5A 41 59 41 58 C3");
        }
        code.Label("original"); code.Emit("FF 25 00 00 00 00"); code.Long(original);
        return code.Finish();
    }

    private sealed class Code
    {
        private readonly List<byte> _bytes = [];
        private readonly Dictionary<string,int> _labels = [];
        private readonly List<(int Offset,string Label)> _branches = [];
        public void Emit(string hex) => _bytes.AddRange(Convert.FromHexString(hex.Replace(" ","")));
        public void Long(long value) => _bytes.AddRange(BitConverter.GetBytes(value));
        public void Int(int value) => _bytes.AddRange(BitConverter.GetBytes(value));
        public void Label(string label) => _labels.Add(label,_bytes.Count);
        public void Branch(byte condition,string label) { _bytes.AddRange([0x0F,condition]); _branches.Add((_bytes.Count,label)); Int(0); }
        public byte[] Finish()
        {
            var bytes=_bytes.ToArray();
            foreach(var(offset,label) in _branches) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset),_labels[label]-offset-4);
            return bytes;
        }
    }
}
