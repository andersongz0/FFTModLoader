#include "JobCommandAPI.hpp"

#include "ContentResolver.hpp"
#include "Logger.hpp"
#include "SignatureScanner.hpp"

#include <windows.h>

#include <nlohmann/json.hpp>

#include <algorithm>
#include <array>
#include <cctype>
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <optional>
#include <string>
#include <vector>

namespace fs = std::filesystem;

namespace
{
#pragma pack(push, 1)
    struct JobCommandData
    {
        std::uint16_t extendAbilityFlags;
        std::uint8_t extendRsmFlags;

        std::uint8_t abilities[16];
        std::uint8_t rsm[6];
    };
#pragma pack(pop)

    static_assert(sizeof(JobCommandData) == 25);

    constexpr int BaseCommandCount = 176;
    constexpr int BaseCommandMaxId = 175;

    constexpr int WotlFirstId = 224;
    constexpr int WotlLastId = 226;
    constexpr int WotlCommandCount =
        WotlLastId - WotlFirstId + 1;

    // Reference source: FFTOJobCommandDataManager.
    // This pattern points to base command entry 5. The table starts five
    // 25-byte records before the match.
    constexpr const char* BaseCommandEntry5Pattern =
        "00 00 FC 92 93 94 95 00 00 00 00 00 00 00 00 00 00 00 00";

    // WotL command 224 / Darkness:
    // flags=0x0008, RSM flags=0xF0,
    // abilities low bytes = 45,184,219,220,101(357-256)
    constexpr const char* WotlCommand224Pattern =
        "08 00 F0 2D B8 DB DC 65 00 00 00 00";

    JobCommandData* g_baseTable = nullptr;
    JobCommandData* g_wotlTable = nullptr;
    std::size_t g_applied = 0;

    nlohmann::json ReadJson(
        const fs::path& path
    )
    {
        std::ifstream input(path);

        if (!input)
            return {};

        try
        {
            nlohmann::json json;
            input >> json;
            return json;
        }
        catch (const std::exception& ex)
        {
            fftml::LogError(
                std::string("[JobCommand] JSON invalido: ") +
                path.generic_string() +
                " | " +
                ex.what()
            );
            return {};
        }
    }

    std::optional<std::uint64_t> ParseUnsigned(
        const nlohmann::json& value
    )
    {
        try
        {
            if (value.is_number_unsigned())
                return value.get<std::uint64_t>();

            if (value.is_number_integer())
            {
                const auto v =
                    value.get<std::int64_t>();

                if (v < 0)
                    return std::nullopt;

                return static_cast<std::uint64_t>(v);
            }

            if (!value.is_string())
                return std::nullopt;

            std::string text =
                value.get<std::string>();

            text.erase(
                std::remove_if(
                    text.begin(),
                    text.end(),
                    [](unsigned char c)
                    {
                        return c == ' ' ||
                               c == '_';
                    }
                ),
                text.end()
            );

            int base = 10;

            if (text.rfind("0x", 0) == 0 ||
                text.rfind("0X", 0) == 0)
            {
                text.erase(0, 2);
                base = 16;
            }
            else if (std::any_of(
                         text.begin(),
                         text.end(),
                         [](unsigned char c)
                         {
                             return
                                 (c >= 'A' && c <= 'F') ||
                                 (c >= 'a' && c <= 'f');
                         }))
            {
                base = 16;
            }

            if (text.empty())
                return std::nullopt;

            return std::stoull(
                text,
                nullptr,
                base
            );
        }
        catch (...)
        {
            return std::nullopt;
        }
    }

    std::optional<int> ParseId(
        const nlohmann::json& value
    )
    {
        const auto parsed =
            ParseUnsigned(value);

        if (!parsed ||
            *parsed > 0xFFFF)
        {
            return std::nullopt;
        }

        return static_cast<int>(*parsed);
    }

    JobCommandData* GetCommand(
        int id
    )
    {
        if (id >= 0 &&
            id <= BaseCommandMaxId)
        {
            return g_baseTable
                ? &g_baseTable[id]
                : nullptr;
        }

        if (id >= WotlFirstId &&
            id <= WotlLastId)
        {
            return g_wotlTable
                ? &g_wotlTable[
                      id - WotlFirstId
                  ]
                : nullptr;
        }

        return nullptr;
    }

    bool IsValidCommandId(
        int id
    )
    {
        return
            (id >= 0 &&
             id <= BaseCommandMaxId) ||
            (id >= WotlFirstId &&
             id <= WotlLastId);
    }

