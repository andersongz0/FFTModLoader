using System.Diagnostics;
using System.Runtime.InteropServices;
using FFTModLoader.ContentExpansion.Api;
using FFTModLoader.ContentExpansion.Runtime;
using Reloaded.Mod.Interfaces;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace FFTModLoader.ContentExpansion.Module;

public sealed class ContentExpansionService(
    IReloadedHooks? hooks, string frameworkDirectory, ILogger logger) : IContentExpansion
{
    private readonly object _gate = new();
    private readonly object _logGate = new();
    private string? _registeredDirectory;
    private ChemistPlayableHost? _host;
    private StreamWriter? _file;
    public int ContractVersion => 1;
    public void ActivateReworkedChemist(string contentDirectory)
    {
        // Retain the host for process lifetime and never double-install native hooks,
        // including after a partially failed activation.
        lock (_gate)
        {
            string directory = Path.GetFullPath(contentDirectory);
            if (_registeredDirectory is not null)
            {
                if (!string.Equals(directory, _registeredDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A different Reworked Chemist content directory is already registered.");
                return;
            }
            if (!File.Exists(Path.Combine(directory, "native", "playable-relocation-plan.json")))
                throw new InvalidDataException("Reworked Chemist content package is incomplete.");
            var module = Process.GetCurrentProcess().MainModule ?? throw new InvalidOperationException("Game module missing.");
            if (!module.ModuleName.Equals("FFT_enhanced.exe", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Enhanced game only.");
            if (hooks is null) throw new InvalidOperationException("Native hook dependency unavailable.");
            _registeredDirectory = directory;
            string logs = Path.Combine(Path.GetDirectoryName(module.FileName)!, "FFTModLoader.Runtime", "Logs");
            Directory.CreateDirectory(logs);
            string stamp = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}";
            _file = new StreamWriter(new FileStream(Path.Combine(logs, $"ReworkedChemist-{stamp}.txt"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            var bridge = new NativeContextBridge(Path.Combine(frameworkDirectory, "FFTModLoader.ContentExpansion.Native.dll"));
            bridge.EnableDiagnosticCapture(Path.Combine(logs, $"Chemist-FirstFault-{stamp}"));
            _host = new ChemistPlayableHost(hooks, (long)module.BaseAddress, bridge, Log);
            _host.Install(module.FileName, directory, Profile);
            Log("[Runtime] Reworked Chemist activated through loader-owned ContentExpansion. Native interception and existing save keys preserved.");
            Log("[Save] Extras remain in FFTModLoader.Runtime/ExpandedSaves/ReworkedChemist. Preserve this directory when copying saves.");
        }
    }
    private void Log(string line)
    {
        lock (_logGate)
        {
            string message = "[ReworkedChemist] " + line;
            try { _file?.WriteLine($"{DateTime.UtcNow:O} {message}"); } catch { }
            string channel = line.StartsWith("[Erro]", StringComparison.Ordinal) ? "[Error] " : "[RuntimeSession] modded content: ";
            try { logger.WriteLine(channel + message); } catch { }
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint SteamUser();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong SteamId(nint self);
    private static string Profile()
    {
        nint library = GetModuleHandle("steam_api64.dll");
        if (library == 0) throw new InvalidDataException("Steam profile library is not initialized.");
        var get = Marshal.GetDelegateForFunctionPointer<SteamUser>(NativeLibrary.GetExport(library, "SteamAPI_SteamUser_v023"));
        nint user = get();
        if (user == 0) throw new InvalidDataException("Steam user is not initialized at the save boundary.");
        ulong id = Marshal.GetDelegateForFunctionPointer<SteamId>(NativeLibrary.GetExport(library, "SteamAPI_ISteamUser_GetSteamID"))(user);
        if (id == 0) throw new InvalidDataException("Native Steam profile is unavailable.");
        return ExpandedSaveRegistry.Hash(BitConverter.GetBytes(id)); // never log account identity
    }
}
