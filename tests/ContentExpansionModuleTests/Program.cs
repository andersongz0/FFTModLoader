using System.Reflection;
using FFTModLoader.ContentExpansion.Api;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;
using Provider = FFTModLoader.ContentExpansion.Module.Startup;
using Consumer = ReworkedChemist.VenomTest.Startup;

static class Program
{
    static void Check(bool yes, string name) { if (!yes) throw new Exception(name); Console.WriteLine("PASS: " + name); }
    static void Main()
    {
        var api = DispatchProxy.Create<IModLoader, LoaderProxy>();
        var state = (LoaderProxy)(object)api;
        var config = DispatchProxy.Create<IModConfigV1, ConfigProxy>();
        var provider = new Provider();
        provider.StartEx(api, config);
        Check(provider.GetTypes().SequenceEqual(new[] { typeof(IContentExpansion) }), "API exported for shared mod contexts");
        Check(state.Controllers[typeof(IContentExpansion)] is IContentExpansion { ContractVersion: 1 }, "loader registers service without activating gameplay");
        Check(!provider.CanUnload() && !provider.CanSuspend(), "native service lifetime retained");
        var service = (IContentExpansion)state.Controllers[typeof(IContentExpansion)];
        try { service.ActivateReworkedChemist(Path.Combine(Path.GetTempPath(), "nonexistent-chemist-fixture")); throw new Exception("Incomplete content accepted"); }
        catch (InvalidDataException) { Check(true, "incomplete content rejected before native access"); }
        var capture = new Capture(); state.Controllers[typeof(IContentExpansion)] = capture;
        var consumer = new Consumer(); consumer.StartEx(api, config);
        Check(capture.Calls == 0, "consumer waits for loader initialization");
        state.Initialized?.Invoke(); state.Initialized?.Invoke();
        Check(capture.Calls == 1 && capture.Directory == state.Directory, "consumer activates exactly once with its own data directory");
        state.Controllers.Clear(); state.Initialized = null; state.Messages.Clear();
        new Consumer().StartEx(api, config); state.Initialized?.Invoke();
        Check(state.Messages.Any(x => x.Contains("ContentExpansion is missing")), "missing framework reports error instead of installing local fallback hooks");
        state.Controllers[typeof(IContentExpansion)] = new Capture { ContractVersion = 999 }; state.Initialized = null; state.Messages.Clear();
        new Consumer().StartEx(api, config); state.Initialized?.Invoke();
        Check(state.Messages.Any(x => x.Contains("Unsupported ContentExpansion")), "unsupported API version rejected");
    }
}
public class Capture : IContentExpansion
{
    public int ContractVersion { get; set; } = 1;
    public int Calls; public string? Directory;
    public void ActivateReworkedChemist(string directory) { Calls++; Directory = directory; }
}
public class ConfigProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == "get_ModId" ? "fixture-mod" : null;
}
public class LoggerProxy : DispatchProxy
{
    public List<string> Messages = [];
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == "WriteLine" && args?.FirstOrDefault() is string s) Messages.Add(s);
        return method?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
    }
}
public class LoaderProxy : DispatchProxy
{
    public Dictionary<Type, object> Controllers = [];
    public Action? Initialized;
    public string Directory = Path.Combine(Path.GetTempPath(), "chemist-content-fixture");
    public List<string> Messages;
    private readonly ILogger _logger;
    public LoaderProxy()
    {
        _logger = DispatchProxy.Create<ILogger, LoggerProxy>();
        Messages = ((LoggerProxy)(object)_logger).Messages;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "GetDirectoryForModId": return Directory;
            case "GetLogger": return _logger;
            case "get_OnModLoaderInitialized": return Initialized;
            case "set_OnModLoaderInitialized": Initialized = (Action?)args![0]; return null;
            case "AddOrReplaceController": Controllers[method.GetGenericArguments()[0]] = args![1]!; return null;
            case "GetController":
                var type = method.GetGenericArguments()[0];
                return Controllers.TryGetValue(type, out var value) ? Activator.CreateInstance(typeof(WeakReference<>).MakeGenericType(type), value) : null;
            default: throw new NotSupportedException(method.Name);
        }
    }
}
