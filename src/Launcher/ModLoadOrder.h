#pragma once
#include <algorithm>
#include <map>
#include <string>
#include <vector>

namespace fftloader
{
    inline constexpr const char* ChemistId = "ffttic.jobs.reworkedchemist";
    inline constexpr const char* LegacyChemistId = "ffttic.tests.reworkedchemist.venom";

    inline std::string CanonicalModId(const std::string& id)
    {
        return id == LegacyChemistId ? ChemistId : id;
    }

    struct ModInfo
    {
        std::string id;
        std::vector<std::string> dependencies;
        bool library = false;
    };

    // Prefer infrastructure among ready nodes only. Dependencies always win;
    // alphabetical discovery must never put content ahead of its provider.
    inline int LoadStage(const ModInfo& mod)
    {
        if (mod.library || mod.id == "Reloaded.Memory.SigScan.ReloadedII" ||
            mod.id == "reloaded.sharedlib.hooks") return 0;
        if (mod.id == "fftivc.utility.modloader") return 1;
        if (mod.id == "ffttic.jobs.genericjobs") return 2;
        if (mod.id == "fftmodloader.jobexpansion") return 3;
        if (mod.id == "fftmodloader.contentexpansion") return 4;
        return 5;
    }

    inline bool SortMods(const std::map<std::string, ModInfo>& mods,
                         std::vector<std::string>& sorted, std::string& error)
    {
        sorted.clear();
        error.clear();
        for (const auto& [id, mod] : mods)
            for (const auto& dep : mod.dependencies)
                if (!mods.contains(dep))
                {
                    error = "Dependencia ausente para " + id + ": " + dep;
                    return false;
                }
        while (sorted.size() < mods.size())
        {
            const ModInfo* next = nullptr;
            for (const auto& [id, mod] : mods)
            {
                if (std::find(sorted.begin(), sorted.end(), id) != sorted.end()) continue;
                if (!std::all_of(mod.dependencies.begin(), mod.dependencies.end(), [&](const auto& dep) {
                    return std::find(sorted.begin(), sorted.end(), dep) != sorted.end();
                })) continue;
                if (!next || LoadStage(mod) < LoadStage(*next) ||
                    (LoadStage(mod) == LoadStage(*next) && id < next->id)) next = &mod;
            }
            if (!next)
            {
                error = "Dependencia circular entre mods.";
                sorted.clear();
                return false;
            }
            sorted.push_back(next->id);
        }
        return true;
    }
}
