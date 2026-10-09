using System.Buffers.Binary;
using FFTModLoader.ContentExpansion;
using FFTModLoader.ContentExpansion.Runtime;

static class IntegrationProbeTests
{
    public static void Run()
    {
        int originalCalls = 0, errors = 0;
        int result = NativeObservation.Invoke(() => { originalCalls++; return -73; },
            () => throw new IOException("before observer failed"),
            _ => throw new IOException("after observer failed"), _ => errors++);
        Check(result == -73 && originalCalls == 1 && errors == 2, "Observer changed or retried native result");
        result = NativeObservation.Invoke(() => { originalCalls++; return 23; },
            () => throw new IOException(), null, _ => throw new Exception("logging failure"));
        Check(result == 23 && originalCalls == 2, "Failing reporter suppressed native call");
        try
        {
            NativeObservation.Invoke<int>(() => { originalCalls++; throw new ArithmeticException("original"); },
                null, _ => throw new Exception("after must not run"), _ => errors++);
            throw new Exception("Original exception was swallowed");
        }
        catch (ArithmeticException) { }
        Check(originalCalls == 3 && errors == 2, "Native exception was retried/reported as observation failure");

        var memory = new FakeMemory();
        var logs = new List<string>();
        const long image = 0x140000000, unit = 0x200000, list = 0x300000;
        var probe = new NativeIntegrationProbe(image, memory.Read, logs.Add);
        Check(!NativeProbeHooks.InstallationAllowed, "Revoked diagnostic became installable");
        try
        {
            // Null controller/image and nonexistent path prove the veto occurs
            // BEFORE accessing executable, native memory or hook controller.
            new NativeProbeHooks(probe).Install(null!, 0, "must-not-open-revoked-probe");
            throw new Exception("Revoked diagnostic created native hooks");
        }
        catch (NotSupportedException ex) when (ex.Message.Contains("revoked")) { }
        Check(logs.Count == 0, "Revocation ran observers");
        byte[] pointer = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(pointer, unit);
        memory.Add(image + 0x1800F50, pointer);
        byte[] fields = new byte[0xCE];
        fields[0x7E] = 0x80;
        BinaryPrimitives.WriteInt16LittleEndian(fields.AsSpan(0xCC), 150);
        memory.Add(unit, fields);
        memory.Add(list, Words(0x1170, 0x4173, 0xFFFF));
        probe.AbilityList(0, 75, 0, 2, list, 2);
        probe.AbilityList(0, 75, 0, 2, list, 2);
        Check(logs.Count == 1 && logs[0].Contains("jp=150") && logs[0].Contains("1170,4173"), "Learning snapshot/dedup failed");
        // Mastery has no output buffer. Null MUST NOT be read.
        probe.AbilityList(0, 75, 0, 3, 0, 13);
        Check(logs[^1].Contains("count-only"), "Mastery read a nonexistent output list");
        int logCount = logs.Count;
        probe.AbilityList(0, 75, 0, 1, 0, 24);
        probe.AbilityList(0, 75, 1, 2, 0, 24);
        probe.AbilityList(0, 162, 0, 2, 0, 24);
        Check(logs.Count == logCount, "Probe entered command/passive/GenericKnights route");
        Reject(() => probe.AbilityList(0, 75, 0, 2, list, 25));
        memory.Add(0x310000, Words(240, 243, 0xFFFF));
        probe.ShopList(0, 3, 0, 1, 0x310000, 2);
        Check(logs[^1].Contains("00F0,00F3"), "Shop read failed");
        logCount = logs.Count;
        probe.ShopList(0, 5, 0, 1, 0, 2);
        probe.ShopList(0, 3, 1, 1, 0, 2);
        probe.ShopList(100, 3, 0, 1, 0, 2);
        Check(logs.Count == logCount, "Probe entered equip/poach shop route");
        Reject(() => probe.ShopList(0, 3, 0, 1, list, 257));
        memory.Add(0x320000, Words(240, 0));
        Reject(() => probe.ShopList(0, 3, 0, 1, 0x320000, 1));

        memory.Add(image + 0x11A7C00 + 240, [2]);
        int? before = probe.StockBefore(0x40F0, 1); // Native ten-bit mask.
        Check(before == 2 && probe.StockBefore(261, 1) is null && probe.StockBefore(240, 0) is null,
            "Probe stock domain/read-only check failed");
        memory.Add(image + 0x11A7C00 + 240, [3]);
        probe.StockAfter(240, 1, before, 3);
        Check(logs[^1].Contains("before=2 after=3") && logs[^1].Contains("shared-native-route"), "Stock before/after failed");

        byte[] packet = new byte[33];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 0x11);
        packet[16] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), NativeSavePacket.Crc32(packet.AsSpan(16)));
        memory.Add(0x400000, packet);
        string hash = probe.Packet(0x400000, packet.Length)!;
        Check(hash == NativeSavePacket.ValidateAndHash(packet), "Save identity/CRC failed");
        Check(probe.Packet(0, packet.Length) is null && probe.Packet(0x400000, long.MaxValue) is null, "Unbounded packet read");
        byte[] other = packet.ToArray(); other[8] = 0x12;
        memory.Add(0x410000, other);
        Check(probe.Packet(0x410000, other.Length) is null, "Other save kind observed as party data");
        other = packet.ToArray(); other[^1] ^= 1;
        memory.Add(0x420000, other);
        Reject(() => probe.Packet(0x420000, other.Length));

        byte[] queue = new byte[0x148];
        BinaryPrimitives.WriteInt64LittleEndian(queue, packet.Length);
        BinaryPrimitives.WriteInt64LittleEndian(queue.AsSpan(8), 0x400000);
        memory.Add(0x500000, queue);
        var captured = probe.WriteBefore(0x500000, 0);
        memory.Remove(0x400000); memory.Remove(0x500000); // Original frees both.
        probe.Completed(captured, 0, 0);
        Check(logs[^1].Contains(hash), "Post-completion reread freed native data");
        BinaryPrimitives.WriteInt64LittleEndian(queue.AsSpan(8), 0);
        BinaryPrimitives.WriteInt32LittleEndian(queue.AsSpan(0x140), -9);
        memory.Add(0x510000, queue);
        captured = probe.WriteBefore(0x510000, 0);
        Check(captured.Hash is null && captured.EntryError == -9, "Failed save read an invalid packet");
        probe.Completed(captured, 0, -9);
        Check(logs[^1].Contains("entry-error=-9") && logs[^1].Contains("result=-9"), "Original failure result lost");
        // A failed logger must not become a native exception or infinite spam.
        var failing = new NativeIntegrationProbe(image, memory.Read, _ => throw new IOException());
        failing.Failure(new InvalidDataException());
        for (int i = 0; i < 400; i++) probe.StockAfter(240, 1, 2, 3);
        Check(logs.Count == 301 && logs[^1].Contains("limit reached"), "Diagnostic log was unbounded");
        Check(fields[0x7E] == 0x80 && BinaryPrimitives.ReadInt16LittleEndian(fields.AsSpan(0xCC)) == 150,
            "Probe changed synthetic native state");
        Console.WriteLine("PASS: integration observers preserve original result/call count under observer/logger failures; mastery/equipment/other jobs excluded, list bounds/flags, native stock before/after, CRC and pre-free save capture, log budget. Synthetic memory only.");
    }

    private static byte[] Words(params ushort[] entries)
    {
        byte[] bytes = new byte[entries.Length * 2];
        for (int i = 0; i < entries.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), entries[i]);
        return bytes;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Unsafe observer read accepted");
    }
}
