using FFTModLoader.ContentExpansion.Api;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace FFTModLoader.ContentExpansion.Module;

public sealed class Startup : IMod, IExports
{
    private ContentExpansionService? _service;
    public void StartEx(IModLoaderV1 api, IModConfigV1 config)
    {
        var loader = (IModLoader)api;
        IReloadedHooks? hooks = null;
        loader.GetController<IReloadedHooks>()?.TryGetTarget(out hooks);
        _service = new ContentExpansionService(hooks,
            loader.GetDirectoryForModId(config.ModId), (ILogger)loader.GetLogger());
        loader.AddOrReplaceController<IContentExpansion>(this, _service);
    }
    // Explicit export ensures the same controller interface identity across mod load contexts.
    public Type[] GetTypes() => [typeof(IContentExpansion)];
    public bool CanUnload() => false;
    public bool CanSuspend() => false;
    public void Suspend() { }
    public void Resume() { }
    public void Unload() { }
    public Action Disposing => () => { };
}
