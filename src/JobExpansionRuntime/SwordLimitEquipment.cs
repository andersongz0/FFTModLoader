using System.Diagnostics;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace GenericJobs;

public partial class Mod
{
    private IHook<EquipmentBonusDelegate>? _swordLimitEquipment;
    private NativeJobRouteMemory? _swordLimitRoute;
    private nint _swordLimitCommands;

    [Function(CallingConventions.Microsoft)]
    private delegate void EquipmentBonusDelegate(nint unit, int mode);

    private unsafe void InstallSwordLimitEquipment()
    {
        string gameDirectory = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName)!;
        byte[] commands = JobActivationPolicy.ReadSwordLimitCommands(_modLoader.GetAppConfig().EnabledMods.Select(id =>
            ModDirectoryResolver.Resolve(gameDirectory, id, _modLoader.GetDirectoryForModId)));
        if (_hooks == null || !commands.Contains((byte)1)) return;
        // Image hash is checked before registering this initialization callback.
        nuint address = _gameBase + 0x2779EC;
        byte[] expected = Convert.FromHexString("488BC448895808488968104889701848897820");
        byte[] actual = new byte[expected.Length];
        Marshal.Copy((nint)address, actual, 0, actual.Length);
        if (!actual.SequenceEqual(expected)) throw new NotSupportedException("Equipment resolver changed; Sword Limit route not installed.");
        _swordLimitCommands = Marshal.AllocHGlobal(commands.Length);
        Marshal.Copy(commands, 0, _swordLimitCommands, commands.Length);
        _swordLimitRoute = new NativeJobRouteMemory();
        _swordLimitEquipment = _hooks.CreateHook<EquipmentBonusDelegate>((void*)_swordLimitRoute.Address, (long)address);
        _swordLimitRoute.CompleteShape(NativeJobRoutes.BuildSwordLimitEquipment(
            _swordLimitEquipment.OriginalFunctionAddress, (long)_swordLimitCommands));
        _swordLimitEquipment.Activate();
        _logger.WriteLine($"[{_modConfig.ModId}] Registered Sword Limit equipment route: native only; original Cloud/Materia requirement preserved.");
    }
}