    bool WriteWithTemporaryProtection(
        JobCommandData* target,
        const JobCommandData& value
    )
    {
        if (!target)
            return false;

        DWORD oldProtection = 0;

        if (!VirtualProtect(
                target,
                sizeof(JobCommandData),
                PAGE_EXECUTE_READWRITE,
                &oldProtection))
        {
            fftml::LogError(
                "[JobCommand] VirtualProtect falhou."
            );
            return false;
        }

        *target = value;

        DWORD ignored = 0;

        VirtualProtect(
            target,
            sizeof(JobCommandData),
            oldProtection,
            &ignored
        );

        FlushInstructionCache(
            GetCurrentProcess(),
            target,
            sizeof(JobCommandData)
        );

        return true;
    }

    void EncodeAbilities(
        JobCommandData& data,
        const nlohmann::json& array
    )
    {
        std::fill(
            std::begin(data.abilities),
            std::end(data.abilities),
            static_cast<std::uint8_t>(0)
        );

        data.extendAbilityFlags = 0;

        const std::size_t count =
            std::min<std::size_t>(
                array.size(),
                16
            );

        for (std::size_t i = 0;
             i < count;
             ++i)
        {
            const auto parsed =
                ParseUnsigned(array[i]);

            if (!parsed ||
                *parsed > 511)
            {
                continue;
            }

            std::uint16_t id =
                static_cast<std::uint16_t>(
                    *parsed
                );

            if (id >= 256)
            {
                // Flags are reversed inside each group of 8:
                // slot 0 -> bit 7, slot 7 -> bit 0,
                // slot 8 -> bit 15, slot 15 -> bit 8.
                const int bit =
                    i < 8
                        ? 7 - static_cast<int>(i)
                        : 23 - static_cast<int>(i);

                data.extendAbilityFlags |=
                    static_cast<std::uint16_t>(
                        1u << bit
                    );

                id =
                    static_cast<std::uint16_t>(
                        id - 256
                    );
            }

            data.abilities[i] =
                static_cast<std::uint8_t>(id);
        }
    }

    void EncodeRsm(
        JobCommandData& data,
        const nlohmann::json& array
    )
    {
        std::fill(
            std::begin(data.rsm),
            std::end(data.rsm),
            static_cast<std::uint8_t>(0)
        );

        data.extendRsmFlags = 0;

        const std::size_t count =
            std::min<std::size_t>(
                array.size(),
                6
            );

        for (std::size_t i = 0;
             i < count;
             ++i)
        {
            const auto parsed =
                ParseUnsigned(array[i]);

            if (!parsed ||
                *parsed > 511)
            {
                continue;
            }

            std::uint16_t id =
                static_cast<std::uint16_t>(
                    *parsed
                );

            if (id >= 256)
            {
                const int bit =
                    7 - static_cast<int>(i);

                data.extendRsmFlags |=
                    static_cast<std::uint8_t>(
                        1u << bit
                    );

                id =
                    static_cast<std::uint16_t>(
                        id - 256
                    );
            }

            data.rsm[i] =
                static_cast<std::uint8_t>(id);
        }
    }

    std::size_t CountNonZero(
        const std::uint8_t* values,
        std::size_t count
    )
    {
        std::size_t result = 0;

        for (std::size_t i = 0;
             i < count;
             ++i)
        {
            if (values[i] != 0)
                ++result;
        }

        return result;
    }

