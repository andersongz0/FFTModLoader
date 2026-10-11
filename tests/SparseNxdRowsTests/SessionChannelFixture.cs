namespace GenericJobs;

// Exercise the production channel source without constructing an injected Mod.
// Its event shape is also checked against the real ILogger by the runtime build.
public partial class Mod
{
    private readonly EventLogger _logger = new();
    private readonly TestConfig _modConfig = new();

    public static void TestSessionChannel()
    {
        string path = Path.Combine(Path.GetTempPath(), "fft-content-channel-" + Guid.NewGuid() + ".txt");
        string? previous = Environment.GetEnvironmentVariable("FFTMODLOADER_SESSION_LOG");
        Mod instance = new();
        try
        {
            Environment.SetEnvironmentVariable("FFTMODLOADER_SESSION_LOG", path);
            instance.StartSessionContentLog();
            instance._logger.WriteLine("[VisualExpansion] SPR file=28842 served: verificação");
            instance._logger.WriteLine("unrelated scanner noise");
            instance._logger.WriteLine("[ExpansionConfig] Validated capacities: menu=21, sprites=70");
            instance._logger.WriteLine("[NxdExpansion] Sparse table resolved by actual keys: rows=167, requested=170, found=True");
            instance._logger.WriteLine("[BestialWarrior] [Runtime] installed");
            instance._logger.WriteLine("[BestialWarrior] [Form] Wolf active");
            instance._logger.WriteLine("[RuntimeSession] [ReworkedChemist] [Runtime] activated");
            instance._logger.WriteLine("Registered Sword Limit");
            using var file = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            using var reader = new StreamReader(file);
            string text = reader.ReadToEnd();
            if (!text.Contains("Live content channel ready.") || !text.Contains("verificação") ||
                !text.Contains("requested=170") || !text.Contains("menu=21, sprites=70") || text.Contains("scanner noise") ||
                !text.Contains("[BestialWarrior] [Runtime]") || !text.Contains("Wolf active") ||
                !text.Contains("[ReworkedChemist] [Runtime]") || !text.Contains("Registered Sword Limit"))
                throw new Exception("Live session channel failed");
            Console.WriteLine("PASS: production session-channel source, logger events, UTF-8, filtering and immediate shared reads while writer remains open.");
        }
        finally
        {
            instance._sessionContent?.Dispose();
            Environment.SetEnvironmentVariable("FFTMODLOADER_SESSION_LOG", previous);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class TestConfig { public string ModId => "fftmodloader.jobexpansion"; }
    private sealed class EventLogger
    {
        public event EventHandler<(string text, int color)>? OnWriteLine;
        public void WriteLine(string text) => OnWriteLine?.Invoke(this,(text,0));
    }
}
