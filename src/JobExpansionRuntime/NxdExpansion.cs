using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace GenericJobs;

public partial class Mod
{
    private IHook<NxdGetRowDelegate>? _nxdGetRow;
    private bool _nxdExpansionReady;
    private readonly HashSet<nint> _reportedSparseTables = new();
    private readonly HashSet<(nint Table, uint Key)> _reportedExpandedKeys = new();
    private readonly object _nxdTraceLock = new();

    [Function(CallingConventions.Microsoft)]
    private delegate nint NxdGetRowDelegate(nint table, uint key);

    private void InstallNxdExpansion()
    {
        try
        {
            if (_hooks == null) return;
            using var stream = File.OpenRead(Process.GetCurrentProcess().MainModule!.FileName);
            if (Convert.ToHexString(SHA256.HashData(stream)) != VisualImageHash)
                throw new NotSupportedException("Unsupported executable; sparse NXD lookup not installed.");
            _nxdGetRow = _hooks.CreateHook<NxdGetRowDelegate>(GetExpandedNxdRow, (long)_gameBase + 0x3D3AE8);
            _nxdGetRow.Activate();
            _nxdExpansionReady = true;
            _logger.WriteLine($"[{_modConfig.ModId}] [NxdExpansion] READY: single-key tables with non-consecutive IDs resolve by actual key. Mod files unchanged.");
        }
        catch (Exception ex)
        {
            _logger.WriteLine($"[{_modConfig.ModId}] [NxdExpansion] DISABLED: {ex.Message}", _logger.ColorRed);
        }
    }

    private nint GetExpandedNxdRow(nint table, uint key)
    {
        if (SparseNxdRows.TryLookup(table, key, out nint row, out int count))
        {
            lock (_nxdTraceLock)
                if (_reportedSparseTables.Add(table) || (row != 0 &&
                    (ulong)key >= (ulong)unchecked((uint)Marshal.ReadInt32(table,0x44)) + (uint)count &&
                    _reportedExpandedKeys.Add((table,key))))
                    _logger.WriteLine($"[{_modConfig.ModId}] [NxdExpansion] Sparse table resolved by actual keys: rows={count}, requested={key}, found={row != 0}");
            return row;
        }
        return _nxdGetRow!.OriginalFunction(table, key);
    }
}
