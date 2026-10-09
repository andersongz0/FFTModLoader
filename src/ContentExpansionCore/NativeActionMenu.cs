namespace FFTModLoader.ContentExpansion;

public enum ActionMenuView { All, LearnedActions, Learn, UnlearnedCount }

public sealed record MenuAction(string Key, ushort AbilityId, ushort JpCost, bool Learnable = true);

/// <summary>
/// Formats a registered action list without native numeric category filters or
/// the packed nine-bit command format. IDs are ten-bit UI identities, flags are
/// separate. This is not an installed hook and does not enable new abilities.
/// </summary>
public sealed class NativeActionMenu
{
    public const ushort Terminator = 0xFFFF;
    public const ushort AbilityIdMask = 0x03FF;
    public const ushort LearnedFlag = 0x1000;
    public const ushort UnavailableFlag = 0x4000;
    public const ushort UnlearnableFlags = 0x6000;
    private readonly MenuAction[] _actions;

    public NativeActionMenu(IEnumerable<MenuAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        _actions = actions.ToArray();
        if (_actions.Length is < 1 or > 16 || _actions.Any(a => a is null ||
                string.IsNullOrWhiteSpace(a.Key) || a.AbilityId is 0 or 512 || a.AbilityId > AbilityIdMask) ||
            _actions.Select(a => a.Key).Distinct(StringComparer.Ordinal).Count() != _actions.Length ||
            _actions.Select(a => a.AbilityId).Distinct().Count() != _actions.Length)
            throw new InvalidDataException("Expected at most sixteen unique registered action identities; ID 512 is reserved.");
    }

    // GetAbilityList(mode=1, category=0) returns COMMANDS, not actions. A hook
    // using this adapter must leave that call (and every passive category) alone.
    public static bool HandlesChemistActionList(int job, int category, int nativeMode)
        => job == 75 && category == 0 && nativeMode is 0 or 2 or 3;

    public int Build(ActionMenuView view, uint learnedSlots, int availableJp, Span<ushort> output)
    {
        if (!Enum.IsDefined(view) || (learnedSlots & 0xFF000000) != 0 || availableJp < 0)
            throw new ArgumentException("Invalid action menu state.");
        var entries = new List<ushort>();
        int unlearned = 0;
        for (int slot = 0; slot < _actions.Length; slot++)
        {
            var action = _actions[slot];
            bool learned = (learnedSlots & (1u << slot)) != 0;
            if (!learned) unlearned++;
            ushort flags = 0;
            switch (view)
            {
                case ActionMenuView.All:
                    if (!learned) flags = action.Learnable ? UnavailableFlag : UnlearnableFlags;
                    break;
                case ActionMenuView.LearnedActions:
                    if (!learned) continue;
                    break;
                case ActionMenuView.Learn:
                    if (learned) flags = LearnedFlag;
                    else if (!action.Learnable) continue;
                    else if (availableJp < action.JpCost) flags = UnavailableFlag;
                    break;
                case ActionMenuView.UnlearnedCount:
                    continue;
            }
            entries.Add((ushort)(action.AbilityId | flags));
        }
        // Native mastery mode has NO output writes, not even a terminator.
        if (view == ActionMenuView.UnlearnedCount) return unlearned;
        // Preflight capacity before any caller memory is changed.
        if (output.Length < entries.Count + 1)
            throw new ArgumentException("Action list output lacks room for entries and native terminator.");
        entries.CopyTo(output);
        output[entries.Count] = Terminator;
        return entries.Count;
    }

    public static ushort DecodeIdentity(ushort menuEntry)
    {
        int flags = menuEntry & ~AbilityIdMask;
        if (menuEntry == Terminator || flags is not (0 or LearnedFlag or UnavailableFlag or UnlearnableFlags))
            throw new InvalidDataException("Not an audited ability menu entry.");
        ushort identity = (ushort)(menuEntry & AbilityIdMask);
        if (identity is 0 or 512) throw new InvalidDataException("Empty or reserved ability identity.");
        return identity;
    }
}
