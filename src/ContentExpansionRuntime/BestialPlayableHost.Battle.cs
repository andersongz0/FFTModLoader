using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion.Runtime;

public sealed unsafe partial class BestialPlayableHost
{
    private bool CanTransform(long worker)=>!_blocked&&Read(worker+3,1)[0]==174&&Known(worker)&&
        (Read(worker+0x61,1)[0]&0x60)==0&&(Read(worker+0x62,1)[0]&0x81)==0&&(Read(worker+0x63,1)[0]&2)==0;
    private bool CanReturn(long worker)=>!_blocked&&_forms.HasTemporaryForm(_battle,Read(worker+1,1)[0]);
    private void InstallBattle()
    {
        _routes.Leaf(0x0F8474,p=>NativeBestialCaptions.Build(p,_image));
        _routes.Leaf(0x0E512C,p=>NativeBestialCaptions.BuildActionCaption(p,_image),3);
        // The black top-of-screen action capsule is NOT the status heading.
        // It is owned by current_animation, not the selected/displayed worker.
        _routes.Leaf(0x109F0C,p=>NativeBestialCaptions.BuildActionCaption(p,_image,true),3);
        _routes.Observe(0x0FBFF8,0,f=>
        {
            if(!_active||f->Phase!=0||(int)f->Arguments[1]!=55)return;
            byte[] key=System.Text.Encoding.UTF8.GetBytes("UITextureParts://ui/ffto/icon/job/textureparts/j_176_uitx\0");
            // This separate formatter renders the LARGE command/detail icon.
            // 55 is a new icon identity; original icon 27 remains untouched.
            f->Skip(0);
            if(f->Arguments[0]>0&&f->Arguments[2]>=key.Length)Write(f->Arguments[0],key);
        },Block,false,NativeBestialCommandGate.BuildIcon);
        _routes.Observe(0x0FBF9C,0,f=>
        {
            if(!_active||f->Phase!=0||!OwnCommand((int)f->Arguments[1]))return;
            // New command icons must not hit the native 1..24 IconId clamp.
            f->Skip(0);
            // UITextureParts keys omit the file extension, exactly as the
            // game's Icon table/BuildPlaceIconPath formatter does.
            Write(f->Arguments[0],System.Text.Encoding.UTF8.GetBytes("UITextureParts://ui/ffto/icon/job/textureparts/j_176_uitx\0"));
        },Block,false,NativeBestialCommandGate.Build);
        // Human confirmation (237FA4) is independent of AI's SetTmp_UA.
        // Its legacy mask turns 524 into 12 (Stone) and 532 into 20.
        // Capture the actual ushort menu entry BEFORE the native routine,
        // then restore only this command/action association after it returns.
        ushort confirmed=0;byte confirmedCommand=0;
        Bind(0x237FA4,0,f=>
        {
            if(f->Phase==0)
            {
                confirmed=0;confirmedCommand=0;
                int commandIndex=Read(_image+0x783292,1)[0];
                int actionIndex=I32(_image+0x783294);
                if(commandIndex>=16||actionIndex is <0 or >=82)return;
                byte command=Read(_image+0xD40838+commandIndex,1)[0];
                if(!OwnCommand(command))return;
                long list=Ptr(_image+0x184A9B0);
                if(list<=0)return;
                ushort action=(ushort)(U16(list+actionIndex*2)&1023);
                if(!BestialBattleData.Own(command,action))return;
                confirmed=action;confirmedCommand=command;
            }
            else if(confirmed!=0 && Read(_image+0x2FD35A5,1)[0]==confirmedCommand &&
                U16(_image+0x2FD35A6)==(confirmed&511))
            {
                Word(_image+0x2FD35A6,confirmed);Int(_image+0x7DC4AC,confirmed);
                f->ReplaceResult(((f->OriginalResult&0xFFFF)|(long)confirmed<<16));
                _log($"[Action] manual command={confirmedCommand}; ability={confirmed}; automatic self target.");
                confirmed=0;
            }
        });
        Bind(0x30E368,4,f=>BuildBattleList(f,true));
        Bind(0x30DD8C,4,f=>BuildBattleList(f,false));
        Bind(0x30EC48,1,f=>
        {
            if(f->Phase!=0||(byte)f->Arguments[1]!=228)return;f->Skip(0);
            long worker=Worker((int)f->Arguments[0]),output=Ptr(_image+0x184A9B0);int n=0;
            if(output<=0)throw new InvalidDataException("Monster list destination is null.");
            if((Read(worker+0x63,1)[0]&2)!=0){Word(output,367);Write(f->Arguments[4],[0]);n=1;}
            else if(CanReturn(worker))foreach(ushort action in WolfActions)
            {long data=Call(0x2BB060,action);byte flags=Read(data+5,1)[0];Word(output+n*2,action);Write(f->Arguments[4]+n,[(byte)((flags&0x20|flags>>3)>>4)]);n++;}
            Word(output+n*2,65535);
        });
        _routes.Observe(0x3170CC,0,f=>
        {
            if(!_active||f->Phase!=0||!OwnCommand((int)f->Arguments[1]))return;
            int command=(int)f->Arguments[1],slot=(int)f->Arguments[2];long worker=Worker((int)f->Arguments[0]);
            f->Skip(command==227?(slot==0&&CanTransform(worker)?1:0):(slot is >=0 and <5&&CanReturn(worker)?1:0));
        },Block,false,NativeBestialCommandGate.Build);
        Helper(0x316D24);
        _routes.Observe(0x316D24,0,f=>
        {
            if(!_active||f->Phase!=0||!OwnCommand((int)f->Arguments[1]))return;
            int index=(int)f->Arguments[0],command=(int)f->Arguments[1],start=(int)f->Arguments[2];
            long worker=Worker(index);int list=Read(_image+0x1873038+index,1)[0];
            if(list>=21||start is <0 or >32)throw new InvalidDataException("Invalid unit action cache.");
            f->Skip(start);ushort[] actions=command==227?[524]:WolfActions;
            if(command==227?!CanTransform(worker):!CanReturn(worker))return;
            foreach(ushort action in actions)
            {
                if(start>=32)break;long entry=_image+0x1872598+(list*34+start)*4;
                Word(entry,(index<<10)|action);Write(entry+2,[(byte)command,0]);start++;
            }
            f->Result=start;
        },Block,false,NativeBestialCommandGate.Build);
        Bind(0x316B04,0,f=>
        {
            if(f->Phase!=0)return;int index=(int)f->Arguments[0];
            if(index is <0 or >20||Read(_image+0x1853CE0+index*512+3,1)[0]!=175)return;
            f->Skip(0);int list=Read(_image+0x1873038+index,1)[0];
            if(list>=21)throw new InvalidDataException("Invalid Wolf action cache index.");
            Write(_image+0x1872598+list*136,new byte[136]);
            int n=(int)Call(0x316D24,index,228,0);long end=_image+0x1872598+(list*34+n)*4;
            Word(end,(index<<10)|367);Word(end+2,0x84A9);if(n+1<34)Write(end+6,[255]);
        });
        Bind(0x320504,0,f=>
        {
            if(f->Phase!=1)return;byte[] request=Read(f->Arguments[0],4);ushort encoded=BinaryPrimitives.ReadUInt16LittleEndian(request);
            int ability=encoded&1023;if(!OwnCommand(request[2])||!OwnAbility(ability))return;
            _=Worker(encoded>>10);byte[] packet=BestialBattleData.Selection(Read(_image+0x18716A0,20),(ushort)ability,request[2]);
            Write(_image+0x18716A0,packet);
        });
        Bind(0x281488,0,f=>
        {
            if(f->Phase!=0)return;byte[] packet=Read(f->Arguments[0],20);ushort action=BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));
            if(!OwnCommand(packet[1])||!OwnAbility(action))return;f->Skip(-1);
            long worker=Worker(packet[0]);
            if(action==524?packet[1]!=227||!CanTransform(worker):packet[1]!=228||!CanReturn(worker))return;
            // An immediate, self-only action. Never consume an item or use a
            // native nine-bit ID alias. Coordinates/status/CT remain native.
            packet=BestialBattleData.Commitment(packet,Read(worker,512));
            Write(f->Arguments[1],packet);
            if((uint)f->Arguments[2]!=0)Write(worker+0x18D,[0]);
            if(((uint)f->Arguments[2]&1)!=0)Write(worker+0x1BA,[1]);f->Result=1;
        });
        foreach(bool effect in new[]{false,true})Bind(effect?0x27FFE8:0x27FE20,0,f=>
        {
            if(f->Phase!=0)return;byte[] packet=Read(f->Arguments[0],20);
            if(!OwnCommand(packet[1])||!OwnAbility(BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2))))return;
            f->Skip(-1);long worker=Worker(packet[0]);
            if(_blocked)return;
            // Match native automatic self-use (flag 0x02, range zero):
            // return 2 skips the target cursor. Effect/prediction still uses
            // the actual self-target packet, never a selected neighbouring tile.
            byte[] self=BestialBattleData.Commitment(packet,Read(worker,512));
            Write(f->Arguments[0],self);
            if(!effect){f->Result=2;return;}
            int cells=(int)Call(effect?0x280B40:0x27F974,f->Arguments[0]);
            f->Result=effect?(cells==0?1:cells<0?-1:0):(cells==0?3:cells<0?-1:0);
        });
    }
    private static void WordIn(byte[] bytes,int offset,int value)=>BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset),checked((ushort)value));
    private void BuildBattleList(NativeContextBridge.Frame* f,bool filter)
    {
        if(f->Phase!=0||!OwnCommand((byte)f->Arguments[1]))return;f->Skip(0);
        long worker=Worker((int)f->Arguments[0]);int command=(byte)f->Arguments[1],count=0;
        byte* events=stackalloc byte[160];Call(0x30FFC4,(long)events,0,0);
        bool toad=(Read(worker+0x63,1)[0]&2)!=0;
        ushort[] actions=command==227?new ushort[]{524}:WolfActions;
        if(!_blocked)foreach(ushort action in actions)
        {
            if(filter&&(command==227?!CanTransform(worker):!CanReturn(worker))||toad)continue;
            long p=Call(0x2BB060,action);byte[] data=Read(p,20);int mp=data[13],ct=data[12]&127;
            if((Read(worker+0x99,1)[0]&128)!=0)mp>>=1;
            if((data[5]&4)==0){if((Read(worker+0x9B,1)[0]&4)!=0)ct=0;else if((Read(worker+0x9B,1)[0]&8)!=0)ct=(ct+1)>>1;}
            byte position=(byte)Call(0x30E2F0,(int)f->Arguments[0],ct,(long)events,0);
            Word(f->Arguments[2]+count*2,action);Write(f->Arguments[3]+count,[(byte)mp]);Write(f->Arguments[4]+count,[(byte)ct]);
            Write(f->Arguments[6]+count,[(byte)((data[5]&32|data[5]>>3)>>4)]);Write(f->Arguments[7]+count,[position]);count++;
        }
        Word(f->Arguments[2]+count*2,65535);f->Result=count;
    }
}
