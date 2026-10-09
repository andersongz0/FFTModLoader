#pragma once

#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace fftml
{
    enum class JobMenuPresentation
    {
        Auto,
        Actual,
        DarkKnight,
        OnionKnight
    };

    struct JobTextOverride
    {
        std::string from;
        std::string to;
    };

    struct JobAbilityNameOverride
    {
        std::uint16_t abilityId = 0;
        std::string displayName;
    };

    struct JobPresentationInfo
    {
        std::uint16_t jobId = 0;
        // Optional native Job used only while the game recalculates the
        // unit's spriteset/portrait fields. The custom Job ID is restored
        // immediately after that native call.
        std::uint16_t unitVisualSourceJobId = 0xFFFF;
        JobMenuPresentation menuPresentation = JobMenuPresentation::Auto;

        std::string displayNameMetadata;
        std::string descriptionMetadata;

        // Contextual UI text replacements. These are intentionally scoped to
        // the selected custom Job and do not rename the base-game text
        // globally. 0.10.15 uses this first for Chant -> Sanguine Sword.
        std::vector<JobTextOverride> abilityTextOverrides;

        // Preferred 0.10.19 presentation mapping. Unlike the old text-to-text
        // compatibility override, this remains usable when the game's hidden
        // WotL ability name is blank.
        std::vector<JobAbilityNameOverride> abilityNameOverrides;

        // Optional requirement table row (0..21).
        int requirementSlot = -1;
    };

    bool InitializeJobDataAPI();

    const std::vector<std::uint16_t>& GetMenuJobIds();

    const JobPresentationInfo* GetJobPresentationInfo(
        std::uint16_t jobId
    );

    JobMenuPresentation GetJobMenuPresentation(
        std::uint16_t jobId
    );

    int GetJobRequirementSlot(
        std::uint16_t jobId
    );

    std::size_t AppliedJobPatchCount();
    bool JobDataTableFound();
}
