using GenericJobs;
using System.Runtime.InteropServices;

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

Require(!SparseNxdRows.TryLookup(0, 170, out _, out _), "Null table must forward");
using (var table = new Fixture([0, 1, 2]))
{
    Require(!SparseNxdRows.TryLookup(table.Table, 1, out _, out _), "Contiguous table must use native lookup");
}
using (var table = new Fixture([96, 97, 98]))
{
    Require(!SparseNxdRows.TryLookup(table.Table, 97, out _, out _), "Nonzero contiguous base must forward");
}
using (var table = new Fixture([0, 1, 170, 173]))
{
    foreach (uint key in new uint[] {0, 1, 170, 173})
    {
        Require(SparseNxdRows.TryLookup(table.Table, key, out nint row, out int count) && row == table.Row(key) && count == 4, "Sparse key must return existing union");
    }
    foreach (uint key in new uint[] {2, 163, 171, 174, uint.MaxValue})
        Require(SparseNxdRows.TryLookup(table.Table, key, out nint row, out _) && row == 0, "Missing key must not substitute another character");
    Marshal.WriteInt64(table.Row(170), unchecked((long)0x8000000000000001UL));
    Require(SparseNxdRows.TryLookup(table.Table, 170, out nint flagged, out _) && flagged == table.Row(170), "Preserve flagged row union");
    Marshal.WriteInt64(table.Table, 0x38, 2);
    Require(!SparseNxdRows.TryLookup(table.Table, 170, out _, out _), "Multikey table must forward");
    Marshal.WriteInt64(table.Table, 0x38, 1);
    Marshal.WriteByte(table.Table, 0x41, 0);
    Require(!SparseNxdRows.TryLookup(table.Table, 170, out _, out _), "Native key-search tables must forward");
}

// Use the actual NXD produced by the user's crashed session, not a hand-made
// CharShape substitute. Unions point to untouched records in this file.
if (args.Length < 1) throw new Exception("Pass the merged charshape.nxd from the crashed session (then optional launcher JSON files)");
byte[] bytes = File.ReadAllBytes(args[0]);
Require(bytes[10] == 1, "Expected UsesBaseRowId");
int offset = BitConverter.ToInt32(bytes, 0x20), length = BitConverter.ToInt32(bytes, 0x24);
uint[] keys = Enumerable.Range(0, length).Select(i => BitConverter.ToUInt32(bytes, offset + i * 8)).ToArray();
Require(length == 167 && keys.SequenceEqual(Enumerable.Range(0, 163).Select(i => (uint)i).Concat(new uint[] {170,171,172,173})), "Actual crashed table key layout changed");
using (var actual = new Fixture(keys, bytes, offset))
{
    // The original fast path rejects 170: count 167 => max 166.
    Require(170 >= length, "Original native lookup should reject shape 170");
    foreach (uint key in keys)
    {
        Require(SparseNxdRows.TryLookup(actual.Table, key, out nint row, out _) && row == actual.Row(key), $"Actual key {key} failed");
        nint record = Marshal.ReadIntPtr(row, 8);
        Require(unchecked((uint)Marshal.ReadInt32(record)) == key, "Returned wrong record");
        nint payload = record + Marshal.ReadInt32(record, 4);
        // Same payload read as native crash instruction at RVA 217172.
        byte height = Marshal.ReadByte(payload, 0x2C);
        if (key >= 170) Console.WriteLine($"CharShape {key}: existing row resolved; payload+0x2C={height}");
    }
    for (uint key = 163; key < 170; key++)
        Require(SparseNxdRows.TryLookup(actual.Table, key, out nint row, out _) && row == 0, "Actual gap must remain absent");
}
Console.WriteLine("PASS: actual merged NXD (167 records), every key, gaps, original contiguous tables, nonzero base, multikey and flagged unions.");
Mod.TestSessionChannel();
foreach (byte job in new byte[] {0xA2, 0xA3})
{
    foreach (byte sex in new byte[] {0x80, 0x81})
    {
        Require(MenuPortraitPolicy.IsEligible(0, sex, job), "Registered generic unit not eligible");
        Require(MenuPortraitPolicy.IsEligible(53, sex, job), "Last valid unit not eligible");
        Require(!MenuPortraitPolicy.IsEligible(54, sex, job), "Out-of-range unit eligible");
        // Native arithmetic exceeds the table and yields face 0 for both jobs.
        int oldIndex = job * 2 - (sex == 0x80 ? 0x34 : 0x33);
        Require(oldIndex >= 211, "Native menu overflow not reproduced");
    }
    foreach (byte identity in Enumerable.Range(0, 256).Select(i => (byte)i))
        if (identity is not (0x80 or 0x81))
            Require(!MenuPortraitPolicy.IsEligible(1, identity, job), "Named unit intercepted");
}
foreach (byte job in Enumerable.Range(0, 0xA2).Select(i => (byte)i))
    Require(!MenuPortraitPolicy.IsEligible(1, 0x80, job), "Native job intercepted");
Require(!MenuPortraitPolicy.IsEligible(uint.MaxValue, 0x80, 0xA2), "Index wraparound");
Console.WriteLine("PASS: menu portrait policy, native overflow reproduction, both sexes, all native jobs/identities and unit bounds.");
MenuPreviewTests.Run();
ExpansionSettingsTests.Run();

sealed class Fixture : IDisposable
{
    public nint Table { get; }
    private readonly nint rows, records;
    private readonly Dictionary<uint, nint> byKey = new();
    public nint Row(uint key) => byKey[key];
    public Fixture(uint[] keys, byte[]? file = null, int indexOffset = 0)
    {
        Table = Marshal.AllocHGlobal(0x60);
        Marshal.Copy(new byte[0x60], 0, Table, 0x60);
        rows = Marshal.AllocHGlobal(keys.Length * 16);
        records = Marshal.AllocHGlobal(file?.Length ?? keys.Length * 64);
        if (file != null) Marshal.Copy(file, 0, records, file.Length);
        for (int i = 0; i < keys.Length; i++)
        {
            nint row = rows + i * 16;
            nint record = records + (file == null ? i * 64 : indexOffset + i * 8);
            if (file == null)
            {
                Marshal.Copy(new byte[64], 0, record, 64);
                Marshal.WriteInt32(record, unchecked((int)keys[i]));
                Marshal.WriteInt32(record, 4, 8);
            }
            Marshal.WriteInt64(row, 1); Marshal.WriteIntPtr(row, 8, record);
            byKey.Add(keys[i], row);
        }
        Marshal.WriteIntPtr(Table, 0x10, rows); Marshal.WriteInt64(Table, 0x28, keys.Length);
        Marshal.WriteInt64(Table, 0x38, 1); Marshal.WriteByte(Table, 0x41, 1);
        Marshal.WriteInt32(Table, 0x44, unchecked((int)keys[0]));
        Marshal.WriteInt32(Table, 0x48, unchecked((int)(keys[0] + keys.Length - 1)));
    }
    public void Dispose()
    {
        Marshal.FreeHGlobal(Table); Marshal.FreeHGlobal(rows); Marshal.FreeHGlobal(records);
    }
}
