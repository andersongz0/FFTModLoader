using System.Diagnostics;
using System.Runtime.InteropServices;
using FFTModLoader.ContentExpansion.Api;
using FFTModLoader.ContentExpansion;
using FFTModLoader.ContentExpansion.Runtime;
using Reloaded.Mod.Interfaces;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace FFTModLoader.ContentExpansion.Module;

public sealed class ContentExpansionService(
    IReloadedHooks? hooks, string frameworkDirectory, ILogger logger, IExpandedJobVisuals? visuals=null) : IContentExpansion, IExpandedContentRegistry, IBestialWarriorExpansion
{
    private readonly object _gate = new();
    private readonly object _logGate = new();
    private string? _registeredDirectory;
    private ChemistPlayableHost? _host;
    private BestialPlayableHost? _bestial;
    private string? _bestialDirectory;
    private NativeContextBridge? _bridge;
    private LoaderNativeRoutes? _routes;
    private StreamWriter? _file;
    private readonly ExpandedContentCatalog _catalog = new();
    public int ContractVersion => 1;
    public int RegistryVersion => 1;
    public int BestialContractVersion => 1;
    public void ActivateBestialWarrior(string contentDirectory)
    {
        lock(_gate)
        {
            string directory=Path.GetFullPath(contentDirectory);
            if(_bestialDirectory is not null)
            {
                if(!string.Equals(directory,_bestialDirectory,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Different Bestial Warrior directory already registered.");
                return;
            }
            if(hooks is null || visuals is null)throw new InvalidOperationException("Expanded jobs/visuals unavailable.");
            RegisterContent(directory);
            var module=Process.GetCurrentProcess().MainModule ?? throw new InvalidOperationException("Game module missing.");
            if(!module.ModuleName.Equals("FFT_enhanced.exe",StringComparison.OrdinalIgnoreCase))throw new NotSupportedException("Enhanced game only.");
            _bestialDirectory=directory;
            _bridge ??= new NativeContextBridge(Path.Combine(frameworkDirectory,"FFTModLoader.ContentExpansion.Native.dll"));
            _routes ??= new LoaderNativeRoutes(hooks,_bridge,(long)module.BaseAddress,module.FileName,visuals);
            _bestial=new((long)module.BaseAddress,_bridge,_routes,visuals,line=>
                { try{logger.WriteLine("[BestialWarrior] "+line);}catch{} });
            _bestial.Install(module.FileName,directory,Path.Combine(frameworkDirectory,"playable-relocation-plan.json"),Profile);
        }
    }
    public void RegisterContent(string contentDirectory)
    {
        string directory = Path.GetFullPath(contentDirectory);
        string path = Path.Combine(directory, "FFTModLoader.content.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 65536)
            throw new InvalidDataException("Expanded content registration is missing or too large.");
        var definition = ExpandedContentCatalog.Read(File.ReadAllText(path));
        using var config = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"ModConfig.json")));
        if (config.RootElement.GetProperty("ModId").GetString() != definition.ModId)
            throw new InvalidDataException("Content ownership does not match its mod identity.");
        _catalog.Register(definition);
    }
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
            _bridge ??= new NativeContextBridge(Path.Combine(frameworkDirectory, "FFTModLoader.ContentExpansion.Native.dll"));
            _bridge.EnableDiagnosticCapture(Path.Combine(logs, $"Chemist-FirstFault-{stamp}"));
            _routes ??= new LoaderNativeRoutes(hooks,_bridge,(long)module.BaseAddress,module.FileName,visuals);
            _host = new ChemistPlayableHost(hooks, (long)module.BaseAddress, _bridge, Log,_routes);
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
    private static readonly Lazy<string> ProfileKey=new(ResolveProfile,System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
    private static string Profile()=>ProfileKey.Value;
    private static string ResolveProfile()
    {
        ulong? account=AvailableSteamProfile();
        if(account is >0)return SaveProfileIdentity.Resolve(account,null);
        using var identity=System.Security.Principal.WindowsIdentity.GetCurrent();
        return SaveProfileIdentity.Resolve(null,identity.User?.Value);
    }
    private static ulong? AvailableSteamProfile()
    {
        nint library = GetModuleHandle("steam_api64.dll");
        if (library == 0 || !NativeLibrary.TryGetExport(library,"SteamAPI_SteamUser_v023",out nint userExport) ||
            !NativeLibrary.TryGetExport(library,"SteamAPI_ISteamUser_GetSteamID",out nint idExport))return null;
        var get = Marshal.GetDelegateForFunctionPointer<SteamUser>(userExport);
        nint user = get();
        if (user == 0)return null;
        ulong id = Marshal.GetDelegateForFunctionPointer<SteamId>(idExport)(user);
        return id==0?null:id;
    }
}
