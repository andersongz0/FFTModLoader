using GenericJobs;

static void Equal(ushort[] expected, ushort[] actual, string label)
{
    if (!expected.SequenceEqual(actual)) throw new Exception(label);
    Console.WriteLine("PASS: " + label);
}

ushort[] knights = [0xA0, 0xA1, 0xA2, 0xA3];
Equal([], JobActivationPolicy.SelectJobs([], "A0, A1, A2, A3"), "loader-only ignores legacy phantom slots");
Equal([], JobActivationPolicy.SelectJobs([], "bad-data"), "invalid settings cannot create jobs");
Equal(knights, JobActivationPolicy.SelectJobs(knights, ""), "installed consumer registers its jobs");
Equal(knights, JobActivationPolicy.SelectJobs(knights, "A0, A1, A2, A3"), "legacy settings remain compatible with Knights");
Equal([0xA2], JobActivationPolicy.SelectJobs(knights, "0xA2, A2, FF"), "unregistered jobs ignored and duplicates removed");
Equal(knights, JobActivationPolicy.SelectJobs(knights, "not-a-job"), "invalid settings fall back only to registered jobs");
Equal([], JobActivationPolicy.SelectJobs([], "160,161,162,163"), "consumer removal disables previously selected jobs");

// Isolated temporary fixture owned solely by this test.
string fixture = Path.Combine(Path.GetTempPath(), "FFT-JobActivationTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
try
{
    Equal([], JobActivationPolicy.ReadRegisteredJobs([fixture]), "no registration means no jobs");
    string manifest = Path.Combine(fixture, "FFTModLoader.jobs.json");
    File.WriteAllText(manifest, "{\"SchemaVersion\":1,\"JobIds\":[160,161,162,163,162],\"RequiresGenericJobsContent\":true}");
    Equal(knights, JobActivationPolicy.ReadRegisteredJobs([fixture]), "manifest discovery and deduplication");
    foreach (string invalid in new[] { "{\"SchemaVersion\":2,\"JobIds\":[162]}", "{\"SchemaVersion\":1,\"JobIds\":[256]}", "{\"SchemaVersion\":1,\"JobIds\":null}" })
    {
        File.WriteAllText(manifest, invalid);
        bool rejected = false;
        try { JobActivationPolicy.ReadRegisteredJobs([fixture]); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new Exception("Invalid registration accepted.");
    }
    Console.WriteLine("PASS: malformed, unsupported and out-of-range registrations rejected");
}
finally
{
    File.Delete(Path.Combine(fixture, "FFTModLoader.jobs.json"));
    Directory.Delete(fixture);
}
