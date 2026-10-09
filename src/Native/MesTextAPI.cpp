#include "MesTextAPI.hpp"

#include "ContentResolver.hpp"
#include "Logger.hpp"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <array>
#include <cctype>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

namespace fs = std::filesystem;

namespace
{
    struct MessagePatch
    {
        std::string text;
        std::string modUniqueId;
        int priority = 0;
    };

    using PatchTable =
        std::map<std::size_t, MessagePatch>;

    std::unordered_map<
        std::string,
        PatchTable
    > g_patches;

    std::size_t g_patchCount = 0;

    constexpr std::string_view ExtendedTextPath =
        "fftpack/text_sys/9999_extend.lzw";

    constexpr std::string_view CommandNamePath =
        "fftpack/text_sys/9999_sys_commandn.mes";

    constexpr std::string_view AbilityNamePath =
        "fftpack/text_sys/9999_sys_abilityn.mes";

    constexpr std::string_view JobHelpPath =
        "fftpack/text_sys/9999_sys_jobcha_h.mes";

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
                std::string("[MesText] JSON invalido: ") +
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

                return
                    static_cast<std::uint64_t>(
                        v
                    );
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
                        return
                            c == ' ' ||
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

    std::optional<std::size_t> ParseIndex(
        const nlohmann::json& value
    )
    {
        const auto parsed =
            ParseUnsigned(value);

        if (!parsed)
            return std::nullopt;

        return
            static_cast<std::size_t>(
                *parsed
            );
    }

    void PutPatch(
        std::string_view path,
        std::size_t index,
        std::string text,
        const fftml::ModManifest& mod
    )
    {
        if (text.empty())
            return;

        auto& table =
            g_patches[
                std::string(path)
            ];

        const auto existing =
            table.find(index);

        if (existing != table.end())
        {
            if (mod.priority <
                existing->second.priority)
            {
                return;
            }

            if (mod.priority ==
                    existing->second.priority &&
                mod.uniqueId <
                    existing->second.modUniqueId)
            {
                return;
            }

            fftml::LogWarning(
                std::string("[MesText] Sobrescrevendo mensagem ") +
                std::string(path) +
                " #" +
                std::to_string(index) +
                " | " +
                existing->second.modUniqueId +
                " -> " +
                mod.uniqueId
            );
        }
        else
        {
            ++g_patchCount;
        }

        table[index] = {
            std::move(text),
            mod.uniqueId,
            mod.priority
        };
    }

    void LoadJobText(
        const fftml::ModManifest& mod
    )
    {
        const fs::path path =
            mod.directory /
            L"jobs.json";

        if (!fs::exists(path))
            return;

        const auto root =
            ReadJson(path);

        if (!root.is_object() ||
            !root.contains("Jobs") ||
            !root["Jobs"].is_array())
        {
            return;
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
                ParseIndex(job["Id"]);

            if (!id)
                continue;

            // Job names for cloned/extended jobs are owned by the locale NXD
            // rows.  Reloaded's working GenericJobs implementation does not
            // add A0/A1 entries to 9999_extend.lzw.  Injecting those IDs here
            // corrupts the formatter's lookup contract and makes UI paths
            // print literal macros such as <jobname(lnum0, lnum1)>.
            if (job.contains("DisplayName") &&
                job["DisplayName"].is_string())
            {
                fftml::LogInfo(
                    std::string("[MesText] JobName mantido no NXD (sem patch MES): ID=") +
                    std::to_string(*id) +
                    " -> " +
                    job["DisplayName"]
                        .get<std::string>()
                );
            }

            // Do NOT patch JobHelp by a guessed numeric index.
            // The corrected real dump proves the old test index 21 belongs
            // to Squire text, not hidden A0.
            if (job.contains("DescriptionIndex"))
            {
                fftml::LogWarning(
                    std::string("[MesText] DescriptionIndex ignorado para Job ID=") +
                    std::to_string(*id) +
                    "; lookup real de JobHelp ainda nao foi identificado."
                );
            }
        }
    }

