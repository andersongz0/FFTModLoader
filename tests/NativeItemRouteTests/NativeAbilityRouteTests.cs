using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using FFTModLoader.ContentExpansion.Runtime;
using Iced.Intel;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

static class NativeAbilityRouteTests
{
    [Function(CallingConventions.Microsoft)]
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Lookup(uint id, nint commonOut, nint secondaryOut);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Capture(nint target, uint id, nint snapshot);

    public static void Run(byte[] image, PEReader pe)
    {
        // Supported original getters and records at original relative offsets
        // in OWN allocation. Set its override-loaded byte; never run the NXD
        // initialization routine, game imports, game entry or saves.
        using var fixture = new OwnedNativeMemory(0xD41000);
        Copy(0x275A78,0x10A); Copy(0x2BB060,0x3F);
        Copy(0x787F80,512*8); Copy(0x78B2E0,368*20);
        fixture.Write(0xD407B7,[1]);
        fixture.ExecutablePage(0x275000); fixture.ExecutablePage(0x2BB000);
        using var state = new OwnedNativeMemory(20480);
        using var records = new OwnedNativeMemory(4096);
        using var output = new OwnedNativeMemory(4096);
        using var caller = new OwnedNativeMemory(4096);
        caller.Write(0,CaptureCaller()); caller.ExecutablePage();
        byte[] seed=Enumerable.Range(0,16).Select(i=>(byte)(0x31+i)).ToArray(); output.Write(352,seed);
        var capture=Marshal.GetDelegateForFunctionPointer<Capture>(caller.Address);
        int[] entries=[0x275A78,0x2BB060];
        var resultBefore=new int[2][]; var snapshotBefore=new byte[2][];
        for(int kind=0;kind<2;kind++)
        {
            resultBefore[kind]=new int[65536]; snapshotBefore[kind]=new byte[65536*352];
            for(uint id=0;id<=ushort.MaxValue;id++)
            {
                resultBefore[kind][id]=capture(fixture.Address+entries[kind],id,output.Address);
                output.Read(0,352).CopyTo(snapshotBefore[kind],id*352);
            }
        }
        int[] extras=new[] { 368, 371, 380, 381 }.Concat(Enumerable.Range(513,11)).Append(1023).ToArray();
        for(int index=0;index<extras.Length;index++)
        {
            int id=extras[index];
            // Distinct fixture bytes, not claimed as actual formula/JP data.
            records.Write(index*32,Enumerable.Range(0,28).Select(i=>(byte)(index*7+i+1)).ToArray());
            state.Write(NativeAbilityCatalog.RegisteredOffset+id,[1]);
            state.Write(NativeAbilityCatalog.CommonPointersOffset+id*8,BitConverter.GetBytes((long)records.Address+index*32));
            state.Write(NativeAbilityCatalog.ActionPointersOffset+id*8,BitConverter.GetBytes((long)records.Address+index*32+8));
        }
        var routes=new OwnedNativeMemory[]{new(4096),new(4096)};
        var hooks=new IHook<Lookup>[2];
        try
        {
            for(int kind=0;kind<2;kind++)
            {
                unsafe { hooks[kind]=ReloadedHooks.Instance.CreateHook<Lookup>((void*)routes[kind].Address,(long)fixture.Address+entries[kind]); }
                byte[] code=kind==0 ? NativeAbilityCatalog.BuildAddress((long)hooks[kind].OriginalFunctionAddress,(long)state.Address)
                    : NativeAbilityCatalog.BuildSecondary((long)hooks[kind].OriginalFunctionAddress,(long)state.Address);
                Audit(code); routes[kind].Write(0,code); routes[kind].ExecutablePage(); hooks[kind].Activate();
            }
            state.Write(0,[1]); byte[] stateBefore=state.Read(0,20480),recordBefore=records.Read(0,4096);
            for(int kind=0;kind<2;kind++)
                for(uint raw=0;raw<=ushort.MaxValue;raw++)
                {
                    int result=capture(fixture.Address+entries[kind],raw,output.Address);
                    byte[] observed=output.Read(0,352);
                    int id=(int)(raw&1023), flags=(int)(raw&~1023u);
                    bool registered=kind==0 ? extras.Contains(id) && flags is 0 or 0x1000 or 0x4000 or 0x6000 : extras.Contains((int)raw);
                    if(!registered)
                    {
                        Require(result==resultBefore[kind][raw],$"Native ability result changed: {kind}/{raw}");
                        Require(observed.AsSpan().SequenceEqual(snapshotBefore[kind].AsSpan((int)raw*352,352)), $"Original ability post-state changed: {kind}/{raw}");
                    }
                    else
                    {
                        int index=Array.IndexOf(extras,id); long common=(long)records.Address+index*32,secondary=common+8;
                        byte[] expected=snapshotBefore[kind].AsSpan(1*352,352).ToArray();
                        Put(expected,0,id*5);
                        if(kind==0)
                        {
                            Put(expected,24,id); Put(expected,48,common); Put(expected,56,secondary);
                            Require(result==0,"New action must use kind 0, NOT one-byte item kind 1");
                        }
                        else
                        {
                            Require(result==unchecked((int)secondary),"Raw secondary getter returned wrong action record");
                            Put(expected,64,secondary); // full RAX snapshot, not truncated managed int return
                        }
                        Require(observed.SequenceEqual(expected), $"Extra ability GP/SIMD/outputs differ: {kind}/{raw}");
                    }
                }
            Require(state.Read(0,20480).SequenceEqual(stateBefore),"Lookup changed ability registration/guard bytes");
            Require(records.Read(0,4096).SequenceEqual(recordBefore),"Lookup changed common/action records or guard bytes");
            foreach(byte enabled in new byte[]{0,2,255})
            {
                state.Write(0,[enabled]); AssertFallback(513);
            }
            state.Write(0,[1]); state.Write(NativeAbilityCatalog.RegisteredOffset+513,[2]); AssertFallback(513);
            state.Write(NativeAbilityCatalog.RegisteredOffset+513,[1]);
            foreach(int offset in new[]{NativeAbilityCatalog.CommonPointersOffset,NativeAbilityCatalog.ActionPointersOffset})
            {
                byte[] pointer=state.Read(offset+513*8,8); state.Write(offset+513*8,new byte[8]);
                AssertFallback(513); state.Write(offset+513*8,pointer);
            }
            // High uint bits must NOT alias an extra record (native fallback
            // retains its existing normalization/invalid-ID semantics).
            foreach(uint invalid in new[]{0x10000201u,0x80000201u,uint.MaxValue})
                for(int k=0;k<2;k++)
                {
                    hooks[k].Disable(); int expectedResult=capture(fixture.Address+entries[k],invalid,output.Address);
                    byte[] expected=output.Read(0,352); hooks[k].Enable();
                    Require(capture(fixture.Address+entries[k],invalid,output.Address)==expectedResult && output.Read(0,352).SequenceEqual(expected),"High-bit entry aliased extra identity");
                }
            // Even a malformed host registering the native special ID must not
            // take ownership of 512. Verify both outputs and private registers.
            state.Write(NativeAbilityCatalog.RegisteredOffset+512,[1]);
            state.Write(NativeAbilityCatalog.CommonPointersOffset+512*8,BitConverter.GetBytes((long)records.Address));
            state.Write(NativeAbilityCatalog.ActionPointersOffset+512*8,BitConverter.GetBytes((long)records.Address+8));
            AssertFallback(512);
            Console.WriteLine("PASS: registered retained and expanded native ability getters, actual Reloaded hooks and copied original functions; 131072 ushort calls plus baseline, audited UI flags, raw helper IDs, malicious registration of reserved ID 512 refused, high-bit/no-pointer/disabled fallback, GP and XMM0-15 preserved. Kind=0 full action record, NOT byte item transport; learning/combat/save NOT activated.");
            void AssertFallback(uint id)
            {
                for(int k=0;k<2;k++)
                {
                    Require(capture(fixture.Address+entries[k],id,output.Address)==resultBefore[k][id],"Unbound/invalid ability registration accepted");
                    Require(output.Read(0,352).AsSpan().SequenceEqual(snapshotBefore[k].AsSpan((int)id*352,352)),"Fallback changed native outputs/registers");
                }
            }
        }
        finally
        {
            foreach(var hook in hooks) if(hook is not null && hook.IsHookEnabled) hook.Disable();
            foreach(var route in routes) route.Dispose(); GC.KeepAlive(hooks); GC.KeepAlive(capture);
        }
        void Copy(int rva,int count)
        {
            var s=pe.PEHeaders.SectionHeaders.Single(s=>s.VirtualAddress<=rva && rva+count<=s.VirtualAddress+s.SizeOfRawData);
            fixture.Write(rva,image.AsSpan(s.PointerToRawData+rva-s.VirtualAddress,count).ToArray());
        }
    }

