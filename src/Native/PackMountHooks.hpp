#pragma once

#include <filesystem>

namespace fftml
{
    bool InstallPackMountHooks(
        const std::filesystem::path& gameDirectory
    );

    void RemovePackMountHooks();

    bool IsGeneratedModPackPresent();
    bool IsGeneratedModPackMounted();
}
