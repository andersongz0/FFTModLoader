namespace GenericJobs;

// UnitSaveData is a 54-element array of 600-byte records in enhanced 1.0.4.0.
// Unlike the battle worker, its identity/sex is at +0 and current job at +2.
internal static class MenuPortraitPolicy
{
    internal const uint UnitCount = 54;
    internal const int RecordSize = 600;
    internal const int SaveDataRva = 0x11A7D10;

    internal static bool IsEligible(uint unitIndex, byte identity, byte job) =>
        unitIndex < UnitCount && identity is 0x80 or 0x81 && job >= 0xA2;
}
