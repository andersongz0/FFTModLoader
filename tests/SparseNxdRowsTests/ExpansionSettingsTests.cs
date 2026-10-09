using GenericJobs;
using System.Text.Json;

internal static class ExpansionSettingsTests
{
    internal static void Run()
    {
        ModDirectoryResolverTests.Run();
        static void Require(bool value, string message)
        {
            if (!value) throw new Exception(message);
        }
        static void Reject(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException) { return; }
            throw new Exception("Invalid expansion setting was not rejected");
        }
        foreach (string json in new[] {"{}", "{\"KeepLauncherOpen\":true}", "{\"Expansion\":{}}"})
        {
            var defaults = ExpansionSettings.Parse(json);
            Require(defaults.MaxExtraJobs == 21 && defaults.MaxNewSpriteSets == 70 && defaults.ShapeCapacity == 240, "Existing install defaults changed");
        }
        Require(ExpansionSettings.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".missing.json")).ShapeCapacity == 240, "Missing config default changed");
        Require(ExpansionSettings.Parse("{\"Expansion\":{\"MaxExtraJobs\":4}}").MaxNewSpriteSets == 70, "Partial defaults failed");
        Require(ExpansionSettings.Parse("{\"Expansion\":{\"MaxNewSpriteSets\":4}}").MaxExtraJobs == 21, "Partial defaults failed");
        for (int jobs = 0; jobs <= 21; jobs++)
        for (int sprites = 0; sprites <= 70; sprites++)
        {
            var settings = ExpansionSettings.Parse($"{{\"Expansion\":{{\"MaxExtraJobs\":{jobs},\"MaxNewSpriteSets\":{sprites}}}}}");
            Require(settings.MaxExtraJobs == jobs && settings.ShapeCapacity == 170+sprites, "Capacity not applied");
            Require(settings.ShapeCapacity <= byte.MaxValue && settings.ShapeCapacity*8 < 0x1000, "Native bounds/addressed table page overflow");
            settings.EnsureMenuFits(jobs);
            Reject(() => settings.EnsureMenuFits(jobs+1));
            Reject(() => settings.EnsureMenuFits(-1));
            for (int shape = 170; shape < settings.ShapeCapacity; shape++) settings.EnsureShapeFits(shape, "test");
            Reject(() => settings.EnsureShapeFits(169, "test"));
            Reject(() => settings.EnsureShapeFits(settings.ShapeCapacity, "test"));
        }
        foreach (string section in new[] {
            "{\"MaxExtraJobs\":-1}", "{\"MaxExtraJobs\":22}", "{\"MaxNewSpriteSets\":-1}", "{\"MaxNewSpriteSets\":71}",
            "{\"MaxExtraJobs\":1.5}", "{\"MaxExtraJobs\":\"21\"}", "{\"MaxExtraJobs\":true}", "{\"MaxNewSpriteSets\":null}",
            "{\"MaxExtraJobs\":999999999999}", "{\"MaxExtraJobs\":4,\"MaxExtraJobs\":5}",
            "{\"maxExtraJobs\":4}", "{\"MaxNewSprites\":4}", "null", "[]", "false"})
            Reject(() => ExpansionSettings.Parse("{\"Expansion\":"+section+"}"));
        foreach (string json in new[] {"{", "[]", "null", "{\"expansion\":{}}", "{\"Expansion\":{},\"Expansion\":{}}"})
            Reject(() => ExpansionSettings.Parse(json));
        var installedMinimum = ExpansionSettings.Parse("{\"Expansion\":{\"MaxExtraJobs\":4,\"MaxNewSpriteSets\":4}}");
        installedMinimum.EnsureMenuFits(4);
        foreach (int shape in new[] {170,171,172,173}) installedMinimum.EnsureShapeFits(shape,"GenericKnights");
        var gap = ExpansionSettings.Parse("{\"Expansion\":{\"MaxNewSpriteSets\":5}}");
        gap.EnsureShapeFits(174, "gap");
        Reject(() => gap.EnsureShapeFits(175, "gap")); // Sparse IDs consume address space, not just record count.
        foreach (string path in Environment.GetCommandLineArgs().Skip(2))
        {
            var settings = ExpansionSettings.Load(path);
            settings.EnsureMenuFits(4);
            settings.EnsureShapeFits(173,"GenericKnights");
            Console.WriteLine($"PASS: installed JSON {path}: jobs={settings.MaxExtraJobs}, sprites={settings.MaxNewSpriteSets}, total shapes={settings.ShapeCapacity}");
            string game = Path.GetDirectoryName(Path.GetFullPath(path))!;
            string appPath = Path.Combine(game,"FFTModLoader.Runtime","Apps","fft_enhanced.exe","AppConfig.json");
            if (!File.Exists(appPath)) continue;
            using var app = JsonDocument.Parse(File.ReadAllText(appPath));
            var stagedShapes = new HashSet<int>();
            int enabled = 0;
            foreach (var idValue in app.RootElement.GetProperty("EnabledMods").EnumerateArray())
            {
                string id = idValue.GetString()!;
                string directory = ModDirectoryResolver.Resolve(game,id, _ => throw new Exception("Real staged install consulted Reloaded before registration"));
                enabled++;
                string manifestPath = Path.Combine(directory,"FFTModLoader.visuals.json");
                if (!File.Exists(manifestPath)) continue;
                using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
                Require(manifest.RootElement.GetProperty("SchemaVersion").GetInt32() == 1, "Installed visual schema changed");
                foreach (var entry in manifest.RootElement.GetProperty("Entries").EnumerateArray())
                {
                    int shape = entry.GetProperty("ShapeId").GetInt32();
                    settings.EnsureShapeFits(shape,id);
                    stagedShapes.Add(shape);
                }
            }
            Require(stagedShapes.SetEquals(new[] {170,171,172,173}), "Installed Holy/Rune staging changed");
            Console.WriteLine($"PASS: actual enabled-mod staging: {enabled} mods, {stagedShapes.Count} sprite sets validated without the incomplete Reloaded directory map.");
        }
        Console.WriteLine("PASS: 1562 capacity combinations, legacy/partial/missing defaults, malformed/duplicate/unknown/type/range errors, no truncation, sparse-ID high-water bounds and current mod minimum 4/4.");
    }
}
