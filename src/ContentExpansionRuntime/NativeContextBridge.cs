using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>Compiled x64 bridge preserving the game's private GP and enabled XSTATE contract.</summary>
public sealed unsafe class NativeContextBridge
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Frame
    {
        public uint Phase;
        public uint Flags;
        public fixed long Arguments[8];
        public long OriginalResult;
        public long Result;
        public long EntryStack;
        public long XState;
        public long Context;
        public long OriginalAddress;
        public void Skip(long result) { Result = result; Flags |= 1; }
        public void ReplaceResult(long result) { Result = result; Flags |= 2; }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void Observer(Frame* frame);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint Address(uint index);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Configure(uint index, nint original, nint callback, uint stackArguments);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint Version();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SaveDispatch(uint index);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int WaitSave(out uint index,out nint frame);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CompleteSave();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int WaitGameplay(out uint index,out nint frame,out nint token);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int CompleteGameplay(nint token);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int BeginCall(nint helper,long a,long b,long c,long d);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int WaitCall(out long result,out uint index,out nint frame,out nint token);
    [UnmanagedFunctionPointer(CallingConvention.Winapi,CharSet=CharSet.Unicode)]
    private delegate int FirstFaultCapture([MarshalAs(UnmanagedType.LPWStr)] string directory);
    private readonly nint _module;
    private readonly Address _address;
    private readonly Configure _configure;
    private readonly SaveDispatch _saveDispatch;
    private readonly WaitSave _waitSave;
    private readonly CompleteSave _completeSave;
    private readonly SaveDispatch _gameplayDispatch;
    private readonly WaitGameplay _waitGameplay;
    private readonly CompleteGameplay _completeGameplay;
    private readonly BeginCall _beginCall;
    private readonly WaitCall _waitCall;
    [ThreadStatic] private static NativeContextBridge? _gameplayOwner;
    private readonly Observer?[] _roots = new Observer?[64];
    private readonly object _workerGate=new();
    private Thread? _saveWorker;
    private Thread[]? _gameplayWorkers;
    private uint _next;

    public NativeContextBridge(string absoluteLibraryPath)
    {
        if (!Path.IsPathFullyQualified(absoluteLibraryPath)) throw new ArgumentException("Absolute native DLL path required.");
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Windows x64 required.");
        if (Marshal.SizeOf<Frame>() != 120) throw new InvalidOperationException("Bridge frame ABI differs.");
        _module = NativeLibrary.Load(absoluteLibraryPath);
        // Never unload: active detours and Windows unwind records must outlive this host.
        _address = Export<Address>("GetBridgeAddress");
        _configure = Export<Configure>("ConfigureBridge");
        if (Export<Version>("GetBridgeVersion")() != 5) throw new NotSupportedException("Native bridge v5 required (CLR-owned gameplay/save workers and fiber-local helper transport).");
        _saveDispatch=Export<SaveDispatch>("SetBridgeSaveDispatch");
        _waitSave=Export<WaitSave>("WaitForSaveObserver");
        _completeSave=Export<CompleteSave>("CompleteSaveObserver");
        _gameplayDispatch=Export<SaveDispatch>("SetBridgeGameplayDispatch");
        _waitGameplay=Export<WaitGameplay>("WaitForGameplayObserver");
        _completeGameplay=Export<CompleteGameplay>("CompleteGameplayObserver");
        _beginCall=Export<BeginCall>("BeginOwnerCall");
        _waitCall=Export<WaitCall>("WaitOwnerCall");
    }
    public nint ReserveAddress()
    {
        if (_next >= 64) throw new InvalidOperationException("Native bridge capacity exhausted.");
        return _address(_next);
    }
    public void EnableDiagnosticCapture(string directory)
    {
        if(!Path.IsPathFullyQualified(directory))throw new ArgumentException("Absolute diagnostic directory required.");
        Directory.CreateDirectory(directory);
        if(Export<FirstFaultCapture>("EnableFirstFaultCapture")(directory)!=1)
            throw new InvalidOperationException("First-fault capture could not be enabled.");
        NativeLifetimeMemory.SetWriteAudit(Export<NativeLifetimeMemory.WriteAudit>("RecordChemistWrite"));
    }
    public nint Bind(nint original, uint stackArguments, Observer observer, Action<Exception> failure,bool isolateSave=false)
    {
        var address = ReserveAddress();
        Observer guarded = frame =>
        {
            try { observer(frame); }
            catch (Exception ex)
            {
                // An observer may never unwind into the game. No replacement return
                // is accepted after an incomplete callback; caller blocks its session.
                frame->Flags = 0;
                try { failure(ex); } catch { }
            }
        };
        RuntimeHelpers.PrepareDelegate(guarded);
        if (_configure(_next, original, Marshal.GetFunctionPointerForDelegate(guarded), stackArguments) != 1)
            throw new NotSupportedException("Native bridge rejected binding or enabled CPU XSTATE size.");
        Volatile.Write(ref _roots[_next],guarded);
        if(isolateSave)
        {
            if(_saveDispatch(_next)!=1)throw new InvalidOperationException("Isolated save observer initialization failed.");
            StartSaveWorker();
        }
        else
        {
            if(_gameplayDispatch(_next)!=1)throw new InvalidOperationException("Isolated gameplay observer initialization failed.");
            StartGameplayWorkers();
        }
        _next++;
        return address;
    }
    public bool IsGameplayObserver => ReferenceEquals(_gameplayOwner,this);
    public long CallOnNativeOwner(nint helper,long a=0,long b=0,long c=0,long d=0)
    {
        if(!IsGameplayObserver)throw new InvalidOperationException("Game helper transport requires a gameplay observer.");
        if(_beginCall(helper,a,b,c,d)!=1)throw new InvalidOperationException("Game helper request refused.");
        for(;;)
        {
            int phase=_waitCall(out long result,out uint index,out nint frame,out nint token);
            if(phase==1)return result;
            if(phase!=2)Environment.FailFast("Game helper transport stopped unexpectedly.");
            DispatchGameplay(index,frame,token);
        }
    }
    private void StartGameplayWorkers()
    {
        lock(_workerGate)
        {
            if(_gameplayWorkers is not null)return;
            _gameplayWorkers=Enumerable.Range(0,16).Select(i=>new Thread(GameplayLoop,2*1024*1024)
                {IsBackground=true,Name="FFT Chemist gameplay worker "+i}).ToArray();
            foreach(var thread in _gameplayWorkers)thread.Start();
        }
    }
    private void GameplayLoop()
    {
        _gameplayOwner=this;
        while(_waitGameplay(out uint index,out nint frame,out nint token)==1)DispatchGameplay(index,frame,token);
        Environment.FailFast("Native gameplay transport stopped unexpectedly.");
    }
    private void DispatchGameplay(uint index,nint frame,nint token)
    {
        try
        {
            if(index>=64 || frame==0 || token==0)Environment.FailFast("Invalid native gameplay request.");
            var observer=Volatile.Read(ref _roots[index]);
            if(observer is null)Environment.FailFast("Unbound native gameplay observer.");
            observer((Frame*)frame);
        }
        finally
        {
            if(_completeGameplay(token)!=1)Environment.FailFast("Native gameplay completion could not be signaled.");
        }
    }
    private void StartSaveWorker()
    {
        lock(_workerGate)
        {
            if(_saveWorker is not null)return;
            _saveWorker=new Thread(SaveLoop,2*1024*1024){IsBackground=true,Name="FFT Chemist save worker"};
            _saveWorker.Start();
        }
    }
    private void SaveLoop()
    {
        while(_waitSave(out uint index,out nint address)==1)
        {
            try
            {
                if(index>=64 || address==0)Environment.FailFast("Invalid native save request.");
                var observer=Volatile.Read(ref _roots[index]);
                if(observer is null)Environment.FailFast("Unbound native save observer.");
                observer((Frame*)address); // Ordinary managed call, NOT a reverse P/Invoke.
            }
            finally
            {
                if(_completeSave()!=1)Environment.FailFast("Native save completion could not be signaled.");
            }
        }
        Environment.FailFast("Native save transport stopped unexpectedly.");
    }
    private T Export<T>(string name) where T : Delegate
    {
        T function=Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_module,name));
        RuntimeHelpers.PrepareDelegate(function);
        return function;
    }
}
