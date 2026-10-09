using FFTModLoader.ContentExpansion;
using System.Buffers.Binary;

internal static class NativeJpCostsTests
{
    internal static void Run()
    {
        const long image = 0x140000000;
        long start = image + NativeJpCosts.AbilityTableRva;
        byte[] table = Enumerable.Range(0, NativeJpCosts.NativeCount*8).Select(i=>(byte)(i*17+3)).ToArray();
        byte[] baseline = table.ToArray();
        NativeJpCost[] costs = [new(368,50), new(371,150), new(380,300), new(381,90)];
        byte[] Read(long address, int count) => table.AsSpan(checked((int)(address-start)),count).ToArray();
        void Write(long address, byte[] bytes) => bytes.CopyTo(table, checked((int)(address-start)));
        var plan = NativeJpCosts.Plan(image,costs,Read);
        NativeTableRelocation.Apply(plan,Read,Write);
        for (int id=0;id<512;id++)
        {
            var cost=costs.FirstOrDefault(c=>c.AbilityId==id);
            byte[] expected=baseline.AsSpan(id*8,8).ToArray();
            if (cost!=null) BinaryPrimitives.WriteUInt16LittleEndian(expected,cost.Cost);
            Check(table.AsSpan(id*8,8).SequenceEqual(expected),"JP-only writer changed flags, AI or another ability");
        }
        baseline.CopyTo(table,0);
        plan = NativeJpCosts.Plan(image,costs,Read);
        table[380*8+3]^=1; // Another mod changes type after planning.
        byte[] changed=table.ToArray();
        int writes=0;
        Reject(()=>NativeTableRelocation.Apply(plan,Read,(address,bytes)=>{writes++;Write(address,bytes);}));
        Check(writes==0&&table.SequenceEqual(changed),"JP preflight did not guard full live records");
        baseline.CopyTo(table,0);
        plan = NativeJpCosts.Plan(image,costs,Read);
        writes=0;
        try
        {
            NativeTableRelocation.Apply(plan,Read,(address,bytes)=>
            {
                writes++;
                if(writes==3) { Write(address,bytes[..1]); throw new IOException("Partial write"); }
                Write(address,bytes);
            });
            throw new Exception("Partial failure was accepted");
        }
        catch (IOException) { }
        Check(table.SequenceEqual(baseline),"JP partial failure did not restore all four native records");
        Reject(()=>NativeJpCosts.Plan(image,[new(512,70)],Read));
        Reject(()=>NativeJpCosts.Plan(image,[costs[0],costs[0]],Read));
        Reject(()=>NativeJpCosts.Plan(image,costs,(_,_)=>new byte[7]));
        Console.WriteLine("PASS: native JP-only plan changes exactly four ushort fields; all 512 flags/AI records preserved; preflight and partial rollback tested in simulated memory.");
    }
    private static void Check(bool ok,string message) { if(!ok)throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch(InvalidDataException) { return; }
        throw new Exception("Invalid JP-only plan was accepted");
    }
}
