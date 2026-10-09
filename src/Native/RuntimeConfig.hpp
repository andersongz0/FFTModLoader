#pragma once
#include <filesystem>

namespace fftml
{
    struct RuntimeConfig
    {
        bool logAllResourcePaths = true;
        bool enableContentOverrides = true;

        // Disabled by default because increasing Resource + 0x40 while the
        // original Data pointer remains unchanged can be unsafe for engine
        // code that bypasses the known Read Resource function.
        bool enableExperimentalLargerOverrides = false;

        // Experimental native port of the GenericJobs second-page menu logic.
        bool enableExperimentalJobMenuExpansion = false;

        // Diagnostic capture of the four FFTPack text resources needed to
        // reverse-engineer safe custom Job/Command/Ability text support.
        bool dumpJobTextResources = false;
    };

    const RuntimeConfig& GetRuntimeConfig();

    void LoadRuntimeConfig(
        const std::filesystem::path& gameDirectory
    );
}
