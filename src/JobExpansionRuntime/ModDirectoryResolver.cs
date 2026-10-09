using System.Text.Json;

namespace GenericJobs;

internal static class ModDirectoryResolver
{
    // The standalone launcher finishes staging ALL enabled mods before it
    // starts the suspended game. Reloaded's directory map, in contrast, is
    // populated one mod at a time and cannot resolve later mods in a constructor.
    internal static string Resolve(string gameDirectory, string modId, Func<string, string> loadedDirectory)
    {
        if (string.IsNullOrWhiteSpace(modId) || modId is "." or ".." ||
            modId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            modId.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new InvalidDataException($"Invalid enabled mod ID: {modId}");

        string root = Path.Combine(gameDirectory, "FFTModLoader.Runtime", "ResolvedMods");
        if (!Directory.Exists(root)) return loadedDirectory(modId); // External Reloaded host.

        string directory = Path.Combine(root, modId);
        string configPath = Path.Combine(directory, "ModConfig.json");
        if (!File.Exists(configPath))
            throw new InvalidDataException($"Enabled mod was not staged by the launcher: {modId} ({configPath})");
        using var config = JsonDocument.Parse(File.ReadAllText(configPath));
        if (config.RootElement.ValueKind != JsonValueKind.Object ||
            !config.RootElement.TryGetProperty("ModId", out var stagedId) ||
            stagedId.ValueKind != JsonValueKind.String || stagedId.GetString() != modId)
            throw new InvalidDataException($"Staged ModConfig identity mismatch: {modId} ({configPath})");
        return directory;
    }
}
