using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>
/// Native-only stock route for the audited ItemChg leaf contract. This emitter
/// does not allocate memory, install hooks, assign IDs or activate a session.
/// Original items/unregistered IDs tail-jump to the untouched original.
/// Registered extra IDs use a SEPARATE state allocation, never PartyItem+261.
/// </summary>
public static class NativeItemStockLeaf
{
    public const int NativeItemCount = 261;
    public const int IdCapacity = 1024; // ItemChg's existing 10-bit normalization
    public const int EnabledOffset = 0;
    public const int RegisteredOffset = 16;
    public const int CountsOffset = RegisteredOffset + IdCapacity;
    public const int StateLength = CountsOffset + IdCapacity;
    public const byte MaximumStack = 99;

    // Caller must keep both addresses alive and update state only at audited,
    // quiescent game-thread boundaries. No managed callback can run here.
    // RAX/RCX/R8/R9 + flags are scratch, matching the native leaf. RDX/R10/R11,
    // nonvolatile GP, stack, x87/SIMD/AVX and MXCSR are untouched by this route.
    public static byte[] Build(long originalTrampoline, long stateAddress)
    {
        if (originalTrampoline <= 0 || stateAddress <= 0 || originalTrampoline == stateAddress)
            throw new ArgumentOutOfRangeException(nameof(originalTrampoline));
        _ = checked(stateAddress + StateLength);
        var code = new Code();
        code.Emit("44 0F B7 C1 41 81 E0 FF 03 00 00"); // R8D = ushort(CX) & 3ff
        code.Emit("41 81 F8 05 01 00 00"); // native IDs bypass the extension entirely
        code.Branch(0x82, "original");
        code.Emit("49 B9"); code.Pointer(stateAddress);
        code.Emit("41 80 39 01"); // session enabled == 1 only
        code.Branch(0x85, "original");
        code.Emit("43 80 7C 01 10 01"); // registered[id] == 1 only
        code.Branch(0x85, "original");
        code.Emit("43 0F B6 8C 01"); code.Integer(CountsOffset); // ECX = count[id]
        code.Emit("89 C8 85 D2"); // EAX=count; delta=0 is strictly read-only
        code.Branch(0x84, "return");
        code.Emit("48 63 C2 48 01 C8 31 C9 48 85 C0 48 0F 48 C1"); // signed64 sum, clamp >=0
        code.Emit("B9 63 00 00 00 48 39 C8 48 0F 4F C1"); // clamp <=99, no int32 overflow
        code.Emit("43 88 84 01"); code.Integer(CountsOffset);
        code.Label("return"); code.Emit("C3");
        code.Label("original");
        code.Emit("FF 25 00 00 00 00"); code.Pointer(originalTrampoline); // no CALL/managed thunk
        return code.Finish();
    }

    private sealed class Code
    {
        private readonly List<byte> _bytes = new();
        private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);
        private readonly List<(int Operand, string Label)> _branches = new();
        public void Emit(string hex) => _bytes.AddRange(Convert.FromHexString(hex.Replace(" ", "")));
        public void Pointer(long value) => _bytes.AddRange(BitConverter.GetBytes(value));
        public void Integer(int value) => _bytes.AddRange(BitConverter.GetBytes(value));
        public void Label(string name) => _labels.Add(name, _bytes.Count);
        public void Branch(byte condition, string label)
        {
            _bytes.AddRange([0x0F, condition]);
            _branches.Add((_bytes.Count, label)); Integer(0);
        }
        public byte[] Finish()
        {
            byte[] result = _bytes.ToArray();
            foreach (var (operand, label) in _branches)
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(operand, 4), checked(_labels[label] - operand - 4));
            return result;
        }
    }
}
