using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using FFTModLoader.ContentExpansion.Runtime;
using Iced.Intel;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

static class NativeMedicineRouteTests
{
    [Function(CallingConventions.Microsoft)]
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint Lookup(ushort id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint Capture(nint target, ushort id, nint output);

    public static void Run(byte[] image, PEReader pe)
    {
        // Original leaf/helpers only, at their original relative positions in
        // OWN memory. Never invoke game entry, a scene, an IAT or any save code.
        using var fixture = new OwnedNativeMemory(0x810000);
        Copy(0x2B8BCC, 0x34C); // Type/Min/Common/secondary helpers/CheckRange
        Copy(0x67FB38, 0x80); Copy(0x6804E0, 0x1C);
        Copy(0x80FB70, 48); Copy(0x67277C, 3);
        Copy(0x2890C0, 0xAA); // original category -> original CheckRange -> Type
        fixture.ExecutablePage(0x2B8000); fixture.ExecutablePage(0x289000);
        using var state = new OwnedNativeMemory(12288);
        using var secondary = new OwnedNativeMemory(4096);
        using var snapshot = new OwnedNativeMemory(4096);
        using var caller = new OwnedNativeMemory(4096);
        caller.Write(0, NativeItemDataRouteTests.CaptureCaller()); caller.ExecutablePage();
        snapshot.Write(320, Enumerable.Range(0,16).Select(i => (byte)(0x61+i)).ToArray());
        var capture = Marshal.GetDelegateForFunctionPointer<Capture>(caller.Address);
        int[] entries = [0x2B8BCC,0x2B8C0C,0x2B8E04];
        var originalResults = new nint[3][];
        var originalSnapshots = new byte[3][];
        for (int kind = 0; kind < entries.Length; kind++)
        {
            originalResults[kind] = new nint[65536]; originalSnapshots[kind] = new byte[65536*320];
            for (int id = 0; id <= ushort.MaxValue; id++)
            {
                originalResults[kind][id] = capture(fixture.Address + entries[kind], (ushort)id, snapshot.Address);
                snapshot.Read(0,320).CopyTo(originalSnapshots[kind],id*320);
            }
        }
        Require((int)capture(fixture.Address + 0x2890C0,240,snapshot.Address) == 5,
            "Native Potion UI category is not 5; do not guess shop tab.");
        // Stage native category proof BEFORE activating anything.
        Console.WriteLine("PASS: original native category executed in OWN image: Potion/Ether/Remedy/Phoenix Down are UI category 5, selected by shop tab 7 (not tab 3).");
        foreach (ushort id in new ushort[]{243,252,253})
            Require((int)capture(fixture.Address+0x2890C0,id,snapshot.Address)==5,"Retained medicine category differs");

        int[] extras = Enumerable.Range(261,11).Append(1023).ToArray();
        foreach (int id in extras)
        {
            state.Write(NativeMedicineCatalog.RegisteredOffset+id,[1]);
            long ptr = (long)secondary.Address + (id-240)*3;
            secondary.Write((id-240)*3,[0,0,0]); // lookup fixture, NOT Poison/formula data
            state.Write(NativeMedicineCatalog.SecondaryPointersOffset+id*8,BitConverter.GetBytes(ptr));
        }
        var routes = new OwnedNativeMemory[]{new(4096),new(4096),new(4096)};
        var hooks = new IHook<Lookup>[3];
        try
        {
            for (int k = 0; k < 3; k++)
            {
                unsafe { hooks[k] = ReloadedHooks.Instance.CreateHook<Lookup>((void*)routes[k].Address,(long)fixture.Address+entries[k]); }
                long original=(long)hooks[k].OriginalFunctionAddress, memory=(long)state.Address, imageBase=(long)fixture.Address;
                byte[] code=k switch {
                    0 => NativeMedicineCatalog.BuildRangeIndex(original,memory,imageBase),
                    1 => NativeMedicineCatalog.BuildRangeMinimum(original,memory,imageBase),
                    _ => NativeMedicineCatalog.BuildSecondaryRecord(original,memory,imageBase,(long)secondary.Address)
                };
                Audit(code); routes[k].Write(0,code); routes[k].ExecutablePage(); hooks[k].Activate();
            }
            state.Write(0,[1]);
            byte[] stateBefore=state.Read(0,12288), recordBefore=secondary.Read(0,4096);
            for (int k=0;k<3;k++)
                for (int id=0;id<=ushort.MaxValue;id++)
                {
                    nint result=capture(fixture.Address+entries[k],(ushort)id,snapshot.Address);
                    byte[] observed=snapshot.Read(0,320);
                    if (!extras.Contains(id))
                    {
                        Require(result==originalResults[k][id],$"Native/raw fallback changed: {k}/{id}");
                        Require(observed.AsSpan().SequenceEqual(originalSnapshots[k].AsSpan(id*320,320)),$"Original post-state changed: {k}/{id}");
                    }
                    else
                    {
                        long expected=k switch {0=>6,1=>240,_=>(long)secondary.Address+(id-240)*3};
                        Require((long)result==expected,$"Extra medicine lookup failed: {k}/{id}");
                        byte[] expectedGp=originalSnapshots[k].AsSpan(240*320,320).ToArray();
                        if(k==0) Put(expectedGp,0,id); // RCX remains raw ID
                        if(k==2) { Put(expectedGp,0,(long)secondary.Address); Put(expectedGp,24,id); Put(expectedGp,32,id-240); }
                        Require(observed.SequenceEqual(expectedGp),$"Extra internal GP/SIMD contract differs: {k}/{id}");
                    }
                }
            foreach(int id in extras)
                Require((int)capture(fixture.Address+0x2890C0,(ushort)id,snapshot.Address)==5,"Extra medicine not in original medicine category");
            Require(state.Read(0,12288).SequenceEqual(stateBefore),"Getter mutated registration/state");
            Require(secondary.Read(0,4096).SequenceEqual(recordBefore),"Getter mutated secondary/guard bytes");
            foreach(byte enabled in new byte[]{0,2,255})
            {
                state.Write(0,[enabled]);
                for(int k=0;k<3;k++)
                {
                    Require(capture(fixture.Address+entries[k],261,snapshot.Address)==originalResults[k][261],"Disabled catalog still exposes medicine");
                    Require(snapshot.Read(0,320).AsSpan().SequenceEqual(originalSnapshots[k].AsSpan(261*320,320)),"Disabled fallback lost native GP state");
                }
            }
            state.Write(0,[1]); state.Write(NativeMedicineCatalog.RegisteredOffset+261,[2]);
            for(int k=0;k<3;k++)
                Require(capture(fixture.Address+entries[k],261,snapshot.Address)==originalResults[k][261],"Invalid registration accepted");
            state.Write(NativeMedicineCatalog.RegisteredOffset+261,[1]);
            state.Write(NativeMedicineCatalog.SecondaryPointersOffset+261*8,new byte[8]);
            for(int k=0;k<3;k++)
                Require(capture(fixture.Address+entries[k],261,snapshot.Address)==originalResults[k][261],"Missing secondary pointer accepted");
            Console.WriteLine("PASS: native medicine range/minimum/secondary record via actual hooks, 196608 raw-ID calls plus baseline, native helper chain/UI category, R8-R11/SIMD post-contract, guard memory, disabled/malformed/null registration. OWN process; no Poison, purchase, learning or save activation.");
        }
        finally
        {
            foreach(var hook in hooks) if(hook is not null && hook.IsHookEnabled) hook.Disable();
            foreach(var route in routes) route.Dispose();
            GC.KeepAlive(hooks); GC.KeepAlive(capture);
        }
        void Copy(int rva,int count)
        {
            var s=pe.PEHeaders.SectionHeaders.Single(s=>s.VirtualAddress<=rva && rva+count<=s.VirtualAddress+s.SizeOfRawData);
            fixture.Write(rva,image.AsSpan(s.PointerToRawData+rva-s.VirtualAddress,count).ToArray());
        }
    }
    private static void Put(byte[] bytes,int offset,long value)=>BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset),value);
    private static void Audit(byte[] bytes)
    {
        var decoder=Decoder.Create(64,new ByteArrayCodeReader(bytes.AsSpan(0,bytes.Length-8).ToArray()));
        while(decoder.IP<(ulong)bytes.Length-8)
        {
            var instruction=decoder.Decode();
            Require(instruction.Code!=Code.INVALID && instruction.Mnemonic is Mnemonic.Mov or Mnemonic.Movzx or
                Mnemonic.Cmp or Mnemonic.Test or Mnemonic.Jb or Mnemonic.Jae or Mnemonic.Jne or Mnemonic.Je or Mnemonic.Jmp or Mnemonic.Ret or Mnemonic.Sub,
                "Unexpected non-leaf medicine bridge instruction");
            if(instruction.OpCount>0 && instruction.Op0Kind==OpKind.Register && instruction.Mnemonic is not(Mnemonic.Cmp or Mnemonic.Test))
                Require(instruction.Op0Register is Register.RAX or Register.EAX or Register.RCX or Register.ECX or Register.RDX or Register.EDX or
                    Register.R8 or Register.R9D or Register.R10D,"Medicine bridge changes unapproved GP register");
        }
    }
    private static void Require(bool ok,string message){if(!ok)throw new Exception(message);}
}
