using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FFTModLoader.ContentExpansion;

// Read-only source executable, isolated test process. No injection, game launch,
// hooks, saves or writes to the installation. No game code is stored in outputs.
if(args.Length!=1 || !OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture!=Architecture.X64)
    throw new ArgumentException("Pass the supported FFT_enhanced.exe path to this Windows x64 fixture.");
byte[] executable=File.ReadAllBytes(args[0]);
Check(Convert.ToHexString(SHA256.HashData(executable))=="937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5","Unsupported executable");
using var pe=new PEReader(new MemoryStream(executable));
byte[] ReadImage(int rva,int length)
{
    var section=pe.PEHeaders.SectionHeaders.Single(s=>rva>=s.VirtualAddress && (long)rva+length<=s.VirtualAddress+s.SizeOfRawData);
    return executable.AsSpan(section.PointerToRawData+rva-section.VirtualAddress,length).ToArray();
}
byte[] helper=ReadImage(0x276EBC,510);
// Ghidra instruction audit: contiguous function, no calls, no RIP/external
// references. Refuse even one changed byte before making an executable copy.
Check(Convert.ToHexString(SHA256.HashData(helper))=="E6FB5B81CC7DE02CDF657A86A568238CBF06F6F3AA6F267258B4E2182584B33A","Native stat helper differs");
byte[] panther=ReadImage(0x785E30+103*49,49);
Check(panther[15]==116 && panther[17]==50 && panther[19]==116 && panther[21]==98 && panther[23]==91,"Wrong Panther row");
using var code=new Owned(4096);code.Write(helper);code.Executable();
using var buffer=new Owned(4096);
var native=Marshal.GetDelegateForFunctionPointer<NativeStats>(code.Address);
var random=new Random(704175);
for(int test=0;test<10000;test++)
{
    var human=new byte[512];random.NextBytes(human);
    human[0]=(byte)(0x80+test%2);human[6]=(byte)(test%2==0?0:0x40);
    if(test%3==0)human[6]|=4; // audited special resource-cap/divisor rule
    human[2]=2;human[3]=174;
    foreach(int at in new[]{0x14,0x16,0x18})Word(human,at,0);
    if(test%5==0)Word(human,0x16,484); // native Max HP Boost
    var calculated=MonsterFormAttributes.Calculate(human,panther);
    var reference=calculated.ToArray();
    // Replace only calculator outputs with poison sentinels. Native helper
    // consumes the identical raw bases/multipliers/passive flags, then must
    // independently recover every result. Mode2 avoids growth/refills.
    Word(reference,0x32,0xA5A5);Word(reference,0x36,0xA5A5);
    reference[0x38]=reference[0x39]=reference[0x3A]=0xA5;
    buffer.Write(reference);native(buffer.Address,2,0);
    byte[] actual=buffer.Read(512);
    foreach(int at in new[]{0x32,0x33,0x36,0x37,0x38,0x39,0x3A})
        Check(actual[at]==calculated[at],$"Native comparison differs at test {test}, offset {at:X}");
    foreach(int at in new[]{0x30,0x31,0x34,0x35,0x61,0x66,0x41,0x4F,0x50})
        Check(actual[at]==human[at],"Native reference fixture unexpectedly changed battle state");
}
Console.WriteLine("PASS: 10,000 isolated comparisons with the audited native stat helper and original Panther multipliers. Percentage tests are separate. No game process, installation or save was modified.");
static void Word(byte[] bytes,int at,int value)=>BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at),(ushort)value);
static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
[UnmanagedFunctionPointer(CallingConvention.Winapi)]delegate ulong NativeStats(nint worker,uint mode,nint extra);
sealed class Owned:IDisposable
{
    public nint Address{get;private set;}
    private readonly int _length;
    public Owned(int length){_length=length;Address=VirtualAlloc(0,(nuint)length,0x3000,4);if(Address==0)throw new Exception("Allocation failed");}
    public void Write(byte[] data){if(data.Length>_length)throw new Exception("Fixture overflow");Marshal.Copy(data,0,Address,data.Length);}
    public byte[] Read(int length){if(length>_length)throw new Exception("Fixture overflow");var bytes=new byte[length];Marshal.Copy(Address,bytes,0,length);return bytes;}
    public void Executable(){if(!VirtualProtect(Address,(nuint)_length,0x20,out _)||!FlushInstructionCache(GetCurrentProcess(),Address,(nuint)_length))throw new Exception("RX allocation failed");}
    public void Dispose(){if(Address!=0){VirtualFree(Address,0,0x8000);Address=0;}}
    [DllImport("kernel32.dll")]static extern nint VirtualAlloc(nint a,nuint size,uint type,uint protection);
    [DllImport("kernel32.dll")]static extern bool VirtualProtect(nint a,nuint size,uint p,out uint old);
    [DllImport("kernel32.dll")]static extern bool VirtualFree(nint a,nuint s,uint type);
    [DllImport("kernel32.dll")]static extern bool FlushInstructionCache(nint process,nint a,nuint size);
    [DllImport("kernel32.dll")]static extern nint GetCurrentProcess();
}
