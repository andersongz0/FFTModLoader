#pragma once

#include <cstdint>
#include <filesystem>
#include <string>
#include <vector>

namespace fftml
{
    enum class ContentKind
    {
        Raw,
        Tga,
        Tex,
        Mes,
        Lzw,
        Nxd,
        Pac,
        Bin
    };

    const char* ContentKindName(ContentKind kind);

    ContentKind DetectContentKind(
        const std::string& virtualPath
    );

    bool ValidateContentBytes(
        ContentKind kind,
        const std::vector<std::uint8_t>& bytes,
        std::string& reason
    );
}
