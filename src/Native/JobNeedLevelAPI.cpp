#include "JobNeedLevelAPI.hpp"

#include "ContentResolver.hpp"
#include "JobDataAPI.hpp"
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
#include <unordered_map>

namespace fs = std::filesystem;

namespace
{
#pragma pack(push, 1)
    struct JobNeedLevelData
    {
        std::uint8_t needLevels[12];
    };
#pragma pack(pop)

    static_assert(sizeof(JobNeedLevelData) == 12);

    constexpr int kRequirementRowCount = 22;

    // Source manager locates row 16 with this signature and backs up
    // 16 entries to obtain row 0.
    constexpr const char* kNeedLevelEntry16Pattern =
        "02 00 03 33 50 53 00 00 00 00 00 00 "
        "20 33 40 00 04 00 55 00 00 00 00 00 "
        "88 33 43 33 54 53 55 00 00 00 00 00";

    JobNeedLevelData* g_table = nullptr;
    std::size_t g_applied = 0;

    const std::array<const char*, 24> kRequirementNames =
    {
        "Squire",
        "Chemist",
        "Knight",
        "Archer",
        "Monk",
        "WhiteMage",
        "BlackMage",
        "TimeMage",
        "Summoner",
        "Thief",
        "Orator",
        "Mystic",
        "Geomancer",
        "Dragoon",
        "Samurai",
        "Ninja",
        "Arithmetician",
        "Bard",
        "Dancer",
        "Mime",
        "DarkKnight",
        "OnionKnight",
        "Unknown1",
        "Unknown2"
    };

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
                std::string("[JobNeedLevel] JSON invalido: ") +
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
                        return c == ' ' || c == '_';
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

    int FindRequirementIndex(
        const std::string& key
    )
    {
        std::string normalized = key;

        normalized.erase(
            std::remove_if(
                normalized.begin(),
                normalized.end(),
                [](unsigned char c)
                {
                    return
                        c == ' ' ||
                        c == '_' ||
                        c == '-';
                }
            ),
            normalized.end()
        );

        std::transform(
            normalized.begin(),
            normalized.end(),
            normalized.begin(),
            [](unsigned char c)
            {
                return static_cast<char>(
                    std::tolower(c)
                );
            }
        );

        for (std::size_t i = 0;
             i < kRequirementNames.size();
             ++i)
        {
            std::string candidate =
                kRequirementNames[i];

            std::transform(
                candidate.begin(),
                candidate.end(),
                candidate.begin(),
                [](unsigned char c)
                {
                    return static_cast<char>(
                        std::tolower(c)
                    );
                }
            );

            if (candidate == normalized)
                return static_cast<int>(i);
        }

        return -1;
    }

    std::uint8_t GetRequirementNibble(
        const JobNeedLevelData& row,
        int index
    )
    {
        if (index < 0 || index >= 24)
            return 0;

        const int byteIndex =
            index / 2;

        const bool high =
            (index % 2) == 0;

        const std::uint8_t value =
            row.needLevels[byteIndex];

        return high
            ? static_cast<std::uint8_t>(
                (value >> 4) & 0x0F
            )
            : static_cast<std::uint8_t>(
                value & 0x0F
            );
    }

    void SetRequirementNibble(
        JobNeedLevelData& row,
        int index,
        std::uint8_t level
    )
    {
        if (index < 0 || index >= 24)
            return;

        level &=
            static_cast<std::uint8_t>(0x0F);

        const int byteIndex =
            index / 2;

        const bool high =
            (index % 2) == 0;

        auto& value =
            row.needLevels[byteIndex];

        if (high)
        {
            value =
                static_cast<std::uint8_t>(
                    (value & 0x0F) |
                    (level << 4)
                );
        }
        else
        {
            value =
                static_cast<std::uint8_t>(
                    (value & 0xF0) |
                    level
                );
        }
    }

    bool MakeTableWritable()
    {
        if (!g_table)
            return false;

        DWORD oldProtection = 0;

        return VirtualProtect(
            g_table,
            sizeof(JobNeedLevelData) *
                kRequirementRowCount,
            PAGE_EXECUTE_READWRITE,
            &oldProtection
        ) != FALSE;
    }

    int RequirementSlotFor(
        int jobId,
        const nlohmann::json& job
    )
    {
        if (job.contains("RequirementSlot"))
        {
            if (const auto slot =
                    ParseUnsigned(
                        job["RequirementSlot"]))
            {
                if (*slot <
                    static_cast<std::uint64_t>(
                        kRequirementRowCount))
                {
                    return static_cast<int>(*slot);
                }
            }
        }

        if (jobId >= 0 &&
            jobId < kRequirementRowCount)
        {
            return jobId;
        }

        if (jobId == 0xA0)
            return 20;

        if (jobId == 0xA1)
            return 21;

        return -1;
    }

