#include "ContentTypes.hpp"

#include <algorithm>
#include <cctype>

namespace
{
    std::string ExtensionOf(const std::string& path)
    {
        const auto slash = path.find_last_of("/\\");
        const auto dot = path.find_last_of('.');

        if (dot == std::string::npos ||
            (slash != std::string::npos && dot < slash))
        {
            return {};
        }

        std::string ext = path.substr(dot);

        std::transform(
            ext.begin(),
            ext.end(),
            ext.begin(),
            [](unsigned char c)
            {
                return static_cast<char>(std::tolower(c));
            }
        );

        return ext;
    }
}

namespace fftml
{
    const char* ContentKindName(ContentKind kind)
    {
        switch (kind)
        {
            case ContentKind::Tga: return "TGA";
            case ContentKind::Tex: return "TEX";
            case ContentKind::Mes: return "MES";
            case ContentKind::Lzw: return "LZW";
            case ContentKind::Nxd: return "NXD";
            case ContentKind::Pac: return "PAC";
            case ContentKind::Bin: return "BIN";
            default: return "RAW";
        }
    }

    ContentKind DetectContentKind(
        const std::string& virtualPath
    )
    {
        const std::string ext = ExtensionOf(virtualPath);

        if (ext == ".tga") return ContentKind::Tga;
        if (ext == ".tex") return ContentKind::Tex;
        if (ext == ".mes") return ContentKind::Mes;
        if (ext == ".lzw") return ContentKind::Lzw;
        if (ext == ".nxd") return ContentKind::Nxd;
        if (ext == ".pac") return ContentKind::Pac;
        if (ext == ".bin") return ContentKind::Bin;

        return ContentKind::Raw;
    }

    bool ValidateContentBytes(
        ContentKind kind,
        const std::vector<std::uint8_t>& bytes,
        std::string& reason
    )
    {
        if (bytes.empty())
        {
            reason = "arquivo vazio";
            return false;
        }

        switch (kind)
        {
            case ContentKind::Tga:
            {
                if (bytes.size() < 18)
                {
                    reason = "header TGA menor que 18 bytes";
                    return false;
                }

                const std::uint8_t imageType = bytes[2];

                if (imageType != 2 &&
                    imageType != 3 &&
                    imageType != 10 &&
                    imageType != 11)
                {
                    reason = "tipo de imagem TGA nao reconhecido";
                    return false;
                }

                const std::uint16_t width =
                    static_cast<std::uint16_t>(
                        bytes[12] |
                        (bytes[13] << 8)
                    );

                const std::uint16_t height =
                    static_cast<std::uint16_t>(
                        bytes[14] |
                        (bytes[15] << 8)
                    );

                if (width == 0 || height == 0)
                {
                    reason = "dimensoes TGA invalidas";
                    return false;
                }

                return true;
            }

            case ContentKind::Pac:
            {
                if (bytes.size() < 4 ||
                    bytes[0] != 'P' ||
                    bytes[1] != 'A' ||
                    bytes[2] != 'C' ||
                    bytes[3] != 'K')
                {
                    reason = "PAC sem magic PACK";
                    return false;
                }

                return true;
            }

            // We can identify these types by extension, but we do not yet
            // have a safe semantic parser for them. Non-empty is sufficient
            // for the binary replacement layer.
            case ContentKind::Tex:
            case ContentKind::Mes:
            case ContentKind::Lzw:
            case ContentKind::Nxd:
            case ContentKind::Bin:
            case ContentKind::Raw:
            default:
                return true;
        }
    }
}
