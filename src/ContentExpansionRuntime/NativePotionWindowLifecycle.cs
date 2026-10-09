using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>Keep the native list task alive until its synchronous caller has
/// received the final potion choice. Never revive a killed task or change its
/// scheduler ownership fields. Target deferral requests native window completion;
/// all unrelated window/task creation and termination remains native.</summary>
public static class NativePotionWindowLifecycle
{
    // Active, deferred flag, task root, UI root, target index, target function,
    // target window, original list descriptor. Owned records, not game fields.
    public const int Slots=17,RecordSize=64;
    /// <summary>Veto empty child confirmation before native keyright writes
    /// the selected result or starts an error/target window. Hover remains native.
    /// The inline call uses live RDI=list descriptor and RBX=row index.</summary>
    public static byte[] BuildEmptyConfirmGuard(long image,long scopes)
    {
        List<byte> c=[];List<int> fallback=[];
        void Emit(string s)=>c.AddRange(Convert.FromHexString(s.Replace(" ","")));
        void Address(long p)=>c.AddRange(BitConverter.GetBytes(p));
        void Branch(byte op){c.AddRange([0x0F,op]);fallback.Add(c.Count);c.AddRange(new byte[4]);}
        Emit("50 41 52 41 53 49 BA");Address(image+0x22BADC);
        Emit("4C 39 54 24 18");Branch(0x85);
        Emit("49 BA");Address(image+0xD40948);
        Emit("41 8B 02 83 F8 01");Branch(0x82);Emit("83 F8 10");Branch(0x87);
        Emit("48 6B C0 40 49 BB");Address(scopes);
        Emit("49 01 C3 41 83 3B 02");Branch(0x85);
        Emit("49 BA");Address(image+0x783088);
        Emit("4D 8B 12 4D 39 53 08");Branch(0x85);
        Emit("49 BA");Address(image+0xD40950);
        Emit("4D 8B 12 4D 39 53 10");Branch(0x85);
        Emit("49 3B 7B 30");Branch(0x85);
        Emit("48 83 FB 06");Branch(0x87);
        Emit("49 BA");Address(image+0x184A9B0);
        Emit("4D 8B 12 4D 85 D2");Branch(0x84);
        Emit("41 0F B7 04 5A 2D F0 38 00 00 83 F8 0C");Branch(0x87);
        Emit("49 BA");Address(image+0x11A7C00+240);
        Emit("41 80 3C 02 00");Branch(0x85);
        Emit("41 5B 41 5A 58 31 C0 C3"); // unchanged selection/result/stock/task; no confirmation
        int at=c.Count;Emit("41 5B 41 5A 58 FF 25 00 00 00 00");Address(image+0x22B9F4);
        byte[] result=c.ToArray();
        foreach(int p in fallback)BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(p),at-p-4);
        return result;
    }
    public static byte[] Build(long image,long scopes)
    {
        List<byte> c=[];List<int> fallback=[];
        void Emit(string s)=>c.AddRange(Convert.FromHexString(s.Replace(" ","")));
        void Address(long p)=>c.AddRange(BitConverter.GetBytes(p));
        void Branch(byte condition){c.AddRange([0x0F,condition]);fallback.Add(c.Count);c.AddRange(new byte[4]);}
        Emit("49 BA");Address(image+0x231C6A);
        Emit("4C 39 14 24");Branch(0x85);
        Emit("49 BA");Address(image+0xD40948);
        Emit("41 8B 02 83 F8 10");Branch(0x87);
        Emit("48 6B C0 40 49 BB");Address(scopes);
        Emit("49 01 C3 41 83 3B 00");Branch(0x84);
        Emit("49 BA");Address(image+0x783088);
        Emit("49 8B 02 49 3B 43 08");Branch(0x85);
        Emit("49 BA");Address(image+0xD40950);
        Emit("49 8B 02 49 3B 43 10");Branch(0x85);
        Emit("C3"); // only this list's implicit kill is deferred
        int at=c.Count;Emit("FF 25 00 00 00 00");Address(image+0x2F7EC8);
        byte[] result=c.ToArray();
        foreach(int p in fallback)BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(p),at-p-4);
        return result;
    }

    /// <summary>The real confirm routine starts the target task BEFORE the
    /// list terminates. Defer only its Potion callsite, then release exactly
    /// one native target task after the final stocked variant is returned.
    /// R11 is a live target-window pointer across this original callsite.</summary>
    public static byte[] BuildTargetDeferral(long image,long scopes)
    {
        List<byte> c=[];List<int> fallback=[],ordinary=[];
        void Emit(string s)=>c.AddRange(Convert.FromHexString(s.Replace(" ","")));
        void Address(long p)=>c.AddRange(BitConverter.GetBytes(p));
        void Branch(List<int> list,byte condition){c.AddRange([0x0F,condition]);list.Add(c.Count);c.AddRange(new byte[4]);}
        Emit("49 BA");Address(image+0x22BB60);
        Emit("4C 39 14 24");Branch(fallback,0x85);
        Emit("49 BA");Address(image+0xD40948);
        Emit("41 8B 02 83 F8 01");Branch(fallback,0x82);
        Emit("83 F8 10");Branch(fallback,0x87);
        Emit("44 8D 50 FF 44 39 D1");Branch(fallback,0x85);
        Emit("48 6B C0 40 49 B9");Address(scopes);
        Emit("49 01 C1 41 83 39 00");Branch(fallback,0x84);
        Emit("49 BA");Address(image+0x783088);
        Emit("49 8B 02 49 3B 41 08");Branch(fallback,0x85);
        Emit("49 BA");Address(image+0xD40950);
        Emit("49 8B 02 49 3B 41 10");Branch(fallback,0x85);
        // Enhanced starts validation window36 (1024 -> 24), not the legacy
        // target window0. Error/help windows must never be deferred.
        List<int> target=[];
        Emit("4C 39 D8");Branch(target,0x84);
        Emit("4C 8D 90 60 0C 00 00 4D 39 D3");Branch(fallback,0x85);
        int descriptor=c.Count;
        Emit("49 3B 79 30");Branch(fallback,0x85);
        Emit("0F BF 47 50 83 F8 4F");Branch(fallback,0x87);
        Emit("49 BA");Address(image+0x184A9B0);
        Emit("4D 8B 12 4D 85 D2");Branch(fallback,0x84);
        Emit("41 0F B7 04 42 2D 70 71 00 00 83 F8 0C");
        List<int> medicine=[];Branch(medicine,0x86);
        Branch(ordinary,0x87);
        int defer=c.Count;
        Emit("41 89 49 18 49 89 51 20 4D 89 59 28 41 C7 41 04 01 00 00 00");
        // A normal target task closes its parent after confirming the map.
        // Here finish this list now, with the native window completion flag,
        // so the post-observer can choose a variant before starting that task.
        Emit("44 8D 51 01 49 C1 E2 0B 4D 03 51 08 49 C7 42 10 01 00 00 00 C3");
        int pass=c.Count;Emit("41 C7 41 04 00 00 00 00");
        int native=c.Count;Emit("FF 25 00 00 00 00");Address(image+0x2F7DE4);
        byte[] result=c.ToArray();
        foreach(var pair in new[]{(target,descriptor),(medicine,defer),(ordinary,pass),(fallback,native)})
            foreach(int p in pair.Item1)BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(p),pair.Item2-p-4);
        return result;
    }
}
