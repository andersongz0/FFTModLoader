using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

if (args.Length != 1) throw new ArgumentException("Provide the isolated prepared game fixture.");
string root = Path.Combine(Path.GetFullPath(args[0]), "FFTModLoader.Runtime");
string loader = Path.Combine(root, "Loader", "X64");
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string path = Path.Combine(loader, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(loader, "Reloaded.Mod.Loader.IO.dll"));
var type = assembly.GetType("Reloaded.Mod.Loader.IO.Config.ModConfig", true)!;
using var app = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Apps", "fft_enhanced.exe", "AppConfig.json")));
string[] expected = app.RootElement.GetProperty("EnabledMods").EnumerateArray().Select(v => v.GetString()!).ToArray();
var mods = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
foreach (string id in expected)
{
    var mod = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(root, "ResolvedMods", id, "ModConfig.json")), type)!;
    mods.Add(mod);
}
var sorted = (IEnumerable)type.GetMethod("SortMods", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [mods])!;
string[] actual = sorted.Cast<object>().Select(v => (string)type.GetProperty("ModId")!.GetValue(v)!).ToArray();
if (!actual.SequenceEqual(expected)) throw new InvalidOperationException("Actual integrated runtime sorting differs: " + string.Join(",", actual));
Console.WriteLine("PASS: actual bundled runtime SortMods exactly preserves prepared console/application order; no game or mod entry points executed.");
