namespace FFTModLoader.ContentExpansion;

/// <summary>Existing native items grouped by paid learning, without new save bits.</summary>
public static class ChemistMedicineFamilies
{
    public static readonly ushort[] Ether=[371,372];
    public static readonly ushort[] Remedy=[374,375,376,377,378,379,380];
    public static readonly ushort[] All=[..ChemistPotionFamily.Abilities,..Ether,..Remedy];
    public static ushort[] ForSlot(int slot)=>slot switch
    {0=>ChemistPotionFamily.Abilities,1=>Ether,2=>Remedy,_=>[]};
    public static int Group(ushort ability)=>ChemistPotionFamily.Contains(ability)?0:
        Array.IndexOf(Ether,ability)>=0?1:Array.IndexOf(Remedy,ability)>=0?2:-1;
    public static int CombatSlot(ushort ability)=>Group(ability) is var group && group>=0?group:ChemistActionBindings.Slot(ability);
    public static ushort Item(ushort ability)=>Group(ability)>=0?(ushort)(ability-128):
        throw new ArgumentOutOfRangeException(nameof(ability));
    public static bool HasStock(int slot,Func<ushort,int> stock)=>ForSlot(slot).Any(a=>stock(Item(a))>0);
}
