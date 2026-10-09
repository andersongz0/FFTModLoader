using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using FF16Tools.Pack;
using FF16Tools.Pack.Crypto;
using FF16Tools.Pack.Packing;

using Microsoft.Extensions.Logging.Abstractions;

internal static class Program
{
    private static readonly HashSet<string> Locales =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "en", "ja", "de", "fr", "cs", "ct", "ko"
        };

    private sealed record ModInfo(
        string Directory,
        string Name,
        string UniqueId,
        int Priority,
        string Version,
        IReadOnlyList<ModDependency> Dependencies,
        bool RuntimeVisible = true
    );

    private sealed record ModDependency(
        string UniqueId,
        string? MinimumVersion,
        bool Required
    );

    private sealed record PackEntry(
        string Target,
        string Source,
        string ModId,
        string Mode,
        string PackName
    );

    private sealed record NexInput(
        string Target,
        string Source,
        string ModId,
        string Mode,
        string PackName
    );

    private sealed record JobNexClone(
        string ModId,
        uint TargetId,
        uint SourceId,
        uint? OverviewSourceId
    );

    public static async Task<int> Main(string[] args)
    {
        var packManagers =
            new Dictionary<string, FF16PackManager>(
                StringComparer.OrdinalIgnoreCase
            );

        try
        {
            string gameDir =
                args.Length > 0
                    ? Path.GetFullPath(args[0])
                    : AppContext.BaseDirectory;

            string? reloadedRoot = GetOptionValue(args, "--reloaded-root");
            if (!string.IsNullOrWhiteSpace(reloadedRoot))
                reloadedRoot = Path.GetFullPath(reloadedRoot);
            HashSet<string>? reloadedModIds = ParseIdFilter(
                GetOptionValue(args, "--reloaded-mod-ids"));

            string modsDir = Path.Combine(gameDir, "Mods");
            string dataDir = Path.Combine(gameDir, "data");
            string tempDir = Path.Combine(gameDir, "FFTModLoader.temp");

            Console.WriteLine("[FFTModPacker] Game: " + gameDir);

            if (args.Contains(
                    "--verify-job-visuals",
                    StringComparer.OrdinalIgnoreCase))
            {
                return JobVisualVerifier.Verify(
                    dataDir,
                    DetectGameVersion(gameDir));
            }

            if (args.Contains(
                    "--dump-job-catalog",
                    StringComparer.OrdinalIgnoreCase))
            {
                return JobCatalogDumper.Dump(
                    dataDir,
                    DetectGameVersion(gameDir));
            }

            if (args.Contains(
                    "--dump-sprite-assets",
                    StringComparer.OrdinalIgnoreCase))
            {
                return JobCatalogDumper.DumpSpriteAssets(dataDir);
            }

            string planPath = Path.Combine(gameDir, "FFTModLoader.modplan.json");
            if (File.Exists(planPath))
                File.Delete(planPath); // Never reuse a plan from a previous run.

            if (!Directory.Exists(modsDir))
            {
                Console.WriteLine(
                    "[FFTModPacker] Mods/ nao existe; nada para gerar."
                );

                WriteModPlan(planPath, []);
                if (args.Contains("--plan-only", StringComparer.OrdinalIgnoreCase))
                    return 0;
                DeleteOldGeneratedPacks(dataDir);
                WriteIndex(gameDir, []);
                return 0;
            }

            List<ModInfo> mods = LoadMods(modsDir);
            if (!string.IsNullOrWhiteSpace(reloadedRoot))
            {
                List<ModInfo> imported = LoadReloadedMods(
                    reloadedRoot, reloadedModIds);
                var known = mods.Select(m => m.UniqueId).ToHashSet(
                    StringComparer.OrdinalIgnoreCase);
                foreach (ModInfo mod in imported)
                {
                    if (known.Add(mod.UniqueId))
                        mods.Add(mod);
                    else
                        Console.Error.WriteLine(
                            $"[ReloadedCompat] {mod.UniqueId} ignorado: ja existe em Mods/."
                        );
                }
            }
            WriteModPlan(planPath, mods);

            // Validate dependency resolution without opening or modifying packs.
            if (args.Contains("--plan-only", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[FFTModPacker] Plano validado: {mods.Count} mods; {planPath}");
                return 0;
            }

            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);

            Directory.CreateDirectory(tempDir);
            DeleteOldGeneratedPacks(dataDir);

            Console.WriteLine(
                $"[FFTModPacker] Mods ativos: {mods.Count}"
            );

            var entries =
                new Dictionary<
                    (string Mode, string Pack, string Target),
                    PackEntry>();

            var nexInputs =
                new Dictionary<
                    (string Mode, string Target),
                    List<NexInput>>();

            var jobNexClones = new List<JobNexClone>();

            foreach (ModInfo mod in mods)
            {
                ScanContentJson(mod, entries, nexInputs);
                ScanLegacyData(mod, entries, nexInputs);
                ScanJobNexClones(mod, jobNexClones);
            }

            OpenBasePackManagers(
                dataDir,
                packManagers
            );

            MergeNexInputs(
                tempDir,
                packManagers,
                nexInputs,
                entries
            );

            ApplyJobNexClones(
                tempDir,
                packManagers,
                entries,
                jobNexClones
            );

            foreach (FF16PackManager manager in packManagers.Values)
                manager.Dispose();

            packManagers.Clear();

            var indexEntries = new List<object>();

            foreach (var group in
                     entries.Values.GroupBy(x =>
                         (x.Mode, x.PackName)))
            {
                string modeDir =
                    Path.Combine(dataDir, group.Key.Mode);

                if (!Directory.Exists(modeDir))
                {
                    Console.WriteLine(
                        $"[FFTModPacker] Ignorando {group.Key.Mode}: " +
                        "pasta data inexistente."
                    );
                    continue;
                }

                var builder = new FF16PackBuilder(
                    new PackBuildOptions
                    {
                        Name = string.Empty,
                        CodeName = PackKeyStore.FFT_IVALICE_CODENAME,
                    });

                int count = 0;

                foreach (PackEntry entry in group)
                {
                    builder.AddFile(entry.Source, entry.Target);

                    indexEntries.Add(new
                    {
                        entry.Target,
                        entry.ModId,
                        entry.Mode,
                        Pack = entry.PackName,
                    });

                    ++count;
                }

                string output =
                    Path.Combine(
                        modeDir,
                        group.Key.PackName + ".pac"
                    );

                Console.WriteLine(
                    $"[FFTModPacker] Escrevendo {output} ({count} arquivos)..."
                );

                await builder.WriteToAsync(output);
            }

            await EnsureBasePacksForLocales(dataDir);

            WriteIndex(gameDir, indexEntries);

            Console.WriteLine(
                $"[FFTModPacker] Concluido. Targets empacotados: {indexEntries.Count}"
            );

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "[FFTModPacker] ERRO: " + ex
            );
            return 1;
        }
        finally
        {
            foreach (FF16PackManager manager in packManagers.Values)
                manager.Dispose();
        }
    }

    private static uint? ParseJobId(JsonNode? node)
    {
        if (node is null)
            return null;

        try
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue<uint>(out uint unsigned))
                    return unsigned;

                if (value.TryGetValue<int>(out int signed) && signed >= 0)
                    return (uint)signed;

                if (value.TryGetValue<string>(out string? text) &&
                    !string.IsNullOrWhiteSpace(text))
                {
                    text = text.Trim().Replace("_", string.Empty);
                    int radix = 10;

                    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        text = text[2..];
                        radix = 16;
                    }

                    return Convert.ToUInt32(text, radix);
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static void ScanJobNexClones(
        ModInfo mod,
        List<JobNexClone> clones)
    {
        string path = Path.Combine(mod.Directory, "jobs.json");
        if (!File.Exists(path))
            return;

        JsonObject? root;

        try
        {
            root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[FFTModPacker:JobClone] JSON ignorado: {path}: {ex.Message}");
            return;
        }

        if (root?["Jobs"] is not JsonArray jobs)
            return;

        foreach (JsonNode? node in jobs)
        {
            if (node is not JsonObject job ||
                job["ClonePresentationFrom"] is null)
            {
                continue;
            }

            uint? target = ParseJobId(job["Id"]);
            uint? source = ParseJobId(job["ClonePresentationFrom"]);
            uint? overviewSource = ParseJobId(job["CloneOverviewFrom"]);

            if (target is null ||
                target < 0xA0 ||
                target > 0xAF ||
                source is null ||
                source > 175 ||
                source == target)
            {
                throw new InvalidDataException(
                    $"{mod.UniqueId}: ClonePresentationFrom aceita apenas " +
                    "destinos da expansao 0xA0..0xAF e fontes 0..175.");
            }

            if (job["CloneOverviewFrom"] is not null &&
                (overviewSource is null ||
                 overviewSource > 175 ||
                 overviewSource == target))
            {
                throw new InvalidDataException(
                    $"{mod.UniqueId}: CloneOverviewFrom aceita apenas " +
                    "fontes 0..175 diferentes do destino.");
            }

            clones.RemoveAll(x => x.TargetId == target.Value);
            clones.Add(new JobNexClone(
                mod.UniqueId,
                target.Value,
                source.Value,
                overviewSource));

            Console.WriteLine(
                $"[FFTModPacker:JobClone] {mod.UniqueId}: " +
                $"Job 0x{target:X2} <- 0x{source:X2}" +
                (overviewSource is null
                    ? string.Empty
                    : $"; Overview <- 0x{overviewSource:X2}"));
        }
    }

    private static void ApplyJobNexClones(
        string tempDir,
        IReadOnlyDictionary<string, FF16PackManager> packManagers,
        Dictionary<(string Mode, string Pack, string Target), PackEntry> entries,
        IReadOnlyList<JobNexClone> clones)
    {
        if (clones.Count == 0)
            return;

        string gameDir = Directory.GetParent(tempDir)!.FullName;
        Version gameVersion = DetectGameVersion(gameDir);
        const string mode = "enhanced";

        if (!packManagers.TryGetValue(mode, out FF16PackManager? manager))
        {
            throw new DirectoryNotFoundException(
                "data/enhanced nao esta disponivel para clonar Job/JobType.");
        }

        string[] targets =
        [
            "nxd/jobtype.nxd",
            .. Locales
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(locale => $"nxd/job.{locale.ToLowerInvariant()}.nxd")
        ];

        string referenceJobTarget = "nxd/job.en.nxd";
        var referenceJobKey = (mode, "modded.en", referenceJobTarget);
        byte[] referenceJobBytes = entries.TryGetValue(
            referenceJobKey, out PackEntry? stagedReferenceJob)
            ? File.ReadAllBytes(stagedReferenceJob.Source)
            : manager.GetFileDataBytes(referenceJobTarget);

        var unresolvedDirectives = clones.Select(x =>
            new JobNexCloneBuilder.Directive(
                x.ModId,
                x.TargetId,
                x.SourceId,
                x.OverviewSourceId,
                0)).ToArray();
        IReadOnlyDictionary<uint, uint> sourceJobTypes =
            JobNexCloneBuilder.ResolveSourceJobTypes(
                referenceJobBytes,
                gameVersion,
                unresolvedDirectives);
        var directives = clones.Select(x =>
            new JobNexCloneBuilder.Directive(
                x.ModId,
                x.TargetId,
                x.SourceId,
                x.OverviewSourceId,
                sourceJobTypes[x.TargetId])).ToArray();

        foreach (JobNexCloneBuilder.Directive directive in directives)
        {
            Console.WriteLine(
                $"[FFTModPacker:JobClone] Job 0x{directive.SourceId:X2} " +
                $"usa JobType 0x{directive.SourceJobTypeId:X2}; " +
                $"destino=0x{directive.TargetId:X2}.");
        }

        foreach (string target in targets)
        {
            string packName = DetectPackName(target);
            var key = (mode, packName, target);
            byte[] original;

            if (entries.TryGetValue(key, out PackEntry? staged))
                original = File.ReadAllBytes(staged.Source);
            else
                original = manager.GetFileDataBytes(target);

            string output = Path.Combine(
                tempDir,
                "generated-job-clones",
                mode,
                target.Replace('/', Path.DirectorySeparatorChar));

            JobNexCloneBuilder.Build(
                original,
                Path.GetFileName(target).Split('.')[0],
                gameVersion,
                directives,
                output);

            PutEntry(
                entries,
                new PackEntry(
                    target,
                    output,
                    clones[^1].ModId,
                    mode,
                    packName));
        }
    }

    private static void OpenBasePackManagers(
        string dataDir,
        Dictionary<string, FF16PackManager> managers
    )
    {
        foreach (string mode in new[] { "enhanced", "classic" })
        {
            string dir = Path.Combine(dataDir, mode);
            if (!Directory.Exists(dir))
                continue;

            var manager =
                new FF16PackManager(
                    NullLoggerFactory.Instance
                );

            manager.Open(
                dir,
                PackKeyStore.FFT_IVALICE_CODENAME
            );

            managers[mode] = manager;

            Console.WriteLine(
                $"[FFTModPacker] Packs originais abertos: {mode}"
            );
        }
    }

    private static Version DetectGameVersion(string gameDir)
    {
        try
        {
            string exe = Path.Combine(gameDir, "FFT_enhanced.exe");
            if (!File.Exists(exe))
                return new Version(1, 0, 0);

            FileVersionInfo info =
                FileVersionInfo.GetVersionInfo(exe);

            if (Version.TryParse(info.FileVersion, out Version? version))
                return version;
        }
        catch
        {
        }

        return new Version(1, 0, 0);
    }

    private static void MergeNexInputs(
        string tempDir,
        IReadOnlyDictionary<string, FF16PackManager> packManagers,
        Dictionary<(string Mode, string Target), List<NexInput>> nexInputs,
        Dictionary<(string Mode, string Pack, string Target), PackEntry> entries
    )
    {
        if (nexInputs.Count == 0)
            return;

        string gameDir = Directory.GetParent(tempDir)!.FullName;
        Version gameVersion = DetectGameVersion(gameDir);

        Console.WriteLine(
            $"[FFTModPacker:NEX] Mesclando {nexInputs.Count} tabela(s), " +
            $"layout version={gameVersion}."
        );

        foreach (var pair in nexInputs)
        {
            string mode = pair.Key.Mode;
            string target = pair.Key.Target;
            List<NexInput> inputs = pair.Value;

            if (!packManagers.TryGetValue(
                    mode,
                    out FF16PackManager? manager))
            {
                Console.WriteLine(
                    $"[FFTModPacker:NEX] Sem base pack para {mode}; " +
                    $"fallback last-wins: {target}"
                );

                NexInput last = inputs[^1];
                PutEntry(
                    entries,
                    new PackEntry(
                        target,
                        last.Source,
                        last.ModId,
                        mode,
                        last.PackName
                    )
                );
                continue;
            }

            try
            {
                byte[] original = manager.GetFileDataBytes(target);

                string tableName =
                    Path.GetFileNameWithoutExtension(target);

                string staging =
                    Path.Combine(
                        tempDir,
                        mode,
                        target.Replace('/', Path.DirectorySeparatorChar)
                    );

                NexMergeEngine.Merge(
                    original,
                    tableName,
                    gameVersion,
                    inputs
                        .Select(x =>
                            new NexMergeEngine.Input(
                                x.ModId,
                                x.Source))
                        .ToArray(),
                    staging
                );

                NexInput winner = inputs[^1];

                PutEntry(
                    entries,
                    new PackEntry(
                        target,
                        staging,
                        "FFTModLoader.NexMerge",
                        mode,
                        winner.PackName
                    )
                );

                Console.WriteLine(
                    $"[FFTModPacker:NEX] Merge concluido: {target} " +
                    $"({inputs.Count} mod(s))"
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[FFTModPacker:NEX] Falha no merge de {target}: {ex.Message}"
                );

                NexInput last = inputs[^1];
                PutEntry(
                    entries,
                    new PackEntry(
                        target,
                        last.Source,
                        last.ModId,
                        mode,
                        last.PackName
                    )
                );
            }
        }
    }

    private static List<ModInfo> LoadMods(string modsDir)
    {
        var candidates = new Dictionary<string, ModInfo>(
            StringComparer.OrdinalIgnoreCase);
        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string dir in Directory.EnumerateDirectories(modsDir))
        {
            string manifestPath = Path.Combine(dir, "manifest.json");
            string reloadedConfigPath = Path.Combine(dir, "ModConfig.json");
            bool reloadedFormat = !File.Exists(manifestPath) && File.Exists(reloadedConfigPath);
            if (!File.Exists(manifestPath) && !reloadedFormat)
                continue;

            try
            {
                string metadataPath = reloadedFormat ? reloadedConfigPath : manifestPath;
                JsonObject? json = JsonNode.Parse(File.ReadAllText(metadataPath)) as JsonObject;
                if (json is null || !(json["Enabled"]?.GetValue<bool>() ?? true))
                    continue;

                string name = reloadedFormat
                    ? json["ModName"]?.GetValue<string>() ?? Path.GetFileName(dir)
                    : json["Name"]?.GetValue<string>() ?? Path.GetFileName(dir);
                string uniqueId = reloadedFormat
                    ? json["ModId"]?.GetValue<string>() ?? string.Empty
                    : json["UniqueID"]?.GetValue<string>() ?? string.Empty;
                string version = reloadedFormat
                    ? json["ModVersion"]?.GetValue<string>() ?? string.Empty
                    : json["Version"]?.GetValue<string>() ?? string.Empty;
                int priority = json["Priority"]?.GetValue<int>() ?? (reloadedFormat ? 10000 : 0);
                if (string.IsNullOrWhiteSpace(uniqueId) || string.IsNullOrWhiteSpace(version))
                    throw new InvalidDataException("UniqueID/Version ausente");

                var dependencies = new List<ModDependency>();
                if (!reloadedFormat && json["Dependencies"] is not null && json["Dependencies"] is not JsonArray)
                    throw new InvalidDataException("Dependencies deve ser uma lista");
                if (!reloadedFormat && json["Dependencies"] is JsonArray array)
                {
                    foreach (JsonNode? node in array)
                    {
                        if (node is not JsonObject dep)
                            throw new InvalidDataException("Dependencies contem entrada invalida");
                        string id = dep["UniqueID"]?.GetValue<string>() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(id))
                            throw new InvalidDataException("Dependencies.UniqueID ausente");
                        dependencies.Add(new ModDependency(
                            id,
                            dep["MinimumVersion"]?.GetValue<string>(),
                            dep["Required"]?.GetValue<bool>() ?? true));
                    }
                }

                var mod = new ModInfo(dir, name, uniqueId, priority, version, dependencies);
                if (reloadedFormat)
                    Console.WriteLine($"[Mods] Formato Reloaded-II aceito localmente: {uniqueId} {version}");
                if (candidates.ContainsKey(uniqueId))
                    duplicates.Add(uniqueId);
                else
                    candidates.Add(uniqueId, mod);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Mods] Metadados ignorados em {dir}: {ex.Message}");
            }
        }

        foreach (string id in duplicates)
        {
            candidates.Remove(id);
            Console.Error.WriteLine($"[Mods] UniqueID duplicado, ambos ignorados: {id}");
        }

        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool changed;
        do
        {
            changed = false;
            foreach (ModInfo mod in candidates.Values)
            {
                if (rejected.Contains(mod.UniqueId))
                    continue;
                foreach (ModDependency dep in mod.Dependencies.Where(d => d.Required))
                {
                    if (!candidates.TryGetValue(dep.UniqueId, out ModInfo? provider) ||
                        rejected.Contains(dep.UniqueId) ||
                        !MeetsMinimum(provider.Version, dep.MinimumVersion))
                    {
                        rejected.Add(mod.UniqueId);
                        Console.Error.WriteLine($"[Mods] {mod.UniqueId} ignorado: dependencia {dep.UniqueId} ausente/incompativel");
                        changed = true;
                        break;
                    }
                }
            }
        } while (changed);

        var sorted = new List<ModInfo>();
        var remaining = candidates.Values.Where(m => !rejected.Contains(m.UniqueId))
            .ToDictionary(m => m.UniqueId, StringComparer.OrdinalIgnoreCase);
        while (remaining.Count > 0)
        {
            ModInfo? next = remaining.Values
                .Where(m => m.Dependencies.Where(d => d.Required)
                    .All(d => !remaining.ContainsKey(d.UniqueId)))
                .OrderBy(m => m.Priority)
                .ThenBy(m => m.UniqueId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (next is null)
            {
                foreach (ModInfo mod in remaining.Values)
                    Console.Error.WriteLine($"[Mods] {mod.UniqueId} ignorado: ciclo de dependencias ou dependente de ciclo");
                break;
            }
            sorted.Add(next);
            remaining.Remove(next.UniqueId);
        }
        return sorted;
    }

    private static string? GetOptionValue(string[] args, string option)
    {
        for (int i = 0; i + 1 < args.Length; ++i)
        {
            if (args[i].Equals(option, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static HashSet<string>? ParseIdFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries |
                                StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static List<ModInfo> LoadReloadedMods(
        string reloadedRoot,
        IReadOnlySet<string>? allowedIds)
    {
        string appConfig = Path.Combine(
            reloadedRoot, "Apps", "fft_enhanced.exe", "AppConfig.json");
        string modsRoot = Path.Combine(reloadedRoot, "Mods");
        if (!File.Exists(appConfig) || !Directory.Exists(modsRoot))
        {
            Console.Error.WriteLine(
                $"[ReloadedCompat] Instalacao invalida: {reloadedRoot}"
            );
            return [];
        }

        JsonObject? app = JsonNode.Parse(File.ReadAllText(appConfig)) as JsonObject;
        var enabled = (app?["EnabledMods"] as JsonArray)?
            .Select(x => x?.GetValue<string>() ?? string.Empty)
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ??
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sorted = (app?["SortedMods"] as JsonArray)?
            .Select(x => x?.GetValue<string>() ?? string.Empty)
            .Where(x => x.Length > 0)
            .ToList() ?? [];

        var result = new List<ModInfo>();
        int priority = 10000;
        foreach (string modId in sorted.Where(enabled.Contains))
        {
            if (allowedIds is not null && !allowedIds.Contains(modId))
                continue;
            string directory = Path.Combine(modsRoot, modId);
            string configPath = Path.Combine(directory, "ModConfig.json");
            if (!File.Exists(configPath))
                continue;

            try
            {
                JsonObject? config = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
                if (config is null)
                    continue;
                string id = config["ModId"]?.GetValue<string>() ?? modId;
                string name = config["ModName"]?.GetValue<string>() ?? id;
                string version = config["ModVersion"]?.GetValue<string>() ?? "0.0.0";
                string managedDll = config["ModDll"]?.GetValue<string>() ?? string.Empty;
                string nativeDll = config["ModNativeDll64"]?.GetValue<string>() ?? string.Empty;
                bool hasData = Directory.Exists(Path.Combine(directory, "FFTIVC", "data"));
                bool hasTables = Directory.Exists(Path.Combine(directory, "FFTIVC", "tables"));
                bool hasCode = managedDll.Length > 0 || nativeDll.Length > 0;

                if (!hasData)
                {
                    if (hasCode)
                        Console.Error.WriteLine(
                            $"[ReloadedCompat] {id}: somente codigo Reloaded; requer adaptador nativo e nao foi importado."
                        );
                    continue;
                }

                if (hasCode)
                    Console.Error.WriteLine(
                        $"[ReloadedCompat] {id}: arquivos FFTIVC importados; componente DLL ainda nao e executado."
                    );
                if (hasTables)
                    Console.WriteLine(
                        $"[ReloadedCompat] {id}: tabelas XML detectadas; NXDs publicados pelo mod terao prioridade."
                    );

                result.Add(new ModInfo(
                    directory, name, id, priority++, version, [], false));
                Console.WriteLine($"[ReloadedCompat] Ativo: {id} {version}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[ReloadedCompat] ModConfig invalido em {configPath}: {ex.Message}"
                );
            }
        }

        Console.WriteLine(
            $"[ReloadedCompat] {result.Count} mods com conteudo FFTIVC importaveis."
        );
        return result;
    }

    private static bool MeetsMinimum(string actual, string? minimum)
    {
        if (string.IsNullOrWhiteSpace(minimum))
            return true;
        return Version.TryParse(actual, out Version? a) &&
               Version.TryParse(minimum, out Version? b) &&
               a.CompareTo(b) >= 0;
    }

    private static void WriteModPlan(string path, List<ModInfo> mods)
    {
        var plan = new
        {
            SchemaVersion = 1,
            Mods = mods.Where(m => m.RuntimeVisible).Select(m => new
            {
                Directory = Path.GetFileName(m.Directory),
                m.UniqueId,
                m.Version,
                m.Priority
            }).ToArray()
        };
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(plan));
        File.Move(temp, path, overwrite: true);
    }

    private static void ScanContentJson(
        ModInfo mod,
        Dictionary<(string Mode, string Pack, string Target), PackEntry> entries,
        Dictionary<(string Mode, string Target), List<NexInput>> nexInputs
    )
    {
        string path = Path.Combine(mod.Directory, "content.json");
        if (!File.Exists(path))
            return;

        JsonObject? json;

        try
        {
            json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch
        {
            return;
        }

        if (json?["Changes"] is not JsonArray changes)
            return;

        foreach (JsonNode? node in changes)
        {
            if (node is not JsonObject change)
                continue;

            string action =
                change["Action"]?.GetValue<string>() ?? string.Empty;

            if (!action.Equals("Replace", StringComparison.OrdinalIgnoreCase) &&
                !action.Equals("Load", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string target = Normalize(
                change["Target"]?.GetValue<string>() ?? string.Empty);

            string fromFile =
                change["FromFile"]?.GetValue<string>() ?? string.Empty;

            if (target.Length == 0 || fromFile.Length == 0)
                continue;

            if (IsSpecialContainerTarget(target))
            {
                Console.WriteLine(
                    $"[FFTModPacker] Direct hook: {target}"
                );
                continue;
            }

            string source = Path.GetFullPath(
                Path.Combine(mod.Directory, fromFile));

            string modRoot =
                Path.GetFullPath(mod.Directory)
                    .TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;

            if (!source.StartsWith(
                    modRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(source))
            {
                continue;
            }

            string requestedMode =
                (change["GameMode"]?.GetValue<string>() ?? "enhanced")
                    .ToLowerInvariant();

            if (requestedMode == "combined")
            {
                AddSource(mod, "enhanced", target, source, entries, nexInputs);
                AddSource(mod, "classic", target, source, entries, nexInputs);
            }
            else
            {
                AddSource(
                    mod,
                    requestedMode == "classic" ? "classic" : "enhanced",
                    target,
                    source,
                    entries,
                    nexInputs
                );
            }
        }
    }

    private static void ScanLegacyData(
        ModInfo mod,
        Dictionary<(string Mode, string Pack, string Target), PackEntry> entries,
        Dictionary<(string Mode, string Target), List<NexInput>> nexInputs
    )
    {
        string[] roots =
        [
            Path.Combine(mod.Directory, "data"),
            Path.Combine(mod.Directory, "FFTIVC", "data")
        ];

        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
                continue;

            foreach (string declaredMode in
                     new[] { "enhanced", "classic", "combined" })
            {
                string modeDir = Path.Combine(root, declaredMode);
                if (!Directory.Exists(modeDir))
                    continue;

                foreach (string file in Directory.EnumerateFiles(
                             modeDir,
                             "*",
                             SearchOption.AllDirectories))
                {
                    string target = Normalize(
                        Path.GetRelativePath(modeDir, file));

                    if (target.EndsWith(
                            ".path",
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (IsSpecialContainerTarget(target))
                        continue;

                    if (declaredMode == "combined")
                    {
                        AddSource(mod, "enhanced", target, file, entries, nexInputs);
                        AddSource(mod, "classic", target, file, entries, nexInputs);
                    }
                    else
                    {
                        AddSource(mod, declaredMode, target, file, entries, nexInputs);
                    }
                }
            }
        }
    }

    private static void AddSource(
        ModInfo mod,
        string mode,
        string target,
        string source,
        Dictionary<(string Mode, string Pack, string Target), PackEntry> entries,
        Dictionary<(string Mode, string Target), List<NexInput>> nexInputs
    )
    {
        string packName = DetectPackName(target);

        if (IsNexFile(target, source))
        {
            var key = (mode, target);

            if (!nexInputs.TryGetValue(key, out List<NexInput>? list))
            {
                list = [];
                nexInputs.Add(key, list);
            }

            list.Add(new NexInput(
                target,
                source,
                mod.UniqueId,
                mode,
                packName
            ));

            Console.WriteLine(
                $"[FFTModPacker:NEX] Registrado {target} <- {mod.UniqueId}"
            );
            return;
        }

        PutEntry(
            entries,
            new PackEntry(
                target,
                source,
                mod.UniqueId,
                mode,
                packName
            )
        );
    }

    private static void PutEntry(
        Dictionary<(string Mode, string Pack, string Target), PackEntry> entries,
        PackEntry entry
    )
    {
        var key = (entry.Mode, entry.PackName, entry.Target);

        if (entries.TryGetValue(key, out PackEntry? previous))
        {
            Console.WriteLine(
                $"[FFTModPacker] Conflito: {entry.Target} | " +
                $"{previous.ModId} -> {entry.ModId}"
            );
        }

        entries[key] = entry;
    }

    private static bool IsNexFile(string target, string source)
    {
        if (target.EndsWith(
                ".nxd",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var stream = File.OpenRead(source);
            Span<byte> magic = stackalloc byte[4];

            if (stream.Read(magic) != 4)
                return false;

            return magic.SequenceEqual("NXDF"u8);
        }
        catch
        {
            return false;
        }
    }

    private static string DetectPackName(string target)
    {
        string fileName = Path.GetFileName(target);
        string[] parts = fileName.Split('.');

        if (parts.Length > 2)
        {
            string locale = parts[^2];
            if (Locales.Contains(locale))
                return "modded." + locale.ToLowerInvariant();
        }

        return "modded";
    }

    private static bool IsSpecialContainerTarget(string target)
    {
        return
            target.StartsWith(
                "fftpack/",
                StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(
                "system/ffto/g2d/",
                StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        return path
            .Replace('\\', '/')
            .TrimStart('/')
            .ToLowerInvariant();
    }

    private static void DeleteOldGeneratedPacks(string dataDir)
    {
        foreach (string mode in new[] { "enhanced", "classic" })
        {
            string dir = Path.Combine(dataDir, mode);
            if (!Directory.Exists(dir))
                continue;

            foreach (string file in Directory.EnumerateFiles(
                         dir,
                         "modded*.pac"))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    throw new IOException(
                        $"Nao foi possivel substituir {file}. " +
                        "Feche outra instancia do jogo."
                    );
                }
            }
        }
    }

    private static async Task EnsureBasePacksForLocales(string dataDir)
    {
        foreach (string mode in new[] { "enhanced", "classic" })
        {
            string modeDir = Path.Combine(dataDir, mode);
            if (!Directory.Exists(modeDir))
                continue;

            bool localePackExists =
                Directory.EnumerateFiles(
                    modeDir,
                    "modded.*.pac").Any();

            string basePack = Path.Combine(modeDir, "modded.pac");

            if (!localePackExists || File.Exists(basePack))
                continue;

            var empty = new FF16PackBuilder(
                new PackBuildOptions
                {
                    Name = string.Empty,
                    CodeName = PackKeyStore.FFT_IVALICE_CODENAME,
                });

            Console.WriteLine(
                $"[FFTModPacker] Criando pack base vazio: {basePack}"
            );

            await empty.WriteToAsync(basePack);
        }
    }

    private static void WriteIndex(
        string gameDir,
        IEnumerable<object> entries
    )
    {
        string path = Path.Combine(
            gameDir,
            "FFTModLoader.pack-index.json");

        var root = new
        {
            Version = 2,
            GeneratedAtUtc = DateTime.UtcNow,
            Entries = entries.ToArray(),
        };

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                root,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                }
            )
        );
    }
}