    void LoadCommandText(
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

            const auto id =
                ParseIndex(command["Id"]);

            if (!id)
                continue;

            if (command.contains("DisplayName") &&
                command["DisplayName"].is_string())
            {
                PutPatch(
                    CommandNamePath,
                    *id,
                    command["DisplayName"]
                        .get<std::string>(),
                    mod
                );
            }

            if (command.contains("AbilityNames") &&
                command["AbilityNames"].is_object())
            {
                for (auto it =
                         command["AbilityNames"].begin();
                     it !=
                         command["AbilityNames"].end();
                     ++it)
                {
                    if (!it.value().is_string())
                        continue;

                    nlohmann::json key =
                        it.key();

                    const auto abilityId =
                        ParseIndex(key);

                    if (!abilityId)
                        continue;

                    PutPatch(
                        AbilityNamePath,
                        *abilityId,
                        it.value()
                            .get<std::string>(),
                        mod
                    );
                }
            }
        }
    }

    std::optional<std::uint8_t>
    EncodeAsciiCharacter(
        char c
    )
    {
        if (c >= 'A' &&
            c <= 'Z')
        {
            return
                static_cast<std::uint8_t>(
                    0x0A +
                    (c - 'A')
                );
        }

        if (c >= 'a' &&
            c <= 'z')
        {
            return
                static_cast<std::uint8_t>(
                    0x24 +
                    (c - 'a')
                );
        }

        if (c >= '0' &&
            c <= '9')
        {
            return
                static_cast<std::uint8_t>(
                    c - '0'
                );
        }

        switch (c)
        {
            case ' ':
                return 0x88;

            case '\n':
                return 0xF8;

            case '.':
                return 0x78;

            case ',':
                return 0x8B;

            case '\'':
                return 0x7D;

            case '-':
                return 0x77;

            case '+':
                return 0x76;

            default:
                return std::nullopt;
        }
    }

    std::vector<std::uint8_t>
    EncodeText(
        const std::string& text,
        const MessagePatch& patch,
        std::string_view path,
        std::size_t index
    )
    {
        std::vector<std::uint8_t> result;
        result.reserve(text.size());

        bool warned = false;

        for (char c : text)
        {
            const auto encoded =
                EncodeAsciiCharacter(c);

            if (encoded)
            {
                result.push_back(*encoded);
                continue;
            }

            // Until the full FFT character table is documented, unsupported
            // characters become spaces instead of producing corrupt control
            // codes.
            result.push_back(0x88);

            if (!warned)
            {
                warned = true;

                fftml::LogWarning(
                    std::string("[MesText] Caracter nao suportado em ") +
                    patch.modUniqueId +
                    " | " +
                    std::string(path) +
                    " #" +
                    std::to_string(index) +
                    ". Substituido por espaco."
                );
            }
        }

        return result;
    }

    constexpr std::size_t ExtendOffsetCount = 32;
    constexpr std::size_t ExtendHeaderSize =
        ExtendOffsetCount *
        sizeof(std::uint32_t);

    constexpr std::size_t ExtendJobNameSection = 2;

    std::uint32_t ReadLe32(
        const std::vector<std::uint8_t>& data,
        std::size_t offset
    )
    {
        return
            static_cast<std::uint32_t>(
                data[offset + 0]
            ) |
            (static_cast<std::uint32_t>(
                data[offset + 1]
            ) << 8) |
            (static_cast<std::uint32_t>(
                data[offset + 2]
            ) << 16) |
            (static_cast<std::uint32_t>(
                data[offset + 3]
            ) << 24);
    }

    void WriteLe32(
        std::vector<std::uint8_t>& data,
        std::size_t offset,
        std::uint32_t value
    )
    {
        data[offset + 0] =
            static_cast<std::uint8_t>(
                value & 0xFF
            );

        data[offset + 1] =
            static_cast<std::uint8_t>(
                (value >> 8) & 0xFF
            );

        data[offset + 2] =
            static_cast<std::uint8_t>(
                (value >> 16) & 0xFF
            );

        data[offset + 3] =
            static_cast<std::uint8_t>(
                (value >> 24) & 0xFF
            );
    }

    std::vector<
        std::vector<std::uint8_t>
    > SplitMes(
        const std::vector<std::uint8_t>& data
    )
    {
        std::vector<
            std::vector<std::uint8_t>
        > entries;

        entries.emplace_back();

        for (std::uint8_t value : data)
        {
            if (value == 0xFE)
            {
                entries.emplace_back();
            }
            else
            {
                entries.back()
                    .push_back(value);
            }
        }

        return entries;
    }

    std::vector<std::uint8_t>
    JoinMes(
        const std::vector<
            std::vector<std::uint8_t>
        >& entries
    )
    {
        std::size_t total = 0;

        for (const auto& entry : entries)
            total += entry.size();

        if (!entries.empty())
            total += entries.size() - 1;

        std::vector<std::uint8_t> result;
        result.reserve(total);

        for (std::size_t i = 0;
             i < entries.size();
             ++i)
        {
            if (i != 0)
                result.push_back(0xFE);

            result.insert(
                result.end(),
                entries[i].begin(),
                entries[i].end()
            );
        }

        return result;
    }

    std::shared_ptr<
        const std::vector<std::uint8_t>
    > BuildPatchedExtend(
        const std::vector<std::uint8_t>& original,
        const PatchTable& table
    )
    {
        if (original.size() <
                ExtendHeaderSize ||
            table.empty())
        {
            return nullptr;
        }

        std::array<
            std::uint32_t,
            ExtendOffsetCount
        > offsets{};

        for (std::size_t i = 0;
             i < ExtendOffsetCount;
             ++i)
        {
            offsets[i] =
                ReadLe32(
                    original,
                    i * 4
                );
        }

        for (std::size_t i = 1;
             i < ExtendOffsetCount;
             ++i)
        {
            if (offsets[i] <
                offsets[i - 1])
            {
                fftml::LogWarning(
                    "[MesText] 9999_extend.lzw possui offsets invalidos."
                );
                return nullptr;
            }
        }

        const std::size_t sectionStart =
            ExtendHeaderSize +
            static_cast<std::size_t>(
                offsets[
                    ExtendJobNameSection
                ]
            );

        const std::size_t sectionEnd =
            ExtendHeaderSize +
            static_cast<std::size_t>(
                offsets[
                    ExtendJobNameSection + 1
                ]
            );

        if (sectionStart >
                sectionEnd ||
            sectionEnd >
                original.size())
        {
            fftml::LogWarning(
                "[MesText] Secao de nomes de Job fora dos limites em 9999_extend.lzw."
            );
            return nullptr;
        }

        std::vector<std::uint8_t>
            jobSection(
                original.begin() +
                    static_cast<std::ptrdiff_t>(
                        sectionStart
                    ),
                original.begin() +
                    static_cast<std::ptrdiff_t>(
                        sectionEnd
                    )
            );

        auto entries =
            SplitMes(jobSection);

        for (const auto& [index, patch] :
             table)
        {
            if (index >= entries.size())
            {
                entries.resize(
                    index + 1
                );
            }

            entries[index] =
                EncodeText(
                    patch.text,
                    patch,
                    ExtendedTextPath,
                    index
                );

            fftml::LogInfo(
                std::string("[MesText] Extend JobName #") +
                std::to_string(index) +
                " <- " +
                patch.text +
                " | " +
                patch.modUniqueId
            );
        }

        const auto rebuiltSection =
            JoinMes(entries);

        const std::int64_t delta =
            static_cast<std::int64_t>(
                rebuiltSection.size()
            ) -
            static_cast<std::int64_t>(
                sectionEnd -
                sectionStart
            );

        std::vector<std::uint8_t> rebuilt;
        rebuilt.reserve(
            static_cast<std::size_t>(
                static_cast<std::int64_t>(
                    original.size()
                ) +
                delta
            )
        );

        rebuilt.insert(
            rebuilt.end(),
            original.begin(),
            original.begin() +
                static_cast<std::ptrdiff_t>(
                    sectionStart
                )
        );

        rebuilt.insert(
            rebuilt.end(),
            rebuiltSection.begin(),
            rebuiltSection.end()
        );

        rebuilt.insert(
            rebuilt.end(),
            original.begin() +
                static_cast<std::ptrdiff_t>(
                    sectionEnd
                ),
            original.end()
        );

        if (delta != 0)
        {
            for (std::size_t i =
                     ExtendJobNameSection + 1;
                 i < ExtendOffsetCount;
                 ++i)
            {
                const std::int64_t adjusted =
                    static_cast<std::int64_t>(
                        offsets[i]
                    ) +
                    delta;

                if (adjusted < 0 ||
                    adjusted >
                        0xFFFFFFFFll)
                {
                    fftml::LogWarning(
                        "[MesText] Offset ajustado invalido em 9999_extend.lzw."
                    );
                    return nullptr;
                }

                WriteLe32(
                    rebuilt,
                    i * 4,
                    static_cast<std::uint32_t>(
                        adjusted
                    )
                );
            }
        }

        fftml::LogInfo(
            std::string("[MesText] 9999_extend.lzw reconstruido | original=") +
            std::to_string(
                original.size()
            ) +
            " | mod=" +
            std::to_string(
                rebuilt.size()
            ) +
            " | secaoJob delta=" +
            std::to_string(
                delta
            )
        );

        return
            std::make_shared<
                const std::vector<std::uint8_t>
            >(
                std::move(rebuilt)
            );
    }
}

