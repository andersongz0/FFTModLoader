using System.Text.Json;
using System.Text.Json.Serialization;

namespace FFTModLoader.ContentExpansion;

public enum ExpandedTable { Job, Command, Ability, Shape, Formula }
public sealed record ExpandedId(ExpandedTable Table, int Id, string Key, bool Reserved = false);
public sealed record ExpandedCommand(int Id, string Key, bool Monster, ushort[] Actions);
public sealed record ExpandedContentDefinition(int SchemaVersion, string ModId,
    ExpandedId[] Ids, ExpandedCommand[] Commands);

/// <summary>
/// Loader-owned, transactional ID ownership. Empty/unnamed original rows are
/// still original IDs, never allocatable holes. Reservation is not activation:
/// the native host must separately install every required route before use.
/// </summary>
public sealed class ExpandedContentCatalog
{
    private readonly Dictionary<(ExpandedTable Table, int Id), (string Owner, ExpandedId Entry)> _ids = [];
    private readonly Dictionary<string, string> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ExpandedCommand> _commands = [];
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public ExpandedContentCatalog()
    {
        // Existing loader compatibility contracts retain these identities even
        // when their consumer is disabled. They cannot be reassigned to a mod.
        for (int id = 513; id <= 523; id++) Claim(ExpandedTable.Ability, id, "ffttic.reworkedchemist", "chemist." + id);
        Claim(ExpandedTable.Formula, 107, "ffttic.reworkedchemist", "chemist.effects");
        for (int id = 170; id <= 173; id++) Claim(ExpandedTable.Shape, id, "ffttic.jobs.genericknights", "knights." + id);
    }

    public static ExpandedContentDefinition Read(string json) =>
        JsonSerializer.Deserialize<ExpandedContentDefinition>(json, JsonOptions)
        ?? throw new InvalidDataException("Empty expanded content definition.");

    public void Register(ExpandedContentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            if (definition.SchemaVersion != 1 || string.IsNullOrWhiteSpace(definition.ModId) ||
                definition.Ids is null || definition.Commands is null || definition.Ids.Length == 0 ||
                definition.Ids.Length > 2048 || definition.Commands.Length > 29)
                throw new InvalidDataException("Unsupported expanded content definition.");
            string fingerprint = JsonSerializer.Serialize(definition, JsonOptions);
            if (_definitions.TryGetValue(definition.ModId, out string? previous))
            {
                if (previous == fingerprint) return;
                throw new InvalidDataException("A content owner cannot change its registered IDs during a session.");
            }
            var pending = new Dictionary<(ExpandedTable, int), ExpandedId>();
            var keys = new HashSet<(ExpandedTable, string)>();
            foreach (var entry in definition.Ids)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Key) || !IsNewId(entry.Table, entry.Id) ||
                    !pending.TryAdd((entry.Table, entry.Id), entry) || !keys.Add((entry.Table, entry.Key)))
                    throw new InvalidDataException("An expanded ID is original, invalid or duplicated.");
                if (_ids.TryGetValue((entry.Table, entry.Id), out var owned))
                    throw new InvalidDataException($"{entry.Table} {entry.Id} is already owned by {owned.Owner}.");
            }
            var commandIds = new HashSet<int>();
            foreach (var command in definition.Commands)
            {
                if (command is null || !commandIds.Add(command.Id) ||
                    !pending.TryGetValue((ExpandedTable.Command, command.Id), out var entry) ||
                    entry.Reserved || command.Key != entry.Key || command.Actions is null ||
                    command.Actions.Length is < 1 or > 16 || command.Actions.Distinct().Count() != command.Actions.Length)
                    throw new InvalidDataException("Command lacks its own active ID or bounded unique action list.");
                foreach (ushort ability in command.Actions)
                {
                    // Explicit reuse is allowed for original ACTION skills,
                    // not passive records, UI flags, zero or the sentinel 512.
                    if (ability is > 0 and < 368) continue;
                    if (!pending.TryGetValue((ExpandedTable.Ability, ability), out var action) || action.Reserved)
                        throw new InvalidDataException("Command references an unregistered or reserved action.");
                }
            }
            if (pending.Values.Any(e => e.Table == ExpandedTable.Command && !e.Reserved && !commandIds.Contains(e.Id)))
                throw new InvalidDataException("Active command has no action row.");
            // Commit only after ALL tables and links pass. A bad final row must
            // not leak the earlier ID reservations into another mod's load.
            foreach (var (identity, entry) in pending) _ids.Add(identity, (definition.ModId, entry));
            foreach (var command in definition.Commands)
                _commands.Add(command.Id, command with { Actions = command.Actions.ToArray() });
            _definitions.Add(definition.ModId, fingerprint);
        }
    }

    public ExpandedCommand? Command(int id)
    {
        lock (_gate) return _commands.TryGetValue(id, out var command)
            ? command with { Actions = command.Actions.ToArray() } : null;
    }

    public bool IsActive(ExpandedTable table, int id)
    {
        lock (_gate) return _ids.TryGetValue((table, id), out var entry) && !entry.Entry.Reserved;
    }

    public string? Owner(ExpandedTable table, int id)
    {
        lock (_gate) return _ids.TryGetValue((table, id), out var entry) ? entry.Owner : null;
    }

    public static bool IsNewId(ExpandedTable table, int id) => table switch
    {
        ExpandedTable.Job => id is >= 174 and < 256,
        ExpandedTable.Command => id is >= 227 and < 256,
        ExpandedTable.Ability => id is >= 513 and < 1024,
        ExpandedTable.Shape => id is >= 170 and < 256,
        ExpandedTable.Formula => id is >= 107 and < 256,
        _ => false
    };

    private void Claim(ExpandedTable table, int id, string owner, string key) =>
        _ids.Add((table, id), (owner, new ExpandedId(table, id, key)));
}
