#pragma once

#include "ContentTypes.hpp"

#include <cstdint>
#include <filesystem>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

namespace fftml
{
    enum class ContentAction
    {
        None,
        Replace,
        Edit,
        Load
    };

    struct BinaryPatch
    {
        std::uint64_t offset = 0;
        std::vector<std::uint8_t> bytes;

        std::string modName;
        std::string modUniqueId;
    };

    struct ModManifest
    {
        std::string name;
        std::string author;
        std::string version;
        std::string uniqueId;

        int priority = 0;
        bool enabled = true;

        std::filesystem::path directory;
    };

    struct ContentPlan
    {
        std::string virtualPath;
        ContentKind kind = ContentKind::Raw;

        ContentAction baseAction = ContentAction::None;
        std::filesystem::path baseFile;

        std::string baseModName;
        std::string baseModUniqueId;
        int basePriority = 0;

        std::vector<BinaryPatch> patches;

        // Set when "Load" targets a path which may not exist in the
        // original game. Full synthetic Resource creation is not yet
        // implemented; the hook logs this cleanly instead of crashing.
        bool mayRequireSyntheticResource = false;
    };

    bool InitializeContentResolver(
        const std::filesystem::path& gameDirectory
    );

    const ContentPlan* ResolveContentPlan(
        std::string_view virtualPath
    );

    std::shared_ptr<const std::vector<std::uint8_t>>
    GetBaseBytes(
        const ContentPlan& plan
    );

    void ApplyBinaryPatches(
        const ContentPlan& plan,
        std::uint64_t readOffset,
        void* destination,
        std::size_t length
    );

    std::string NormalizeVirtualPath(
        std::string_view path
    );

    std::size_t LoadedModCount();
    std::size_t ContentPlanCount();

    const std::vector<ModManifest>& GetLoadedMods();

    std::vector<const ContentPlan*> GetAllContentPlans();

    bool IsGeneratedPackTarget(std::string_view virtualPath);
}
