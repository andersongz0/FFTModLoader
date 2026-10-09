namespace FFTModLoader.ContentExpansion;

public enum ExpandedElement { Fire, Ice, Lightning }
public enum ExpandedStatus { Poison, Oil, Silence, Blind, Slow, Haste, Regen, Float }

/// <summary>
/// Pure effect intent, not a final native battle result. Elemental modifiers,
/// reactions, status conflicts and result writes belong to the native adapter.
/// Evaluating it cannot change stock, HP, status or the game save.
/// </summary>
public sealed record EffectIntent(int BaseDamage, ExpandedElement? Element,
    ExpandedStatus? Status, int StatusSuccessPercent, int TargetMaxHpPercent=0)
{
    public int Damage(ushort targetMaxHp)=>BaseDamage+(targetMaxHp*TargetMaxHpPercent+99)/100;
}

public sealed class FormulaRegistry
{
    private readonly Dictionary<string, Func<IReadOnlySet<ExpandedStatus>, EffectIntent>> _formulas;

    private FormulaRegistry(Dictionary<string, Func<IReadOnlySet<ExpandedStatus>, EffectIntent>> formulas)
        => _formulas = formulas;

    public static FormulaRegistry Create(IEnumerable<FormulaDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var formulas = new Dictionary<string, Func<IReadOnlySet<ExpandedStatus>, EffectIntent>>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (string.IsNullOrWhiteSpace(definition.Key) || formulas.ContainsKey(definition.Key))
                throw new InvalidDataException("Formula keys must be nonempty and unique.");
            if (definition.Element is { } element)
            {
                if (!Enum.IsDefined(element) || definition.Status is not null ||
                    definition.BaseDamage is < 1 or > ushort.MaxValue || definition.StatusSuccessPercent != 0 ||
                    definition.TargetMaxHpPercent is <0 or >100 ||
                    definition.BaseDamage+(ushort.MaxValue*definition.TargetMaxHpPercent+99)/100>ushort.MaxValue)
                    throw new InvalidDataException("Invalid fixed elemental formula.");
                formulas.Add(definition.Key, _ => new EffectIntent(definition.BaseDamage, element, null, 0,definition.TargetMaxHpPercent));
            }
            else if (definition.Status is { } status)
            {
                if (!Enum.IsDefined(status) || definition.BaseDamage != 0 || definition.TargetMaxHpPercent!=0 ||
                    definition.StatusSuccessPercent is < 0 or > 100)
                    throw new InvalidDataException("Invalid status formula.");
                formulas.Add(definition.Key, immune => new EffectIntent(0, null, status,
                    immune.Contains(status) ? 0 : definition.StatusSuccessPercent));
            }
            else throw new InvalidDataException("A formula must declare an element or a status.");
        }
        return new FormulaRegistry(formulas);
    }

    // No Faith argument: these registered formulas deliberately have no Faith scaling.
    // Both native prediction and execution must obtain effective immunities from
    // the same native policy before calling this method.
    public EffectIntent Evaluate(string key, IReadOnlySet<ExpandedStatus> effectiveImmunities)
    {
        ArgumentNullException.ThrowIfNull(effectiveImmunities);
        if (!_formulas.TryGetValue(key, out var formula))
            throw new InvalidDataException($"Unregistered expanded formula: {key}");
        return formula(effectiveImmunities);
    }
}

public sealed record FormulaDefinition(string Key, int BaseDamage = 0,
    ExpandedElement? Element = null, ExpandedStatus? Status = null, int StatusSuccessPercent = 0,int TargetMaxHpPercent=0);
