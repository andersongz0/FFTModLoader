namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>Version-locked native branch, not a managed per-frame observer.</summary>
public static class NativeConsumableVisuals
{
    // Exact 4bpp UV/CLUT locations in the independently audited blank atlas
    // regions. The eleven private palettes are uploaded with item_01 itself;
    // no original palette, item record, action ID or GPU hook is replaced.
    public static uint BottleCoordinates(int index)
    {
        if(index is <0 or >10)throw new ArgumentOutOfRangeException(nameof(index));
        int clut=((277+index/3)<<6)|(56+index%3);
        return (uint)(240|((21+16*index)<<8)|(clut<<16));
    }
    public static byte[] BuildBottleUv(long original,long image,long coordinates)
    {
        var c=new Code();
        c.Emit("0F B7 C2 2D 05 01 00 00 83 F8 0A");c.Branch(0x87,"fallback");
        c.Emit("49 89 CA 49 BB");c.Address(coordinates);
        // 260CF0 adds width to U in an EIGHT-bit corner. U240+16
        // wraps to zero before 41AC00 and selects a 512px-wide crop.
        // Column15 is verified transparent in every approved glyph. Use
        // width15, retaining every opaque pixel and ending at U255.
        c.Emit("41 8B 04 83 41 89 42 14 41 C7 42 18 0F 00 10 00 C1 E8 10");
        // Native getter's post-call RCX is image base, RAX is the CLUT.
        c.Emit("48 B9");c.Address(image);c.Emit("C3");
        c.Label("fallback");c.Jump(original);return c.Finish();
    }
    // Enhanced drawItem uses palette bank 14 and a separate 512x512 4bpp
    // source. 41AC00 doubles the PSX UV/crop; changing only item_01.tex
    // never adds these pixels to that source. Fill the private crops after
    // its real decompression, only into the bank's own allocated buffer.
    public static byte[] BuildEnhancedBottleSource(long original,long image,long glyphs)
    {
        var c=new Code();
        c.Emit("81 FA 9E 00 00 00");c.Branch(0x85,"fallback");
        c.Emit("49 BA");c.Address(image+0x2EEE830+0xA88);
        c.Emit("4D 39 02");c.Branch(0x85,"fallback");
        c.Emit("53 56 57 48 83 EC 20 4C 89 C3 48 B8");c.Address(original);
        c.Emit("FF D0 48 89 C7 83 F8 FF");c.Branch(0x84,"return");
        c.Emit("48 BE");c.Address(glyphs);
        for(int i=0;i<11;i++)
        {
            // U=240,V=21+16*i becomes x=480,y=42+32*i. Two
            // duplicated pixels per source pixel preserve sharp pixel art.
            c.Emit("4C 8D 93");c.Address32((42+32*i)*256+240);
            c.Emit("B9 20 00 00 00");c.Label("row"+i);
            c.Emit("F3 0F 6F 06 F3 41 0F 7F 02 48 83 C6 10 49 81 C2 00 01 00 00 FF C9");
            c.Branch(0x85,"row"+i);
        }
        c.Label("return");c.Emit("48 89 F8 48 83 C4 20 5F 5E 5B C3");
        c.Label("fallback");c.Jump(original);return c.Finish();
    }
    public static byte[] EnhancedGlyph(byte[] glyph)
    {
        if(glyph.Length!=128)throw new InvalidDataException("Expected 16x16 4bpp bottle.");
        byte[] result=new byte[512];
        for(int y=0;y<32;y++)for(int x=0;x<16;x++)
        {
            int pixel=x,packed=glyph[(y/2)*8+pixel/2];
            int index=(packed>>((pixel&1)*4))&15;
            result[y*16+x]=(byte)(index|(index<<4));
        }
        return result;
    }
    // FillUnitStatusWindow reconstructs its legacy bubble name from a byte
    // plus 0x100 (2151D1..2151E5), so 513..523 become 257..267. Only that
    // lookup may use the preview packet (2333E8 stores the full ushort menu
    // action BEFORE the timeline's byte encoding), or the displayed animation.
    // Both require command6, the same displayed worker and matching legacy ID.
    // RBP belongs to
    // FillUnitStatusWindow (searchAnimationByBattleID), NOT global Tmp_UA,
    // which AI/target evaluation reuses. Never alter that packet or the queue.
    // Native Braver/Choco names elsewhere stay intact. Diagnostics write only
    // into owned storage and are read by the existing throw observer.
    public static byte[] BuildMapBubbleLookup(long original,long image,long diagnostic)
    {
        var c=new Code();
        c.Emit("49 BA");c.Address(image+0x2151EA);
        c.Emit("4C 39 14 24");c.Branch(0x84,"caller");
        // The execution/casting caption has a second name lookup in the
        // same function. Both routes retain RBP/R15; unrelated callers do not.
        c.Emit("49 BA");c.Address(image+0x215263);
        c.Emit("4C 39 14 24");c.Branch(0x85,"fallback");
        c.Label("caller");
        c.Emit("49 BB");c.Address(diagnostic);
        c.Emit("41 89 0B 41 89 4B 04 49 89 6B 08 41 C7 43 10 00 00 00 00 41 C7 43 14 00 00 00 00 41 C7 43 18 00 00 00 00 41 FF 43 1C");
        c.Emit("49 BA");c.Address(image+0xC6B1CC);
        c.Emit("41 8B 02 83 E8 17 A9 FD FF FF FF");c.Branch(0x85,"animation");
        c.Emit("49 BA");c.Address(image+0x2FD35A4);
        c.Emit("41 80 7A 01 06");c.Branch(0x85,"animation");
        c.Emit("41 0F B6 02 83 F8 14");c.Branch(0x87,"animation");
        c.Emit("C1 E0 09 49 BA");c.Address(image+0x1853CE0);
        c.Emit("49 01 C2 4D 39 D7");c.Branch(0x85,"animation");
        c.Emit("49 BA");c.Address(image+0x2FD35A4);
        c.Emit("41 0F B7 42 02 2D 01 02 00 00 83 F8 0A");c.Branch(0x87,"animation");
        c.Emit("44 8D 90 01 01 00 00 44 39 D1");c.Branch(0x85,"animation");
        c.Emit("8D 88 01 02 00 00 41 89 4B 10 41 C7 43 14 06 00 00 00 41 C7 43 18 02 00 00 00");c.Branch(0x84,"resolved");
        c.Label("animation");
        c.Emit("48 85 ED");c.Branch(0x84,"fallback");
        c.Emit("0F B6 85 79 01 00 00 41 89 43 14 83 F8 06");c.Branch(0x85,"fallback");
        c.Emit("0F B7 85 42 01 00 00 41 89 43 10 2D 01 02 00 00 83 F8 0A");c.Branch(0x87,"fallback");
        c.Emit("44 8D 90 01 01 00 00 44 39 D1");c.Branch(0x85,"fallback");
        c.Emit("8D 88 01 02 00 00 41 C7 43 18 01 00 00 00");
        c.Label("resolved");c.Emit("41 89 4B 04");
        c.Label("fallback");c.Jump(original);return c.Finish();
    }
    // DrawUnitStatusIconGrid reads animation+178's consumable item as an
    // ability name (7000+item), despite animation+142 retaining the real
    // action. Correct the CENTRAL text setter, only for an exact registered
    // item/action/command association on the displayed native animation.
    // In particular, actual monster ability 265 remains Choco Beak.
    public static byte[] BuildActionCaption(long original,long image)
    {
        var c=new Code();
        c.Emit("8D 82 FB 8E FF FF 83 F8 0A");c.Branch(0x87,"fallback"); // EDX-7105
        c.Emit("44 8D 90 05 01 00 00 41 50 41 51 49 BB");c.Address(image+0xD3A410);
        c.Emit("4D 8B 1B 49 B8");c.Address(image+0x7DCF9A);
        c.Emit("45 0F B7 00 41 B9 80 00 00 00");
        c.Label("loop");c.Emit("4D 85 DB");c.Branch(0x84,"restore");
        c.Emit("49 8B 83 48 01 00 00 48 85 C0");c.Branch(0x84,"next");
        c.Emit("0F B6 80 BC 01 00 00 66 44 39 C0");c.Branch(0x84,"match");
        c.Label("next");c.Emit("4D 8B 1B 41 FF C9");c.Branch(0x85,"loop");c.Branch(0x84,"restore");
        c.Label("match");
        c.Emit("66 45 39 93 50 01 00 00");c.Branch(0x85,"restore");
        c.Emit("41 80 BB 79 01 00 00 06");c.Branch(0x85,"restore");
        c.Emit("41 0F B7 83 42 01 00 00 2D FC 00 00 00 44 39 D0");c.Branch(0x85,"restore");
        c.Emit("05 FC 70 00 00 89 C2"); // full-width 7000+actual ability
        c.Label("restore");c.Emit("41 59 41 58");
        c.Label("fallback");c.Jump(original);return c.Finish();
    }
    // Enter at 1FADFF with EAX=full-width action ID. RBX, RDI, RSP and
    // the actual action/item identities are never changed. The displaced
    // MOV ECX,17E is replayed for every non-owned action.
    public static byte[] BuildProjectileSelector(long image)
    {
        List<byte> code=[];
        Dictionary<string,int> labels=[];List<(int Offset,string Label)> branches=[];
        void Emit(string hex)=>code.AddRange(Convert.FromHexString(hex.Replace(" ","")));
        void Branch(byte condition,string label){code.AddRange([0x0F,condition]);branches.Add((code.Count,label));code.AddRange(new byte[4]);}
        void Address(long address)=>code.AddRange(BitConverter.GetBytes(address));
        Emit("8D 88 FF FD FF FF 83 F9 0A");Branch(0x86,"owned");
        // The Potion family now uses the same custom range/target path as
        // flasks, so its original getweaponeffect preparation is bypassed too.
        Emit("8D 88 90 FE FF FF 83 F9 0C");Branch(0x87,"fallback"); //368..380, three medicine groups
        labels["owned"]=code.Count;
        // Expanded target-map construction bypasses getweaponeffect, which
        // normally prepares the global start/end vectors and flight duration.
        // Rebuild those through the REAL native CheckGunResult at commitment,
        // using worker IDs, never the animation IDs or equipped weapon.
        Emit("48 8B 83 48 01 00 00 0F B6 88 BC 01 00 00 44 0F B6 83 AA 01 00 00 44 39 C1");
        Branch(0x84,"instant"); // self-use has no flight
        Emit("48 83 EC 20 31 D2 48 B8");Address(image+0x312ED0);
        Emit("FF D0 48 83 C4 20");
        // A coincident pair must complete its impact, not enqueue a zero-
        // length projectile. Do not touch the division or unrelated items.
        Emit("48 B8");Address(image+0x186D778);
        Emit("8B 08 3B 48 08");Branch(0x85,"flight");
        Emit("0F B7 48 04 66 3B 48 0C");Branch(0x84,"instant");
        labels["flight"]=code.Count;
        Emit("B9 10 00 00 00");
        Jump(code,image+0x1FAE30);
        labels["instant"]=code.Count;
        Emit("48 B8");Address(image+0x18732C8);Emit("83 38 01");Branch(0x85,"done");
        Emit("C7 00 02 00 00 00");
        labels["done"]=code.Count;Jump(code,image+0x1FAE3A);
        labels["fallback"]=code.Count;
        code.AddRange([0xB9,0x7E,1,0,0]);
        Jump(code,image+0x1FAE04);
        byte[] result=code.ToArray();
        foreach(var (offset,label) in branches)
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset),labels[label]-offset-4);
        return result;
    }
    private static void Jump(List<byte> code,long address)
    {code.AddRange([0xFF,0x25,0,0,0,0]);code.AddRange(BitConverter.GetBytes(address));}
    private sealed class Code
    {
        private readonly List<byte> _bytes=[];
        private readonly Dictionary<string,int> _labels=[];
        private readonly List<(int Offset,string Label)> _branches=[];
        public void Emit(string hex)=>_bytes.AddRange(Convert.FromHexString(hex.Replace(" ","")));
        public void Address(long a)=>_bytes.AddRange(BitConverter.GetBytes(a));
        public void Address32(int a)=>_bytes.AddRange(BitConverter.GetBytes(a));
        public void Label(string name)=>_labels.Add(name,_bytes.Count);
        public void Branch(byte condition,string label)
        {_bytes.AddRange([0x0F,condition]);_branches.Add((_bytes.Count,label));_bytes.AddRange(new byte[4]);}
        public void Jump(long a){Emit("FF 25 00 00 00 00");Address(a);}
        public byte[] Finish()
        {
            byte[] result=_bytes.ToArray();
            foreach(var (offset,label) in _branches)
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset),_labels[label]-offset-4);
            return result;
        }
    }
}
