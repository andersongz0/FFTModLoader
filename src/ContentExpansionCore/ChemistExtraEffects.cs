namespace FFTModLoader.ContentExpansion;

/// <summary>Native supported-image status/element identities, no new status bit aliases.</summary>
public static class ChemistExtraEffects
{
    // Keys parallel the eleven persistent extra actions, beginning with Venom.
    // Poison28, OilyTouch299, Silence240, Blind234, Slow34, Haste32,
    // Regen8 and Float38 give the independently audited status index/AI.
    public static readonly int[] Templates=[28,299,16,24,20,240,234,34,32,8,38];
    // Oil's gameplay status still derives from Oily Touch. Its monster-only
    // effect 350 is not a human thrown-item effect; use the native dark cloud
    // (Blind effect 49) for this test rather than executing that monster script.
    public static readonly int[] ImpactTemplates=[28,234,16,24,20,240,234,34,32,8,38];
    // Audited requestCast1Animation selector: 19 maps to zero (no actor pose).
    // Selectors 0 and 9 are NOT no-ops: both request an actor animation.
    public const byte NoCastingVisualSelector=19;
    public static readonly byte[] StatusIndices=[39,110,0,0,0,52,48,43,42,34,46];
    public static readonly byte[] Elements=[0,0,0x80,0x20,0x40,0,0,0,0,0,0];
    public static readonly int[] Prices=[140,300,500,500,500,1400,1400,1400,2000,2000,2000];
    public static bool Elemental(int slot)=>slot is >=6 and <=8;
    // Match native percentage damage's integer ceiling (+99 before /100).
    // Max HP, never remaining HP or caster stats. Safe for the full ushort field.
    public static int ElementalDamage(ushort targetMaxHp)=>5+(targetMaxHp*15+99)/100;
    // Native shop flag 0x6F: chapter starts are 1, 5, 9, 13, not 1..4.
    // This governs buying only; existing stock/learning and NPC supply are untouched.
    public static byte ShopChapter(int slot)=>slot switch
    {4 or 5=>1,6 or 7 or 8=>2,9 or 10 or 11=>3,12 or 13 or 14=>4,_=>throw new ArgumentOutOfRangeException(nameof(slot))};
    public static byte ShopProgress(int slot)=>(byte)(1+4*(ShopChapter(slot)-1));
    public static byte Status(int slot)=>StatusIndices[slot-4];
    public static byte Element(int slot)=>Elements[slot-4];
    public static int Template(int slot)=>Templates[slot-4];
    public static int ImpactTemplate(int slot)=>ImpactTemplates[slot-4];
    public static int Price(int slot)=>Prices[slot-4];
    // Original world-atlas bottle family only; inventory IDs remain 261..271.
    public static int BottleModel(int slot)=>slot==4?240:slot<=8?242:slot<=11?241:244;
}