    private static byte[] CaptureCaller()
    {
        var c=new List<byte>();
        void E(string hex)=>c.AddRange(Convert.FromHexString(hex.Replace(" ","")));
        void Simd(byte op,int reg,bool stack,int offset)
        {
            c.Add(0xF3); if(reg>=8)c.Add(0x44);
            c.AddRange([0x0F,op,(byte)((stack?0x84:0x83)+reg%8*8)]); if(stack)c.Add(0x24);
            c.AddRange(BitConverter.GetBytes(offset));
        }
        E("53 48 81 EC C0 00 00 00 4C 89 C3 48 89 C8 89 D1 48 8D 53 30 4C 8D 43 38 41 B9 55 44 33 22 41 BA 66 55 44 33 41 BB 77 66 55 44");
        E("48 C7 43 30 5A 5A 5A 5A 48 C7 43 38 6B 6B 6B 6B");
        for(int reg=6;reg<16;reg++)Simd(0x7F,reg,true,32+(reg-6)*16);
        for(int reg=0;reg<16;reg++)Simd(0x6F,reg,false,352);
        E("FF D0 48 89 0B 48 89 53 08 4C 89 43 10 4C 89 4B 18 4C 89 53 20 4C 89 5B 28 48 89 43 40");
        for(int reg=0;reg<16;reg++)Simd(0x7F,reg,false,96+reg*16);
        // Clear the unused snapshot gap so comparisons are deterministic.
        E("48 C7 43 48 00 00 00 00 48 C7 43 50 00 00 00 00 48 C7 43 58 00 00 00 00");
        for(int reg=6;reg<16;reg++)Simd(0x6F,reg,true,32+(reg-6)*16);
        E("48 81 C4 C0 00 00 00 5B C3"); return c.ToArray();
    }
    private static void Audit(byte[] code)
    {
        var d=Decoder.Create(64,new ByteArrayCodeReader(code.AsSpan(0,code.Length-8).ToArray()));
        while(d.IP<(ulong)code.Length-8)
        {
            var i=d.Decode();
            Require(i.Code!=Code.INVALID && i.Mnemonic is Mnemonic.Mov or Mnemonic.And or Mnemonic.Cmp or Mnemonic.Test or
                Mnemonic.Jb or Mnemonic.Jae or Mnemonic.Jne or Mnemonic.Je or Mnemonic.Jmp or Mnemonic.Ret or Mnemonic.Lea or Mnemonic.Xor,
                "Non-leaf/unapproved ability route instruction");
            if(i.OpCount>0 && i.Op0Kind==OpKind.Register && i.Mnemonic is not(Mnemonic.Cmp or Mnemonic.Test))
                Require(i.Op0Register is Register.RAX or Register.EAX or Register.RCX or Register.R9D,"Ability route changes preserved GP register");
        }
    }
    private static void Put(byte[] bytes,int offset,long value)=>BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset),value);
    private static void Require(bool ok,string message){if(!ok)throw new Exception(message);}
}
