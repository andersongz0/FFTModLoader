using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FFTModLoader.ContentExpansion.Runtime;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

static class NativeShopRouteTests
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Capture(nint target, nint query, nint list, nint snapshot);
    [Function(CallingConventions.Microsoft)]
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Shop(short unit, short shop, short category, nint list, int filter, byte sort);

    public static void Run()
    {
        using var context = new OwnedNativeMemory(4096);
        using var state = new OwnedNativeMemory(4096);
        using var list = new OwnedNativeMemory(4096);
        using var unknownList = new OwnedNativeMemory(4096);
        using var query = new OwnedNativeMemory(4096);
        using var snapshot = new OwnedNativeMemory(4096);
        using var original = new OwnedNativeMemory(4096);
        using var bridge = new OwnedNativeMemory(4096);
        using var smallBridge = new OwnedNativeMemory(4096);
        using var storyBridge = new OwnedNativeMemory(4096);
        using var storyGetter = new OwnedNativeMemory(4096);
        using var storyData = new OwnedNativeMemory(4096);
        using var caller = new OwnedNativeMemory(4096);
        ushort[] extras = Enumerable.Range(261, 11).Select(id => (ushort)id).ToArray();
        byte[] seed = Enumerable.Range(0, 16).Select(i => (byte)(0x71 + i)).ToArray();
        snapshot.Write(160, seed);
        context.Write(24, BitConverter.GetBytes(2));
        context.Write(32, Words([240, 243, ushort.MaxValue]));
        original.Write(0, OriginalFixture((long)context.Address)); original.ExecutablePage();
        caller.Write(0, CaptureCaller()); caller.ExecutablePage();
        var capture = Marshal.GetDelegateForFunctionPointer<Capture>(caller.Address);
        IHook<Shop> hook;
        unsafe { hook = ReloadedHooks.Instance.CreateHook<Shop>((void*)bridge.Address, (long)original.Address); }
        bridge.Write(0, NativeShopListAppend.Build((long)hook.OriginalFunctionAddress, (long)state.Address,
            (long)list.Address, 256, extras)); bridge.ExecutablePage();
        smallBridge.Write(0, NativeShopListAppend.Build((long)hook.OriginalFunctionAddress, (long)state.Address,
            (long)list.Address, 13, extras)); smallBridge.ExecutablePage();
        // A native getter intentionally clobbers integer arguments; the wrapper
        // must still pass all original six args exactly and preserve output ABI.
        var getterCode=new List<byte>([0x48,0xB8,..BitConverter.GetBytes((long)storyData.Address),0x89,0x48,0x04,0x8B,0x00,
            0x31,0xC9,0x31,0xD2,0x45,0x31,0xC0,0x45,0x31,0xC9]);
        for(int i=0;i<6;i++)getterCode.AddRange([0x66,0x0F,0xEF,(byte)(0xC0+9*i)]);
        getterCode.Add(0xC3);storyGetter.Write(0,getterCode.ToArray());storyGetter.ExecutablePage();
        byte[] thresholds=[1,1,5,5,5,9,9,9,13,13,13];
        storyBridge.Write(0,NativeShopListAppend.Build((long)hook.OriginalFunctionAddress,(long)state.Address,
            (long)list.Address,256,extras,(long)storyGetter.Address,thresholds));storyBridge.ExecutablePage();
        foreach (ushort id in extras) state.Write(NativeItemStockLeaf.RegisteredOffset + id, [1]);
        state.Write(0, [1]);
        hook.Activate();
        try
        {
            foreach (short shop in Enumerable.Range(0, 15).Select(i => (short)i))
            {
                WriteQuery(-1, shop, NativeShopListAppend.MedicineShopTab, 0, 0);
                Expect(original.Address, list, [240, 243, .. extras], 13);
            }
            foreach (short shop in new short[] { -1, 15, 99, 100 })
            { WriteQuery(-1, shop, NativeShopListAppend.MedicineShopTab, 0, 0); Expect(original.Address, list, [240, 243], 2); }
            foreach (short category in new short[] { -1, 0, 1, 2, 3, 4, 5, 6, 8 })
            { WriteQuery(-1, 0, category, 0, 0); Expect(original.Address, list, [240, 243], 2); }
            foreach (int filter in new[] { -1, 1, 2, int.MaxValue })
            { WriteQuery(-1, 0, NativeShopListAppend.MedicineShopTab, filter, 0); Expect(original.Address, list, [240, 243], 2); }
            foreach (byte sort in new byte[] { 1, 2, 255 })
            { WriteQuery(-1, 0, NativeShopListAppend.MedicineShopTab, 0, sort); Expect(original.Address, list, [240, 243], 2); }
            WriteQuery(17, 0, NativeShopListAppend.MedicineShopTab, 0, 0);
            Expect(original.Address, unknownList, [240, 243], 2);
            foreach (byte enabled in new byte[] { 0, 2, 255 })
            { state.Write(0, [enabled]); Expect(original.Address, list, [240, 243], 2); }
            state.Write(0, [1]);
            context.Write(32, Words([240, 261, ushort.MaxValue]));
            Expect(original.Address, list, [240, .. extras], 12); // already present: do not duplicate
            context.Write(32, Words([240, 243, ushort.MaxValue]));
            state.Write(NativeItemStockLeaf.RegisteredOffset + 261, [0]);
            state.Write(NativeItemStockLeaf.RegisteredOffset + 262, [2]);
            Expect(original.Address, list, [240, 243, .. extras.Skip(2)], 11);
            state.Write(NativeItemStockLeaf.RegisteredOffset + 261, [1]);
            state.Write(NativeItemStockLeaf.RegisteredOffset + 262, [1]);
            Expect(smallBridge.Address, list, [240, 243], 2); // capacity insufficient -> no append

            context.Write(24, BitConverter.GetBytes(-1));
            Expect(original.Address, list, [240, 243], -1);
            context.Write(24, BitConverter.GetBytes(260));
            Expect(original.Address, list, [240, 243], 260);
            context.Write(24, BitConverter.GetBytes(2));
            context.Write(36, Words([0]));
            Expect(original.Address, list, [240, 243], 2, terminator: 0);
            context.Write(36, Words([ushort.MaxValue]));
            hook.Disable(); Expect(original.Address, list, [240, 243], 2);
            hook.Enable(); Expect(original.Address, list, [240, 243, .. extras], 13);
            foreach(int progress in new[]{-1,0,1,4,5,8,9,12,13,20,1,13,5})
            foreach(short shop in Enumerable.Range(0,15).Select(i=>(short)i))
            {
                storyData.Write(0,BitConverter.GetBytes(progress));
                WriteQuery(17,shop,NativeShopListAppend.MedicineShopTab,0,0);
                ushort[] available=extras.Where((id,i)=>progress>=thresholds[i]).ToArray();
                Expect(storyBridge.Address,list,[240,243,..available],2+available.Length);
                Check(BinaryPrimitives.ReadInt32LittleEndian(storyData.Read(4,4))==0x6F,"Story getter received wrong event flag");
            }
            foreach(short shop in new short[]{-1,15,99,100})
            {
                storyData.Write(4,BitConverter.GetBytes(123));
                WriteQuery(17,shop,NativeShopListAppend.MedicineShopTab,0,0);
                Expect(storyBridge.Address,list,[240,243],2);
                Check(BinaryPrimitives.ReadInt32LittleEndian(storyData.Read(4,4))==123,"Excluded shop invoked story lookup");
            }
            Console.WriteLine("PASS: native shop append through actual Reloaded hook; all 15 ordinary shops, six args/stack forwarding, no duplicate IDs, no buffer overrun, native prefix/terminator and post-call GP/SIMD state preserved. No gil/stock writes; other categories/filters/sorts/outputs and invalid state pass through. OWN fixture only, not live shop integration.");
        }
        finally { if (hook.IsHookEnabled) hook.Disable(); GC.KeepAlive(hook); GC.KeepAlive(capture); }

        void WriteQuery(short unit, short shop, short category, int filter, byte sort)
        {
            byte[] bytes = new byte[16];
            BinaryPrimitives.WriteInt16LittleEndian(bytes, unit);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2), shop);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(4), category);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), filter); bytes[12] = sort;
            query.Write(0, bytes);
        }
        void Expect(nint target, OwnedNativeMemory output, ushort[] expected, int result, ushort terminator = ushort.MaxValue)
        {
            output.Write(0, Enumerable.Repeat((byte)0xA5, 4096).ToArray());
            byte[] stateBefore = state.Read(0, 4096);
            int calls = BinaryPrimitives.ReadInt32LittleEndian(context.Read(20, 4));
            Check(capture(target, query.Address, output.Address, snapshot.Address) == result, "Shop result changed");
            Check(BinaryPrimitives.ReadInt32LittleEndian(context.Read(20, 4)) == calls + 1, "Original shop call repeated/skipped");
            byte[] expectedBytes = Enumerable.Repeat((byte)0xA5, 4096).ToArray();
            Words([.. expected, terminator]).CopyTo(expectedBytes, 0);
            Check(output.Read(0, 4096).SequenceEqual(expectedBytes), "Shop prefix/tail/guard bytes changed unexpectedly");
            Check(state.Read(0, 4096).SequenceEqual(stateBefore), "Shop listing mutated stock/registration");
            byte[] received = context.Read(0, 20), request = query.Read(0, 16);
            Check(BinaryPrimitives.ReadInt32LittleEndian(received) == BinaryPrimitives.ReadInt16LittleEndian(request) &&
                BinaryPrimitives.ReadInt32LittleEndian(received.AsSpan(4)) == BinaryPrimitives.ReadInt16LittleEndian(request.AsSpan(2)) &&
                BinaryPrimitives.ReadInt32LittleEndian(received.AsSpan(8)) == BinaryPrimitives.ReadInt16LittleEndian(request.AsSpan(4)) &&
                BinaryPrimitives.ReadInt32LittleEndian(received.AsSpan(12)) == BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(8)) &&
                BinaryPrimitives.ReadInt32LittleEndian(received.AsSpan(16)) == request[12], "Six shop args not forwarded exactly");
            byte[] saved = snapshot.Read(0, 160);
            uint[] gp = [0x22334455, 0x11223344, 0x33445566, 0x44556677, 0x55667788, 0x66778899];
            for (int i = 0; i < gp.Length; i++) Check(BinaryPrimitives.ReadUInt64LittleEndian(saved.AsSpan(i * 8)) == gp[i], "Native post-call GP state changed");
            for (int i = 0; i < 6; i++) Check(saved.AsSpan(64 + i * 16, 16).SequenceEqual(seed), "Shop route changed SIMD state");
        }
    }

    private static byte[] OriginalFixture(long context)
    {
        var b = new List<byte>(Enumerable.Repeat((byte)0x90, 16)); // ample relocatable prologue
        void E(string hex) => b.AddRange(Convert.FromHexString(hex.Replace(" ", "")));
        E("48 B8"); b.AddRange(BitConverter.GetBytes(context));
        E("89 08 89 50 04 44 89 40 08 44 8B 54 24 28 44 89 50 0C 44 0F B6 54 24 30 44 89 50 10 FF 40 14");
        E("44 0F B7 50 20 66 45 89 11 44 0F B7 50 22 66 45 89 51 02 44 0F B7 50 24 66 45 89 51 04 8B 40 18");
        E("B9 55 44 33 22 BA 44 33 22 11 41 B8 66 55 44 33 41 B9 77 66 55 44 41 BA 88 77 66 55 41 BB 99 88 77 66 C3");
        return b.ToArray();
    }
    private static byte[] CaptureCaller()
    {
        var b = new List<byte>();
        void E(string hex) => b.AddRange(Convert.FromHexString(hex.Replace(" ", "")));
        E("53 56 57 48 83 EC 40 4C 89 CB 48 89 D6 4C 89 C7 48 89 4C 24 30");
        for (int i = 0; i < 6; i++) { b.AddRange([0xF3, 0x0F, 0x6F, (byte)(0x83 + i * 8)]); b.AddRange(BitConverter.GetBytes(160)); }
        E("0F BF 0E 0F BF 56 02 44 0F BF 46 04 49 89 F9 8B 46 08 89 44 24 20 0F B6 46 0C 89 44 24 28 FF 54 24 30");
        E("48 89 0B 48 89 53 08 4C 89 43 10 4C 89 4B 18 4C 89 53 20 4C 89 5B 28 89 43 30");
        for (int i = 0; i < 6; i++) { b.AddRange([0xF3, 0x0F, 0x7F, (byte)(0x83 + i * 8)]); b.AddRange(BitConverter.GetBytes(64 + i * 16)); }
        E("48 83 C4 40 5F 5E 5B C3"); return b.ToArray();
    }
    private static byte[] Words(ushort[] words)
    { byte[] bytes = new byte[words.Length * 2]; for (int i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), words[i]); return bytes; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
