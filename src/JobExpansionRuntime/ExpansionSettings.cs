using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenericJobs;

internal sealed class ExpansionSettings
{
    internal const int OriginalShapes = 170;
    internal const int SafeMaxExtraJobs = 21;
    internal const int SafeMaxNewSpriteSets = 70;
    public int MaxExtraJobs { get; init; } = SafeMaxExtraJobs;
    public int MaxNewSpriteSets { get; init; } = SafeMaxNewSpriteSets;
    [JsonIgnore] public int ShapeCapacity => OriginalShapes + MaxNewSpriteSets;

    internal static ExpansionSettings Load(string path) =>
        File.Exists(path) ? Parse(File.ReadAllText(path)) : new();

    internal static ExpansionSettings Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("FFTModLoader.config.json must be an object.");
        var sections = document.RootElement.EnumerateObject()
            .Where(p => p.Name.Equals("Expansion", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sections.Length == 0) return new(); // Backwards compatible with existing installs.
        if (sections.Length != 1 || sections[0].Name != "Expansion")
            throw new InvalidDataException("Use a single section named exactly 'Expansion'.");
        var section = sections[0].Value;
        if (section.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Expansion must be an object, not null or a list.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in section.EnumerateObject())
            if (!names.Add(property.Name))
                throw new InvalidDataException($"Duplicate Expansion setting: {property.Name}");
        var settings = section.Deserialize<ExpansionSettings>(new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        })!;
        if (settings.MaxExtraJobs is < 0 or > SafeMaxExtraJobs)
            throw new InvalidDataException($"Expansion.MaxExtraJobs={settings.MaxExtraJobs}: supported range is 0..{SafeMaxExtraJobs} (includes Dark/Onion menu entries).");
        if (settings.MaxNewSpriteSets is < 0 or > SafeMaxNewSpriteSets)
            throw new InvalidDataException($"Expansion.MaxNewSpriteSets={settings.MaxNewSpriteSets}: supported range is 0..{SafeMaxNewSpriteSets}; each gender uses one set.");
        return settings;
    }

    internal void EnsureMenuFits(int count)
    {
        if (count < 0 || count > MaxExtraJobs)
            throw new InvalidDataException($"Expansion.MaxExtraJobs={MaxExtraJobs}, but {count} menu entries are configured. Increase capacity within 0..{SafeMaxExtraJobs} or reduce the job list. Nothing was truncated.");
    }

    internal void EnsureShapeFits(int shape, string mod)
    {
        if (shape < OriginalShapes || shape >= ShapeCapacity)
            throw new InvalidDataException($"Expansion.MaxNewSpriteSets={MaxNewSpriteSets}, but {mod} requires shape {shape}. New shape IDs must be {OriginalShapes}..{ShapeCapacity - 1}; IDs are not renumbered. Increase capacity within 0..{SafeMaxNewSpriteSets} or disable the conflicting mod.");
    }
}
