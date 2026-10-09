using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using GenericJobs;

// Execute the production native machine code, not a managed reimplementation.
// No game process, save, executable or hook activation is involved.
unsafe class Program
{
    static short expectedUnit;
    static ushort expectedJob;
    static int expectedCategory, expectedMode, nativeCount, calls;
    static nint expectedOutput;
    static Type routes = null!, memory = null!;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate int LearnOriginal(short unit, ushort job, int category, nint output, int mode);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate nint RowOriginal(nint table, uint key);
    static long Compile(string method, long original, long list = 0)
    {
        byte[] code = (byte[])routes.GetMethod(method)!.Invoke(null,
            method == "BuildLearn" ? new object[] { original, list } : new object[] { original })!;
        var allocation = Activator.CreateInstance(memory)!;
        memory.GetMethod("Complete")!.Invoke(allocation, new object[] { code, method == "BuildSparse" });
        long address = (long)memory.GetProperty("Address")!.GetValue(allocation)!;
        Check(RtlLookupFunctionEntry((ulong)address, out ulong image, 0) != 0 && image == (ulong)address,
            "Native route lacks Windows unwind registration");
        return address;
    }

    static int Original(short unit, ushort job, int category, nint output, int mode)
    {
        Check(unit == expectedUnit && job == expectedJob && category == expectedCategory &&
            output == expectedOutput && mode == expectedMode, "Native arguments changed");
        for (int i = 0; i < nativeCount; ++i) {
            Marshal.WriteInt16(output, i * 8, unchecked((short)job));
            Marshal.WriteInt16(output, i * 8 + 2, (short)(100 + i));
        }
        Marshal.WriteInt64(output, nativeCount * 8, 0xFFFF0000);
        calls++;
        return nativeCount;
    }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    static void Main(string[] args)
    {
        var type = args.Length >= 1
            ? new AssemblyLoadContext("PackagedLearnHook", isCollectible: true)
                .LoadFromAssemblyPath(Path.GetFullPath(args[0])).GetType("GenericJobs.Mod", throwOnError: true)!
            : typeof(Mod);
        var delegateType = type.GetNestedType("JobAbilityListDelegate", BindingFlags.NonPublic)!;
        Check(delegateType.GetMethod("Invoke")!.ReturnType == typeof(int), "Native ABI lost its return");
        routes = type.Assembly.GetType("GenericJobs.NativeJobRoutes", true)!;
        memory = type.Assembly.GetType("GenericJobs.NativeJobRouteMemory", true)!;
        LearnOriginal original = Original;
        nint list = Marshal.AllocHGlobal(21 * 2);
        nint output = Marshal.AllocHGlobal(25 * 8);
        long originalAddress = (long)Marshal.GetFunctionPointerForDelegate(original);
        var native = (delegate* unmanaged<short, ushort, int, nint, int, int>)Compile("BuildLearn", originalAddress, (long)list);
        try {
            expectedOutput = output;
            foreach (ushort first in new ushort[] { 0x4A, 0x5E }) {
                for (int i = 0; i < 21; ++i) Marshal.WriteInt16(list, i * 2, unchecked((short)(first + i)));
                byte[] previous = new byte[42]; Marshal.Copy(list, previous, 0, previous.Length);
                foreach (short unit in new short[] { 0, 1, 53 })
                foreach (ushort selected in new ushort[] { 0x4A, 0x4B, 0x4C, 0x4D, 0x5E, 0xA0, 0xA1, 0xA2, 0xA3 })
                foreach (int category in new[] { 0, 1, 2, 3, 15 })
                foreach (int mode in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 })
                foreach (int count in new[] { 0, 1, 6, 24 }) {
                    expectedUnit = unit;
                    expectedJob = first != 0x5E && selected == 0x5E ? (ushort)0xA0 : selected;
                    expectedCategory = category; expectedMode = mode; nativeCount = count;
                    Check(native(unit, selected, category, output, mode) == count, "Native Learn count discarded");
                    for (int i = 0; i < count; ++i) {
                        Check(unchecked((ushort)Marshal.ReadInt16(output, i * 8)) == expectedJob, "Wrong row job");
                        Check(Marshal.ReadInt16(output, i * 8 + 2) == 100 + i, "Native ability row changed");
                    }
                    Check(Marshal.ReadInt64(output, count * 8) == 0xFFFF0000, "Terminator changed");
                }
                byte[] after = new byte[42]; Marshal.Copy(list, after, 0, after.Length);
                Check(after.SequenceEqual(previous), "Learn mutated the job list");
            }
            // Missing permanent list preserves the requested job and count.
            native = (delegate* unmanaged<short, ushort, int, nint, int, int>)Compile("BuildLearn", originalAddress);
            expectedJob = 0x5E; nativeCount = 6;
            Check(native(expectedUnit, expectedJob, expectedCategory, output, expectedMode) == 6, "Null-list fallback failed");
            Console.WriteLine($"PASS: {calls} production native Learn calls; counts, rows, all five arguments and Dark Knight compatibility preserved.");
            TestSparse();
        } finally { GC.KeepAlive(original); Marshal.FreeHGlobal(output); Marshal.FreeHGlobal(list); }
    }

    static void TestSparse()
    {
        int forwarded = 0, queries = 0;
        nint requestedTable = 0; uint requestedKey = 0;
        RowOriginal original = (table,key) => {
            Check(table == requestedTable && key == requestedKey, "Sparse fallback arguments changed");
            forwarded++; return (nint)0x12345678;
        };
        var native = (delegate* unmanaged<nint,uint,nint>)Compile("BuildSparse", (long)Marshal.GetFunctionPointerForDelegate(original));
        var oracle = routes.Assembly.GetType("GenericJobs.SparseNxdRows", true)!.GetMethod("TryLookup")!;
        void Query(nint table, uint key)
        {
            requestedTable = table; requestedKey = key;
            object[] parameters = { table, key, (nint)0, 0 };
            bool handled = (bool)oracle.Invoke(null, parameters)!;
            int previous = forwarded;
            nint actual = native(table, key);
            Check(actual == (handled ? (nint)parameters[2] : (nint)0x12345678), "Native sparse result differs from reference");
            Check(forwarded == previous + (handled ? 0 : 1), "Sparse fallback count differs");
            queries++;
        }
        Query(0, 17);
        foreach (uint[] keys in new[] {
            new uint[] {0,1,2,3}, new uint[] {96,97,98}, new uint[] {0,1,170,173},
            new uint[] {96,100,173}, new uint[] {0}, new uint[] {uint.MaxValue-2,uint.MaxValue},
            Enumerable.Range(0,163).Select(x=>(uint)x).Concat(new uint[]{170,171,172,173}).ToArray() })
        {
            nint table=Marshal.AllocHGlobal(0x60), rows=Marshal.AllocHGlobal(keys.Length*16), records=Marshal.AllocHGlobal(keys.Length*8);
            try {
                Marshal.Copy(new byte[0x60],0,table,0x60);
                Marshal.WriteIntPtr(table,0x10,rows); Marshal.WriteInt64(table,0x28,keys.Length);
                Marshal.WriteInt64(table,0x38,1); Marshal.WriteByte(table,0x41,1);
                Marshal.WriteInt32(table,0x44,unchecked((int)keys[0]));
                for(int i=0;i<keys.Length;i++) {
                    Marshal.WriteInt64(rows,i*16,1); Marshal.WriteIntPtr(rows,i*16+8,records+i*8);
                    Marshal.WriteInt32(records,i*8,unchecked((int)keys[i]));
                }
                foreach(long tag in new[] {1L,unchecked((long)0x8000000000000001UL)}) {
                    for(int i=0;i<keys.Length;i++) Marshal.WriteInt64(rows,i*16,tag);
                    for(uint key=0;key<220;key++) Query(table,key);
                    foreach(uint key in keys) Query(table,key);
                    Query(table,uint.MaxValue);
                }
                Marshal.WriteInt64(table,0x38,2); Query(table,keys[0]);
                Marshal.WriteInt64(table,0x38,1); Marshal.WriteByte(table,0x41,0); Query(table,keys[0]);
                Marshal.WriteByte(table,0x41,1); Marshal.WriteInt64(table,0x28,0); Query(table,0);
                Marshal.WriteInt64(table,0x28,1000001); Query(table,0);
                Marshal.WriteInt64(table,0x28,keys.Length); Marshal.WriteInt64(rows,2); Query(table,keys[0]);
                Marshal.WriteInt64(rows,1); Marshal.WriteIntPtr(rows,8,0); Query(table,keys[0]);
                Marshal.WriteIntPtr(rows,8,records);
                // CLR collections run between native lookups; no route enters it.
                for(int repeat=0;repeat<100;repeat++) {
                    GC.Collect(); GC.WaitForPendingFinalizers(); Query(table,keys[repeat%keys.Length]);
                }
                if(keys.SequenceEqual(new uint[]{0,1,170,173})) {
                    long routeAddress=(long)native;
                    Parallel.For(0,8,worker => {
                        var route=(delegate* unmanaged<nint,uint,nint>)routeAddress;
                        for(int i=0;i<100000;i++) {
                            Check(route(table,170)==rows+32,"Concurrent sparse result changed");
                            Check(route(table,172)==0,"Concurrent gap resolved to another row");
                            if(worker==0 && i%10000==0) GC.Collect();
                        }
                    });
                    Console.WriteLine("PASS: 1,600,000 native-only concurrent lookups with CLR collections; no managed original call for handled rows.");
                }
            } finally { Marshal.FreeHGlobal(records); Marshal.FreeHGlobal(rows); Marshal.FreeHGlobal(table); }
        }
        GC.KeepAlive(original);
        Console.WriteLine($"PASS: {queries} production native NXD lookups match reference; sparse/contiguous/gap/type/flag/fallback/GC cases.");
    }
    [DllImport("kernel32.dll")] static extern nint RtlLookupFunctionEntry(ulong pc,out ulong image,nint history);
}
