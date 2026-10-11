using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>One physical detour per entry point, composed solely by ContentExpansion.</summary>
public sealed unsafe class LoaderNativeRoutes
{
    [Function(CallingConventions.Microsoft)] private delegate long Native(long a,long b,long c,long d);
    private sealed record Route(NativeLifetimeMemory Gate, IHook<Native> Hook, byte[] LiveEntry);
    private readonly IReloadedHooks _hooks;
    private readonly NativeContextBridge _bridge;
    private readonly long _image;
    private readonly byte[] _exe;
    private readonly PEReader _pe;
    private readonly Dictionary<int,Route> _routes=[];
    private readonly List<NativeLifetimeMemory> _memory=[];
    private readonly object _gate=new();
    private LoaderPlayableTables? _tables;
    private LoaderJobRecords? _jobs;
    private readonly FFTModLoader.ContentExpansion.Api.IExpandedJobVisuals? _jobOwner;
    internal LoaderPlayableTables Tables=>_tables??=new(_image);
    internal LoaderJobRecords Jobs=>_jobs??=new(_image);

    public LoaderNativeRoutes(IReloadedHooks hooks,NativeContextBridge bridge,long image,string executable,
        FFTModLoader.ContentExpansion.Api.IExpandedJobVisuals? jobOwner=null)
    {
        _hooks=hooks;_bridge=bridge;_image=image;_jobOwner=jobOwner;
        _exe=File.ReadAllBytes(executable);
        if(Convert.ToHexString(SHA256.HashData(_exe))!=ChemistPlayableHost.ImageHash)
            throw new NotSupportedException("Unsupported executable for expanded native routing.");
        _pe=new PEReader(new MemoryStream(_exe,false));
    }

    public void Guard(int rva)
    {
        lock(_gate)
        {
            byte[] expected;
            if(_routes.TryGetValue(rva,out var route))expected=route.LiveEntry;
            else
            {
                var section=_pe.PEHeaders.SectionHeaders.Single(s=>s.VirtualAddress<=rva&&rva+14<=s.VirtualAddress+s.SizeOfRawData);
                expected=_jobOwner?.GetOwnedHookEntry(rva)??_exe.AsSpan(section.PointerToRawData+rva-section.VirtualAddress,14).ToArray();
            }
            if(!CheckedNativeRead.Read(_image+rva,14).SequenceEqual(expected))
                throw new InvalidDataException($"Foreign or changed native route at {rva:X}.");
        }
    }

    private Route Get(int rva)
    {
        Guard(rva);
        if(_routes.TryGetValue(rva,out var prior))return prior;
        var memory=new NativeLifetimeMemory(4096);_memory.Add(memory);
        var hook=_hooks.CreateHook<Native>((void*)memory.Address,_image+rva);
        // Pointer is at +8 (naturally aligned). Native readers see an atomic
        // pointer replacement, never a partially rewritten instruction stream.
        memory.Write(0,[0xFF,0x25,2,0,0,0,0x90,0x90]);
        memory.Write(8,BitConverter.GetBytes(hook.OriginalFunctionAddress));memory.Executable();
        hook.Activate();
        var route=new Route(memory,hook,CheckedNativeRead.Read(_image+rva,14));
        _routes.Add(rva,route);return route;
    }

    public void Leaf(int rva,Func<long,byte[]> build,int unwindKind=0)
    {
        lock(_gate)
        {
            var route=Get(rva);
            long previous=Marshal.ReadInt64((nint)(route.Gate.Address+8));
            var memory=new NativeLifetimeMemory(4096);_memory.Add(memory);
            byte[] code=build(previous);memory.Write(0,code);
            if(unwindKind==1)memory.ExecutableWithVolatilePushes(code.Length);
            else if(unwindKind==2)memory.ExecutableWithCommandArrayFrame(code.Length);
            else if(unwindKind==3)memory.ExecutableWithActionCaptionFrame(code.Length);
            else if(unwindKind==0)memory.Executable();
            else throw new ArgumentOutOfRangeException(nameof(unwindKind));
            NativeLifetimeMemory.WriteProtected(route.Gate.Address+8,BitConverter.GetBytes(memory.Address));
        }
    }

    public void Observe(int rva,uint stack,NativeContextBridge.Observer observer,Action<Exception> failure,
        bool isolateSave=false,Func<long,long,byte[]>? gateBuilder=null)
    {
        lock(_gate)
        {
            var route=Get(rva);
            long previous=Marshal.ReadInt64((nint)(route.Gate.Address+8));
            long observerAddress=(long)_bridge.Bind((nint)previous,stack,observer,failure,isolateSave);
            long next=observerAddress;
            if(gateBuilder is not null)
            {
                var memory=new NativeLifetimeMemory(4096);_memory.Add(memory);
                byte[] code=gateBuilder(previous,observerAddress);memory.Write(0,code);
                memory.ExecutableWithVolatilePushes(code.Length);next=memory.Address;
            }
            NativeLifetimeMemory.WriteProtected(route.Gate.Address+8,BitConverter.GetBytes(next));
        }
    }
    internal void ObserveAddress(long address,uint stack,NativeContextBridge.Observer observer,Action<Exception> failure,bool isolateSave)
    {
        long rva=address-_image;
        if(rva is <0 or >int.MaxValue)throw new ArgumentOutOfRangeException(nameof(address));
        Observe((int)rva,stack,observer,failure,isolateSave);
    }
}
