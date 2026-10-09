#pragma once

#include <cstdint>
#include <optional>
#include <string_view>
#include <vector>

namespace fftml
{
    struct PatternByte
    {
        std::uint8_t value{};
        bool wildcard{};
    };

    std::vector<PatternByte> ParsePattern(std::string_view pattern);

    std::uintptr_t FindPatternInMainModule(
        const std::vector<PatternByte>& pattern
    );
}
