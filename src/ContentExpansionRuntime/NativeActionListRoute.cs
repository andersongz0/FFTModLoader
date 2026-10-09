using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>
/// Native-only postprocessor for GetAbilityList, not an installed hook.
/// The host must publish an immutable, validated menu frame for exactly one
/// party unit and known output allocation, retaining every allocation until
/// game exit. Stale JP, learning bytes or party pointer refuse replacement.
/// Does not purchase learning, persist it, or enable any battle actions.
/// </summary>
public static class NativeActionListRoute
{
    public const int StateLength = 64;
    public const int UnitIdOffset = 2;
    public const int OutputOffset = 8;
    public const int AllEntriesOffset = 16;
    public const int LearnEntriesOffset = 24;
    public const int AllCountOffset = 32;
    public const int LearnCountOffset = 36;
    public const int UnlearnedCountOffset = 40;
    public const int OutputCapacityOffset = 44;
    public const int PartyPointerOffset = 48;
    public const int ExpectedNativeBitsOffset = 56;
    public const int ExpectedJpOffset = 60;
    public const int PartyPointersRva = 0x1800F50;
    public const int NativeLearningOffset = 0x7E;
    public const int NativeJpOffset = 0xCC;

    public static byte[] Build(long original, long state, long imageBase)
    {
        if (original <= 0 || state <= 0 || imageBase <= 0 || original == state)
            throw new ArgumentOutOfRangeException(nameof(original));
        _ = checked(state + StateLength);
        _ = checked(imageBase + PartyPointersRva + 54 * 8);
        var c = new Code();
        c.Emit("83 FA 4B"); c.Branch(0x85, "original"); // exact Chemist job
        c.Emit("45 85 C0"); c.Branch(0x85, "original"); // action category only
        c.Emit("83 F9 36"); c.Branch(0x83, "original"); // host bound party slots 0..53
        c.Emit("8B 44 24 28 83 F8 00"); c.Branch(0x84, "mode-ok");
        c.Emit("83 F8 02"); c.Branch(0x84, "mode-ok");
        c.Emit("83 F8 03"); c.Branch(0x85, "original"); // mode1 is COMMANDS
        c.Label("mode-ok");
        // Save call arguments and fifth stack argument; preserve original
        // output writes and every original side effect exactly once.
        c.Emit("48 83 EC 58 89 4C 24 30 4C 89 4C 24 38 89 44 24 28 89 44 24 20");
        Validate("fallback-before", before: true);
        c.Call(original);
        c.Emit("48 89 44 24 40 51 52 41 50 41 51 41 52 41 53");
        Validate("restore", before: false);
        c.Emit("8B 54 24 58 83 FA 03"); c.Branch(0x84, "count-only");
        c.Emit("83 FA 02"); c.Branch(0x84, "learn");
        c.Emit("4C 8B 40 10 8B 50 20"); c.Jump("copy");
        c.Label("learn"); c.Emit("4C 8B 40 18 8B 50 24");
        c.Label("copy");
        c.Emit("4C 8B 4C 24 68 45 31 D2");
        c.Label("word"); c.Emit("41 39 D2"); c.Branch(0x83, "terminator");
        c.Emit("43 0F B7 0C 50 66 43 89 0C 51 41 FF C2"); c.Jump("word");
        c.Label("terminator"); c.Emit("66 43 C7 04 51 FF FF 48 89 54 24 70"); c.Jump("restore");
        c.Label("count-only"); c.Emit("8B 50 28 48 89 54 24 70");
        c.Label("restore");
        c.Emit("41 5B 41 5A 41 59 41 58 5A 59 48 8B 44 24 40 48 83 C4 58 C3");
        c.Label("fallback-before"); c.Emit("48 83 C4 58");
        c.Label("original"); c.Emit("FF 25 00 00 00 00"); c.Pointer(original);
        return c.Finish();

        void Validate(string fail, bool before)
        {
            // RAX is state; scratch RCX/RDX/R8/R9 are restored before calling
            // original or by post-call pushes. Inputs R10/R11 never touched
            // before the original (internal native ABI, not Microsoft alone).
            // Preflight uses only RAX + saved RCX; RDX/R8/R9 inputs stay intact.
            if (before) c.Emit("51");
            c.Emit("48 B8"); c.Pointer(state);
            c.Emit("80 38 01"); c.Branch(0x85, before ? "pre-fail" : fail);
            c.Emit(before ? "8B 4C 24 38" : "8B 4C 24 60");
            c.Emit("66 3B 48 02"); c.Branch(0x85, before ? "pre-fail" : fail);
            c.Emit("48 B9"); c.Pointer(imageBase + PartyPointersRva);
            // Address the registered slot only, using saved unit argument.
            c.Emit(before ? "50 8B 44 24 40 48 8B 0C C1 58" : "50 8B 44 24 68 48 8B 0C C1 58");
            c.Emit("48 85 C9"); c.Branch(0x84, before ? "pre-fail" : fail);
            c.Emit("48 3B 48 30"); c.Branch(0x85, before ? "pre-fail" : fail);
            // Capture precisely three bytes, never the neighbouring job bit.
            c.Emit("50 0F B7 41 7E 0F B6 89 80 00 00 00 C1 E1 10 09 C1 58 3B 48 38");
            c.Branch(0x85, before ? "pre-fail" : fail);
            c.Emit("48 8B 48 30 0F B7 89 CC 00 00 00 66 3B 48 3C");
            c.Branch(0x85, before ? "pre-fail" : fail);
            c.Emit("66 85 C9"); c.Branch(0x88, before ? "pre-fail" : fail);
            c.Emit("83 78 20 10"); c.Branch(0x87, before ? "pre-fail" : fail);
            c.Emit("8B 48 24 3B 48 20"); c.Branch(0x87, before ? "pre-fail" : fail);
            c.Emit("8B 48 28 3B 48 20"); c.Branch(0x87, before ? "pre-fail" : fail);
            c.Emit("48 83 78 10 00"); c.Branch(0x84, before ? "pre-fail" : fail);
            c.Emit("48 83 78 18 00"); c.Branch(0x84, before ? "pre-fail" : fail);
            c.Emit(before ? "83 7C 24 30 03" : "83 7C 24 58 03");
            c.Branch(0x84, before ? "pre-ok" : "post-ok");
            c.Emit("8B 48 20 FF C1 3B 48 2C"); c.Branch(0x87, before ? "pre-fail" : fail);
            c.Emit(before ? "48 8B 4C 24 40" : "48 8B 4C 24 68");
            c.Emit("48 85 C9"); c.Branch(0x84, before ? "pre-fail" : fail);
            c.Emit("48 3B 48 08"); c.Branch(0x85, before ? "pre-fail" : fail);
            c.Label(before ? "pre-ok" : "post-ok");
            if (before)
            {
                c.Emit("59"); c.Jump("pre-done");
                c.Label("pre-fail"); c.Emit("59"); c.Jump(fail);
                c.Label("pre-done");
            }
        }
    }
    private sealed class Code
    {
        private readonly List<byte> _bytes = new();
        private readonly Dictionary<string, int> _labels = new();
        private readonly List<(int Offset, string Label)> _branches = new();
        public void Emit(string hex) => _bytes.AddRange(Convert.FromHexString(hex.Replace(" ", "")));
        public void Pointer(long value) => _bytes.AddRange(BitConverter.GetBytes(value));
        public void Label(string name) => _labels.Add(name, _bytes.Count);
        public void Branch(byte condition, string name) { _bytes.AddRange([0x0F, condition]); _branches.Add((_bytes.Count, name)); _bytes.AddRange(new byte[4]); }
        public void Jump(string name) { _bytes.Add(0xE9); _branches.Add((_bytes.Count, name)); _bytes.AddRange(new byte[4]); }
        public void Call(long original) { Emit("FF 15 02 00 00 00 EB 08"); Pointer(original); }
        public byte[] Finish()
        {
            byte[] result = _bytes.ToArray();
            foreach (var (offset, label) in _branches) BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), checked(_labels[label] - offset - 4));
            return result;
        }
    }
}