    void LogRow(
        int rowIndex
    )
    {
        if (!g_table ||
            rowIndex < 0 ||
            rowIndex >= kRequirementRowCount)
        {
            return;
        }

        std::string line =
            "[JobNeedLevel] Row " +
            std::to_string(rowIndex) +
            " => ";

        bool first = true;

        for (int i = 0; i < 24; ++i)
        {
            const auto level =
                GetRequirementNibble(
                    g_table[rowIndex],
                    i
                );

            if (level == 0)
                continue;

            if (!first)
                line += ", ";

            first = false;

            line +=
                std::string(
                    kRequirementNames[
                        static_cast<std::size_t>(i)
                    ]
                ) +
                "=" +
                std::to_string(level);
        }

        if (first)
            line += "sem requisitos";

        fftml::LogInfo(line);
    }
}

namespace fftml
{
    bool InitializeJobNeedLevelAPI()
    {
        g_table = nullptr;
        g_applied = 0;

        const auto entry16 =
            FindPatternInMainModule(
                ParsePattern(
                    kNeedLevelEntry16Pattern
                )
            );

        if (!entry16)
        {
            LogWarning(
                "[JobNeedLevel] Tabela de requisitos nao encontrada."
            );
            return false;
        }

        const auto tableAddress =
            entry16 -
            sizeof(JobNeedLevelData) * 16;

        g_table =
            reinterpret_cast<JobNeedLevelData*>(
                tableAddress
            );

        if (!MakeTableWritable())
        {
            LogError(
                "[JobNeedLevel] Nao foi possivel tornar "
                "a tabela gravavel."
            );

            g_table = nullptr;
            return false;
        }

        char header[180]{};

        std::snprintf(
            header,
            sizeof(header),
            "[JobNeedLevel] Tabela encontrada em 0x%llX | rows=%d | sizeof=%zu",
            static_cast<unsigned long long>(
                tableAddress
            ),
            kRequirementRowCount,
            sizeof(JobNeedLevelData)
        );

        LogInfo(header);

        for (const auto& mod :
             GetLoadedMods())
        {
            const fs::path jobsPath =
                mod.directory / L"jobs.json";

            if (!fs::exists(jobsPath))
                continue;

            const auto root =
                ReadJson(jobsPath);

            if (!root.is_object() ||
                !root.contains("Jobs") ||
                !root["Jobs"].is_array())
            {
                continue;
            }

            for (const auto& job :
                 root["Jobs"])
            {
                if (!job.is_object() ||
                    !job.contains("Id"))
                {
                    continue;
                }

                const auto id =
                    ParseId(job["Id"]);

                if (!id)
                    continue;

                const int slot =
                    RequirementSlotFor(
                        *id,
                        job
                    );

                if (slot < 0 ||
                    slot >= kRequirementRowCount)
                {
                    if (job.contains("Requirements"))
                    {
                        LogWarning(
                            std::string("[JobNeedLevel] ") +
                            mod.uniqueId +
                            " pediu requisitos para Job ID=" +
                            std::to_string(*id) +
                            ", mas nao ha RequirementSlot valido."
                        );
                    }

                    continue;
                }

                bool changed = false;

                if (job.contains(
                        "CloneRequirementsFrom"))
                {
                    if (const auto clone =
                            ParseUnsigned(
                                job[
                                    "CloneRequirementsFrom"
                                ]))
                    {
                        if (*clone <
                            static_cast<std::uint64_t>(
                                kRequirementRowCount))
                        {
                            g_table[slot] =
                                g_table[
                                    static_cast<int>(
                                        *clone
                                    )
                                ];

                            changed = true;
                        }
                    }
                }

                if (job.contains("Requirements") &&
                    job["Requirements"].is_object())
                {
                    for (auto it =
                             job["Requirements"].begin();
                         it !=
                             job["Requirements"].end();
                         ++it)
                    {
                        const int requirementIndex =
                            FindRequirementIndex(
                                it.key()
                            );

                        if (requirementIndex < 0)
                        {
                            LogWarning(
                                std::string("[JobNeedLevel] Campo desconhecido: ") +
                                it.key()
                            );
                            continue;
                        }

                        const auto level =
                            ParseUnsigned(
                                it.value()
                            );

                        if (!level)
                            continue;

                        if (*level > 15)
                        {
                            LogWarning(
                                std::string("[JobNeedLevel] ") +
                                it.key() +
                                " usa nivel > 15; valor limitado a 15."
                            );
                        }

                        SetRequirementNibble(
                            g_table[slot],
                            requirementIndex,
                            static_cast<std::uint8_t>(
                                std::min<std::uint64_t>(
                                    *level,
                                    15
                                )
                            )
                        );

                        changed = true;
                    }
                }

                if (changed)
                {
                    ++g_applied;

                    LogInfo(
                        std::string("[JobNeedLevel] ") +
                        mod.uniqueId +
                        " aplicou RequirementSlot=" +
                        std::to_string(slot) +
                        " para Job ID=" +
                        std::to_string(*id)
                    );

                    LogRow(slot);
                }
            }
        }

        LogInfo(
            std::string("[JobNeedLevel] Patches aplicados: ") +
            std::to_string(g_applied)
        );

        return true;
    }

    std::size_t AppliedJobRequirementPatchCount()
    {
        return g_applied;
    }

    bool JobNeedLevelTableFound()
    {
        return g_table != nullptr;
    }
}
