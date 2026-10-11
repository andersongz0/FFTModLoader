using FFTModLoader.ContentExpansion;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Reject(Action action)
{
    try { action(); } catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException) { return; }
    throw new Exception("Unsafe registration accepted.");
}
var catalog = new ExpandedContentCatalog();
ExpandedContentDefinition valid = new(1, "ffttic.jobs.bestialwarrior",
    [new(ExpandedTable.Job,174,"bestial"),new(ExpandedTable.Job,175,"wolf"),
     new(ExpandedTable.Command,227,"bestial-arts"),new(ExpandedTable.Command,228,"wolf-arts"),
     new(ExpandedTable.Ability,524,"transform"),new(ExpandedTable.Ability,532,"de-transform"),
     new(ExpandedTable.Ability,525,"pending-1",true),new(ExpandedTable.Formula,108,"reversible-form")],
    [new(227,"bestial-arts",false,[524]),new(228,"wolf-arts",true,[532,280,281,283,282])]);
foreach (ExpandedTable table in Enum.GetValues<ExpandedTable>())
{
    int first = table switch { ExpandedTable.Job=>174,ExpandedTable.Command=>227,ExpandedTable.Ability=>513,
        ExpandedTable.Shape=>170,_=>107 };
    for (int id=0;id<first;id++)
        Reject(()=>new ExpandedContentCatalog().Register(new(1,"fixture",[new(table,id,"old")],[])));
}
Reject(()=>catalog.Register(valid with { Ids = [..valid.Ids,new(ExpandedTable.Ability,513,"collision")] }));
Check(catalog.Owner(ExpandedTable.Job,174) is null,"Failed transaction leaked job ownership");
Reject(()=>catalog.Register(valid with { Commands=[new(227,"bestial-arts",false,[525]),valid.Commands[1]] }));
Reject(()=>catalog.Register(valid with { Commands=[new(227,"bestial-arts",false,[512]),valid.Commands[1]] }));
Reject(()=>catalog.Register(valid with { Commands=[new(227,"bestial-arts",false,[524,524]),valid.Commands[1]] }));
Reject(()=>catalog.Register(valid with { Ids=[..valid.Ids,new(ExpandedTable.Ability,1024,"overflow")] }));
catalog.Register(valid); catalog.Register(valid);
Check(catalog.Command(228)!.Actions.SequenceEqual(new ushort[]{532,280,281,283,282}),"Wolf lost its fifth action");
Check(!catalog.IsActive(ExpandedTable.Ability,525),"Undefined skill became usable");
Check(catalog.IsActive(ExpandedTable.Ability,524),"Transform reservation not active");
Reject(()=>catalog.Register(valid with { ModId="different-owner" }));
Reject(()=>catalog.Register(valid with { Ids=valid.Ids.Reverse().ToArray() }));
var isolated=catalog.Command(228)!; isolated.Actions[0]=1;
Check(catalog.Command(228)!.Actions[0]==532,"Caller modified catalog state");
var json="""{"SchemaVersion":1,"ModId":"fixture","Ids":[{"Table":"Ability","Id":533,"Key":"new","Reserved":true}],"Commands":[]}""";
catalog.Register(ExpandedContentCatalog.Read(json));
Reject(()=>ExpandedContentCatalog.Read(json.Replace("\"Commands\"","\"TypoCommands\"")));
if(args.Length==1)
{
    var local=new ExpandedContentCatalog();local.Register(ExpandedContentCatalog.Read(File.ReadAllText(args[0])));
    Check(local.Command(228)!.Actions.Length==5,"Generated Bestial content disagrees with loader contract");
}
Console.WriteLine("PASS: original IDs never allocated, collision rejection, atomic multi-table registration, reservations remain inactive, five-action monster command and generated manifest.");
var progress=new ExpandedJobProgress(174,[0,100,300,700,1200,1800,2500,3500],[new("transform",524,0)]);
string unitKey=new('A',64),otherKey=new('B',64);
Check(progress.Level(unitKey)==1&&progress.Read(unitKey).UnspentJp==0,"New job borrowed legacy progress");
Check(progress.Learn(unitKey,524)&&!progress.Learn(unitKey,524),"Zero-cost test learning is not idempotent");
Check(!progress.Learn(unitKey,525)&&!progress.Learn(unitKey,1),"Undefined or original action became learnable");
progress.Gain(unitKey,300);Check(progress.Level(unitKey)==3&&progress.Next(unitKey)==400,"New job level thresholds mismatch");
Check(progress.Level(otherKey)==1&&progress.Read(otherKey).Learned.Length==0,"Progress leaked between units");
var snapshot=progress.Snapshot();snapshot[0].Learned[0]=532;
Reject(()=>progress.Restore(snapshot));Check(progress.Read(unitKey).Learned.Single()==524,"Rejected progress restore changed state");
Reject(()=>progress.Restore([progress.Read(unitKey),progress.Read(unitKey)]));
progress.Gain(unitKey,int.MaxValue);Check(progress.Read(unitKey).UnspentJp==9999&&progress.Level(unitKey)==8,"JP overflow or cap mismatch");
var req=new byte[12];req[0]=0x40;req[1]=4;req[5]=4;
Check(ExpandedJobProgress.MeetsBestialRequirements(req),"Squire/Archer/Mystic4 not accepted");
foreach(int offset in new[]{0,1,5}){var missing=req.ToArray();missing[offset]=0;Check(!ExpandedJobProgress.MeetsBestialRequirements(missing),"Missing prerequisite accepted");}
var wrongMystic=req.ToArray();wrongMystic[5]=0x40;
Check(!ExpandedJobProgress.MeetsBestialRequirements(wrongMystic),"Orator must not satisfy Mystic requirement");
Console.WriteLine("PASS: independent new-job JP, learning, caps, atomic restore, unit isolation and three unlock prerequisites.");
var forms=new ReversibleBattleForms(new(174,175,227,228,176,177,0,1,4));
for(int gender=0;gender<2;gender++)
{
    var human=Enumerable.Range(0,512).Select(i=>(byte)(i*7)).ToArray();
    human[0]=(byte)(0x80+gender);human[1]=(byte)gender;human[2]=3;human[3]=174;
    human[6]=(byte)((gender==1?0x40:0)|0x10);human[0x12]=227;human[0x18F]=(byte)(174+gender);
    var wolf=forms.PlanTransform(1,gender,human);
    Check(wolf[3]==175 && wolf[0x12]==228 && wolf[0x18F]==176+gender && wolf[0x190]==gender,"Wrong form/sex palette");
    Check(wolf[6]==(human[6]|0x80),"Native monster kind not set or unrelated flags modified");
    Check(!forms.HasTemporaryForm(1,gender),"Planning committed before native success");
    var mismatched=wolf.ToArray();mismatched[0x30]^=1;
    Reject(()=>forms.AcknowledgeTransform(1,gender,human,mismatched));
    forms.AcknowledgeTransform(1,gender,human,wolf);
    Reject(()=>forms.PlanTransform(1,gender,human));
    // Changes made during battle must survive returning, including equipment,
    // status timers, damage, MP use, CT, location and worker action flags.
    wolf[0x30]=9;wolf[0x34]=3;wolf[0x41]=80;wolf[0x4F]=7;wolf[0x50]=11;
    wolf[0x61]=0xA0;wolf[0x66]=4;wolf[0x1BA]=1;wolf[0x20]=5;
    Reject(()=>forms.PlanReturn(2,gender,wolf));
    var recycled=wolf.ToArray();recycled[2]=4;Reject(()=>forms.PlanReturn(1,gender,recycled));
    var restored=forms.PlanReturn(1,gender,wolf);
    Check(restored[3]==174 && restored[0]==0x80+gender && restored[0x12]==227,"Wrong original identity");
    Check(restored[6]==human[6],"Return lost sex/original kind flags");
    foreach(int offset in new[]{0x30,0x34,0x41,0x4F,0x50,0x61,0x66,0x1BA,0x20})
        Check(restored[offset]==wolf[offset],"Return rewound battle state");
    var end=forms.PlanBattleEnd(1,new Dictionary<int,byte[]>{{gender,wolf}});
    Check(end[gender].SequenceEqual(restored),"Battle-end plan differs from manual return");
    forms.AcknowledgeReturn(1,gender,wolf,restored);
    Check(!forms.HasTemporaryForm(1,gender),"Committed return retained the temporary form");
}
Console.WriteLine("PASS: isolated reversible form planner, male/female palettes, no planning-time commit, no HP/status/CT/location rewind, recycled-worker veto and battle-end restoration plan. No live battle adapter implied.");
static void Word(byte[] bytes,int offset,int value)=>System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset),(ushort)value);
static int ReadWord(byte[] bytes,int offset)=>System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
var proportional=new ReversibleBattleForms(new(174,175,227,228,176,177,0,1,4,true));
var warrior=new byte[512];warrior[0]=0x81;warrior[1]=2;warrior[2]=7;warrior[3]=174;
warrior[6]=0x40;warrior[0x12]=227;warrior[0x18F]=175;
warrior[0x20]=9;warrior[0x38]=10;warrior[0x3E]=10;warrior[0x42]=3;
warrior[0x61]=0xA0;warrior[0x66]=4;warrior[0x41]=77;warrior[0x4F]=8;warrior[0x50]=9;
Word(warrior,0x30,50);Word(warrior,0x32,100);Word(warrior,0x34,10);Word(warrior,0x36,20);
var panther=new byte[512];panther[0x38]=20;panther[0x3E]=20;panther[0x42]=5;
Word(panther,0x32,200);Word(panther,0x36,10);
Reject(()=>proportional.PlanTransform(3,2,warrior));
var beast=proportional.PlanTransform(3,2,warrior,panther);
Check(ReadWord(beast,0x30)==100 && ReadWord(beast,0x34)==5,"Transform did not retain resource percentage");
Check(beast[0x38]==20 && beast[0x42]==5 && beast[0x20]==0,"Panther attributes/equipment not applied");
foreach(int offset in new[]{0x61,0x66,0x41,0x4F,0x50})Check(beast[offset]==warrior[offset],"Attribute calculation reset live battle state");
proportional.AcknowledgeTransform(3,2,warrior,beast,panther);
Word(beast,0x30,80);Word(beast,0x34,3);beast[0x38]-=2;beast[0x3E]-=2;
beast[0x61]=0xFF;beast[0x66]=1;beast[0x41]=90;beast[0x4F]=10;
var returnPlan=proportional.PlanReturn(3,2,beast);
Check(ReadWord(returnPlan,0x30)==40 && ReadWord(returnPlan,0x34)==6,"Return erased damage or MP spent in Wolf form");
Check(returnPlan[0x38]==8 && returnPlan[0x3E]==8 && returnPlan[0x42]==3 && returnPlan[0x20]==9,"Return did not restore equipment/base attributes or retain permanent stat loss");
foreach(int offset in new[]{0x61,0x66,0x41,0x4F,0x50})Check(returnPlan[offset]==beast[offset],"Return rewound live battle state");
Check(proportional.PlanBattleEnd(3,new Dictionary<int,byte[]>{{2,beast}})[2].SequenceEqual(returnPlan),"Battle-end percentage policy differs");
proportional.AcknowledgeReturn(3,2,beast,returnPlan);
// Fractional values do not drift across an otherwise unchanged round trip.
foreach(var sample in new[]{(Hp:1,Max:1000,WolfMax:7),(Hp:33,Max:100,WolfMax:73),(Hp:0,Max:100,WolfMax:200)})
{
    Word(warrior,0x30,sample.Hp);Word(warrior,0x32,sample.Max);Word(panther,0x32,sample.WolfMax);
    Word(panther,0x36,0); // Panther variants may have zero MP capacity.
    var transformed=proportional.PlanTransform(4,2,warrior,panther);
    Check(sample.Hp==0?ReadWord(transformed,0x30)==0:ReadWord(transformed,0x30)>0,"Conversion changed KO state");
    Check(ReadWord(transformed,0x34)==0,"Zero maximum MP produced usable MP");
    proportional.AcknowledgeTransform(4,2,warrior,transformed,panther);
    var back=proportional.PlanReturn(4,2,transformed);
    Check(ReadWord(back,0x30)==sample.Hp && ReadWord(back,0x34)==10,"Unchanged resources lost their original percentage through rounding");
    proportional.AcknowledgeReturn(4,2,transformed,back);
}
Console.WriteLine("PASS: Panther attribute inputs, percentage HP/MP transitions, retained damage/MP costs/stat loss, zero MP capacity, unchanged fractional round trips and unchanged KO state. Native attribute calculator still requires integration.");
// Native 49-byte Panther schema, copied from the audited field definitions.
var pantherJob=new byte[49];pantherJob[0]=228;
Word(pantherJob,1,497);Word(pantherJob,3,492);Word(pantherJob,5,442);
pantherJob[14]=6;pantherJob[15]=116;pantherJob[16]=30;pantherJob[17]=50;
pantherJob[18]=85;pantherJob[19]=116;pantherJob[20]=39;pantherJob[21]=98;
pantherJob[22]=7;pantherJob[23]=91;pantherJob[24]=4;pantherJob[25]=4;pantherJob[26]=23;
static void Base24(byte[] bytes,int stat,int value)
{int at=0x7B+stat*3;bytes[at]=(byte)value;bytes[at+1]=(byte)(value>>8);bytes[at+2]=(byte)(value>>16);}
Base24(warrior,0,100<<14);Base24(warrior,1,50<<14);
for(int stat=2;stat<5;stat++)Base24(warrior,stat,10<<14);
var inputCopy=warrior.ToArray();var calculated=MonsterFormAttributes.Calculate(warrior,pantherJob);
Check(warrior.SequenceEqual(inputCopy),"Attribute calculator mutated its live input");
Check(ReadWord(calculated,0x32)==116 && ReadWord(calculated,0x36)==25,"Wrong Panther maximum resources");
Check(calculated[0x3A]==11 && calculated[0x38]==9 && calculated[0x39]==9,"Wrong Panther multipliers/rounding");
Check(calculated[0x40]==11 && calculated[0x3E]==9 && calculated[0x3F]==9,"Effective stats retained armor bonuses");
Check(calculated[0x42]==4 && calculated[0x43]==4 && calculated[0x4B]==23,"Wrong Panther movement/evasion");
Check(ReadWord(calculated,0x14)==442 && calculated[0x96]==8 && calculated[0x9D]==0x10 && calculated[0x9C]==2,"Native Panther passives lost");
foreach(int offset in new[]{0x30,0x34,0x61,0x66,0x41,0x4F,0x50})Check(calculated[offset]==warrior[offset],"Calculator touched current resources/status/position");
Word(warrior,0x18,486); // Move + 1, native movement bit zero.
Check(MonsterFormAttributes.Calculate(warrior,pantherJob)[0x42]==5,"Equipped movement ability lost");
Word(warrior,0x16,484); // Max HP Boost: support byte 0x9B bit 2.
Check(ReadWord(MonsterFormAttributes.Calculate(warrior,pantherJob),0x32)==139,"Max HP Boost arithmetic wrong");
for(int stat=0;stat<5;stat++)Base24(warrior,stat,0xFFFFFF);
var bounded=MonsterFormAttributes.Calculate(warrior,pantherJob);
Check(ReadWord(bounded,0x32)==1198 && ReadWord(bounded,0x36)==511 && bounded[0x3A]==50 && bounded[0x38]==99,"Native stat limits differ");
Reject(()=>MonsterFormAttributes.Calculate(warrior,pantherJob.AsSpan(0,48)));
Console.WriteLine("PASS: loader-owned Panther stat calculation, existing raw bases, native multipliers/caps/passive bits, armor-free maxima, no live resource/status writes. No random monster generation or party-save writes.");
