namespace FFTModLoader.ContentExpansion.Runtime;

public sealed unsafe partial class BestialPlayableHost
{
    private void Gain(long worker,int jp,byte modified)
    {
        byte[] data=Read(worker,512);
        if(jp<0||data[1]==255||(data[0x61]&0x60)!=0||(data[0x62]&1)!=0||(data[6]&0x20)!=0||(data[0x1EE]&0x30)!=modified)return;
        Progress().Gain(UnitKey(data[2]),jp);
    }
    private void InstallProgressGain()
    {
        foreach(int rva in new[]{0x2175F8,0x2332D8,0x3D325C,0x3D3AE8,0x3D2FD8,0x278AD8})Helper(rva);
        Bind(0x28338C,0,f=>
        {
            if(f->Phase!=0)return;long worker=Worker((int)f->Arguments[0]);byte[] data=Read(worker,512);
            if(data[3] is not (174 or 175))return;f->Skip(-1);long output=f->Arguments[1];Int(output,0);
            if(_blocked||Call(0x2175F8,0)!=0||I32(_image+0x186AFF8)!=0||(data[0x61]&0x60)!=0||(data[0x62]&0x81)!=0)return;
            if(Read(_image+0x7B078B,1)[0]==0){f->Result=0;return;}
            int experience=Read(_image+0x7B078A,1)[0],oldLevel=data[0x29];
            Write(worker+0x28,[(byte)Math.Min(255,data[0x28]+experience)]);
            int levelChanged=(int)Call(0x278AD8,worker);Write(output,[(byte)experience]);
            if(levelChanged!=0||Read(_image+0x7B07B6,1)[0]!=0)Write(output+2,Read(worker+0x29,1));
            if((data[6]&32)==0)
            {
                string key=UnitKey(data[2]);int before=Progress().Level(key),baseJp=experience==0?0:(oldLevel>>2)+8+before*2;
                // Same faction/event restriction and quarter spillover as the
                // native route, but never into Squire's packed JP/learning cell.
                for(int i=0;i<21;i++)if(i!=data[1]&&Read(_image+0x1853CE0+i*512+1,1)[0]!=255)
                    Gain(_image+0x1853CE0+i*512,baseJp>>2,(byte)(data[5]&0x30));
                int jp=baseJp*StoryJpMultiplier()/100;
                if((data[0x99]&64)!=0)jp=jp*3/2;
                else if((BitConverter.ToUInt32(data,0x1F8)&0x800)!=0)jp=(int)(jp*BitConverter.ToSingle(Read(_image+0x722E0C,4)));
                Gain(worker,jp,(byte)(data[0x1EE]&0x30));Write(output+1,[(byte)Math.Min(255,jp)]);
                if(Progress().Level(key)!=before)Write(output+3,[(byte)Progress().Level(key)]);
            }
            f->Result=0;
        });
        Bind(0x30C7C4,0,f=>
        {
            if(f->Phase!=0)return;long worker=Ptr(_image+0x186AF68);if(worker<=0||Read(worker+3,1)[0] is not (174 or 175))return;
            f->Skip(0);if(_blocked)return;byte[] data=Read(worker,512);
            if((data[6]&32)!=0)return;string key=UnitKey(data[2]);int level=Progress().Level(key);
            Gain(worker,data[0x1E9],(byte)(data[0x1EE]&0x30));if(level!=Progress().Level(key))Write(_image+0x7B07B7,[1]);
        });
    }
    private int StoryJpMultiplier()
    {
        long nex=Ptr(_image+0x3CD9CD0);if(nex<=0)return 100;
        long table=Call(0x3D325C,nex,0x6D);if(table<=0)return 100;
        long row=Call(0x3D3AE8,table,Call(0x2332D8,0x32));if(row<=0||Ptr(row)<0)return 100;
        long data=Call(0x3D2FD8,row);if(data<=0)return 100;
        long date=Ptr(_image+0xC69690);if(date<=0)return 100;
        int index=Read(date+0xC4,1)[0],length=I32(data+0x18),offset=I32(data+0x14);
        if(length is <0 or >256||index>=length||offset is <0 or >65536)return 100;
        int value=I32(data+offset+index*4);if(value is <0 or >10000)throw new InvalidDataException("Native JP multiplier outside bounds.");
        return value==0?100:value;
    }
}
