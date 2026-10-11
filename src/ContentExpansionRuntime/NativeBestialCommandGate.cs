using System.Buffers.Binary;
namespace FFTModLoader.ContentExpansion.Runtime;
/// <summary>Keep all other commands on the native fast path, including AI scans.</summary>
public static class NativeBestialCommandGate
{
    public static byte[] Build(long original,long observer)=>BuildIds(original,observer,[227,228]);
    public static byte[] BuildIcon(long original,long observer)=>BuildIds(original,observer,[55]);
    private static byte[] BuildIds(long original,long observer,int[] ids)
    {
        var b=new List<byte>();
        void Emit(string s)=>b.AddRange(Convert.FromHexString(s.Replace(" ","")));
        Emit("50 51 52 41 50 41 51 41 52 41 53");
        var branches=new List<int>();
        foreach(int id in ids){Emit("81 FA");b.AddRange(BitConverter.GetBytes(id));Emit("0F 84");branches.Add(b.Count);b.AddRange(new byte[4]);}
        Emit("41 5B 41 5A 41 59 41 58 5A 59 58 FF 25 00 00 00 00");b.AddRange(BitConverter.GetBytes(original));
        int owned=b.Count;
        Emit("41 5B 41 5A 41 59 41 58 5A 59 58 FF 25 00 00 00 00");b.AddRange(BitConverter.GetBytes(observer));
        byte[] result=b.ToArray();
        foreach(int branch in branches)BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(branch),owned-branch-4);
        return result;
    }
}