namespace fftml
{
    bool InitializeMesTextAPI()
    {
        g_patches.clear();
        g_patchCount = 0;

        for (const auto& mod :
             GetLoadedMods())
        {
            LoadJobText(mod);
            LoadCommandText(mod);
        }

        LogInfo(
            std::string("[MesText] Patches registrados: ") +
            std::to_string(g_patchCount)
        );

        return true;
    }

    bool HasMesTextPatches(
        std::string_view virtualPath
    )
    {
        const auto it =
            g_patches.find(
                std::string(virtualPath)
            );

        return
            it != g_patches.end() &&
            !it->second.empty();
    }

    std::shared_ptr<
        const std::vector<std::uint8_t>
    > BuildPatchedMes(
        std::string_view virtualPath,
        const std::vector<std::uint8_t>& original
    )
    {
        const auto tableIt =
            g_patches.find(
                std::string(virtualPath)
            );

        if (tableIt == g_patches.end() ||
            tableIt->second.empty())
        {
            return nullptr;
        }

        if (virtualPath ==
            ExtendedTextPath)
        {
            return BuildPatchedExtend(
                original,
                tableIt->second
            );
        }

        auto entries =
            SplitMes(original);

        for (const auto& [index, patch] :
             tableIt->second)
        {
            if (index >= entries.size())
            {
                entries.resize(index + 1);
            }

            entries[index] =
                EncodeText(
                    patch.text,
                    patch,
                    virtualPath,
                    index
                );

            LogInfo(
                std::string("[MesText] Aplicado ") +
                std::string(virtualPath) +
                " #" +
                std::to_string(index) +
                " <- " +
                patch.modUniqueId
            );
        }

        auto rebuilt =
            std::make_shared<
                std::vector<std::uint8_t>
            >(
                JoinMes(entries)
            );

        LogInfo(
            std::string("[MesText] Resource reconstruido: ") +
            std::string(virtualPath) +
            " | original=" +
            std::to_string(original.size()) +
            " | mod=" +
            std::to_string(rebuilt->size())
        );

        return rebuilt;
    }

    std::size_t MesTextPatchCount()
    {
        return g_patchCount;
    }
}
