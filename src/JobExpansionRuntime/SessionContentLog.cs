using System.Text;

namespace GenericJobs;

public partial class Mod
{
    private StreamWriter? _sessionContent;
    private readonly object _sessionContentLock = new();

    private void StartSessionContentLog()
    {
        string? path = Environment.GetEnvironmentVariable("FFTMODLOADER_SESSION_LOG");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            _sessionContent = new StreamWriter(file, new UTF8Encoding(false)) { AutoFlush = true };
            _logger.OnWriteLine += (_, message) =>
            {
                if (!ContentMessage(message.text)) return;
                lock (_sessionContentLock)
                {
                    try { _sessionContent.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message.text}"); }
                    catch (IOException) { } // Reporting failure must not crash the game.
                }
            };
            _sessionContent.WriteLine("[RuntimeSession] Live content channel ready.");
        }
        catch (Exception ex)
        {
            _logger.WriteLine($"[{_modConfig.ModId}] [Error] Live content channel unavailable: {ex.Message}");
        }
    }

    private static bool ContentMessage(string text) => text.Contains("[VisualExpansion]") || text.Contains("[ExpansionConfig]") ||
        text.Contains("[NxdExpansion]") || text.Contains("nex table") || text.Contains("Applying ") ||
        text.Contains("Adding file '") || text.Contains("modded") ||
        text.Contains("[Error]") || text.Contains("[Warning]") || text.Contains("[ERROR]");
}
