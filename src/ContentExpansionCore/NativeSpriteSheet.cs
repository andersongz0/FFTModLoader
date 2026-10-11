namespace FFTModLoader.ContentExpansion;

/// <summary>Bounded SPR defrost before sprite-slot allocation or live form mutation.</summary>
public static class NativeSpriteSheet
{
    public const int RawPrefix=0x9200;
    public const int TailBytes=0x6400;
    public const int UnpackedBytes=RawPrefix+TailBytes;
    public static byte[] Unpack(ReadOnlySpan<byte> packed)
    {
        if(packed.Length is <=RawPrefix or >45056)throw new InvalidDataException("Invalid packed SPR length.");
        byte[] result=new byte[UnpackedBytes];packed[..RawPrefix].CopyTo(result);
        var stream=packed[RawPrefix..];int cursor=0,output=0;
        while(output<TailBytes*2)
        {
            int value=Nibble(stream,ref cursor);
            if(value!=0){Put(result,output++,value);continue;}
            int count=Nibble(stream,ref cursor);
            if(count==0)count=Nibble(stream,ref cursor);
            else if(count==7)count=Nibble(stream,ref cursor)|(Nibble(stream,ref cursor)<<4);
            else if(count==8)count=Nibble(stream,ref cursor)|(Nibble(stream,ref cursor)<<4)|(Nibble(stream,ref cursor)<<8);
            if(count<=0 || count>TailBytes*2-output)throw new InvalidDataException("Invalid SPR zero run.");
            output+=count; // fresh zero-filled owned output, never a native destination
        }
        return result;
    }
    private static int Nibble(ReadOnlySpan<byte> stream,ref int cursor)
    {
        if(cursor>=stream.Length*2)throw new InvalidDataException("Truncated SPR stream.");
        int value=(cursor&1)==0?stream[cursor/2]>>4:stream[cursor/2]&15;cursor++;return value;
    }
    private static void Put(byte[] output,int nibble,int value)
    {
        int at=RawPrefix+nibble/2;
        if((nibble&1)==0)output[at]=(byte)(value<<4);else output[at]|=(byte)value;
    }
}
