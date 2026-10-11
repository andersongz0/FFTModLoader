using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion.Runtime;

public sealed unsafe partial class BestialPlayableHost
{
    private bool MenuLearned(long unit)=>!_blocked&&Progress().Read(MenuKey(unit)).Learned.Contains((ushort)524);
    private void InstallMenus()
    {
        Bind(0x2867BC,1,f=>
        {
            if(f->Phase!=0||(ushort)f->Arguments[1]!=174)return;
            int category=(int)f->Arguments[2],mode=(int)f->Arguments[4];
            f->Skip(0);long output=f->Arguments[3];
            if(category is not (0 or 15)){if(mode!=3)Word(output,65535);return;}
            long unit=Unit((short)f->Arguments[0]);bool known=MenuLearned(unit);
            if(mode==1){if(known){Word(output,227);Word(output+2,65535);f->Result=1;}else Word(output,65535);return;}
            Span<ushort> list=stackalloc ushort[2];
            int n=_menu.Build(mode==0?ActionMenuView.All:mode==3?ActionMenuView.UnlearnedCount:ActionMenuView.Learn,
                known?1u:0u,Progress().Read(MenuKey(unit)).UnspentJp,list);
            if(mode!=3)for(int i=0;i<=n;i++)Word(output+i*2,list[i]);f->Result=n;
        });
        Bind(0x28A370,0,f=>
        {
            if(f->Phase!=0||!OwnCommand((ushort)f->Arguments[0]))return;
            if((int)f->Arguments[1] is 0 or 15)f->Skip((ushort)f->Arguments[0]==227?_humanRow.Address:_wolfRow.Address);
            else{var empty=_emptyRow;f->Skip(empty.Address);}
        });
        Bind(0x28A474,2,f=>
        {
            if(f->Phase!=0||(ushort)f->Arguments[2]!=174)return;
            byte[] row=Read(f->Arguments[0],48);
            if(BinaryPrimitives.ReadUInt16LittleEndian(row)!=524||row.AsSpan(2).IndexOfAnyExcept((byte)0)>=0)return;
            int mode=(int)f->Arguments[5];if(mode is <0 or >8)throw new InvalidDataException("Unknown action-pair view.");
            bool known=MenuLearned(Unit((short)f->Arguments[1]));
            bool include=mode switch{1 or 5 or 8=>known,3 or 6=>!known,_=>true};
            f->Skip(include?1:0);if(mode is 3 or 8)return;
            long output=f->Arguments[4];
            if(include){Word(output,174);Word(output+2,524|(known?(mode is 2 or 4 or 5 or 7?0x1000:0):mode==0?0x4000:0));output+=8;}
            Write(output,new byte[]{0,0,255,255,0,0,0,0});
        });
        Bind(0x2866E4,0,f=>
        {
            if(f->Phase!=1||_blocked||!MenuLearned(Unit((short)f->Arguments[0])))return;
            int n=(int)f->OriginalResult;long p=f->Arguments[1];
            if(n is <0 or >22||U16(p+n*2)!=65535)throw new InvalidDataException("Command list is not bounded.");
            for(int i=0;i<n;i++)if(U16(p+i*2)==227)return;
            Word(p+n*2,227);Word(p+(n+1)*2,65535);f->ReplaceResult(n+1);
        });
        Bind(0x2B9AD0,0,f=>
        {
            if(f->Phase!=0||(ushort)f->Arguments[1]!=174)return;f->Skip(0);
            if(_blocked||(ushort)f->Arguments[0]!=524)return;
            int selected=(short)U16(_image+0x1811428);long unit=Unit(selected);
            if(!ExpandedJobProgress.MeetsBestialRequirements(Read(unit+0xBD,12)))return;
            if(Progress().Learn(MenuKey(unit),524))
            {Call(0x285744,selected);Call(0x2BD05C);Call(0xF4BCC,selected,0x81);_log("[Learning] Transform learned in its own expanded progress record.");}
        });
        Bind(0x0FBB7C,0,f=>
        {
            if(f->Phase!=1)return;long descriptor=f->Arguments[0];if(I32(descriptor+0x48)!=174)return;
            long unit=Unit((short)U16(_image+0x1811428));var state=Progress().Read(MenuKey(unit));
            Int(descriptor+0x44,ExpandedJobProgress.MeetsBestialRequirements(Read(unit+0xBD,12))?Math.Max(1,Progress().Level(state.UnitKey)):0);
            Int(descriptor+0x4C,state.UnspentJp);Int(descriptor+0x50,state.TotalJp);
            Int(descriptor+0x54,Progress().Next(state.UnitKey)==0?0:state.TotalJp+Progress().Next(state.UnitKey));
        });
        foreach(int rva in new[]{0x2B8F4C,0x2B8FA8,0x2B8FE4,0x2B9044})Bind(rva,0,f=>
        {
            int index=(int)f->Arguments[0];if(f->Phase!=0||index is <0 or >35||U16(_image+0x375F800+index*2)!=174)return;
            long unit=Unit((short)U16(_image+0x1811428));var state=Progress().Read(MenuKey(unit));
            int value=rva==0x2B8F4C?Progress().Level(state.UnitKey):rva==0x2B8FA8?state.UnspentJp:
                rva==0x2B8FE4?state.TotalJp:Progress().Next(state.UnitKey)==0?0:state.TotalJp+Progress().Next(state.UnitKey);
            if(rva==0x2B8F4C)Int(_image+0x375F864,value);
            if(rva==0x2B8FE4)Int(_image+0x375F868,value);
            if(rva==0x2B9044)Int(_image+0x375F86C,value);
            f->Skip(value);
        });
    }
    private NativeLifetimeMemory _emptyRow=null!;
}
