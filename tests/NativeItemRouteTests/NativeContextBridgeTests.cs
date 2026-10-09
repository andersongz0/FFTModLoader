using System.Runtime.InteropServices;
using FFTModLoader.ContentExpansion.Runtime;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using Iced.Intel;

static unsafe class NativeContextBridgeTests
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint Capture(nint target, int first, int second, nint output);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate long Eight(long a,long b,long c,long d,long e,long f,long g,long h);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint FiberProbe(nint target,uint iterations);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int FaultProbe(nint target);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint SnapshotSize();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern nint GetModuleHandle(string name);
    [Function(CallingConventions.Microsoft)]
    private delegate long Native(long a, long b, long c, long d);
    [DllImport("kernel32.dll")]
    private static extern nint RtlLookupFunctionEntry(ulong address, out ulong imageBase, nint history);
    public static void Run()
    {
        string path = Path.GetFullPath(@"AlchemistRework\builds\native-bridge-007\FFTModLoader.ContentExpansion.Native.dll");
        var bridge = new NativeContextBridge(path);
        using var code = new OwnedNativeMemory(4096);
        using var caller = new OwnedNativeMemory(4096);
        using var output = new OwnedNativeMemory(4096);
        // Deliberately non-Microsoft internal post-state: preserved RDX/R10/R11
        // and specific scratch results in RCX/R8/R9, while all XMM retain seed.
        code.Write(0,Convert.FromHexString("4889C84801D04883C1104183C0014183C102C3"));
        // Eight-argument sum uses ALL four stack arguments at original offsets.
        code.Write(128,Convert.FromHexString("4889C84801D04C01C04C01C84803442428480344243048034424384803442440C3"));
        code.ExecutablePage();
        caller.Write(0,NativeCommandRouteTests.CaptureCaller()); caller.ExecutablePage();
        output.Write(320,Enumerable.Range(0,16).Select(i=>(byte)(0x51+i)).ToArray());
        var capture=Marshal.GetDelegateForFunctionPointer<Capture>(caller.Address);
        capture(code.Address,7,19,output.Address);
        byte[] expected=output.Read(0,320);
        int calls=0;
        nint thunk=bridge.Bind(code.Address,0,frame=>
        {
            calls++;
            _=string.Join("|",Enumerable.Range(0,100).Select(i=>i.ToString()));
            GC.Collect(2,GCCollectionMode.Forced,true,true);
        },ex=>throw new Exception("Unexpected observer failure",ex));
        for(int i=0;i<100;i++)
        {
            capture(thunk,7,19,output.Address);
            Require(output.Read(0,320).SequenceEqual(expected),"Managed observer changed original private GP/XMM contract");
        }
        Require(calls==200,"Original before/after phases missing");
        Require(RtlLookupFunctionEntry((ulong)thunk,out _,0)!=0,"Compiled bridge lacks OS unwind metadata");
        VerifyEpilogueUnwind(thunk);
        bool fail=false;
        nint guarded=bridge.Bind(code.Address,0,frame=>throw new InvalidOperationException("fixture"),ex=>fail=true);
        capture(guarded,7,19,output.Address);
        Require(fail && output.Read(0,320).SequenceEqual(expected),"Observer exception escaped or changed native behavior");
        nint sum=bridge.Bind(code.Address+128,4,frame=>{},ex=>throw ex);
        var eight=Marshal.GetDelegateForFunctionPointer<Eight>(sum);
        Require(eight(1,2,3,4,5,6,7,8)==36,"Native stack-argument forwarding changed");
        nint replace=bridge.Bind(code.Address+128,4,frame=>
        {
            if(frame->Phase==0) frame->Arguments[4]=50;
            else frame->ReplaceResult(frame->OriginalResult+100);
        },ex=>throw ex);
        Require(Marshal.GetDelegateForFunctionPointer<Eight>(replace)(1,2,3,4,5,6,7,8)==181,"Before argument/after result override failed");
        int after=0;
        nint skip=bridge.Bind(code.Address+128,4,frame=>
        {
            if(frame->Phase==0) frame->Skip(777); else after++;
        },ex=>throw ex);
        Require(Marshal.GetDelegateForFunctionPointer<Eight>(skip)(1,2,3,4,5,6,7,8)==777 && after==0,"Skip path called original/after callback");
        // Exercise the actual installed Reloaded pointer-hook adapter as well:
        // direct thunk calls alone do not test trampoline binding/activation.
        using var hookedCode = new OwnedNativeMemory(4096);
        hookedCode.Write(0, Convert.FromHexString("4889C84801D04883C1104183C0014183C102C3"));
        hookedCode.ExecutablePage();
        nint hookTarget = bridge.ReserveAddress();
        var hook = ReloadedHooks.Instance.CreateHook<Native>((void*)hookTarget, (long)hookedCode.Address);
        bridge.Bind(hook.OriginalFunctionAddress, 0, frame => GC.Collect(0), ex => throw ex);
        try
        {
            hook.Activate();
            for (int i=0;i<100;i++)
            {
                capture(hookedCode.Address,7,19,output.Address);
                Require(output.Read(0,320).SequenceEqual(expected), "Actual pointer detour changed the native private contract");
            }
        }
        finally { if (hook.IsHookEnabled) hook.Disable(); GC.KeepAlive(hook); }
        // Game callbacks arrive from unmanaged threads, not only from a
        // managed test delegate. Exercise background compacting GC while
        // another native bridge re-enters the CLR on a Win32-created thread.
        using var runner=new OwnedNativeMemory(4096);
        byte[] run=Convert.FromHexString("534883EC204889CBC70300000000488B430831C931D24531C04531C9FFD0FF03813B102700007CE64831C04883C4205BC3");
        runner.Write(0,run);runner.ExecutablePage();
        using var block=new OwnedNativeMemory(4096);
        uint owner=0,isolated=0;int stressCallbacks=0;Exception? stressFailure=null;
        nint stress=bridge.Bind(code.Address+128,0,frame=>
        {
            isolated=GetCurrentThreadId();Require(isolated!=owner,"Save observer ran on native owner stack");
            Require(Thread.CurrentThread.Name=="FFT Chemist save worker","Save validation did not originate on the CLR-owned worker");
            var saved=new Dictionary<string,int>(StringComparer.Ordinal);
            for(int k=0;k<11;k++)saved.Add("item-"+k,k);
            var copied=saved.OrderBy(p=>p.Key,StringComparer.Ordinal).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
            Require(copied.Count==11,"Save dictionary corrupted in unmanaged callback");
            // Exercise the production explicit schema codec, avoiding the
            // reflection metadata cache seen in PID 3580's first fault.
            string key=new FFTModLoader.ContentExpansion.SaveSlotIdentity("fixture-profile","resume_enbtl_main.sav").StorageKey();
            Require(key.Length==64,"Slot JSON/hash corrupted under concurrent GC");
            var state=new FFTModLoader.ContentExpansion.ExpandedSaveState(copied,
                [new(3,new string('A',64),["venom","oil","fire"])]);
            var bytes=FFTModLoader.ContentExpansion.ExpandedSaveJson.StateBytes(state);
            Require(bytes[0]=='{',"Save-shaped serialization incomplete");
            Interlocked.Increment(ref stressCallbacks);
        },ex=>stressFailure=ex,isolateSave:true);
        block.Write(8,BitConverter.GetBytes((long)stress));
        using var cancel=new CancellationTokenSource();
        var collector=Task.Run(()=>{while(!cancel.IsCancellationRequested){GC.Collect(2,GCCollectionMode.Forced,true,true);Thread.Sleep(1);}});
        nint thread=CreateThread(0,0,runner.Address,block.Address,4,out owner);
        ResumeThread(thread);
        Require(thread!=0,"Native callback stress thread failed to start");
        try { Require(WaitForSingleObject(thread,60000)==0 && BitConverter.ToInt32(block.Read(0,4))==10000,"Unmanaged background GC stress incomplete"); }
        finally {cancel.Cancel();collector.GetAwaiter().GetResult();CloseHandle(thread);}
        Require(stressFailure is null && stressCallbacks==20000,"Isolated observer failed or lost before/after callbacks: "+stressFailure);
        // Two native fibers reuse one OS thread but have different stacks.
        // Keep CLR save validation on its dedicated, non-fiber worker.
        nint fixture=NativeLibrary.Load(Path.GetFullPath(@"AlchemistRework\builds\native-bridge-007\SaveFiberFixture.dll"));
        var fiberProbe=Marshal.GetDelegateForFunctionPointer<FiberProbe>(NativeLibrary.GetExport(fixture,"RunFiberProbe"));
        int fiberCallbacks=0;Exception? fiberFailure=null;
        nint fiberTarget=bridge.Bind(code.Address,0,frame=>
        {
            var result=Enumerable.Range(0,11).ToDictionary(i=>"key-"+i,i=>i);
            GC.Collect(2,GCCollectionMode.Forced,true,true);
            Require(result.Count==11,"Fiber observer dictionary corrupted");
            Interlocked.Increment(ref fiberCallbacks);
        },ex=>fiberFailure=ex,isolateSave:true);
        Require(fiberProbe(fiberTarget,200)==400 && fiberCallbacks==800 && fiberFailure is null,"Native fiber save callbacks incomplete: "+fiberFailure);
        // Ordinary gameplay callbacks no longer enter CLR on game fibers.
        // A helper actually yields between two different fibers of one OS
        // thread, then re-enters a second hooked function on the SAME fiber.
        // This exposes both TLS-vs-FLS deadlocks and nested transport errors.
        var yieldingProbe=Marshal.GetDelegateForFunctionPointer<FiberProbe>(NativeLibrary.GetExport(fixture,"RunYieldingFiberProbe"));
        nint ownerHelper=NativeLibrary.GetExport(fixture,"YieldingOwnerHelper");
        int gameplayCallbacks=0,nestedCallbacks=0;Exception? gameplayFailure=null;
        nint nested=bridge.Bind(code.Address+128,0,frame=>
        {
            Require(bridge.IsGameplayObserver&&Thread.CurrentThread.Name!.StartsWith("FFT Chemist gameplay worker "),"Nested gameplay callback ran on game owner");
            if(frame->Phase==0){GC.Collect(2,GCCollectionMode.Forced,true,true);frame->Skip(frame->Arguments[0]+frame->Arguments[1]+frame->Arguments[2]+frame->Arguments[3]);}
            Interlocked.Increment(ref nestedCallbacks);
        },ex=>gameplayFailure=ex);
        nint gameplay=bridge.Bind(code.Address,0,frame=>
        {
            Require(bridge.IsGameplayObserver&&Thread.CurrentThread.Name!.StartsWith("FFT Chemist gameplay worker "),"Gameplay callback ran on game owner");
            Require(frame->Phase==0,"Gameplay skip unexpectedly invoked original");
            long result=bridge.CallOnNativeOwner(ownerHelper,(long)nested,2,3,4);
            Require(result==119,"Helper ran on wrong owner/fiber or nested return changed");
            frame->Skip(result);Interlocked.Increment(ref gameplayCallbacks);
        },ex=>gameplayFailure=ex);
        Require(yieldingProbe(gameplay,200)==400 && gameplayCallbacks==400 && nestedCallbacks==400 && gameplayFailure is null,
            "Yielding/nested gameplay callbacks incomplete: "+gameplayFailure);
        string evidence=Path.Combine(Path.GetTempPath(),"ChemistFirstFaultFixture-"+Guid.NewGuid().ToString("N"));
        bridge.EnableDiagnosticCapture(evidence);
        nint auditLibrary=NativeLibrary.Load(path);
        var sizeProbe=Marshal.GetDelegateForFunctionPointer<SnapshotSize>(NativeLibrary.GetExport(auditLibrary,"GetFirstFaultSnapshotSize"));
        Require(sizeProbe()==88672,"First-fault v2 native structure offsets changed");
        var recordWrite=Marshal.GetDelegateForFunctionPointer<NativeLifetimeMemory.WriteAudit>(NativeLibrary.GetExport(auditLibrary,"RecordChemistWrite"));
        // Harmless same-byte attempt: if a regression removes the guard this
        // fails the assertion without corrupting our process's runtime header.
        foreach(string moduleName in new[]{"coreclr.dll","clrjit.dll","System.Private.CoreLib.dll"})
        {
            nint runtimeImage=GetModuleHandle(moduleName);
            Require(runtimeImage!=0,"Runtime image fixture is not loaded: "+moduleName);
            byte original=Marshal.ReadByte(runtimeImage);bool rejected=false;
            try{NativeLifetimeMemory.WriteProtected((long)runtimeImage,[original]);}
            catch(InvalidDataException ex){rejected=ex.Message.Contains("refused",StringComparison.Ordinal);}
            Require(rejected&&Marshal.ReadByte(runtimeImage)==original,"Runtime image write guard missing: "+moduleName);
        }
        byte[] auditBytes=Enumerable.Range(0,16).Select(i=>(byte)(0xA0+i)).ToArray();
        NativeLifetimeMemory.WriteProtected((long)output.Address+1024,auditBytes);
        // Wrap the ring from concurrent producers. This only records read-only
        // samples of OUR fixture allocation; no simultaneous page writes.
        Parallel.For(0,4096,i=>recordWrite((nuint)output.Address+1024,16,(uint)(i&1)));
        byte[] finalBytes=Enumerable.Range(0,16).Select(i=>(byte)(0xB0+i)).ToArray();
        NativeLifetimeMemory.WriteProtected((long)output.Address+1024,finalBytes);
        using var faultCode=new OwnedNativeMemory(4096);
        // R11 points at known owned memory, RAX is the deliberately invalid
        // write target. Volatile R11 avoids changing the caller's private ABI.
        faultCode.Write(0,Convert.FromHexString("49BB").Concat(BitConverter.GetBytes((long)output.Address+1024))
            .Concat(Convert.FromHexString("48B8FEFFFFFF00000000C70001000000C3")).ToArray());faultCode.ExecutablePage();
        var faultProbe=Marshal.GetDelegateForFunctionPointer<FaultProbe>(NativeLibrary.GetExport(fixture,"RunFaultCaptureProbe"));
        for(int k=0;k<20;k++)Require(faultProbe(faultCode.Address)==1,"Diagnostic handler suppressed/changed the native exception");
        var samples=Directory.GetFiles(evidence,"*.bin").Select(p=>
        {
            using var stream=new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            byte[] bytes=new byte[checked((int)stream.Length)];stream.ReadExactly(bytes);return bytes;
        }).ToArray();
        Require(samples.Length==16 && samples.All(b=>b.Length==sizeProbe()&&BitConverter.ToUInt32(b,8)==2),"First-fault capture is missing or not bounded");
        Require(samples.All(b=>b.AsSpan(0,8).SequenceEqual("CEXFAULT"u8)&&BitConverter.ToUInt32(b,48)==0xC0000005),"Diagnostic exception ABI changed");
        Require(samples.All(b=>BitConverter.ToUInt32(b,20)>0 && BitConverter.ToUInt32(b,24)>0),"First-fault code/stack evidence missing");
        Require(samples.All(b=>BitConverter.ToUInt64(b,88)==0xFFFFFFFE),"Original invalid write address was not retained");
        foreach(byte[] snapshot in samples)
        {
            int pointerOffset=18864+3*2064;
            Require(BitConverter.ToUInt64(snapshot,pointerOffset)==(ulong)output.Address+1024&&
                BitConverter.ToUInt32(snapshot,pointerOffset+8)==2048&&snapshot.AsSpan(pointerOffset+16,16).SequenceEqual(finalBytes),
                "Bounded R11 pointed memory sample missing or changed");
            Require(BitConverter.ToInt64(snapshot,39504)==4100,"Audit ticket count changed or rejected writes were recorded");
            var records=Enumerable.Range(0,1024).Select(i=>39512+i*48).Where(i=>BitConverter.ToInt64(snapshot,i)>0)
                .OrderBy(i=>BitConverter.ToInt64(snapshot,i)).ToArray();
            Require(records.Length>0&&records.Length<=1024&&records.Select(i=>BitConverter.ToInt64(snapshot,i)).Distinct().Count()==records.Length,
                "Concurrent/wrapped write audit has duplicate or unbounded records");
            foreach(int offset in records)Require(BitConverter.ToUInt64(snapshot,offset+8)==(ulong)output.Address+1024&&
                BitConverter.ToUInt32(snapshot,offset+16)==16&&BitConverter.ToUInt32(snapshot,offset+40)<=1&&
                BitConverter.ToInt64(snapshot,offset)<=4100,"Mixed/torn write audit record");
            int before=records[^2],afterWrite=records[^1];
            Require(BitConverter.ToInt64(snapshot,before)==4099&&BitConverter.ToInt64(snapshot,afterWrite)==4100&&
                BitConverter.ToUInt32(snapshot,before+40)==0&&BitConverter.ToUInt32(snapshot,afterWrite+40)==1&&
                snapshot.AsSpan(before+24,16).SequenceEqual(auditBytes)&&snapshot.AsSpan(afterWrite+24,16).SequenceEqual(finalBytes),
                "Protected write before/after evidence changed");
        }
        Console.WriteLine("INFO: first-fault v2 OWN fixture verified: 20 handled invalid writes,16 bounded snapshots, ten pointed ranges; 4100 audit tickets with concurrent ring wrap; protected before/after bytes; all three runtime image writes rejected unchanged. Exceptions remain visible; no game/saves touched. Evidence: "+evidence);
        NativeLibrary.Free(auditLibrary);
        NativeLibrary.Free(fixture);
        Require(isolated!=0,"Isolated save worker missing");
        Console.WriteLine("PASS: native bridge v5; original GP/XMM state and OS epilogue unwind; CLR-owned gameplay/save observers (no game-stack reverse P/Invoke), 20000 unmanaged save requests under concurrent compacting GC, 800 alternating-fiber save requests and 400 yielding gameplay plus 400 nested callbacks; helpers execute on original native owner/fiber, eight arguments, exception/skip/override routes. OWN process only.");
        GC.KeepAlive(bridge);
    }
    [DllImport("kernel32.dll")] private static extern nint CreateThread(nint attributes,nuint stack,nint start,nint parameter,uint flags,out uint id);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle,uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern uint ResumeThread(nint handle);
    [DllImport("kernel32.dll")] private static extern nint RtlVirtualUnwind(uint type,ulong imageBase,ulong pc,nint function,nint context,out nint handler,out ulong establisher,nint pointers);
    private static void VerifyEpilogueUnwind(nint thunk)
    {
        nint function=RtlLookupFunctionEntry((ulong)thunk,out ulong imageBase,0);
        uint end=(uint)Marshal.ReadInt32(function,4);
        byte[] bytes=new byte[checked((int)(imageBase+end-(ulong)thunk))];Marshal.Copy(thunk,bytes,0,bytes.Length);
        var decoder=Decoder.Create(64,new ByteArrayCodeReader(bytes));decoder.IP=(ulong)thunk;
        var ins=new List<Instruction>();while(decoder.IP<(ulong)thunk+(ulong)bytes.Length){decoder.Decode(out var i);ins.Add(i);}
        var add=ins.Last(i=>i.Mnemonic==Mnemonic.Add && i.Op0Register==Register.RSP);
        using var fake=new OwnedNativeMemory(131072);using var ctx=new OwnedNativeMemory(4096);
        long sp=(long)fake.Address+4096;
        // v2 saved pushes interleave volatile and nonvolatile registers.
        Register[] pops=ins.Where(i=>i.IP>add.IP&&i.Mnemonic==Mnemonic.Pop).Select(i=>i.Op0Register).ToArray();
        var indices=new Dictionary<Register,int>{{Register.RAX,0},{Register.RCX,1},{Register.RDX,2},{Register.RBX,3},{Register.RSP,4},{Register.RBP,5},{Register.RSI,6},{Register.RDI,7},{Register.R8,8},{Register.R9,9},{Register.R10,10},{Register.R11,11},{Register.R12,12},{Register.R13,13},{Register.R14,14},{Register.R15,15}};
        long allocation=add.Immediate32;long saved=sp+allocation;
        for(int k=0;k<pops.Length;k++)fake.Write(checked((int)(saved-(long)fake.Address+k*8)),BitConverter.GetBytes(0x12340000L+indices[pops[k]]));
        fake.Write(checked((int)(saved-(long)fake.Address+pops.Length*8)),BitConverter.GetBytes(0x1234567812345678L));
        ctx.Write(48,BitConverter.GetBytes(0x10000Bu));ctx.Write(152,BitConverter.GetBytes(sp));ctx.Write(248,BitConverter.GetBytes((ulong)add.IP));
        RtlVirtualUnwind(0,imageBase,add.IP,function,ctx.Address,out _,out _,0);
        Require((ulong)Marshal.ReadInt64(ctx.Address,248)==0x1234567812345678UL,"OS cannot unwind the bridge epilogue return");
        foreach(Register r in new[]{Register.RBX,Register.RBP,Register.RSI,Register.RDI,Register.R12,Register.R13,Register.R14,Register.R15})
            Require(Marshal.ReadInt64(ctx.Address,120+indices[r]*8)==0x12340000L+indices[r],"OS epilogue unwind lost "+r);
    }
    private static void Require(bool value,string message) { if(!value)throw new Exception(message); }
}
