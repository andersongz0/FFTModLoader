using FFTModLoader.ContentExpansion;

internal static class NativeCommandItemTests
{
    public static void Run(IReadOnlyList<MenuAction> menuActions, IReadOnlyList<ItemActionIdentity> itemActions)
    {
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        void Reject(Action call)
        {
            try { call(); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { return; }
            throw new Exception("Invalid command/item transport was accepted.");
        }
        // Exact supported command 6 bytes at 67E210 + 6*25; distinct from the
        // unit's three learned bytes, despite both being MSB-first.
        byte[] originalRow = Convert.FromHexString("FFFCF8707172737475767778797A7B7C7D0000B9DADBE0FD00");
        var decoded = ExpandedCommandSlots.DecodeOriginal(originalRow);
        ushort[] expected = Enumerable.Range(368, 14).Select(i => (ushort)i)
            .Concat(new ushort[] { 0, 0, 441, 474, 475, 480, 509, 0, 0, 0 }).ToArray();
        Require(decoded.SequenceEqual(expected), "Native packed row was misdecoded or crossed into next row");
        var command = new ExpandedCommandSlots(originalRow, menuActions);
        ushort[] commandOutput = Enumerable.Repeat((ushort)0xBEEF, 25).ToArray();
        command.CopyTo(commandOutput);
        Require(commandOutput.Take(15).SequenceEqual(menuActions.Select(a => a.AbilityId)), "Expanded command IDs truncated");
        Require(commandOutput[15] == 0 && commandOutput.Skip(16).Take(8).SequenceEqual(expected.Skip(16)),
            "Original passive IDs or empty slots changed");
        Require(commandOutput[24] == 0xBEEF && command.GetSlot(24) == 0, "Command output exceeds 24 slots");
        Require(command.IsRegisteredAction(513) && !command.IsRegisteredAction(441), "Actions classified by numeric range");
        ushort[] shortCommand = Enumerable.Repeat((ushort)0xBEEF, 23).ToArray();
        Reject(() => command.CopyTo(shortCommand));
        Require(shortCommand.All(v => v == 0xBEEF), "Command capacity failure caused partial output");
        Reject(() => ExpandedCommandSlots.DecodeOriginal(new byte[26]));
        Reject(() => new ExpandedCommandSlots(originalRow, new[] { new MenuAction("collision", 441, 1) }));

        var items = new ExpandedBattleItemList(itemActions);
        var stock = itemActions.ToDictionary(a => a.Key, _ => 1);
        var selected = items.Select(0x7FFF, stock);
        Require(selected.Count == 15 && selected[4].ItemId == 261 && selected[14].ItemId == 271,
            "Expanded items aliased/truncated or reordered");
        Require(stock.Values.All(count => count == 1), "List generation consumed inventory");
        ushort[] wide = Enumerable.Repeat((ushort)0xBEEF, 17).ToArray();
        Require(ExpandedBattleItemList.WriteWideItems(selected, wide) == 15 && wide[15] == 0xFFFF && wide[16] == 0xBEEF,
            "Wide item output lacks correct native terminator/capacity");
        Require(wide.Take(15).SequenceEqual(itemActions.Select(a => a.ItemId)), "Wide item identity changed");
        byte[] legacy = Enumerable.Repeat((byte)0xA5, 17).ToArray();
        Reject(() => ExpandedBattleItemList.WriteLegacyItems(selected, legacy));
        Require(legacy.All(v => v == 0xA5), "Expanded item corrupted legacy byte buffer");
        var retained = selected.Take(4).ToArray();
        Require(ExpandedBattleItemList.WriteLegacyItems(retained, legacy) == 4 &&
            legacy.Take(5).SequenceEqual(new byte[] { 240, 243, 252, 253, 0xFF }) && legacy[5] == 0xA5,
            "Retained native byte transport regressed");
        var onlyVenom = items.Select(1u << 4, stock);
        Require(onlyVenom.Count == 1 && onlyVenom[0].AbilityId == 513 && onlyVenom[0].ItemId == 261,
            "Registered ability-to-item binding changed");
        stock[onlyVenom[0].Key] = 0;
        Require(items.Select(1u << 4, stock).Count == 0 && items.Select(0, stock).Count == 0,
            "Unlearned or empty item selectable");
        ushort[] tooShort = Enumerable.Repeat((ushort)0xBEEF, 15).ToArray();
        Reject(() => ExpandedBattleItemList.WriteWideItems(selected, tooShort));
        Require(tooShort.All(v => v == 0xBEEF), "Expanded item buffer preflight was not atomic");
        stock.Remove(itemActions[0].Key);
        Reject(() => items.Select(0x7FFF, stock));
        stock[itemActions[0].Key] = -1;
        Reject(() => items.Select(0x7FFF, stock));
        stock[itemActions[0].Key] = 100;
        Reject(() => items.Select(0x7FFF, stock));
        Reject(() => new ExpandedBattleItemList(itemActions.Concat(new[] { itemActions[0] })));
        Reject(() => new ExpandedBattleItemList(new[] { new ItemActionIdentity("bad", 513, 1024) }));
        Console.WriteLine("PASS: expanded 24-slot command preserves original passives; 15 learned/stock-filtered ability/item bindings retain full ushort identities; legacy byte truncation and buffer overflow rejected before writes. No native caller adaptation/activation is implied.");
    }
}
