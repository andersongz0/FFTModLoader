using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenericJobs;

/// <summary>Capacity and old user settings never register gameplay classes.</summary>
public static class JobActivationPolicy
{
    public sealed class Registration
    {
        public int SchemaVersion { get; set; }
        public ushort[] JobIds { get; set; } = [];
        public bool RequiresGenericJobsContent { get; set; }
        public ushort[] SwordLimitCommandIds { get; set; } = [];
        public Requirement[] Requirements { get; set; } = [];
    }

    public sealed class Requirement
    {
        public ushort JobId { get; set; }
        [JsonConverter(typeof(NumericByteArrayConverter))]
        public byte[] NativeJobIds { get; set; } = [];
        [JsonConverter(typeof(NumericByteArrayConverter))]
        public byte[] Levels { get; set; } = [];
    }

    /// <summary>
    /// Requirements are written by content manifests as readable number arrays.
    /// Older local manifests may contain the standard JSON base64 byte form, so
    /// accept both forms. A malformed requirement must be rejected by the
    /// normal validation path, not prevent every other registered job from
    /// starting because the serializer expected a string.
    /// </summary>
    private sealed class NumericByteArrayConverter : JsonConverter<byte[]>
    {
        public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String) return reader.GetBytesFromBase64();
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected a byte array.");
            var values = new List<byte>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetByte(out byte value))
                    throw new JsonException("Requirement values must be bytes.");
                values.Add(value);
            }
            if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Unterminated byte array.");
            return values.ToArray();
        }

        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (byte item in value) writer.WriteNumberValue(item);
            writer.WriteEndArray();
        }
    }

    public static IReadOnlyDictionary<ushort,Requirement> ReadRequirements(IEnumerable<string> modDirectories)
    {
        var result=new Dictionary<ushort,Requirement>();
        foreach(string directory in modDirectories)
        {
            string path=Path.Combine(directory,"FFTModLoader.jobs.json");
            if(!File.Exists(path))continue;
            var registration=JsonSerializer.Deserialize<Registration>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Empty job registration.");
            if(registration.SchemaVersion!=1 || registration.Requirements is null)
                throw new InvalidDataException("Invalid requirement registration.");
            foreach(var requirement in registration.Requirements)
            {
                if(!registration.JobIds.Contains(requirement.JobId) || requirement.NativeJobIds is null || requirement.Levels is null ||
                    requirement.NativeJobIds.Length==0 || requirement.NativeJobIds.Length!=requirement.Levels.Length ||
                    requirement.NativeJobIds.Any(j=>j>=22) || requirement.Levels.Any(l=>l is <1 or >8) ||
                    requirement.NativeJobIds.Distinct().Count()!=requirement.NativeJobIds.Length || !result.TryAdd(requirement.JobId,requirement))
                    throw new InvalidDataException("Invalid or duplicate new-job prerequisites.");
            }
        }
        return result;
    }
    public static bool Meets(Requirement requirement,ReadOnlySpan<byte> packedLevels)
    {
        if(packedLevels.Length!=12)throw new InvalidDataException("Native level array size differs.");
        for(int i=0;i<requirement.NativeJobIds.Length;i++)
        {
            int id=requirement.NativeJobIds[i];
            int level=(id&1)==0?packedLevels[id/2]>>4:packedLevels[id/2]&15;
            if(level<requirement.Levels[i])return false;
        }
        return true;
    }

    public static ushort[] ReadRegisteredJobs(IEnumerable<string> modDirectories)
    {
        var jobs = new List<ushort>();
        foreach (string directory in modDirectories)
        {
            string path = Path.Combine(directory, "FFTModLoader.jobs.json");
            if (!File.Exists(path)) continue;
            var registration = JsonSerializer.Deserialize<Registration>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Empty job registration.");
            if (registration.SchemaVersion != 1 || registration.JobIds == null ||
                registration.JobIds.Any(job => job < 0xA0 || job > 0xFF))
                throw new InvalidDataException("Unsupported job registration.");
            jobs.AddRange(registration.JobIds);
        }
        // Native job IDs define the appended class sequence. Directory/mod
        // discovery order must not put a later class before earlier classes.
        return jobs.Distinct().Order().ToArray();
    }

    public static byte[] ReadSwordLimitCommands(IEnumerable<string> modDirectories)
    {
        var commands = new byte[256];
        foreach (string directory in modDirectories)
        {
            string path = Path.Combine(directory, "FFTModLoader.jobs.json");
            if (!File.Exists(path)) continue;
            var registration = JsonSerializer.Deserialize<Registration>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Empty job registration.");
            if (registration.SchemaVersion != 1 || registration.JobIds == null || registration.SwordLimitCommandIds == null ||
                registration.SwordLimitCommandIds.Any(id => id < 0xA0 || id > 0xFF || !registration.JobIds.Contains(id)))
                throw new InvalidDataException("Sword Limit requires an explicitly registered generic command/job.");
            foreach (ushort id in registration.SwordLimitCommandIds) commands[id] = 1;
        }
        return commands;
    }

    public static ushort[] SelectJobs(ushort[] registered, string? preference)
    {
        if (string.IsNullOrWhiteSpace(preference)) return registered.ToArray();
        try
        {
            var requested = preference.Split(',').Select(value => value.Trim())
                .Where(value => value.Length != 0)
                .Select(value => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToUInt16(value[2..], 16)
                    : Convert.ToUInt16(value, value.Any(c => "ABCDEFabcdef".Contains(c)) ? 16 : 10));
            return requested.Where(registered.Contains).Distinct().ToArray();
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            // Malformed legacy configuration must not invent fallback classes.
            return registered.ToArray();
        }
    }
}
