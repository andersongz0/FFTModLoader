using System.Buffers.Binary;
using FFTModLoader.ContentExpansion;

internal static class NativeLearningMenuTests
{
    public static void Run(IReadOnlyList<ActionIdentity> actions, IReadOnlyList<MenuAction> menuActions)
    {
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        void Reject(Action call)
        {
            try { call(); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { return; }
            throw new Exception("Unsafe native learning/menu operation accepted.");
        }
        // Independent known-byte fixtures catch a symmetric but reversed codec.
        Require(NativeLearningBits.Decode(new byte[] { 0x80, 0x01, 0x80 }) == 0x018001,
            "MSB-first native slots decoded incorrectly");
        Require(NativeLearningBits.Encode(0x018001).SequenceEqual(new byte[] { 0x80, 0x01, 0x80 }),
            "Native byte order changed");
        int[] original = Enumerable.Range(368, 14).Concat(new[] { 0, 0 }).ToArray();
        var noNew = new Dictionary<string, bool>();
        for (uint bits = 0; bits <= ushort.MaxValue; bits++)
        {
            uint logical = bits | ((bits & 0xFF) << 16);
            byte[] bytes = NativeLearningBits.Encode(logical);
            Require(NativeLearningBits.Decode(bytes) == logical, "Native learning round-trip failed");
            uint projected = ChemistNativeLearning.Project(bytes, actions, noNew);
            uint restored = ActionLearning.ProjectBackToNative(logical, projected, original, actions);
            Require(NativeLearningBits.Encode(restored).SequenceEqual(bytes), "Native reversible migration changed bytes");
            Require(NativeLearningBits.Encode(projected)[2] == bytes[2], "Native passive learning byte changed");
        }
        for (int slot = 0; slot < 24; slot++)
        {
            byte[] expected = new byte[3];
            expected[slot / 8] = (byte)(0x80 >> (slot % 8));
            Require(NativeLearningBits.SetLearned(new byte[3], slot).SequenceEqual(expected), "Native learned bit shifted");
        }
        Reject(() => NativeLearningBits.Decode(new byte[2]));
        Reject(() => NativeLearningBits.Encode(0x1000000));
        Reject(() => NativeLearningBits.SetLearned(new byte[3], 24));
        Reject(() => ChemistNativeLearning.Project(new byte[3], actions, new Dictionary<string, bool> { ["potion"] = false }));
        Reject(() => ChemistNativeLearning.Project(new byte[3], actions, new Dictionary<string, bool> { ["unknown"] = true }));
        Require((ChemistNativeLearning.Project(new byte[3], actions, new Dictionary<string, bool> { [actions[4].Key] = true }) & 16) != 0,
            "Persisted new learning not projected by key");

        var menu = new NativeActionMenu(menuActions);
        ushort[] output = Enumerable.Repeat((ushort)0xBEEF, 25).ToArray();
        int count = menu.Build(ActionMenuView.Learn, 0, 150, output);
        Require(count == 15 && output[count] == 0xFFFF && output[count + 1] == 0xBEEF, "Menu count/sentinel overflow");
        Require(output[0] == 368 && output[1] == 371 && output[2] == (380 | 0x4000) && output[3] == 381,
            "Retained JP flags changed");
        Require(output[4] == 513 && output[5] == 514 && output[6] == (515 | 0x4000),
            "Expanded action was masked to nine bits or treated as movement");
        count = menu.Build(ActionMenuView.LearnedActions, 0b1011, 0, output);
        Require(count == 3 && output.Take(4).SequenceEqual(new ushort[] { 368, 371, 381, 0xFFFF }),
            "Learned-action view includes locked or wrong identities");
        output = Enumerable.Repeat((ushort)0xBEEF, 25).ToArray();
        Require(menu.Build(ActionMenuView.UnlearnedCount, 0b1011, 0, output) == 12 && output.All(v => v == 0xBEEF),
            "Native mastery mode wrote an output buffer");
        menu.Build(ActionMenuView.Learn, 0b1011, 0, output);
        Require(output[0] == (368 | 0x1000) && output[1] == (371 | 0x1000) && output[3] == (381 | 0x1000),
            "Learned markers lost");
        ushort[] tooShort = Enumerable.Repeat((ushort)0xBEEF, 15).ToArray();
        Reject(() => menu.Build(ActionMenuView.All, 0, 0, tooShort));
        Require(tooShort.All(v => v == 0xBEEF), "Output changed before capacity preflight");
        var unlearnable = new NativeActionMenu(new[] { new MenuAction("special", 600, 50, false) });
        unlearnable.Build(ActionMenuView.All, 0, 0, output);
        Require(output[0] == (600 | 0x6000), "Native unlearnable markers lost");
        Require(unlearnable.Build(ActionMenuView.Learn, 0, 0, output) == 0 && output[0] == 0xFFFF,
            "Unlearnable ability exposed in learn menu");
        foreach (ushort id in menuActions.Select(a => a.AbilityId))
            foreach (ushort flags in new ushort[] { 0, 0x1000, 0x4000, 0x6000 })
                Require(NativeActionMenu.DecodeIdentity((ushort)(id | flags)) == id, "UI markers contaminated identity lookup");
        Reject(() => NativeActionMenu.DecodeIdentity(0xFFFF));
        Reject(() => NativeActionMenu.DecodeIdentity(0x0401));
        Reject(() => NativeActionMenu.DecodeIdentity(0x2001));
        Reject(() => NativeActionMenu.DecodeIdentity(0x5001));
        Reject(() => new NativeActionMenu(new[] { new MenuAction("reserved", 512, 1) }));
        Reject(() => new NativeActionMenu(new[] { new MenuAction("too-wide", 1024, 1) }));
        Reject(() => new NativeActionMenu(menuActions.Concat(new[] { menuActions[0] })));
        Require(NativeActionMenu.HandlesChemistActionList(75, 0, 2), "Chemist learn route not selected");
        Require(!NativeActionMenu.HandlesChemistActionList(75, 0, 1), "GetCommandList intercepted as an action list");
        for (int category = 1; category <= 3; category++)
            Require(!NativeActionMenu.HandlesChemistActionList(75, category, 2), "Passive category intercepted");
        Require(!NativeActionMenu.HandlesChemistActionList(162, 0, 2), "GenericKnights menu intercepted");

        // Test actual party-unit offsets against a synthetic 600-byte unit;
        // this is not evidence of a live-game hook or save integration.
        const long unitAddress = 0x100000;
        byte[] unit = Enumerable.Repeat((byte)0xA5, 600).ToArray();
        byte[] Read(long address, int length) => unit.AsSpan(checked((int)(address - unitAddress)), length).ToArray();
        void Write(long address, byte[] bytes) => bytes.CopyTo(unit, checked((int)(address - unitAddress)));
        int[] ids = [368, 371, 380, 381];
        int[] slots = [0, 3, 12, 13];
        ushort[] costs = [50, 150, 300, 90];
        for (int i = 0; i < ids.Length; i++)
        {
            Array.Clear(unit, ChemistNativeLearning.PartyLearningOffset, 2);
            unit[ChemistNativeLearning.PartyLearningOffset + 2] = 0xA5;
            BinaryPrimitives.WriteInt16LittleEndian(unit.AsSpan(ChemistNativeLearning.PartyJpOffset, 2), 1000);
            byte[] before = unit.ToArray();
            var writes = ChemistNativeLearning.PlanRetainedPurchase(unitAddress, ids[i], costs[i], Read);
            Require(unit.SequenceEqual(before), "Planning mutated native fields");
            NativeTableRelocation.Apply(writes, Read, Write);
            byte[] expected = before.ToArray();
            expected[ChemistNativeLearning.PartyLearningOffset + slots[i] / 8] |= (byte)(0x80 >> (slots[i] % 8));
            BinaryPrimitives.WriteInt16LittleEndian(expected.AsSpan(ChemistNativeLearning.PartyJpOffset, 2), (short)(1000 - costs[i]));
            Require(unit.SequenceEqual(expected), "Purchase touched another field or used reordered learning position");
            Reject(() => ChemistNativeLearning.PlanRetainedPurchase(unitAddress, ids[i], costs[i], Read));
            Require(unit.SequenceEqual(expected), "Duplicate purchase mutated learning/JP");
        }
        Reject(() => ChemistNativeLearning.PlanRetainedPurchase(unitAddress, 513, 70, Read));
        Array.Clear(unit, ChemistNativeLearning.PartyLearningOffset, 2);
        BinaryPrimitives.WriteInt16LittleEndian(unit.AsSpan(ChemistNativeLearning.PartyJpOffset, 2), 49);
        Reject(() => ChemistNativeLearning.PlanRetainedPurchase(unitAddress, 368, 50, Read));
        BinaryPrimitives.WriteInt16LittleEndian(unit.AsSpan(ChemistNativeLearning.PartyJpOffset, 2), 1000);
        byte[] rollbackBefore = unit.ToArray();
        var purchase = ChemistNativeLearning.PlanRetainedPurchase(unitAddress, 371, 150, Read);
        int attempts = 0;
        try
        {
            NativeTableRelocation.Apply(purchase, Read, (address, bytes) =>
            {
                if (++attempts == 2)
                {
                    unit[checked((int)(address - unitAddress))] = bytes[0];
                    throw new IOException("Simulated partial JP field failure");
                }
                Write(address, bytes);
            });
            throw new Exception("Expected purchase rollback");
        }
        catch (IOException) { }
        Require(unit.SequenceEqual(rollbackBefore), "JP failure did not roll back native learned bit");
        purchase = ChemistNativeLearning.PlanRetainedPurchase(unitAddress, 371, 150, Read);
        unit[ChemistNativeLearning.PartyJpOffset] ^= 1;
        byte[] changed = unit.ToArray();
        Reject(() => NativeTableRelocation.Apply(purchase, Read, Write));
        Require(unit.SequenceEqual(changed), "Stale state purchase made writes");
        Console.WriteLine("PASS: native MSB-first learning codec (65536 migrations), 15-action ushort menu with JP flags/capacity and passive routing, original retained-learning positions and guarded JP purchase rollback. Synthetic memory only; new IDs are test fixtures, not allocated in game.");
    }
}
