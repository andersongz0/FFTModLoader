using System.Text.Json;

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
        return jobs.Distinct().ToArray();
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
