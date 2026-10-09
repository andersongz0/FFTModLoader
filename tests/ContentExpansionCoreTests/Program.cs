using FFTModLoader.ContentExpansion;
using System.Text.Json;

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void Reject(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { return; }
    throw new Exception("Invalid transaction/migration was accepted.");
}
if (args.Length is < 1 or > 2) throw new ArgumentException("Pass Alchemist.definition.json, then optional native-relocation-plan.json.");
using var definition = JsonDocument.Parse(File.ReadAllText(args[0]));
var actionRows = definition.RootElement.GetProperty("Actions").EnumerateArray().ToArray();
var actions = actionRows.Select(a => new ActionIdentity(a.GetProperty("Key").GetString()!,
    a.TryGetProperty("NativeAbilityId",out var id) ? id.GetInt32() : null)).ToArray();
// Native command 6, decoded directly from supported executable row at RVA
// 67E210 + 6*25: first fourteen slots 368..381, then two empty slots.
int[] old = Enumerable.Range(368,14).Concat(new[]{0,0}).ToArray();
int[] retainedSlots = [0,3,12,13];
for (uint bits=0;bits<=ushort.MaxValue;bits++)
{
    uint flags = bits | ((bits & 255)<<16);
    uint projected = ActionLearning.ProjectToReworkedCommand(flags,old,actions,new Dictionary<string,bool>());
    uint expected = flags & 0xFF0000;
    for (int i=0;i<4;i++) if ((bits & (1u<<retainedSlots[i]))!=0) expected|=1u<<i;
    Require(projected==expected,"Learning changed identity or passive bits");
    Require((projected & 0xFFF0)==0,"Old learned actions automatically unlocked a new item");
    Require(ActionLearning.ProjectBackToNative(flags,projected,old,actions)==flags,"Disabling rework loses old learning");
}
var learned = actions.Skip(4).ToDictionary(a=>a.Key,_=>true);
Require(ActionLearning.ProjectToReworkedCommand(0xA50000,old,actions,learned)==0xA57FF0,"New learning does not map by stable key");
Reject(()=>ActionLearning.ProjectToReworkedCommand(0,old,actions.Concat(new[]{actions[0],actions[0]}).ToArray(),learned));
Reject(()=>ActionLearning.ProjectToReworkedCommand(0,old,new[]{new ActionIdentity("wrong",500)},learned));
Console.WriteLine("PASS: all 65536 native action bitmasks; retained ability identities, new locked skills, passive bits and reversible projection preserved. No save adapter or native writer is implied.");

var newRows = actionRows.Where(a=>a.TryGetProperty("NewItem",out var value)&&value.GetBoolean()).ToArray();
var keys = newRows.Select(a=>a.GetProperty("Key").GetString()!).ToArray();
var ledger = new InventoryLedger(keys);
foreach (var row in newRows)
{
    string key=row.GetProperty("Key").GetString()!;
    int price=row.GetProperty("PriceGil").GetInt32();
    Require(ledger.TryBuy(key,2,price,2*price,out int gil)&&gil==0&&ledger.Count(key)==2,"Purchase transaction failed");
    Require(!ledger.TryBuy(key,1,price,price-1,out gil)&&gil==price-1&&ledger.Count(key)==2,"Failed purchase changed stock/gil");
    Guid use=Guid.NewGuid();
    Require(ledger.TryCommitUse(key,use)&&ledger.Count(key)==1,"Use did not consume exactly one item");
    Require(!ledger.TryCommitUse(key,use)&&ledger.Count(key)==1,"Repeated native callback consumed twice");
    Require(ledger.TryCommitUse(key,Guid.NewGuid())&&ledger.Count(key)==0,"Second use failed");
    Require(!ledger.TryCommitUse(key,Guid.NewGuid())&&ledger.Count(key)==0,"Empty stock used an item");
}
string first=keys[0];
Require(ledger.TryBuy(first,99,1,99,out _)&&ledger.Count(first)==99,"Full stack failed");
Require(!ledger.TryBuy(first,1,1,1,out _)&&ledger.Count(first)==99,"Stack exceeded 99");
var copy=ledger.Snapshot();
var restored=new InventoryLedger(keys);
restored.Restore(copy);
Require(restored.Count(first)==99,"Logical snapshot restore failed");
Reject(()=>restored.Restore(new Dictionary<string,int>{{first,-1}}));
Reject(()=>restored.Restore(new Dictionary<string,int>{{first,100}}));
Reject(()=>restored.Restore(new Dictionary<string,int>{{"unregistered",1}}));
Require(restored.Count(first)==99,"Failed restore partially replaced state");
Reject(()=>restored.TryBuy(first,-1,1,1,out _));
Require(!restored.TryBuy(keys[1],99,int.MaxValue,int.MaxValue,out _),"Purchase overflow granted free items");
Console.WriteLine("PASS: eleven registered consumables; purchase cost/stock checks, no duplicate consumption, empty stock, stack limits, snapshot isolation and atomic validation. Native shop/save integration remains pending.");

