using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace GenericJobs;

public partial class Mod
{
    private IHook<NxdGetRowDelegate>? _nxdGetRow;
    private bool _nxdExpansionReady;
    private NativeJobRouteMemory? _nxdRoute;

    [Function(CallingConventions.Microsoft)]
    private delegate nint NxdGetRowDelegate(nint table, uint key);

    private unsafe void InstallNxdExpansion()
    {
        try
        {
            if (_hooks == null) return;
            using var stream = File.OpenRead(Process.GetCurrentProcess().MainModule!.FileName);
            if (Convert.ToHexString(SHA256.HashData(stream)) != VisualImageHash)
                throw new NotSupportedException("Unsupported executable; sparse NXD lookup not installed.");
            _nxdRoute = new NativeJobRouteMemory();
            _nxdGetRow = _hooks.CreateHook<NxdGetRowDelegate>((void*)_nxdRoute.Address, (long)_gameBase + 0x3D3AE8);
            _nxdRoute.Complete(NativeJobRoutes.BuildSparse(_nxdGetRow.OriginalFunctionAddress), true);
            _nxdGetRow.Activate();
            // ContentExpansion's Bestial runtime reads the chapter-JP table
            // through this same native lookup. Publish the live entry as a
            // loader-owned composable route so it is never mistaken for a
            // foreign hook after JobExpansion has activated it.
            var entry = new byte[14];
            Marshal.Copy((nint)((long)_gameBase + 0x3D3AE8), entry, 0, entry.Length);
            _ownedComposableEntries[0x3D3AE8] = entry;
            _nxdExpansionReady = true;
            _logger.WriteLine($"[{_modConfig.ModId}] [NxdExpansion] READY: native single-key lookup, no managed callback on game fibers. Mod files unchanged.");
        }
        catch (Exception ex)
        {
            _logger.WriteLine($"[{_modConfig.ModId}] [NxdExpansion] DISABLED: {ex.Message}", _logger.ColorRed);
        }
    }

}
