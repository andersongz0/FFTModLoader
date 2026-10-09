using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FFTModLoader.ContentExpansion;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>
/// Separate native backend, NOT an installed mod or automatic startup. Activation
/// requires a real session implementing profile/serialized-unit capture and load
/// binding. The approved JobExpansion assembly is not modified by this class.
/// </summary>
public sealed class NativeSaveHooks
{
    private const string ImageHash = "937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5";
    private readonly NativeSaveEvents _events;
    private IHook<PrepareSave>? _prepare;
    private IHook<WriteCompletion>? _write;
    private IHook<ReadCompletion>? _read;
    private volatile bool _active;

    [Function(CallingConventions.Microsoft)]
    private delegate int PrepareSave(nint manager, nint slotObject, nint nativeName, nint buffer, long size);
    [Function(CallingConventions.Microsoft)]
    private delegate int WriteCompletion(nint ignoredManager, int result, nint queueEntry);
    [Function(CallingConventions.Microsoft)]
    private delegate int ReadCompletion(nint slotObject, nint nativeName, nint errorInfo, int result, nint buffer, long size);

    public NativeSaveHooks(NativeSaveEvents events) => _events = events;

    public void Install(IReloadedHooks hooks, nint imageBase, string executablePath)
    {
        if (_prepare is not null) throw new InvalidOperationException("Save hooks already created.");
        // Check the actual session held by the callback adapter, not an unrelated
        // readiness object supplied by the caller.
        _events.RequireReadyBindings();
        using (var input = File.OpenRead(executablePath))
            if (Convert.ToHexString(SHA256.HashData(input)) != ImageHash)
                throw new NotSupportedException("Unsupported game executable; no save hooks installed.");
        Guard(imageBase + 0x32B94, "488BC448895808488968104889701848");
        Guard(imageBase + 0x32E60, "48895C24084889742410574883EC2049");
        Guard(imageBase + 0x33DF0, "4053555657415641574883EC78488B05");
        _prepare = hooks.CreateHook<PrepareSave>(Queue, (long)imageBase + 0x32B94);
        _write = hooks.CreateHook<WriteCompletion>(Write, (long)imageBase + 0x32E60);
        _read = hooks.CreateHook<ReadCompletion>(Read, (long)imageBase + 0x33DF0);
        try
        {
            _prepare.Activate();
            _write.Activate();
            _read.Activate();
            _active = true;
        }
        catch (Exception activationFailure)
        {
            _active = false;
            // Keep hook references rooted even during rollback; callbacks that
            // raced activation fall straight through while _active is false.
            var failures = new List<Exception> { activationFailure };
            try { if (_read.IsHookActivated) _read.Disable(); } catch (Exception ex) { failures.Add(ex); }
            try { if (_write.IsHookActivated) _write.Disable(); } catch (Exception ex) { failures.Add(ex); }
            try { if (_prepare.IsHookActivated) _prepare.Disable(); } catch (Exception ex) { failures.Add(ex); }
            throw new AggregateException("Save hook activation failed; all attempted hooks were disabled or left in pass-through mode.", failures);
        }
    }

    private int Queue(nint manager, nint slotObject, nint name, nint buffer, long size)
    {
        bool captured = false;
        if (_active)
            try { captured = _events.BeforeQueue((long)slotObject, (long)name, (long)buffer, size); }
            catch (Exception ex) { _events.ReportFailure(ex); }
        // Native function is called exactly once, outside observer exception handling.
        int result = _prepare!.OriginalFunction(manager, slotObject, name, buffer, size);
        if (captured && result < 0) _events.CancelQueue((long)slotObject);
        return result;
    }

    private int Write(nint ignoredManager, int nativeResult, nint queueEntry)
    {
        NativeWriteCompletion? completion = null;
        if (_active)
            try { completion = _events.CaptureWriteCompletion((long)queueEntry, nativeResult); }
            catch (Exception ex) { _events.ReportFailure(ex); }
        int result = _write!.OriginalFunction(ignoredManager, nativeResult, queueEntry);
        if (completion is not null)
            try { _events.AfterWriteCompletion(completion, result); }
            catch (Exception ex) { _events.ReportFailure(ex); }
        return result;
    }

    private int Read(nint slotObject, nint name, nint errorInfo, int nativeResult, nint buffer, long size)
    {
        NativeReadCompletion? completion = null;
        if (_active)
            try { completion = _events.CaptureReadCompletion((long)name, nativeResult, (long)buffer, size); }
            catch (Exception ex) { _events.ReportFailure(ex); }
        int result = _read!.OriginalFunction(slotObject, name, errorInfo, nativeResult, buffer, size);
        if (completion is not null)
            try { _events.AfterReadCompletion(completion, result); }
            catch (Exception ex) { _events.ReportFailure(ex); }
        return result;
    }

    private static void Guard(nint address, string expectedHex)
    {
        var expected = Convert.FromHexString(expectedHex);
        byte[] actual = CheckedNativeRead.Read((long)address, expected.Length);
        if (!actual.SequenceEqual(expected)) throw new InvalidDataException("Save function prologue changed; refuse conflicting detour.");
    }
}