var balance = definition.RootElement.GetProperty("Balance");
var formulaDefinitions = newRows.Select(row => row.GetProperty("Effect").GetString() switch
{
    "FixedElementalDamage" => new FormulaDefinition(row.GetProperty("Key").GetString()!,
        balance.GetProperty("FixedElementalDamage").GetInt32(),
        Enum.Parse<ExpandedElement>(row.GetProperty("Element").GetString()!),TargetMaxHpPercent:balance.GetProperty("TargetMaxHpPercent").GetInt32()),
    "Status" => new FormulaDefinition(row.GetProperty("Key").GetString()!,
        Status: Enum.Parse<ExpandedStatus>(row.GetProperty("Status").GetString()!),
        StatusSuccessPercent: balance.GetProperty("StatusSuccessPercent").GetInt32()),
    _ => throw new InvalidDataException("Unknown expanded formula")
}).ToArray();
var registry = FormulaRegistry.Create(formulaDefinitions);
IReadOnlySet<ExpandedStatus> noImmunity = new HashSet<ExpandedStatus>();
foreach (var formula in formulaDefinitions)
{
    var prediction = registry.Evaluate(formula.Key, noImmunity);
    var execution = registry.Evaluate(formula.Key, noImmunity);
    Require(prediction == execution, "Prediction/execution policy mismatch");
    if (formula.Element is not null)
        Require(prediction.BaseDamage == 5 && prediction.TargetMaxHpPercent==15 && prediction.Damage(100)==20 && prediction.Damage(150)==28 && prediction.Damage(151)==28 && prediction.Damage(161)==30 && prediction.Element == formula.Element && prediction.Status is null,
            "Elemental base damage changed");
    else
    {
        Require(prediction.Status == formula.Status && prediction.StatusSuccessPercent == 100,
            "Status policy changed");
        var immune = new HashSet<ExpandedStatus> { formula.Status!.Value };
        Require(registry.Evaluate(formula.Key, immune).StatusSuccessPercent == 0, "Status bypasses immunity");
        var otherImmunity = Enum.GetValues<ExpandedStatus>().Where(s => s != formula.Status).ToHashSet();
        Require(registry.Evaluate(formula.Key, otherImmunity).StatusSuccessPercent == 100,
            "Unrelated immunity blocks status");
    }
    Require(ledger.Count(formula.Key) == copy.GetValueOrDefault(formula.Key), "Formula evaluation consumed stock");
}
Reject(() => registry.Evaluate("unregistered", noImmunity));
Reject(() => FormulaRegistry.Create(formulaDefinitions.Concat(new[] { formulaDefinitions[0] })));
Reject(() => FormulaRegistry.Create(new[] { new FormulaDefinition("bad", -1, ExpandedElement.Fire) }));
Reject(() => FormulaRegistry.Create(new[] { new FormulaDefinition("bad", Status: ExpandedStatus.Poison, StatusSuccessPercent: 101) }));
Reject(() => FormulaRegistry.Create(new[] { new FormulaDefinition("bad", 60, ExpandedElement.Fire, ExpandedStatus.Poison) }));
Console.WriteLine("PASS: eleven pure formula intents; elemental5 + ceiling15% maxHP, no Faith input, 100% status gated by immunity; deterministic and inventory-neutral. Native elemental adjustment verified separately.");
if (args.Length == 2) NativeRelocationTests.Run(args[1]);
NativeJpCostsTests.Run();
// Expanded IDs here are deliberately local fixtures. The live registry must
// reserve them only after every native capability and save adapter is ready.
int fixtureNewId = 513;
var menuActions = actionRows.Select(a => new MenuAction(a.GetProperty("Key").GetString()!,
    checked((ushort)(a.TryGetProperty("NativeAbilityId", out var nativeId) ? nativeId.GetInt32() : fixtureNewId++)),
    checked((ushort)a.GetProperty("JpCost").GetInt32()))).ToArray();
NativeLearningMenuTests.Run(actions, menuActions);
int fixtureNewItem = 261;
var itemActions = actionRows.Select((a, i) => new ItemActionIdentity(a.GetProperty("Key").GetString()!,
    menuActions[i].AbilityId, checked((ushort)(a.TryGetProperty("NativeItemId", out var item) ? item.GetInt32() : fixtureNewItem++)))).ToArray();
NativeCommandItemTests.Run(menuActions, itemActions);
ExpandedSaveTests.Run(keys);
ChemistActionBindingsTests.Run();
NativePartySaveCommitTests.Run();
