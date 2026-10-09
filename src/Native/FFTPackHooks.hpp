#pragma once

#include <cstddef>
#include <filesystem>

namespace fftml
{
    bool InitializeFFTPackOverrides(
        const std::filesystem::path& gameDirectory
    );

    bool InstallFFTPackHooks();
    void RemoveFFTPackHooks();

    std::size_t FFTPackOverrideCount();
}
