using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;

// Exercise the actual compiled SetupHooks with the packaged native hook provider
// in this isolated fixture process. Record and then disable its exit/crash hooks.
if (args.Length < 2)
    throw new ArgumentException("Usage: <runtime folder> <absent|present> [fake steam_api64.dll]");
var runtime = Path.GetFullPath(args[0]);
var hooksDirectory = Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", "Mods", "reloaded.sharedlib.hooks", "x64"));
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    // Use the shared hook provider's own memory/assembler dependency set.
    foreach (var directory in new[] { hooksDirectory, runtime })
    {
        var path = Path.Combine(directory, name.Name + ".dll");
        if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
    return null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(runtime, "Reloaded.Mod.Loader.dll"));
void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
Require(assembly.GetType("Reloaded.Mod.Loader.Utilities.Steam.SteamHook") == null,
    "Steam interception code is still compiled into the runtime.");
Require(assembly.GetType("Reloaded.Mod.Loader.Utilities.Steam.SteamAppId") == null,
    "Automatic Steam app-id writer is still compiled into the runtime.");
Require(!assembly.GetReferencedAssemblies().Any(a => a.Name!.StartsWith("Indieteur", StringComparison.Ordinal)),
    "Runtime still depends on Steam client discovery libraries.");

var caseRoot = Path.Combine(Path.GetTempPath(), "FFTModLoader-SteamNeutral-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(caseRoot);
Environment.CurrentDirectory = caseRoot;
File.WriteAllText("steam_appid.txt", "unchanged platform-owned sentinel\n");
var appIdHash = SHA256.HashData(File.ReadAllBytes("steam_appid.txt"));
var nativeHandle = IntPtr.Zero;
var watchedExports = new Dictionary<IntPtr, byte[]>();
if (args[1] == "present")
{
    Require(args.Length == 3, "Present case requires the native fake SteamAPI fixture.");
    nativeHandle = NativeLibrary.Load(Path.GetFullPath(args[2]));
    foreach (var name in new[] { "SteamAPI_IsSteamRunning", "SteamAPI_RestartAppIfNecessary" })
    {
        var address = NativeLibrary.GetExport(nativeHandle, name);
        var bytes = new byte[32];
        Marshal.Copy(address, bytes, 0, bytes.Length);
        watchedExports[address] = bytes;
    }
}
else
{
    Require(args[1] == "absent", "Unknown fixture case.");
    Require(Kernel.GetModuleHandleW("steam_api64.dll") == IntPtr.Zero, "Absent case is not isolated.");
}

var hooksAssembly = Assembly.Load("Reloaded.Hooks.Definitions");
var hooksType = hooksAssembly.GetType("Reloaded.Hooks.Definitions.IReloadedHooks", true)!;
var hooks = DispatchProxy.Create(hooksType, typeof(RecordingHooks));
var recorded = (RecordingHooks)hooks;
recorded.Provider = Assembly.Load("Reloaded.Hooks").GetType("Reloaded.Hooks.ReloadedHooks", true)!
    .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
var entry = assembly.GetType("Reloaded.Mod.Loader.EntryPoint", true)!;
var setup = entry.GetMethod("SetupHooks", BindingFlags.NonPublic | BindingFlags.Static)!;
try { setup.Invoke(null, new[] { hooks }); }
finally
{
    // Never let an isolated fixture's process exit invoke the production logger.
    foreach (var hook in recorded.Created)
        hook.GetType().GetMethod("Disable", Type.EmptyTypes)!.Invoke(hook, null);
}
Require(recorded.Targets.Count == 2, "Exit/crash diagnostics must remain the only platform hooks.");
var kernel32 = Kernel.GetModuleHandleW("kernel32.dll");
var expected = new HashSet<long>
{
    Kernel.GetProcAddress(kernel32, "ExitProcess").ToInt64(),
    Kernel.GetProcAddress(kernel32, "UnhandledExceptionFilter").ToInt64()
};
Require(expected.SetEquals(recorded.Targets), "Unexpected platform API hook requested.");
foreach (var export in watchedExports)
{
    var bytes = new byte[export.Value.Length];
    Marshal.Copy(export.Key, bytes, 0, bytes.Length);
    Require(bytes.SequenceEqual(export.Value), "SteamAPI export bytes changed.");
}
if (nativeHandle != IntPtr.Zero)
{
    var calls = Marshal.GetDelegateForFunctionPointer<CallCount>(NativeLibrary.GetExport(nativeHandle, "FFTFakeSteamCallCount"));
    Require(calls() == 0, "Runtime called a SteamAPI export.");
}
else
    Require(Kernel.GetModuleHandleW("steam_api64.dll") == IntPtr.Zero, "Runtime loaded SteamAPI in absent case.");
Require(SHA256.HashData(File.ReadAllBytes("steam_appid.txt")).SequenceEqual(appIdHash), "Steam app-id sentinel changed.");
Require(Directory.GetFiles(caseRoot).Length == 1, "Platform setup wrote unexpected files.");
Console.WriteLine($"PASS ({args[1]}): compiled runtime has no Steam hooks/client discovery dependency; SetupHooks requests only exit/crash diagnostics; SteamAPI calls=0, exports and app-id untouched.");

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate int CallCount();
public class RecordingHooks : DispatchProxy
{
    public object Provider { get; set; } = null!;
    public List<long> Targets { get; } = new();
    public List<object> Created { get; } = new();
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        if (method!.Name != "CreateHook") throw new InvalidOperationException("Unexpected hook-provider call: " + method.Name);
        var target = arguments!.Last();
        Targets.Add(target is IntPtr pointer ? pointer.ToInt64() : Convert.ToInt64(target));
        var hook = method.Invoke(Provider, arguments)!;
        Created.Add(hook);
        return hook;
    }
}
public static class Kernel
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    public static extern IntPtr GetProcAddress(IntPtr module, string name);
}
