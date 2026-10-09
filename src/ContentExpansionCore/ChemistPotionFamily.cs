namespace FFTModLoader.ContentExpansion;

/// <summary>Existing native medicines grouped under the paid Potion action.
/// No new IDs, learning bits or save representation are allocated.</summary>
public static class ChemistPotionFamily
{
    public static readonly ushort[] Abilities=[368,369,370,373];
    public static bool Contains(ushort ability)=>Array.IndexOf(Abilities,ability)>=0;
    public static ushort Item(ushort ability)=>Contains(ability)?(ushort)(ability-128):
        throw new ArgumentOutOfRangeException(nameof(ability));
    public static ushort[] Available(Func<ushort,int> stock)=>Abilities.Where(a=>stock(Item(a))>0).ToArray();
    public static int CombatSlot(ushort ability)=>Contains(ability)?0:ChemistActionBindings.Slot(ability);
}
