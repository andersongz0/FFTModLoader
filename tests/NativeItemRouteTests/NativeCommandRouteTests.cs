using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using FFTModLoader.ContentExpansion.Runtime;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

static class NativeCommandRouteTests
{
    [Function(CallingConventions.Microsoft)]
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint Native(int command,int argument);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint Capture(nint target,int command,int argument,nint output);

    public static void Run(byte[] image,PEReader pe)
    {
        const int slotRva=0x275860,arrayRva=0x275980,scratchRva=0x3D1A1C0;
        // OWN image: functions plus packed command tables and global scratch.
        // No IAT calls; original functions call only each other in this fixture.
        using var fixture=new OwnedNativeMemory(0x3D1B000);
        Copy(slotRva,0x11E); Copy(arrayRva,0xF5);
        Copy(0x67E210,176*25); Copy(0x67F4C0,48*5); Copy(0x67F340,3*25);
        fixture.ExecutablePage(0x275000);
        using var state=new OwnedNativeMemory(4096);
        using var slots=new OwnedNativeMemory(4096);
        using var output=new OwnedNativeMemory(4096);
        using var caller=new OwnedNativeMemory(4096);
        using var slotRoute=new OwnedNativeMemory(4096);
        using var arrayRoute=new OwnedNativeMemory(4096);
        caller.Write(0,CaptureCaller()); caller.ExecutablePage();
        output.Write(320,Enumerable.Range(0,16).Select(i=>(byte)(0x11+i)).ToArray());
        var capture=Marshal.GetDelegateForFunctionPointer<Capture>(caller.Address);
        ushort[] actions=[368,371,380,381,513,514,515,516,517,518,519,520,521,522,523];
        ushort[] expanded=[..actions,0,441,474,475,480,509,0,0,0];
        slots.Write(0,Words(expanded));
        state.Write(NativeCommandCatalog.SlotsPointerOffset,BitConverter.GetBytes((long)slots.Address));
        state.Write(NativeCommandCatalog.AuditedOutputOffset,BitConverter.GetBytes((long)fixture.Address+scratchRva));

        var slotResults=new nint[256*24]; var slotSnapshots=new byte[256*24*320];
        var arraySnapshots=new byte[256*256*320]; var arrayLists=new byte[256*256*48];
        for(int cmd=0;cmd<256;cmd++)
        {
            for(int slot=0;slot<24;slot++)
            {
                int index=cmd*24+slot;
                slotResults[index]=capture(fixture.Address+slotRva,cmd,slot,output.Address);
                output.Read(0,320).CopyTo(slotSnapshots,index*320);
            }
            for(int flags=0;flags<256;flags++)
            {
                int index=cmd*256+flags;
                Require(capture(fixture.Address+arrayRva,cmd,flags,output.Address)==fixture.Address+scratchRva,"Native command scratch pointer differs");
                output.Read(0,320).CopyTo(arraySnapshots,index*320);
                fixture.Read(scratchRva,48).CopyTo(arrayLists,index*48);
            }
        }
        IHook<Native> slotHook,arrayHook;
        unsafe
        {
            slotHook=ReloadedHooks.Instance.CreateHook<Native>((void*)slotRoute.Address,(long)fixture.Address+slotRva);
            arrayHook=ReloadedHooks.Instance.CreateHook<Native>((void*)arrayRoute.Address,(long)fixture.Address+arrayRva);
        }
        slotRoute.Write(0,NativeCommandCatalog.BuildSlot((long)slotHook.OriginalFunctionAddress,(long)state.Address)); slotRoute.ExecutablePage();
        arrayRoute.Write(0,NativeCommandCatalog.BuildActionArray((long)arrayHook.OriginalFunctionAddress,(long)state.Address)); arrayRoute.ExecutablePage();
        slotHook.Activate(); arrayHook.Activate(); state.Write(0,[1]);
        try
        {
            byte[] stateBefore=state.Read(0,4096),slotsBefore=slots.Read(0,4096);
            for(int cmd=0;cmd<256;cmd++)
            {
                for(int slot=0;slot<24;slot++)
                {
                    int index=cmd*24+slot;
                    nint result=capture(fixture.Address+slotRva,cmd,slot,output.Address);
                    nint expected=cmd==6?expanded[slot]:slotResults[index];
                    Require(result==expected,$"Expanded command ID lost/truncated: {cmd}/{slot}");
                    byte[] expectedGp=slotSnapshots.AsSpan(index*320,320).ToArray(); Put(expectedGp,48,(long)expected);
                    Require(output.Read(0,320).SequenceEqual(expectedGp),$"Slot internal GP/SIMD contract changed: {cmd}/{slot}");
                }
                for(int flags=0;flags<256;flags++)
                {
                    int index=cmd*256+flags;
                    Require(capture(fixture.Address+arrayRva,cmd,flags,output.Address)==fixture.Address+scratchRva,"Array route changed original scratch pointer");
                    Require(output.Read(0,320).AsSpan().SequenceEqual(arraySnapshots.AsSpan(index*320,320)),$"Array GP/SIMD contract changed: {cmd}/{flags}");
                    byte[] expected=arrayLists.AsSpan(index*48,48).ToArray();
                    if(cmd==6)
                        for(int s=0;s<16;s++)BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(s*2),(flags&1)!=0?expanded[s]:(ushort)0);
                    Require(fixture.Read(scratchRva,48).SequenceEqual(expected),$"Action/passive filtering incorrect: {cmd}/{flags}");
                }
            }
            Require(state.Read(0,4096).SequenceEqual(stateBefore),"Command decoder mutated state/guard bytes");
            Require(slots.Read(0,4096).SequenceEqual(slotsBefore),"Command decoder mutated registered row/guard bytes");
            foreach(byte enabled in new byte[]{0,2,255})
            {
                state.Write(0,[enabled]); AssertOriginal();
            }
            state.Write(0,[1]); byte[] pointer=state.Read(8,8); state.Write(8,new byte[8]); AssertOriginal(); state.Write(8,pointer);
            // Wrong/null audited output refuses writes by ARRAY bridge only;
            // disable slot bridge so its original filled list is comparable.
            slotHook.Disable(); pointer=state.Read(16,8);
            foreach(long wrong in new[]{0L,(long)fixture.Address+scratchRva+2})
            {
                state.Write(16,BitConverter.GetBytes(wrong));
                Require(capture(fixture.Address+arrayRva,6,31,output.Address)==fixture.Address+scratchRva,"Unknown scratch changed return");
                Require(fixture.Read(scratchRva,48).AsSpan().SequenceEqual(arrayLists.AsSpan((6*256+31)*48,48)),"Array bridge wrote unknown scratch allocation");
            }
            state.Write(16,pointer); slotHook.Enable();
            foreach(int slot in new[]{-1,24,25,int.MaxValue})
            {
                slotHook.Disable(); nint expected=capture(fixture.Address+slotRva,6,slot,output.Address); byte[] snapshot=output.Read(0,320);
                slotHook.Enable(); Require(capture(fixture.Address+slotRva,6,slot,output.Address)==expected && output.Read(0,320).SequenceEqual(snapshot),"Out-of-range slot changed native fallback");
            }
            slotHook.Disable(); arrayHook.Disable(); AssertOriginal();
            slotHook.Enable(); arrayHook.Enable();
            Require(capture(fixture.Address+slotRva,6,4,output.Address)==513,"Re-enable lost Venom identity");
            Console.WriteLine("PASS: native expanded command slot/action-array decoders, actual hooks on original functions; 6144 slots + 65536 command/filter pairs plus baselines, all 256 filter bytes; fifteen full ushort actions, native passive behavior, exact original GP/XMM state, explicit slots 22/23, guard bytes and disabled/null/unknown-buffer fallback. OWN memory only; no learning confirmation, battle or save activation.");

            void AssertOriginal()
            {
                for(int s=0;s<24;s++)
                {
                    int index=6*24+s;
                    Require(capture(fixture.Address+slotRva,6,s,output.Address)==slotResults[index] && output.Read(0,320).AsSpan().SequenceEqual(slotSnapshots.AsSpan(index*320,320)),"Disabled/unregistered slot differs from native");
                }
                foreach(int flags in new[]{0,1,2,4,8,31,255})
                {
                    int index=6*256+flags;
                    Require(capture(fixture.Address+arrayRva,6,flags,output.Address)==fixture.Address+scratchRva,"Disabled array pointer changed");
                    Require(fixture.Read(scratchRva,48).AsSpan().SequenceEqual(arrayLists.AsSpan(index*48,48)),"Disabled array changed original slots");
                }
            }
        }
        finally
        {
            if(arrayHook.IsHookEnabled)arrayHook.Disable(); if(slotHook.IsHookEnabled)slotHook.Disable();
            GC.KeepAlive(slotHook);GC.KeepAlive(arrayHook);GC.KeepAlive(capture);
        }
        void Copy(int rva,int count)
        {
            var s=pe.PEHeaders.SectionHeaders.Single(s=>s.VirtualAddress<=rva && rva+count<=s.VirtualAddress+s.SizeOfRawData);
            fixture.Write(rva,image.AsSpan(s.PointerToRawData+rva-s.VirtualAddress,count).ToArray());
        }
    }
    internal static byte[] CaptureCaller()
    {
        var code=new List<byte>(); void E(string hex)=>code.AddRange(Convert.FromHexString(hex.Replace(" ","")));
        void Simd(byte op,int reg,bool stack,int offset)
        {
            code.Add(0xF3); if(reg>=8)code.Add(0x44);code.AddRange([0x0F,op,(byte)((stack?0x84:0x83)+reg%8*8)]);
            if(stack)code.Add(0x24);code.AddRange(BitConverter.GetBytes(offset));
        }
        E("53 48 81 EC C0 00 00 00 4C 89 CB 48 89 C8 89 D1 44 89 C2 41 B8 44 33 22 11 41 B9 55 44 33 22 41 BA 66 55 44 33 41 BB 77 66 55 44");
        for(int r=6;r<16;r++)Simd(0x7F,r,true,32+(r-6)*16);
        for(int r=0;r<16;r++)Simd(0x6F,r,false,320);
        E("FF D0 48 89 0B 48 89 53 08 4C 89 43 10 4C 89 4B 18 4C 89 53 20 4C 89 5B 28 48 89 43 30 48 C7 43 38 00 00 00 00");
        for(int r=0;r<16;r++)Simd(0x7F,r,false,64+r*16);
        for(int r=6;r<16;r++)Simd(0x6F,r,true,32+(r-6)*16);
        E("48 81 C4 C0 00 00 00 5B C3");return code.ToArray();
    }
    private static byte[] Words(ushort[] values)
    {byte[] result=new byte[values.Length*2];for(int i=0;i<values.Length;i++)BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(i*2),values[i]);return result;}
    private static void Put(byte[] b,int offset,long value)=>BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(offset),value);
    private static void Require(bool ok,string message){if(!ok)throw new Exception(message);}
}
