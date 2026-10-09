using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using FFTModLoader.ContentExpansion.Runtime;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

static class NativeActionPurchaseRouteTests
{
    [Function(CallingConventions.Microsoft)]
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Native(int ability,int job);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint Capture(nint target,int ability,int job,nint output);
    public static void Run(byte[] image,PEReader pe)
    {
        const int purchase=0x2B9AD0,partyTable=0x1800F50,selected=0x1811428;
        using var fixture=new OwnedNativeMemory(0x1820000);
        Copy(purchase,0x5F);Copy(0x2B8F18,0x33);
        // Original outer confirmation and local-job decoder are native.
        // Learning refresh, NEX JP cost and sound are explicit test stubs;
        // these are NOT evidence for production notifications/persistence.
        fixture.Write(0x28936C,Convert.FromHexString("C3"));
        fixture.Write(0x2CC0AC,Convert.FromHexString("B846000000C3"));
        fixture.Write(0xF4BCC,Convert.FromHexString("B881000000C3"));
        foreach(int page in new[]{0x2B9000,0x2B8000,0x289000,0x2CC000,0xF4000})fixture.ExecutablePage(page);
        using var party=new OwnedNativeMemory(4096);
        fixture.Write(partyTable,BitConverter.GetBytes((long)party.Address));
        using var state=new OwnedNativeMemory(4096);
        using var bits=new OwnedNativeMemory(4096);
        using var costs=new OwnedNativeMemory(4096);
        using var snapshot=new OwnedNativeMemory(4096);
        using var caller=new OwnedNativeMemory(4096);
        using var route=new OwnedNativeMemory(4096);
        caller.Write(0,NativeCommandRouteTests.CaptureCaller());caller.ExecutablePage();
        snapshot.Write(320,Enumerable.Range(0,16).Select(i=>(byte)(0x71+i)).ToArray());
        var capture=Marshal.GetDelegateForFunctionPointer<Capture>(caller.Address);
        ushort[] ids=[368,371,380,381,513,514,515,516,517,518,519,520,521,522,523];
        ushort[] jp=[50,150,300,90,70,150,250,250,250,700,700,700,1000,1000,1000];
        costs.Write(0,Words(jp));
        IHook<Native> hook;
        unsafe{hook=ReloadedHooks.Instance.CreateHook<Native>((void*)route.Address,(long)fixture.Address+purchase);}
        route.Write(0,NativeActionPurchaseRoute.Build((long)hook.OriginalFunctionAddress,(long)state.Address,(long)fixture.Address));route.ExecutablePage();hook.Activate();
        int calls=0;
        try
        {
            for(int slot=0;slot<15;slot++)
                foreach(int available in new[]{0,jp[slot]-1,jp[slot],jp[slot]+1,30000})
                    foreach(uint learned in new uint[]{0,1u<<(slot+1)%15,1u<<slot,0x7FFF})
                    {
                        Publish(available,learned,[0,0,0xDA]);
                        bool success=available>=jp[slot]&&(learned&(1u<<slot))==0;
                        AssertHandled(slot,success);
                        AssertHandled(slot,false); // repeat before frame refresh
                    }
            for(int slot=0;slot<4;slot++)
            {
                int old=new[]{0,3,12,13}[slot];byte[] nativeBits=[0,0,0xDA];nativeBits[old/8]|=(byte)(0x80>>(old%8));
                Publish(3000,0,nativeBits);AssertHandled(slot,false);
            }
            foreach(int offset in new[]{2,8,16,20,24,32,40,48})
            {
                Publish(3000,0,[0,0,0xDA]);
                int size=offset is 8 or 24 or 40?8:offset is 2 or 16?2:4;
                state.Write(offset,offset is 2 or 32 or 48?BitConverter.GetBytes(1).AsSpan(0,size).ToArray():new byte[size]);
                AssertHandled(4,false);
            }
            foreach(int offset in new[]{0x7E,0x80,0xCC})
            {Publish(3000,0,[0,0,0xDA]);party.Write(offset,[(byte)(party.Read(offset,1)[0]^1)]);AssertHandled(4,false);}
            Publish(3000,0,[0,0,0xDA]);fixture.Write(selected,BitConverter.GetBytes((ushort)1));AssertHandled(4,false);
            fixture.Write(selected,new byte[2]);
            Publish(65535,0,[0,0,0xDA]);AssertHandled(4,false);
            Publish(3000,0xFF000000,[0,0,0xDA]);AssertHandled(4,false);
            // No hidden normalizer: flagged IDs, 512 and unrelated jobs use
            // original confirmation and its single native JP subtraction.
            foreach((int ability,int job) in new[]{(512,75),(513|0x1000,75),(513,74),(367,75),(524,75)})
                CompareOriginal(ability,job);
            foreach(byte enabled in new byte[]{0,2,255})
            {Publish(3000,0,[0,0,0xDA]);state.Write(0,[enabled]);CompareOriginal(513,75);}
            Console.WriteLine($"PASS: whole native action-purchase adapter, {calls} transaction/baseline calls, all fifteen costs/IDs, one JP subtraction, old retained bit identities 0/3/12/13 preserved, duplicate/pending/stale-unit/JP/bits/null rejection, no other party writes and exact GP/XMM preservation; disabled/unrelated inputs execute original outer confirmation. OWN memory; notifications/NEX helpers are fixtures, save host absent, NOT installable.");
        }
        finally{if(hook.IsHookEnabled)hook.Disable();GC.KeepAlive(hook);GC.KeepAlive(capture);}

        void Publish(int available,uint learned,byte[] nativeBits)
        {
            party.Write(0x7E,nativeBits);party.Write(0xCA,BitConverter.GetBytes((ushort)available));party.Write(0xCC,BitConverter.GetBytes((ushort)available));
            bits.Write(0,BitConverter.GetBytes(learned));state.Write(0,[1,0,0,0]);
            state.Write(8,BitConverter.GetBytes((long)party.Address));state.Write(16,BitConverter.GetBytes((ushort)available));
            state.Write(20,[..nativeBits,0]);state.Write(24,BitConverter.GetBytes((long)bits.Address));state.Write(32,BitConverter.GetBytes(learned));
            state.Write(40,BitConverter.GetBytes((long)costs.Address));state.Write(48,new byte[4]);
        }
        void AssertHandled(int slot,bool success)
        {
            byte[] partyExpected=party.Read(0,4096),bitsExpected=bits.Read(0,4096),stateExpected=state.Read(0,4096),costsBefore=costs.Read(0,4096);
            if(success)
            {
                ushort available=BinaryPrimitives.ReadUInt16LittleEndian(partyExpected.AsSpan(0xCC));
                BinaryPrimitives.WriteUInt16LittleEndian(partyExpected.AsSpan(0xCC),(ushort)(available-jp[slot]));
                uint learned=BinaryPrimitives.ReadUInt32LittleEndian(bitsExpected);BinaryPrimitives.WriteUInt32LittleEndian(bitsExpected,learned|(1u<<slot));
                if(slot<4){int old=new[]{0,3,12,13}[slot];partyExpected[0x7E+old/8]|=(byte)(0x80>>(old%8));}
                BinaryPrimitives.WriteInt32LittleEndian(stateExpected.AsSpan(48),1);
            }
            int result=(int)capture(fixture.Address+purchase,ids[slot],75,snapshot.Address);
            Require(result==(success?1:0),$"Purchase result incorrect {slot}/{success}: {result}");
            long[] gp=[ids[slot],75,0x11223344,0x22334455,0x33445566,0x44556677,result,0];
            Require(snapshot.Read(0,64).SequenceEqual(gp.SelectMany(BitConverter.GetBytes)),"Purchase GP input contract changed");
            Require(snapshot.Read(64,256).SequenceEqual(Enumerable.Range(0,16).SelectMany(_=>snapshot.Read(320,16))),"Purchase SIMD changed");
            Require(party.Read(0,4096).SequenceEqual(partyExpected),$"JP double charge or wrong native bit/guard {slot}");
            Require(bits.Read(0,4096).SequenceEqual(bitsExpected)&&state.Read(0,4096).SequenceEqual(stateExpected)&&costs.Read(0,4096).SequenceEqual(costsBefore),"Expanded learning/state/guard mutation differs");calls++;
        }
        void CompareOriginal(int ability,int job)
        {
            byte[] partyBefore=party.Read(0,4096),stateBefore=state.Read(0,4096),bitsBefore=bits.Read(0,4096);
            hook.Disable();nint expected=capture(fixture.Address+purchase,ability,job,snapshot.Address);byte[] gp=snapshot.Read(0,320),after=party.Read(0,4096);
            party.Write(0,partyBefore);hook.Enable();nint actual=capture(fixture.Address+purchase,ability,job,snapshot.Address);
            Require(actual==expected&&snapshot.Read(0,320).SequenceEqual(gp)&&party.Read(0,4096).SequenceEqual(after),"Original purchase fallback/JP forwarding differs");
            Require(state.Read(0,4096).SequenceEqual(stateBefore)&&bits.Read(0,4096).SequenceEqual(bitsBefore),"Fallback mutated expanded state");calls++;
        }
        void Copy(int rva,int count)
        {
            var s=pe.PEHeaders.SectionHeaders.Single(s=>s.VirtualAddress<=rva&&rva+count<=s.VirtualAddress+s.SizeOfRawData);
            fixture.Write(rva,image.AsSpan(s.PointerToRawData+rva-s.VirtualAddress,count).ToArray());
        }
    }
    private static byte[] Words(ushort[] values)
    {byte[] result=new byte[values.Length*2];for(int i=0;i<values.Length;i++)BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(i*2),values[i]);return result;}
    private static void Require(bool ok,string message){if(!ok)throw new Exception(message);}
}
