namespace FFTModLoader.ContentExpansion;

/// <summary>AI candidates are actual items, never human menu group aliases.</summary>
public static class ChemistAiActions
{
    public const int Capacity=64,Rows=16,RecordSize=4;
    public static readonly ushort[] All=[..ChemistMedicineFamilies.All,381,
        ..ChemistActionBindings.TestActions.Skip(4).Select(a=>a.AbilityId)];
    public static ushort[] Available(uint learned,Func<ushort,bool> stocked)
        =>All.Where(a=>(learned&(1u<<ChemistMedicineFamilies.CombatSlot(a)))!=0 && stocked(a)).ToArray();
    public static ushort Item(ushort ability)
        =>ChemistMedicineFamilies.Group(ability)>=0?ChemistMedicineFamilies.Item(ability):
            ChemistActionBindings.Item(ChemistMedicineFamilies.CombatSlot(ability));
}
