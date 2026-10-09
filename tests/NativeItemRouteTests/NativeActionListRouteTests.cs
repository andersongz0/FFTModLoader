using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using FFTModLoader.ContentExpansion;
using FFTModLoader.ContentExpansion.Runtime;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

static class NativeActionListRouteTests
{
    [Function(CallingConventions.Microsoft)]
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Native(int unit,int job,int category,nint output,int mode);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Capture(nint target,nint arguments,nint snapshot);

    public static void Run(byte[] image,PEReader pe)
    {
        const int listRva=0x2867BC, outputRva=0x375F830, partyRva=0x1800F50;
        using var fixture=new OwnedNativeMemory(0x3D1B000);
        Copy(listRva,0x2BB); Copy(0x275860,0x11E); Copy(0x275980,0xF5);
        Copy(0x2B8F18,0x33); Copy(0x3609F0,0x82);
        Copy(0x785E30,174*49); Copy(0x787F80,512*8);
        Copy(0x67E210,176*25); Copy(0x67F4C0,48*5); Copy(0x67F340,3*25);
        // NEX/UI helpers are explicit FIXTURES, not evidence of native NEX,
        // command list or purchase integration. The list, local-job mapping,
        // command decoding and actual MSB-first bitstream are original code.
        fixture.Write(0x2CBED4,Convert.FromHexString("31C0C3"));
        fixture.Write(0x2CC0AC,Convert.FromHexString("B846000000C3"));
        fixture.Write(0x2866E4,Convert.FromHexString("66C702FFFFB801000000C3"));
        foreach(int page in new[]{0x275000,0x286000,0x2B8000,0x2CB000,0x2CC000,0x360000})fixture.ExecutablePage(page);
        using var party=new OwnedNativeMemory(4096);
        for(int u=0;u<3;u++)fixture.Write(partyRva+u*8,BitConverter.GetBytes((long)party.Address+u*512));
        using var state=new OwnedNativeMemory(4096);
        using var lists=new OwnedNativeMemory(4096);
        using var args=new OwnedNativeMemory(4096);
        using var snapshot=new OwnedNativeMemory(4096);
        using var caller=new OwnedNativeMemory(4096);
        using var route=new OwnedNativeMemory(4096);
        caller.Write(0,CaptureCaller()); caller.ExecutablePage();
        snapshot.Write(320,Enumerable.Range(0,16).Select(i=>(byte)(0x31+i)).ToArray());
        var capture=Marshal.GetDelegateForFunctionPointer<Capture>(caller.Address);
        ushort[] ids=[368,371,380,381,513,514,515,516,517,518,519,520,521,522,523];
        ushort[] costs=[50,150,300,90,70,150,250,250,250,700,700,700,1000,1000,1000];
        var menu=new NativeActionMenu(ids.Select((id,i)=>new MenuAction("action-"+id,id,costs[i])));
        IHook<Native> hook;
        unsafe{hook=ReloadedHooks.Instance.CreateHook<Native>((void*)route.Address,(long)fixture.Address+listRva);}
        route.Write(0,NativeActionListRoute.Build((long)hook.OriginalFunctionAddress,(long)state.Address,(long)fixture.Address)); route.ExecutablePage(); hook.Activate();
        int calls=0;
        try
        {
            foreach(int mode in new[]{0,2,3})
                for(uint learned=0;learned<32768;learned++)
                {
                    int jp=costs[learned%15]+(int)(learned%3)-1;
                    byte[] nativeBits=[(byte)(learned*3),(byte)(learned>>5),0xA5];
                    party.Write(0x7E,nativeBits);party.Write(0xCC,BitConverter.GetBytes((ushort)jp));
                    Publish(learned,jp,nativeBits);
                    Compare(0,75,0,mode,true);
                }
            Publish(0x15,70,party.Read(0x7E,3));
            party.Write(0xCC,BitConverter.GetBytes((ushort)70));
            foreach(int unit in new[]{0,1,2})
                foreach(int job in new[]{74,75,76,93})
                    foreach(int cat in new[]{0,1,2,3,15})
                        foreach(int mode in new[]{0,1,2,3,4,255})
                            Compare(unit,job,cat,mode,unit==0&&job==75&&cat==0&&mode is 0 or 2 or 3);
            foreach(int offset in new[]{0,2,8,16,24,32,36,40,44,48,56,60})
            {
                byte[] saved=state.Read(offset,offset is 8 or 16 or 24 or 48?8:4);
                state.Write(offset,offset==0?[2,0,0,0]:offset==2?[1,0,0,0]:offset is 32 or 36 or 40?[255,0,0,0]:new byte[saved.Length]);
                foreach(int mode in new[]{0,2,3})
                    Compare(0,75,0,mode,(offset is 8 or 44)&&mode==3);
                state.Write(offset,saved);
            }
            foreach(int mutation in new[]{0x7E,0x80,0xCC})
            {
                byte[] saved=party.Read(mutation,1);party.Write(mutation,[(byte)(saved[0]^1)]);
                foreach(int mode in new[]{0,2,3})Compare(0,75,0,mode,false);
                party.Write(mutation,saved);
            }
            foreach(int mode in new[]{0,2})Compare(0,75,0,mode,false,outputRva+128);
            byte[] stateBefore=state.Read(0,4096),listsBefore=lists.Read(0,4096),partyBefore=party.Read(0,4096);
            Compare(0,75,0,3,true,0);
            Require(state.Read(0,4096).SequenceEqual(stateBefore)&&lists.Read(0,4096).SequenceEqual(listsBefore)&&party.Read(0,4096).SequenceEqual(partyBefore),"Read-only menu mutated state/party/guard bytes");
            // Execute both real command bridges AND the real original list
            // together: its SECOND numeric filter must still discard IDs
            // above 421, and only the bound action-list route may restore them.
            using var commandState=new OwnedNativeMemory(4096);
            using var commandRow=new OwnedNativeMemory(4096);
            using var slotRoute=new OwnedNativeMemory(4096);
            using var arrayRoute=new OwnedNativeMemory(4096);
            commandRow.Write(0,Words([..ids,0,441,474,475,480,509,0,0,0]));
            commandState.Write(0,[1]);commandState.Write(8,BitConverter.GetBytes((long)commandRow.Address));
            commandState.Write(16,BitConverter.GetBytes((long)fixture.Address+0x3D1A1C0));
            IHook<Command> slotHook,arrayHook;
            unsafe
            {
                slotHook=ReloadedHooks.Instance.CreateHook<Command>((void*)slotRoute.Address,(long)fixture.Address+0x275860);
                arrayHook=ReloadedHooks.Instance.CreateHook<Command>((void*)arrayRoute.Address,(long)fixture.Address+0x275980);
            }
            slotRoute.Write(0,NativeCommandCatalog.BuildSlot((long)slotHook.OriginalFunctionAddress,(long)commandState.Address));slotRoute.ExecutablePage();
            arrayRoute.Write(0,NativeCommandCatalog.BuildActionArray((long)arrayHook.OriginalFunctionAddress,(long)commandState.Address));arrayRoute.ExecutablePage();
            slotHook.Activate();arrayHook.Activate();
            try
            {
                foreach(uint learned in new uint[]{0,1,8,16,0x1555,0x2AAA,0x7FFF})
                    foreach(int available in new[]{0,49,50,69,70,89,90,149,150,249,250,299,300,699,700,999,1000})
                    {
                        party.Write(0xCC,BitConverter.GetBytes((ushort)available));
                        Publish(learned,available,party.Read(0x7E,3));
                        foreach(int mode in new[]{0,2,3})Compare(0,75,0,mode,true);
                    }
            }
            finally{arrayHook.Disable();slotHook.Disable();GC.KeepAlive(arrayHook);GC.KeepAlive(slotHook);}
            Console.WriteLine($"PASS: native GetAbilityList postprocessor, actual hook on original list/bitstream/command code; {calls} paired baseline calls, all 32768 learned patterns in modes 0/2/3, JP boundaries, exact original GP/XMM and side effects, 15 actions/full IDs/flags, native passives and mode1 preserved, stale JP/bits/unit/buffer/capacity fallback. OWN memory only; NEX/command-list helpers are fixtures; no purchase, save or battle activation.");
        }
        finally{if(hook.IsHookEnabled)hook.Disable();GC.KeepAlive(hook);GC.KeepAlive(capture);}

        void Publish(uint learned,int jp,byte[] bits)
        {
            var all=new ushort[17];var learn=new ushort[17];
            int ac=menu.Build(ActionMenuView.All,learned,jp,all),lc=menu.Build(ActionMenuView.Learn,learned,jp,learn);
            int uc=menu.Build(ActionMenuView.UnlearnedCount,learned,jp,Span<ushort>.Empty);
            lists.Write(0,Words(all));lists.Write(64,Words(learn));
            state.Write(0,[1,0,0,0]);state.Write(8,BitConverter.GetBytes((long)fixture.Address+outputRva));
            state.Write(16,BitConverter.GetBytes((long)lists.Address));state.Write(24,BitConverter.GetBytes((long)lists.Address+64));
            state.Write(32,BitConverter.GetBytes(ac));state.Write(36,BitConverter.GetBytes(lc));state.Write(40,BitConverter.GetBytes(uc));
            state.Write(44,BitConverter.GetBytes(24));state.Write(48,BitConverter.GetBytes((long)party.Address));
            state.Write(56,[bits[0],bits[1],bits[2],0]);state.Write(60,BitConverter.GetBytes((ushort)jp));
        }
        void Compare(int unit,int job,int category,int mode,bool replace,int outRva=outputRva)
        {
            args.Write(0,BitConverter.GetBytes(unit));args.Write(4,BitConverter.GetBytes(job));args.Write(8,BitConverter.GetBytes(category));
            args.Write(16,BitConverter.GetBytes(outRva==0?0L:(long)fixture.Address+outRva));args.Write(24,BitConverter.GetBytes(mode));
            byte[] seed=Enumerable.Repeat((byte)0xCD,96).ToArray(); if(outRva!=0)fixture.Write(outRva,seed);
            hook.Disable();int native=capture(fixture.Address+listRva,args.Address,snapshot.Address);
            byte[] gp=snapshot.Read(0,320),outputBefore=outRva==0?[]:fixture.Read(outRva,96);
            byte[] effects=Effects();if(outRva!=0)fixture.Write(outRva,seed);
            hook.Enable();int actual=capture(fixture.Address+listRva,args.Address,snapshot.Address);
            int expected=native;byte[] outputExpected=outputBefore;
            if(replace)
            {
                expected=BinaryPrimitives.ReadInt32LittleEndian(state.Read(mode==3?40:mode==2?36:32,4));
                BinaryPrimitives.WriteInt64LittleEndian(gp.AsSpan(48),expected);
                if(mode!=3)lists.Read(mode==2?64:0,(expected+1)*2).CopyTo(outputExpected,0);
            }
            Require(actual==expected,$"List count mismatch {unit}/{job}/{category}/{mode}: {actual} != {expected}");
            Require(snapshot.Read(0,320).SequenceEqual(gp),$"List GP/SIMD changed {unit}/{job}/{category}/{mode}");
            if(outRva!=0)Require(fixture.Read(outRva,96).SequenceEqual(outputExpected),$"List content/guard mismatch {unit}/{job}/{category}/{mode}");
            Require(Effects().SequenceEqual(effects),"Original list bitstream/cache side effects changed"); calls++;
        }
        byte[] Effects()=>[..fixture.Read(0x3C49BF0,8),..fixture.Read(0x906AA8,4),..fixture.Read(0x2FF3CA6,1),..fixture.Read(0x3D1A1C0,48)];
        void Copy(int rva,int count)
        {
            var s=pe.PEHeaders.SectionHeaders.Single(s=>s.VirtualAddress<=rva&&rva+count<=s.VirtualAddress+s.SizeOfRawData);
            fixture.Write(rva,image.AsSpan(s.PointerToRawData+rva-s.VirtualAddress,count).ToArray());
        }
    }
    [Function(CallingConventions.Microsoft)]
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint Command(int command,int argument);
    private static byte[] CaptureCaller()
    {
        var c=new List<byte>();void E(string hex)=>c.AddRange(Convert.FromHexString(hex.Replace(" ","")));
        void Simd(byte op,int reg,bool stack,int offset)
        {
            c.Add(0xF3);if(reg>=8)c.Add(0x44);c.AddRange([0x0F,op,(byte)((stack?0x84:0x83)+reg%8*8)]);
            if(stack)c.Add(0x24);c.AddRange(BitConverter.GetBytes(offset));
        }
        E("53 48 81 EC D0 00 00 00 4C 89 C3 48 89 C8 49 89 D1");
        for(int r=6;r<16;r++)Simd(0x7F,r,true,48+(r-6)*16);
        for(int r=0;r<16;r++)Simd(0x6F,r,false,320);
        E("41 8B 49 18 89 4C 24 20 41 8B 09 41 8B 51 04 45 8B 41 08 4D 8B 49 10 41 BA 66 55 44 33 41 BB 77 66 55 44 FF D0");
        E("48 89 0B 48 89 53 08 4C 89 43 10 4C 89 4B 18 4C 89 53 20 4C 89 5B 28 48 89 43 30 48 C7 43 38 00 00 00 00");
        for(int r=0;r<16;r++)Simd(0x7F,r,false,64+r*16);
        for(int r=6;r<16;r++)Simd(0x6F,r,true,48+(r-6)*16);
        E("48 81 C4 D0 00 00 00 5B C3");return c.ToArray();
    }
    private static byte[] Words(ushort[] values)
    {byte[] result=new byte[values.Length*2];for(int i=0;i<values.Length;i++)BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(i*2),values[i]);return result;}
    private static void Require(bool ok,string message){if(!ok)throw new Exception(message);}
}
