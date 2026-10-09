using System.Buffers.Binary;

namespace FFTModLoader.ContentExpansion;

/// <summary>
/// Audited Chemist-specific native learning/Jp fields. Plans retained-ability
/// purchases only. New learning must be stored by save/unit identity before a
/// host may enable it. Does not hook UnitLearnAbility, write memory or notify UI.
/// Host must hash-guard the executable and own the complete confirmation route:
/// the native caller at 2B9AD0 deducts JP AFTER UnitLearnAbility. Applying this
/// plan inside only that callee and then returning would deduct JP twice.
/// </summary>
public static class ChemistNativeLearning
{
    // GetLocalJobNumber(Chemist 75) = 1. The party unit layout differs from the
    // battle worker layout (whose learning starts at +A2) or serialized unit
    // layout (whose learning starts at +32). Never mix these three layouts.
    public const int PartyLearningOffset = (1 + 0x29) * 3; // +7E
    public const int PartyJpOffset = 0xCA + 1 * 2; // +CC
    private static readonly int[] OriginalActions = Enumerable.Range(368, 14).Concat(new[] { 0, 0 }).ToArray();

    public static uint Project(ReadOnlySpan<byte> nativeBytes, IReadOnlyList<ActionIdentity> registeredActions,
        IReadOnlyDictionary<string, bool> persistedNewLearning)
    {
        ArgumentNullException.ThrowIfNull(persistedNewLearning);
        var newKeys = registeredActions.Where(a => a.NativeAbilityId is null).Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        if (persistedNewLearning.Keys.Any(key => !newKeys.Contains(key)))
            throw new InvalidDataException("Expanded learning may not replace native retained learning or unknown keys.");
        return ActionLearning.ProjectToReworkedCommand(NativeLearningBits.Decode(nativeBytes), OriginalActions,
            registeredActions, persistedNewLearning);
    }

    public static IReadOnlyList<GuardedNativeWrite> PlanRetainedPurchase(long unitAddress,
        int nativeAbilityId, ushort cost, Func<long, int, byte[]> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (unitAddress <= 0) throw new ArgumentOutOfRangeException(nameof(unitAddress));
        if (nativeAbilityId is not (368 or 371 or 380 or 381))
            throw new InvalidDataException("Only retained Chemist abilities can use the native old learning slots.");
        int slot = Array.IndexOf(OriginalActions, nativeAbilityId);
        long learningAddress = checked(unitAddress + PartyLearningOffset);
        long jpAddress = checked(unitAddress + PartyJpOffset);
        byte[] learningBefore = read(learningAddress, NativeLearningBits.ByteCount);
        byte[] jpBefore = read(jpAddress, 2);
        uint logicalBefore = NativeLearningBits.Decode(learningBefore);
        if (jpBefore.Length != 2) throw new InvalidDataException("Truncated native JP read.");
        int jp = BinaryPrimitives.ReadInt16LittleEndian(jpBefore);
        if ((logicalBefore & (1u << slot)) != 0)
            throw new InvalidDataException("Ability already learned; no JP may be deducted again.");
        if (jp < cost) throw new InvalidDataException("Insufficient or invalid native JP; no purchase planned.");
        byte[] jpAfter = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(jpAfter, checked((short)(jp - cost)));
        // Original positions, never positions 0/1/2/3 of the reordered menu.
        return new GuardedNativeWrite[]
        {
            new(learningAddress, learningBefore.ToArray(), NativeLearningBits.SetLearned(learningBefore, slot),
                learningAddress, learningBefore.ToArray()),
            new(jpAddress, jpBefore.ToArray(), jpAfter, jpAddress, jpBefore.ToArray()),
        };
    }
}
