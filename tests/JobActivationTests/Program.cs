using GenericJobs;

static void Equal(ushort[] expected, ushort[] actual, string label)
{
    if (!expected.SequenceEqual(actual)) throw new Exception(label);
    Console.WriteLine("PASS: " + label);
}

ushort[] knights = [0xA0, 0xA1, 0xA2, 0xA3];
ushort[] builtIn = [0xA0, 0xA1];
Equal(builtIn, JobActivationPolicy.SelectJobs(builtIn, ""), "loader-only retains built-in Dark Knight and Onion Knight");
Equal(builtIn, JobActivationPolicy.SelectJobs(builtIn, "A0, A1, A2, A3"), "legacy four-slot settings cannot create phantom Holy/Rune jobs");
Equal([], JobActivationPolicy.SelectJobs([], "bad-data"), "invalid settings cannot create jobs");
Equal(knights, JobActivationPolicy.SelectJobs(knights, ""), "installed consumer registers its jobs");
Equal(knights, JobActivationPolicy.SelectJobs(knights, "A0, A1, A2, A3"), "legacy settings remain compatible with Knights");
Equal([0xA2], JobActivationPolicy.SelectJobs(knights, "0xA2, A2, FF"), "unregistered jobs ignored and duplicates removed");
Equal(knights, JobActivationPolicy.SelectJobs(knights, "not-a-job"), "invalid settings fall back only to registered jobs");
Equal(builtIn, JobActivationPolicy.SelectJobs(builtIn, "160,161,162,163"), "Knights removal retains Dark/Onion and disables Holy/Rune");

// Isolated temporary fixture owned solely by this test.
string fixture = Path.Combine(Path.GetTempPath(), "FFT-JobActivationTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
try
{
    Equal([], JobActivationPolicy.ReadRegisteredJobs([fixture]), "no registration means no jobs");
    string manifest = Path.Combine(fixture, "FFTModLoader.jobs.json");
    File.WriteAllText(manifest, "{\"SchemaVersion\":1,\"JobIds\":[160,161,162,163,162],\"RequiresGenericJobsContent\":true}");
    Equal(knights, JobActivationPolicy.ReadRegisteredJobs([fixture]), "manifest discovery and deduplication");
    string builtInDirectory = Path.Combine(fixture, "BuiltIn");
    Directory.CreateDirectory(builtInDirectory);
    File.WriteAllText(Path.Combine(builtInDirectory, "FFTModLoader.jobs.json"), "{\"SchemaVersion\":1,\"JobIds\":[160,161]}");
    File.WriteAllText(manifest, "{\"SchemaVersion\":1,\"JobIds\":[162,163]}");
    Equal(knights, JobActivationPolicy.ReadRegisteredJobs([builtInDirectory, fixture]), "base and Knights register distinct classes in menu order");
    Equal(builtIn, JobActivationPolicy.ReadRegisteredJobs([builtInDirectory]), "removing Knights preserves built-in registration");
    foreach (string invalid in new[] { "{\"SchemaVersion\":2,\"JobIds\":[162]}", "{\"SchemaVersion\":1,\"JobIds\":[256]}", "{\"SchemaVersion\":1,\"JobIds\":null}" })
    {
        File.WriteAllText(manifest, invalid);
        bool rejected = false;
        try { JobActivationPolicy.ReadRegisteredJobs([fixture]); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new Exception("Invalid registration accepted.");
    }
    Console.WriteLine("PASS: malformed, unsupported and out-of-range registrations rejected");
    File.WriteAllText(manifest, "{\"SchemaVersion\":1,\"JobIds\":[162,163],\"SwordLimitCommandIds\":[163]}");
    byte[] swordCommands = JobActivationPolicy.ReadSwordLimitCommands([builtInDirectory, fixture]);
    if (swordCommands.Length != 256 || swordCommands[163] != 1 || swordCommands.Sum(x => (int)x) != 1)
        throw new Exception("Sword Limit must register only Rune Knight, never Cloud or Holy Knight.");
    if (JobActivationPolicy.ReadSwordLimitCommands([builtInDirectory]).Any(x => x != 0))
        throw new Exception("Loader alone must not change Limit equipment.");
    foreach (string invalid in new[] {
        "{\"SchemaVersion\":1,\"JobIds\":[163],\"SwordLimitCommandIds\":[41]}",
        "{\"SchemaVersion\":1,\"JobIds\":[162],\"SwordLimitCommandIds\":[163]}",
        "{\"SchemaVersion\":1,\"JobIds\":null,\"SwordLimitCommandIds\":[163]}",
        "{\"SchemaVersion\":1,\"JobIds\":[163],\"SwordLimitCommandIds\":null}" })
    {
        File.WriteAllText(manifest, invalid);
        bool rejected = false;
        try { JobActivationPolicy.ReadSwordLimitCommands([fixture]); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new Exception("Invalid Sword Limit registration accepted.");
    }
    Console.WriteLine("PASS: Sword Limit opt-in is Rune-only; absent, removed, original Cloud and invalid registrations protected");
}
finally
{
    File.Delete(Path.Combine(fixture, "FFTModLoader.jobs.json"));
    File.Delete(Path.Combine(fixture, "BuiltIn", "FFTModLoader.jobs.json"));
    if (Directory.Exists(Path.Combine(fixture, "BuiltIn"))) Directory.Delete(Path.Combine(fixture, "BuiltIn"));
    Directory.Delete(fixture);
}