    void ApplyCommandFile(
        const fftml::ModManifest& mod
    )
    {
        const fs::path path =
            mod.directory /
            L"job_commands.json";

        if (!fs::exists(path))
            return;

        const auto root =
            ReadJson(path);

        if (!root.is_object() ||
            !root.contains("Commands") ||
            !root["Commands"].is_array())
        {
            fftml::LogError(
                std::string("[JobCommand] Formato invalido em ") +
                mod.uniqueId +
                ": esperado { \"Commands\": [...] }"
            );
            return;
        }

        for (const auto& command :
             root["Commands"])
        {
            if (!command.is_object() ||
                !command.contains("Id"))
            {
                continue;
            }

            const auto idOpt =
                ParseId(command["Id"]);

            if (!idOpt ||
                !IsValidCommandId(*idOpt))
            {
                fftml::LogWarning(
                    std::string("[JobCommand] ID ignorado em ") +
                    mod.uniqueId +
                    ". Suportado: 0..175 ou 224..226."
                );
                continue;
            }

            const int id = *idOpt;

            JobCommandData* target =
                GetCommand(id);

            if (!target)
            {
                fftml::LogWarning(
                    std::string("[JobCommand] Tabela indisponivel para ID ") +
                    std::to_string(id) +
                    " | " +
                    mod.uniqueId
                );
                continue;
            }

            JobCommandData patched =
                *target;

            bool changed = false;

            if (command.contains("Abilities") &&
                command["Abilities"].is_array())
            {
                EncodeAbilities(
                    patched,
                    command["Abilities"]
                );

                changed = true;
            }

            const nlohmann::json* rsmArray =
                nullptr;

            if (command.contains("RSM") &&
                command["RSM"].is_array())
            {
                rsmArray =
                    &command["RSM"];
            }
            else if (
                command.contains(
                    "ReactionSupportMovement"
                ) &&
                command[
                    "ReactionSupportMovement"
                ].is_array())
            {
                rsmArray =
                    &command[
                        "ReactionSupportMovement"
                    ];
            }

            if (rsmArray)
            {
                EncodeRsm(
                    patched,
                    *rsmArray
                );

                changed = true;
            }

            if (!changed)
            {
                fftml::LogInfo(
                    std::string("[JobCommand] ") +
                    mod.uniqueId +
                    " registrou Command ID=" +
                    std::to_string(id) +
                    " sem alteracoes."
                );
                continue;
            }

            if (!WriteWithTemporaryProtection(
                    target,
                    patched))
            {
                continue;
            }

            ++g_applied;

            fftml::LogInfo(
                std::string("[JobCommand] ") +
                mod.uniqueId +
                " aplicou Command ID=" +
                std::to_string(id) +
                " | abilities=" +
                std::to_string(
                    CountNonZero(
                        patched.abilities,
                        16
                    )
                ) +
                " | rsm=" +
                std::to_string(
                    CountNonZero(
                        patched.rsm,
                        6
                    )
                )
            );
        }
    }
}

namespace fftml
{
    bool InitializeJobCommandAPI()
    {
        g_applied = 0;
        g_baseTable = nullptr;
        g_wotlTable = nullptr;

        const auto baseMatch =
            FindPatternInMainModule(
                ParsePattern(
                    BaseCommandEntry5Pattern
                )
            );

        if (baseMatch)
        {
            g_baseTable =
                reinterpret_cast<JobCommandData*>(
                    baseMatch -
                    sizeof(JobCommandData) * 5
                );

            char line[200]{};

            std::snprintf(
                line,
                sizeof(line),
                "[JobCommand] Tabela base encontrada em %p | entries=%d | sizeof=%zu",
                static_cast<void*>(g_baseTable),
                BaseCommandCount,
                sizeof(JobCommandData)
            );

            LogInfo(line);
        }
        else
        {
            LogWarning(
                "[JobCommand] Tabela base 0..175 nao encontrada."
            );
        }

        const auto wotlMatch =
            FindPatternInMainModule(
                ParsePattern(
                    WotlCommand224Pattern
                )
            );

        if (wotlMatch)
        {
            g_wotlTable =
                reinterpret_cast<JobCommandData*>(
                    wotlMatch
                );

            char line[220]{};

            std::snprintf(
                line,
                sizeof(line),
                "[JobCommand] Tabela WotL encontrada em %p | IDs=224..226 | sizeof=%zu",
                static_cast<void*>(g_wotlTable),
                sizeof(JobCommandData)
            );

            LogInfo(line);
        }
        else
        {
            LogWarning(
                "[JobCommand] Tabela WotL 224..226 nao encontrada."
            );
        }

        if (!g_baseTable &&
            !g_wotlTable)
        {
            return false;
        }

        for (const auto& mod :
             GetLoadedMods())
        {
            ApplyCommandFile(mod);
        }

        LogInfo(
            std::string("[JobCommand] Patches aplicados: ") +
            std::to_string(g_applied)
        );

        if (g_wotlTable)
        {
            const JobCommandData& darkness = g_wotlTable[0];
            char line[240]{};
            std::snprintf(
                line,
                sizeof(line),
                "[JobCommand] ID 224 runtime: AbilityFlags=%04X RsmFlags=%02X Abilities=%u,%u,%u,%u,%u",
                static_cast<unsigned int>(darkness.extendAbilityFlags),
                static_cast<unsigned int>(darkness.extendRsmFlags),
                static_cast<unsigned int>(darkness.abilities[0]),
                static_cast<unsigned int>(darkness.abilities[1]),
                static_cast<unsigned int>(darkness.abilities[2]),
                static_cast<unsigned int>(darkness.abilities[3]),
                static_cast<unsigned int>(darkness.abilities[4])
            );
            LogInfo(line);
        }

        return true;
    }

    std::size_t AppliedJobCommandPatchCount()
    {
        return g_applied;
    }

    bool JobCommandBaseTableFound()
    {
        return g_baseTable != nullptr;
    }

    bool JobCommandWotlTableFound()
    {
        return g_wotlTable != nullptr;
    }
}
