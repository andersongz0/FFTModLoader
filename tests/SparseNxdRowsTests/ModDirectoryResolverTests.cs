using GenericJobs;
using System.Text.Json;

internal static class ModDirectoryResolverTests
{
    internal static void Run()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "FFTModLoader-resolver-" + Guid.NewGuid());
        Directory.CreateDirectory(fixture);
        try
        {
            const string id = "ffttic.jobs.genericknights";
            // Reproduce Reloaded's map BEFORE this later mod is loaded.
            var loaded = new Dictionary<string, string>();
            int lookups = 0;
            string LoadedDirectory(string key) { lookups++; return loaded[key]; }
            string root = Path.Combine(fixture, "FFTModLoader.Runtime", "ResolvedMods");
            string staged = Path.Combine(root, id);
            Directory.CreateDirectory(staged);
            string config = Path.Combine(staged, "ModConfig.json");
            File.WriteAllText(config, JsonSerializer.Serialize(new { ModId = id }));
            File.WriteAllText(Path.Combine(staged, "FFTModLoader.visuals.json"),
                "{\"SchemaVersion\":1,\"Entries\":[{\"ShapeId\":170},{\"ShapeId\":171},{\"ShapeId\":172},{\"ShapeId\":173}]}");
            string actual = ModDirectoryResolver.Resolve(fixture, id, LoadedDirectory);
            if (actual != staged || lookups != 0) throw new Exception("Constructor consulted incomplete loaded-mod map");
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(actual,"FFTModLoader.visuals.json")));
            foreach (var entry in manifest.RootElement.GetProperty("Entries").EnumerateArray())
            {
                int shape = entry.GetProperty("ShapeId").GetInt32();
                ExpansionSettings.Parse("{\"Expansion\":{\"MaxNewSpriteSets\":4}}").EnsureShapeFits(shape,id);
            }
            Reject(() => ExpansionSettings.Parse("{\"Expansion\":{\"MaxNewSpriteSets\":3}}").EnsureShapeFits(173,id));
            foreach (string invalid in new[] {"../outside", "..\\outside", "C:escape", ".", "..", "", "abs/path"})
                Reject(() => ModDirectoryResolver.Resolve(fixture, invalid, LoadedDirectory));
            Reject(() => ModDirectoryResolver.Resolve(fixture,"missing.mod",LoadedDirectory));
            File.WriteAllText(config,"{\"ModId\":\"wrong.id\"}");
            Reject(() => ModDirectoryResolver.Resolve(fixture,id,LoadedDirectory));
            File.WriteAllText(config,"{\"ModId\":null}");
            Reject(() => ModDirectoryResolver.Resolve(fixture,id,LoadedDirectory));
            File.WriteAllText(config,"{");
            Reject(() => ModDirectoryResolver.Resolve(fixture,id,LoadedDirectory));
            // Preserve external Reloaded hosts without standalone staging.
            string externalGame = Path.Combine(fixture,"external-game");
            loaded[id] = staged;
            if (ModDirectoryResolver.Resolve(externalGame,id,LoadedDirectory) != staged || lookups != 1)
                throw new Exception("External Reloaded path lookup changed");
            Console.WriteLine("PASS: later enabled mod resolves before Reloaded registration; staged identity/missing/invalid paths fail closed; insufficient sprite capacity still rejected; external Reloaded fallback preserved.");
        }
        finally { Directory.Delete(fixture, recursive:true); }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException) { return; }
        throw new Exception("Invalid staged mod or insufficient capacity was not rejected");
    }
}
