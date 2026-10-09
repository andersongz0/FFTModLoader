namespace FFTModLoader.ContentExpansion;

public sealed record ActionIdentity(string Key, int? NativeAbilityId);

/// <summary>
/// Pure migration policy, not a native-memory writer. A future save adapter must
/// identify the actual save/slot/unit before using or persisting its output.
/// </summary>
public static class ActionLearning
{
    public static uint ProjectToReworkedCommand(uint nativeFlags, IReadOnlyList<int> oldActionIds,
        IReadOnlyList<ActionIdentity> newActions, IReadOnlyDictionary<string, bool> learnedByKey)
    {
        Validate(oldActionIds, newActions);
        uint result = nativeFlags & 0xFF0000; // Preserve all reaction/support/movement bits.
        for (int i = 0; i < newActions.Count; i++)
        {
            var action = newActions[i];
            bool learned;
            if (!learnedByKey.TryGetValue(action.Key, out learned) && action.NativeAbilityId is int id)
                for (int old = 0; old < oldActionIds.Count; old++)
                    if (oldActionIds[old] == id) learned = (nativeFlags & (1u << old)) != 0;
            if (learned) result |= 1u << i;
        }
        return result;
    }

    public static uint ProjectBackToNative(uint originalNativeFlags, uint reworkedFlags,
        IReadOnlyList<int> oldActionIds, IReadOnlyList<ActionIdentity> newActions)
    {
        Validate(oldActionIds, newActions);
        uint result = (originalNativeFlags & 0xFFFF) | (reworkedFlags & 0xFF0000);
        for (int i = 0; i < newActions.Count; i++)
            if (newActions[i].NativeAbilityId is int id)
                for (int old = 0; old < oldActionIds.Count; old++)
                    if (oldActionIds[old] == id)
                    {
                        uint bit = 1u << old;
                        result = (result & ~bit) | ((reworkedFlags & (1u << i)) != 0 ? bit : 0);
                    }
        return result;
    }

    private static void Validate(IReadOnlyList<int> oldActionIds, IReadOnlyList<ActionIdentity> actions)
    {
        if (oldActionIds.Count > 16 || actions.Count > 16)
            throw new InvalidDataException("The audited native command has sixteen action-learning slots.");
        if (actions.Any(a => string.IsNullOrWhiteSpace(a.Key)) ||
            actions.Select(a => a.Key).Distinct(StringComparer.Ordinal).Count() != actions.Count)
            throw new InvalidDataException("Action identities must be nonempty and unique.");
        var ids = oldActionIds.Where(id => id != 0).ToArray();
        if (ids.Any(id => id < 0) || ids.Distinct().Count() != ids.Length)
            throw new InvalidDataException("Old action IDs must be unambiguous; zero means an empty slot.");
        var retained = actions.Where(a => a.NativeAbilityId.HasValue).Select(a => a.NativeAbilityId!.Value).ToArray();
        if (retained.Any(id => id <= 0 || !ids.Contains(id)) || retained.Distinct().Count() != retained.Length)
            throw new InvalidDataException("Retained actions must match unique original native IDs.");
    }
}
