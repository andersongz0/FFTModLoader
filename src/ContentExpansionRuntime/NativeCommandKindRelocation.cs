using FFTModLoader.ContentExpansion;

namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>All decoded direct and propagated command-kind references in the supported image.</summary>
internal static class NativeCommandKindRelocation
{
    internal static List<GuardedNativeWrite> Plan(long image,long table)
    {
        foreach(var(rva,hex) in new (int,string)[]{
            (0x207193,"803C080A"),
            (0x207199,"40383408"),
            (0x20CADA,"803C0800"),
            (0x20CAE0,"803C0806"),
            (0x2177D0,"418A140A"),
            (0x281C53,"0FB61C08"),
            (0x28229D,"408A3C08"),
            (0x28291F,"408A3408"),
            (0x282C78,"0FB61C08"),
            (0x309913,"0FB60C08"),
            (0x30B1D0,"8A1C08"),
            (0x30B6A4,"803C0808"),
            (0x30EC92,"41803C0706")
        })
            if(!CheckedNativeRead.Read(image+rva,hex.Length/2).SequenceEqual(Convert.FromHexString(hex)))
                throw new InvalidDataException("Command-kind pointer provenance changed.");
        return NativeTableRelocation.Plan(image,0x67E010,table,256,new NativeTableOperand[]{
            new(0x20718C,Convert.FromHexString("488D0D7D6E4700"),3,0,NativeOperandEncoding.RipRelative),
            new(0x20CACC,Convert.FromHexString("488D0D3D154700"),3,0,NativeOperandEncoding.RipRelative),
            new(0x2177C9,Convert.FromHexString("488D0D40684600"),3,0,NativeOperandEncoding.RipRelative),
            new(0x237504,Convert.FromHexString("4280BC3810E067000A"),4,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x238C34,Convert.FromHexString("80BC2810E0670003"),3,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x27FE88,Convert.FromHexString("0FB68C1010E06700"),4,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x280050,Convert.FromHexString("420FB68C0010E06700"),5,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x2801E4,Convert.FromHexString("428ABC1810E06700"),4,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x281516,Convert.FromHexString("420FB68C0810E06700"),5,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x281C40,Convert.FromHexString("488D0DC9C33F00"),3,0,NativeOperandEncoding.RipRelative),
            new(0x281FC2,Convert.FromHexString("4280BC1810E0670007"),4,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x28228C,Convert.FromHexString("488D0D7DBD3F00"),3,0,NativeOperandEncoding.RipRelative),
            new(0x282911,Convert.FromHexString("488D0DF8B63F00"),3,0,NativeOperandEncoding.RipRelative),
            new(0x282C71,Convert.FromHexString("488D0D98B33F00"),3,0,NativeOperandEncoding.RipRelative),
            new(0x2EB47E,Convert.FromHexString("4280BC2810E067000A"),4,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x30990C,Convert.FromHexString("488D0DFD463700"),3,0,NativeOperandEncoding.RipRelative),
            new(0x309C2E,Convert.FromHexString("428AAC2510E06700"),4,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x30B1B1,Convert.FromHexString("488D0D582E3700"),3,0,NativeOperandEncoding.RipRelative),
            new(0x30B69D,Convert.FromHexString("488D0D6C293700"),3,0,NativeOperandEncoding.RipRelative),
            new(0x30D24D,Convert.FromHexString("420FB69C2010E06700"),5,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x30E0F9,Convert.FromHexString("448A841A10E06700"),4,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x30E138,Convert.FromHexString("8A841910E06700"),3,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x30EC87,Convert.FromHexString("488D0582F33600"),3,0,NativeOperandEncoding.RipRelative),
            new(0x316D6B,Convert.FromHexString("468AA40610E06700"),4,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x31FEE3,Convert.FromHexString("8A8C0810E06700"),3,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x320222,Convert.FromHexString("44388C2810E06700"),4,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x320576,Convert.FromHexString("420FB6AC2010E06700"),5,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x320CAE,Convert.FromHexString("80BC0810E067000A"),3,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x388532,Convert.FromHexString("80BC3810E0670003"),3,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x38893A,Convert.FromHexString("4280BC3010E0670004"),4,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x389792,Convert.FromHexString("80BC1810E0670004"),3,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x38A1DE,Convert.FromHexString("4280BC3810E0670001"),4,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x38AA0D,Convert.FromHexString("4280BC3010E0670002"),4,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x38B673,Convert.FromHexString("80BC0810E067000A"),3,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x38BF72,Convert.FromHexString("4280BC2110E0670003"),4,0,NativeOperandEncoding.ImageRelativeIndexed),
            new(0x38D839,Convert.FromHexString("4280BC3810E067000A"),4,0,NativeOperandEncoding.ImageRelativeIndexed)
        },CheckedNativeRead.Read).ToList();
    }
}
