using System.Security.Cryptography;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>Separate pass-through diagnostic detours. No expanded state is activated.</summary>
public sealed class NativeProbeHooks
{
    // Argument layouts and return values are insufficient evidence of the
    // internal ABI. ItemChg callers rely on RDX/R10/R11 (and the leaf preserves
    // SIMD registers) although Microsoft x64 permits a managed detour to
    // clobber them. Revoke ALL observers until each caller contract is audited.
    public static bool InstallationAllowed => false;
    private const string ImageHash = "937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5";
    private readonly NativeIntegrationProbe _probe;
    private IHook<AbilityList>? _ability;
    private IHook<ShopList>? _shop;
    private IHook<ItemChange>? _stock;
    private IHook<PrepareSave>? _prepare;
    private IHook<WriteCompletion>? _write;
    private IHook<ReadCompletion>? _read;
    private volatile bool _active;

    [Function(CallingConventions.Microsoft)]
    private delegate nint AbilityList(short unit, ushort job, int category, nint output, int mode);
    [Function(CallingConventions.Microsoft)]
    private delegate int ShopList(short unit, short shop, short category, nint output, int filterEquip, byte sort);
    [Function(CallingConventions.Microsoft)]
    private delegate int ItemChange(ushort id, int delta);
    [Function(CallingConventions.Microsoft)]
    private delegate int PrepareSave(nint manager, nint slot, nint name, nint buffer, long size);
    [Function(CallingConventions.Microsoft)]
    private delegate int WriteCompletion(nint manager, int result, nint queueEntry);
    [Function(CallingConventions.Microsoft)]
    private delegate int ReadCompletion(nint slot, nint name, nint errorInfo, int result, nint buffer, long size);

    public NativeProbeHooks(NativeIntegrationProbe probe) => _probe = probe;

    public void Install(IReloadedHooks hooks, nint image, string executable)
    {
        if (!InstallationAllowed)
            throw new NotSupportedException("Diagnostic 0.0.1 revoked after Chemist equipment crash: internal register-preservation ABI is not validated. No native hooks created.");
        if (_ability is not null) throw new InvalidOperationException("Diagnostic hooks already created.");
        using (var file = File.OpenRead(executable))
            if (Convert.ToHexString(SHA256.HashData(file)) != ImageHash)
                throw new NotSupportedException("Executable changed; diagnostic hooks refused.");
        // Guard ALL sites before creating/activating any. Existing third-party
        // detours are conflicts, not bytes to overwrite. These RVAs/ABIs are
        // evidenced in integration-probe-abi.txt and native-save-callback-abi.txt.
        Guard(image + 0x2867BC, "488BC448895808488968104889701848");
        Guard(image + 0x288E54, "4C894C242066894C2408535556574154");
        Guard(image + 0x2847F8, "B8FF03000041B8030100006623C88D41");
        Guard(image + 0x32B94, "488BC448895808488968104889701848");
        Guard(image + 0x32E60, "48895C24084889742410574883EC2049");
        Guard(image + 0x33DF0, "4053555657415641574883EC78488B05");
        _ability = hooks.CreateHook<AbilityList>(Ability, (long)image + 0x2867BC);
        _shop = hooks.CreateHook<ShopList>(Shop, (long)image + 0x288E54);
        _stock = hooks.CreateHook<ItemChange>(Stock, (long)image + 0x2847F8);
        _prepare = hooks.CreateHook<PrepareSave>(Queue, (long)image + 0x32B94);
        _write = hooks.CreateHook<WriteCompletion>(Write, (long)image + 0x32E60);
        _read = hooks.CreateHook<ReadCompletion>(Read, (long)image + 0x33DF0);
        try
        {
            _ability.Activate(); _shop.Activate(); _stock.Activate();
            _prepare.Activate(); _write.Activate(); _read.Activate();
            _active = true;
        }
        catch (Exception activation)
        {
            _active = false;
            var failures = new List<Exception> { activation };
            // Always attempt every rollback; leave rooted references alive.
            Disable(_read, failures); Disable(_write, failures); Disable(_prepare, failures);
            Disable(_stock, failures); Disable(_shop, failures); Disable(_ability, failures);
            throw new AggregateException("Diagnostic activation failed; all callbacks are pass-through.", failures);
        }
    }

    private nint Ability(short unit, ushort job, int category, nint output, int mode)
    {
        if (!_active || job != 75 || category != 0 || mode is not (0 or 2 or 3))
            return _ability!.OriginalFunction(unit, job, category, output, mode);
        return NativeObservation.Invoke(() => _ability!.OriginalFunction(unit, job, category, output, mode),
            null, result => _probe.AbilityList(unit, job, category, mode, (long)output, (long)result), _probe.Failure);
    }

    private int Shop(short unit, short shop, short category, nint output, int filterEquip, byte sort)
    {
        if (!_active || shop is < 0 or >= 15 || category != 3 || filterEquip != 0)
            return _shop!.OriginalFunction(unit, shop, category, output, filterEquip, sort);
        return NativeObservation.Invoke(() => _shop!.OriginalFunction(unit, shop, category, output, filterEquip, sort),
            null, count => _probe.ShopList(shop, category, filterEquip, sort, (long)output, count), _probe.Failure);
    }

    private int Stock(ushort id, int delta)
    {
        if (!_active || delta == 0) return _stock!.OriginalFunction(id, delta);
        int? before = null;
        return NativeObservation.Invoke(() => _stock!.OriginalFunction(id, delta),
            () => before = _probe.StockBefore(id, delta),
            result => _probe.StockAfter(id, delta, before, result), _probe.Failure);
    }

    private int Queue(nint manager, nint slot, nint name, nint buffer, long size)
    {
        if (!_active) return _prepare!.OriginalFunction(manager, slot, name, buffer, size);
        string? hash = null;
        return NativeObservation.Invoke(() => _prepare!.OriginalFunction(manager, slot, name, buffer, size),
            () => hash = _probe.Packet((long)buffer, size),
            result => _probe.Queued(hash, size, result), _probe.Failure);
    }

    private int Write(nint manager, int input, nint entry)
    {
        if (!_active) return _write!.OriginalFunction(manager, input, entry);
        (string? Hash, long Size, int EntryError)? capture = null;
        return NativeObservation.Invoke(() => _write!.OriginalFunction(manager, input, entry),
            () => capture = _probe.WriteBefore((long)entry, input),
            result => _probe.Completed(capture, input, result), _probe.Failure);
    }

    private int Read(nint slot, nint name, nint errorInfo, int input, nint buffer, long size)
    {
        if (!_active || input < 0) return _read!.OriginalFunction(slot, name, errorInfo, input, buffer, size);
        string? hash = null;
        return NativeObservation.Invoke(() => _read!.OriginalFunction(slot, name, errorInfo, input, buffer, size),
            () => hash = _probe.Packet((long)buffer, size),
            result => _probe.Loaded(hash, size, input, result), _probe.Failure);
    }

    private static void Guard(nint address, string expectedHex)
    {
        byte[] expected = Convert.FromHexString(expectedHex);
        if (!CheckedNativeRead.Read((long)address, expected.Length).SequenceEqual(expected))
            throw new InvalidDataException("Conflicting or changed native entry; no diagnostic detours activated.");
    }

    private static void Disable<T>(IHook<T>? hook, List<Exception> failures) where T : Delegate
    {
        try { if (hook?.IsHookActivated == true) hook.Disable(); }
        catch (Exception ex) { failures.Add(ex); }
    }
}
