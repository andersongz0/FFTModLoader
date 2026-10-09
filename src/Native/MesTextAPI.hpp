#pragma once

#include <cstddef>
#include <cstdint>
#include <memory>
#include <string_view>
#include <vector>

namespace fftml
{
    bool InitializeMesTextAPI();

    bool HasMesTextPatches(
        std::string_view virtualPath
    );

    std::shared_ptr<
        const std::vector<std::uint8_t>
    > BuildPatchedMes(
        std::string_view virtualPath,
        const std::vector<std::uint8_t>& original
    );

    std::size_t MesTextPatchCount();
}
