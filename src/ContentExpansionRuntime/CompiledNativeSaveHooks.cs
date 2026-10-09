using System.Security.Cryptography;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>Native save observations preserving private GP/XSTATE through compiled thunks.</summary>
public sealed unsafe class CompiledNativeSaveHooks
{
    private const string ImageHash = "937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5";
    private readonly INativeSaveObserver _events;
    private readonly List<IHook<Native>> _hooks = [];
    private readonly ThreadLocal<Dictionary<long, object>> _pending = new(() => []);
    private volatile bool _active;
    private NativeContextBridge? _bridge;
    [Function(CallingConventions.Microsoft)]
    private delegate long Native(long a, long b, long c, long d);

    public CompiledNativeSaveHooks(INativeSaveObserver events) => _events = events;
    internal void Disable()
    {
        _active=false;
        foreach(var hook in _hooks.AsEnumerable().Reverse())if(hook.IsHookActivated)hook.Disable();
    }
    public void Install(IReloadedHooks hooks, NativeContextBridge bridge, nint imageBase, string executablePath)
    {
        if (_hooks.Count != 0) throw new InvalidOperationException("Save hooks already created.");
        _events.RequireReadyBindings();
        using (var input = File.OpenRead(executablePath))
            if (Convert.ToHexString(SHA256.HashData(input)) != ImageHash)
                throw new NotSupportedException("Unsupported game executable; no save hooks installed.");
        Guard(imageBase + 0x32B94, "488BC448895808488968104889701848");
        Guard(imageBase + 0x32E60, "48895C24084889742410574883EC2049");
        Guard(imageBase + 0x33DF0, "4053555657415641574883EC78488B05");
        _bridge = bridge;
        Bind(hooks, bridge, imageBase + 0x32B94, 1, Queue);
        Bind(hooks, bridge, imageBase + 0x32E60, 0, Write);
        Bind(hooks, bridge, imageBase + 0x33DF0, 2, Read);
        try
        {
            foreach (var hook in _hooks) hook.Activate();
            _active = true;
        }
        catch (Exception failure)
        {
            _active = false;
            var failures = new List<Exception> { failure };
            foreach (var hook in _hooks.AsEnumerable().Reverse())
                try { if (hook.IsHookActivated) hook.Disable(); } catch (Exception ex) { failures.Add(ex); }
            throw new AggregateException("Native save observers could not activate; attempted hooks disabled/pass-through.", failures);
        }
    }
    private void Bind(IReloadedHooks hooks, NativeContextBridge bridge, nint address, uint stackArguments,
        NativeContextBridge.Observer observer)
    {
        var hook = hooks.CreateHook<Native>((void*)bridge.ReserveAddress(), (long)address);
        // These three observers never call game helpers. The native owner is
        // blocked until buffer capture/publication completes on a normal stack.
        bridge.Bind(hook.OriginalFunctionAddress, stackArguments, observer, _events.ReportFailure,isolateSave:true);
        _hooks.Add(hook);
    }
    private void Queue(NativeContextBridge.Frame* frame)
    {
        if (!_active) return;
        if (frame->Phase == 0)
        {
            if (_events.BeforeQueue(frame->Arguments[1], frame->Arguments[2], frame->Arguments[3], frame->Arguments[4]))
                _pending.Value![frame->EntryStack] = frame->Arguments[1];
        }
        else if (_pending.Value!.Remove(frame->EntryStack, out var captured) && (int)frame->OriginalResult < 0)
            _events.CancelQueue((long)captured);
    }
    private void Write(NativeContextBridge.Frame* frame)
    {
        if (!_active) return;
        if (frame->Phase == 0)
        {
            var completion = _events.CaptureWriteCompletion(frame->Arguments[2], (int)frame->Arguments[1]);
            if (completion is not null) _pending.Value![frame->EntryStack] = completion;
        }
        else if (_pending.Value!.Remove(frame->EntryStack, out var captured))
            _events.AfterWriteCompletion((NativeWriteCompletion)captured, (int)frame->OriginalResult);
    }
    private void Read(NativeContextBridge.Frame* frame)
    {
        if (!_active) return;
        if (frame->Phase == 0)
        {
            var completion = _events.CaptureReadCompletion(frame->Arguments[1], (int)frame->Arguments[3],
                frame->Arguments[4], frame->Arguments[5]);
            if (completion is not null) _pending.Value![frame->EntryStack] = completion;
        }
        else if (_pending.Value!.Remove(frame->EntryStack, out var captured))
            _events.AfterReadCompletion((NativeReadCompletion)captured, (int)frame->OriginalResult);
    }
    private static void Guard(nint address, string expectedHex)
    {
        var expected = Convert.FromHexString(expectedHex);
        if (!CheckedNativeRead.Read((long)address, expected.Length).SequenceEqual(expected))
            throw new InvalidDataException("Save prologue changed; refuse conflicting detour.");
    }
}
