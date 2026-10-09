using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using GenericJobs;

// Exercise the actual installed callback, not a reimplementation of it.
// No game process, save, executable or hook activation is involved.
unsafe class Program
{
    static short expectedUnit;
    static ushort expectedJob;
    static int expectedCategory, expectedMode, nativeCount, calls;
    static nint expectedOutput;

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
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var type = args.Length == 1
            ? new AssemblyLoadContext("PackagedLearnHook", isCollectible: true)
                .LoadFromAssemblyPath(Path.GetFullPath(args[0])).GetType("GenericJobs.Mod", throwOnError: true)!
            : typeof(Mod);
        var callback = type.GetMethod("JobAbilityListHook", flags)!;
        Check(callback.ReturnType == typeof(int), "Learn callback must return native count");
        var field = type.GetField("_jobAbilityListOriginal", flags)!;
        var delegateType = field.FieldType;
        Check(delegateType.GetMethod("Invoke")!.ReturnType == typeof(int), "Native ABI lost its return");
        var original = Delegate.CreateDelegate(delegateType, typeof(Program).GetMethod(nameof(Original), BindingFlags.Static | BindingFlags.NonPublic)!);
        var mod = RuntimeHelpers.GetUninitializedObject(type);
        field.SetValue(mod, original);
        nint list = Marshal.AllocHGlobal(21 * 2);
        nint output = Marshal.AllocHGlobal(25 * 8);
        var hooked = Delegate.CreateDelegate(delegateType, mod, callback);
        var native = (delegate* unmanaged<short, ushort, int, nint, int, int>)Marshal.GetFunctionPointerForDelegate(hooked);
        try {
            type.GetField("_jobList", flags)!.SetValue(mod, (long)list);
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
            type.GetField("_jobList", flags)!.SetValue(mod, 0L);
            expectedJob = 0x5E; nativeCount = 6;
            Check(native(expectedUnit, expectedJob, expectedCategory, output, expectedMode) == 6, "Null-list fallback failed");
            Console.WriteLine($"PASS: {calls} native-ABI calls through the production Learn hook; counts, rows, arguments and Dark Knight compatibility preserved.");
        } finally { GC.KeepAlive(hooked); GC.KeepAlive(original); Marshal.FreeHGlobal(output); Marshal.FreeHGlobal(list); }
    }
}
