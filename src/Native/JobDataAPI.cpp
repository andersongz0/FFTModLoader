#include "JobDataAPI.hpp"

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
#include <unordered_map>
#include <unordered_set>
#include <utility>

namespace fs = std::filesystem;

namespace
{
#pragma pack(push, 1)
    struct JobData
    {
        std::uint8_t jobCommandId;
        std::uint16_t innateAbility1;
        std::uint16_t innateAbility2;
        std::uint16_t innateAbility3;
        std::uint16_t innateAbility4;

        std::uint8_t equippable1;
        std::uint8_t equippable2;
        std::uint8_t equippable3;
        std::uint8_t equippable4;
        std::uint8_t equippable5;

        std::uint8_t hpGrowth;
        std::uint8_t hpMultiplier;
        std::uint8_t mpGrowth;
        std::uint8_t mpMultiplier;
        std::uint8_t speedGrowth;
        std::uint8_t speedMultiplier;
        std::uint8_t paGrowth;
        std::uint8_t paMultiplier;
        std::uint8_t maGrowth;
        std::uint8_t maMultiplier;

        std::uint8_t move;
        std::uint8_t jump;
        std::uint8_t characterEvasion;

        std::uint8_t innateStatus1;
        std::uint8_t innateStatus2;
        std::uint8_t innateStatus3;
        std::uint8_t innateStatus4;
        std::uint8_t innateStatus5;

        std::uint8_t immuneStatus1;
        std::uint8_t immuneStatus2;
        std::uint8_t immuneStatus3;
        std::uint8_t immuneStatus4;
        std::uint8_t immuneStatus5;

        std::uint8_t startingStatus1;
        std::uint8_t startingStatus2;
        std::uint8_t startingStatus3;
        std::uint8_t startingStatus4;
        std::uint8_t startingStatus5;

        std::uint8_t absorbElements;
        std::uint8_t nullifyElements;
        std::uint8_t halveElements;
        std::uint8_t weakElements;

        std::uint8_t monsterPortrait;
        std::uint8_t monsterPalette;
        std::uint8_t monsterGraphic;
    };
#pragma pack(pop)

    static_assert(sizeof(JobData) == 49);

    constexpr int kJobCount = 176;
    constexpr int kMaxJobId = kJobCount - 1;

    // Reference signature points to entry 1. The reference manager backs up
    // one sizeof(JOB_DATA) entry to obtain ID 0.
    constexpr const char* kJobDataEntry1Pattern =
        "19 00 00 00 00 00 00 00 00 D0 40 06 FF 00 0B 78 "
        "0B 69 5F 64 32 6E 30 64 04 03 0A 00 00 00 00 00 "
        "00 40";

    JobData* g_table = nullptr;
    std::vector<std::uint16_t> g_menuJobs;
    std::unordered_map<std::uint16_t, fftml::JobPresentationInfo> g_presentations;
    std::size_t g_applied = 0;

    nlohmann::json ReadJson(const fs::path& path)
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
                std::string("[JobData] JSON invalido: ") +
                path.generic_string() +
                " | " + ex.what()
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
                const auto v = value.get<std::int64_t>();
                if (v < 0)
                    return std::nullopt;
                return static_cast<std::uint64_t>(v);
            }

            if (!value.is_string())
                return std::nullopt;

            std::string text = value.get<std::string>();

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
                             return (c >= 'A' && c <= 'F') ||
                                    (c >= 'a' && c <= 'f');
                         }))
            {
                base = 16;
            }

            if (text.empty())
                return std::nullopt;

            return std::stoull(text, nullptr, base);
        }
        catch (...)
        {
            return std::nullopt;
        }
    }

    std::optional<int> ParseId(const nlohmann::json& value)
    {
        const auto parsed = ParseUnsigned(value);
        if (!parsed || *parsed > 0xFFFF)
            return std::nullopt;

        return static_cast<int>(*parsed);
    }

    template <class T>
    void SetIfPresent(
        const nlohmann::json& job,
        const char* key,
        T& field
    )
    {
        if (!job.contains(key))
            return;

        const auto parsed = ParseUnsigned(job[key]);
        if (!parsed)
            return;

        field = static_cast<T>(*parsed);
    }

    std::string NormalizeToken(
        std::string text
    )
    {
        std::string result;
        result.reserve(text.size());

        for (unsigned char c : text)
        {
            if (std::isalnum(c))
            {
                result.push_back(
                    static_cast<char>(
                        std::tolower(c)
                    )
                );
            }
        }

        return result;
    }

    std::optional<std::uint64_t>
    ParseEquippableItems(
        const nlohmann::json& value
    )
    {
        if (!value.is_array())
            return ParseUnsigned(value);

        static const std::unordered_map<
            std::string,
            std::uint64_t
        > kEquipment = {
            {"unarmed",     0x80ull << 32},
            {"knife",       0x40ull << 32},
            {"ninjablade",  0x20ull << 32},
            {"sword",       0x10ull << 32},
            {"knightsword", 0x08ull << 32},
            {"katana",      0x04ull << 32},
            {"axe",         0x02ull << 32},
            {"rod",         0x01ull << 32},

            {"staff",       0x80ull << 24},
            {"flail",       0x40ull << 24},
            {"gun",         0x20ull << 24},
            {"crossbow",    0x10ull << 24},
            {"bow",         0x08ull << 24},
            {"instrument",  0x04ull << 24},
            {"book",        0x02ull << 24},
            {"polearm",     0x01ull << 24},

            {"pole",          0x80ull << 16},
            {"bag",           0x40ull << 16},
            {"cloth",         0x20ull << 16},
            {"shield",        0x10ull << 16},
            {"helmet",        0x08ull << 16},
            {"hat",           0x04ull << 16},
            {"hairadornment", 0x02ull << 16},
            {"armor",         0x01ull << 16},

            {"clothing", 0x80ull << 8},
            {"robe",     0x40ull << 8},
            {"shoes",    0x20ull << 8},
            {"armguard", 0x10ull << 8},
            {"ring",     0x08ull << 8},
            {"armlet",   0x04ull << 8},
            {"cloak",    0x02ull << 8},
            {"perfume",  0x01ull << 8},

            {"fellsword", 0x10ull},
            {"liprouge",  0x08ull}
        };

        std::uint64_t mask = 0;

        for (const auto& item : value)
        {
            if (!item.is_string())
                continue;

            const std::string key =
                NormalizeToken(
                    item.get<std::string>()
                );

            const auto it =
                kEquipment.find(key);

            if (it == kEquipment.end())
            {
                fftml::LogWarning(
                    std::string("[JobData] Categoria de equipamento desconhecida: ") +
                    item.get<std::string>()
                );
                continue;
            }

            mask |= it->second;
        }

        return mask;
    }

    void Set40Bit(
        std::uint64_t value,
        std::uint8_t& b1,
        std::uint8_t& b2,
        std::uint8_t& b3,
        std::uint8_t& b4,
        std::uint8_t& b5
    )
    {
        b1 = static_cast<std::uint8_t>((value >> 32) & 0xFF);
        b2 = static_cast<std::uint8_t>((value >> 24) & 0xFF);
        b3 = static_cast<std::uint8_t>((value >> 16) & 0xFF);
        b4 = static_cast<std::uint8_t>((value >> 8) & 0xFF);
        b5 = static_cast<std::uint8_t>(value & 0xFF);
    }

    bool HasJobDataPatchFields(
        const nlohmann::json& job
    )
    {
        static constexpr const char* kFields[] = {
            "CloneFrom",
            "JobCommandId",
            "InnateAbilityIds",
            "EquippableItems",
            "HPGrowth",
            "HPMultiplier",
            "MPGrowth",
            "MPMultiplier",
            "SpeedGrowth",
            "SpeedMultiplier",
            "PAGrowth",
            "PAMultiplier",
            "MAGrowth",
            "MAMultiplier",
            "Move",
            "Jump",
            "CharacterEvasion",
            "InnateStatus",
            "ImmuneStatus",
            "StartingStatus",
            "AbsorbElements",
            "NullifyElements",
            "HalveElements",
            "WeakElements",
            "MonsterPortrait",
            "MonsterPalette",
            "MonsterGraphic"
        };

        for (const char* field : kFields)
        {
            if (job.contains(field))
                return true;
        }

        return false;
    }

    void ApplyObject(JobData& data, const nlohmann::json& job)
    {
        SetIfPresent(job, "JobCommandId", data.jobCommandId);

        if (job.contains("InnateAbilityIds") &&
            job["InnateAbilityIds"].is_array())
        {
            auto& arr = job["InnateAbilityIds"];
            std::uint16_t* values[] = {
                &data.innateAbility1,
                &data.innateAbility2,
                &data.innateAbility3,
                &data.innateAbility4
            };

            for (std::size_t i = 0;
                 i < arr.size() && i < 4;
                 ++i)
            {
                if (const auto value = ParseUnsigned(arr[i]))
                    *values[i] = static_cast<std::uint16_t>(*value);
            }
        }

        if (job.contains("EquippableItems"))
        {
            if (const auto value =
                    ParseEquippableItems(
                        job["EquippableItems"]
                    ))
            {
                Set40Bit(
                    *value,
                    data.equippable1,
                    data.equippable2,
                    data.equippable3,
                    data.equippable4,
                    data.equippable5
                );
            }
        }

        SetIfPresent(job, "HPGrowth", data.hpGrowth);
        SetIfPresent(job, "HPMultiplier", data.hpMultiplier);
        SetIfPresent(job, "MPGrowth", data.mpGrowth);
        SetIfPresent(job, "MPMultiplier", data.mpMultiplier);
        SetIfPresent(job, "SpeedGrowth", data.speedGrowth);
        SetIfPresent(job, "SpeedMultiplier", data.speedMultiplier);
        SetIfPresent(job, "PAGrowth", data.paGrowth);
        SetIfPresent(job, "PAMultiplier", data.paMultiplier);
        SetIfPresent(job, "MAGrowth", data.maGrowth);
        SetIfPresent(job, "MAMultiplier", data.maMultiplier);
        SetIfPresent(job, "Move", data.move);
        SetIfPresent(job, "Jump", data.jump);
        SetIfPresent(job, "CharacterEvasion", data.characterEvasion);

        if (job.contains("InnateStatus"))
        {
            if (const auto value = ParseUnsigned(job["InnateStatus"]))
            {
                Set40Bit(
                    *value,
                    data.innateStatus1,
                    data.innateStatus2,
                    data.innateStatus3,
                    data.innateStatus4,
                    data.innateStatus5
                );
            }
        }

        if (job.contains("ImmuneStatus"))
        {
            if (const auto value = ParseUnsigned(job["ImmuneStatus"]))
            {
                Set40Bit(
                    *value,
                    data.immuneStatus1,
                    data.immuneStatus2,
                    data.immuneStatus3,
                    data.immuneStatus4,
                    data.immuneStatus5
                );
            }
        }

        if (job.contains("StartingStatus"))
        {
            if (const auto value = ParseUnsigned(job["StartingStatus"]))
            {
                Set40Bit(
                    *value,
                    data.startingStatus1,
                    data.startingStatus2,
                    data.startingStatus3,
                    data.startingStatus4,
                    data.startingStatus5
                );
            }
        }

        SetIfPresent(job, "AbsorbElements", data.absorbElements);
        SetIfPresent(job, "NullifyElements", data.nullifyElements);
        SetIfPresent(job, "HalveElements", data.halveElements);
        SetIfPresent(job, "WeakElements", data.weakElements);
        SetIfPresent(job, "MonsterPortrait", data.monsterPortrait);
        SetIfPresent(job, "MonsterPalette", data.monsterPalette);
        SetIfPresent(job, "MonsterGraphic", data.monsterGraphic);
    }

    void AddMenuJob(int id)
    {
        if (id < 0 || id > kMaxJobId)
            return;

        const auto value = static_cast<std::uint16_t>(id);

        if (std::find(
                g_menuJobs.begin(),
                g_menuJobs.end(),
                value) == g_menuJobs.end())
        {
            g_menuJobs.push_back(value);
        }
    }

    fftml::JobMenuPresentation ParsePresentationMode(
        const nlohmann::json& job,
        int jobId
    )
    {
        if (!job.contains("MenuPresentation") ||
            !job["MenuPresentation"].is_string())
        {
            if (jobId == 0xA0)
                return fftml::JobMenuPresentation::DarkKnight;

            if (jobId == 0xA1)
                return fftml::JobMenuPresentation::OnionKnight;

            return fftml::JobMenuPresentation::Auto;
        }

        std::string value =
            job["MenuPresentation"].get<std::string>();

        std::transform(
            value.begin(),
            value.end(),
            value.begin(),
            [](unsigned char c)
            {
                return static_cast<char>(std::tolower(c));
            }
        );

        value.erase(
            std::remove_if(
                value.begin(),
                value.end(),
                [](unsigned char c)
                {
                    return c == ' ' || c == '_' || c == '-';
                }
            ),
            value.end()
        );

        if (value == "actual")
            return fftml::JobMenuPresentation::Actual;

        if (value == "darkknight" ||
            value == "dk")
        {
            return fftml::JobMenuPresentation::DarkKnight;
        }

        if (value == "onionknight" ||
            value == "onion")
        {
            return fftml::JobMenuPresentation::OnionKnight;
        }

        return fftml::JobMenuPresentation::Auto;
    }

    int DefaultRequirementSlotForJob(int jobId)
    {
        if (jobId >= 0 && jobId <= 21)
            return jobId;

        if (jobId == 0xA0)
            return 20; // Dark Knight row

        if (jobId == 0xA1)
            return 21; // Onion Knight row

        return -1;
    }

    void RegisterPresentation(
        int jobId,
        const nlohmann::json& job
    )
    {
        if (jobId < 0 || jobId > kMaxJobId)
            return;

        fftml::JobPresentationInfo info;
        info.jobId = static_cast<std::uint16_t>(jobId);
        info.menuPresentation =
            ParsePresentationMode(job, jobId);

        if (job.contains("UnitVisualFrom"))
        {
            if (const auto source = ParseUnsigned(job["UnitVisualFrom"]);
                source && *source <= 0xFF && *source !=
                    static_cast<std::uint64_t>(jobId))
            {
                info.unitVisualSourceJobId =
                    static_cast<std::uint16_t>(*source);
            }
            else
            {
                fftml::LogWarning(
                    "[JobData] UnitVisualFrom ignorado para Job ID=" +
                    std::to_string(jobId) +
                    ": a fonte deve ser outro Job entre 0 e 255."
                );
            }
        }

        if (job.contains("DisplayName") &&
            job["DisplayName"].is_string())
        {
            info.displayNameMetadata =
                job["DisplayName"].get<std::string>();
        }

        if (job.contains("Description") &&
            job["Description"].is_string())
        {
            info.descriptionMetadata =
                job["Description"].get<std::string>();
        }

        if (job.contains("AbilityTextOverrides") &&
            job["AbilityTextOverrides"].is_object())
        {
            for (auto it =
                     job["AbilityTextOverrides"].begin();
                 it !=
                     job["AbilityTextOverrides"].end();
                 ++it)
            {
                if (!it.value().is_string())
                    continue;

                fftml::JobTextOverride entry;
                entry.from = it.key();
                entry.to =
                    it.value().get<std::string>();

                if (!entry.from.empty() &&
                    !entry.to.empty())
                {
                    // ID 45 is Chant in this build. A contextual text alias
                    // cannot turn its gameplay into Sanguine Sword.
                    if (jobId == 0xA0 &&
                        entry.from == "Chant" &&
                        entry.to == "Sanguine Sword")
                    {
                        fftml::LogWarning(
                            "[JobData] Dark Knight: Chant -> Sanguine Sword ignorado; efeito da habilidade nao foi implementado."
                        );
                        continue;
                    }

                    info.abilityTextOverrides.push_back(
                        std::move(entry)
                    );
                }
            }
        }

        if (job.contains("AbilityNamesById") &&
            job["AbilityNamesById"].is_object())
        {
            for (auto it =
                     job["AbilityNamesById"].begin();
                 it !=
                     job["AbilityNamesById"].end();
                 ++it)
            {
                if (!it.value().is_string())
                    continue;

                const nlohmann::json idValue =
                    it.key();

                const auto id =
                    ParseId(idValue);

                if (!id)
                    continue;

                fftml::JobAbilityNameOverride entry;
                entry.abilityId =
                    static_cast<std::uint16_t>(
                        *id
                    );

                entry.displayName =
                    it.value().get<std::string>();

                if (!entry.displayName.empty())
                {
                    if (jobId == 0xA0 &&
                        entry.abilityId == 45 &&
                        entry.displayName == "Sanguine Sword")
                    {
                        fftml::LogWarning(
                            "[JobData] Dark Knight: nome Sanguine Sword para ID 45 ignorado; ID 45 e Chant nesta build."
                        );
                        continue;
                    }

                    info.abilityNameOverrides.push_back(
                        std::move(entry)
                    );
                }
            }
        }

        if (!info.abilityTextOverrides.empty() ||
            !info.abilityNameOverrides.empty())
        {
            fftml::LogWarning(
                "[JobData] AbilityTextOverrides/AbilityNamesById alteram apenas nomes na UI; nao alteram efeito, alvo ou custo das habilidades."
            );
        }

        info.requirementSlot =
            DefaultRequirementSlotForJob(jobId);

        if (job.contains("RequirementSlot"))
        {
            if (const auto parsed =
                    ParseUnsigned(
                        job["RequirementSlot"]))
            {
                if (*parsed <= 21)
                    info.requirementSlot =
                        static_cast<int>(*parsed);
            }
        }

        g_presentations[
            static_cast<std::uint16_t>(jobId)
        ] = std::move(info);
    }

    bool MakeTableWritable()
    {
        if (!g_table)
            return false;

        DWORD oldProtection = 0;

        return VirtualProtect(
            g_table,
            sizeof(JobData) * kJobCount,
            PAGE_EXECUTE_READWRITE,
            &oldProtection
        ) != FALSE;
    }
}

namespace fftml
{
    bool InitializeJobDataAPI()
    {
        g_table = nullptr;
        g_menuJobs.clear();
        g_presentations.clear();
        g_applied = 0;

        const auto entry1 =
            FindPatternInMainModule(
                ParsePattern(kJobDataEntry1Pattern)
            );

        if (!entry1)
        {
            LogWarning(
                "[JobData] JobDataTable signature nao encontrada."
            );
            return false;
        }

        const auto tableAddress =
            entry1 - sizeof(JobData);

        g_table = reinterpret_cast<JobData*>(tableAddress);

        if (!MakeTableWritable())
        {
            LogError(
                "[JobData] Nao foi possivel tornar a tabela gravavel."
            );
            g_table = nullptr;
            return false;
        }

        char line[180]{};
        std::snprintf(
            line,
            sizeof(line),
            "[JobData] Tabela encontrada em 0x%llX | entries=%d | sizeof=%zu",
            static_cast<unsigned long long>(tableAddress),
            kJobCount,
            sizeof(JobData)
        );
        LogInfo(line);

        for (const auto& mod : GetLoadedMods())
        {
            const fs::path jobsPath =
                mod.directory / L"jobs.json";

            if (!fs::exists(jobsPath))
                continue;

            const auto root = ReadJson(jobsPath);
            if (!root.is_object())
                continue;

            if (root.contains("MenuJobs") &&
                root["MenuJobs"].is_array())
            {
                for (const auto& item : root["MenuJobs"])
                {
                    if (const auto id = ParseId(item))
                        AddMenuJob(*id);
                }
            }

            if (!root.contains("Jobs") ||
                !root["Jobs"].is_array())
            {
                continue;
            }

            for (const auto& job : root["Jobs"])
            {
                if (!job.is_object() || !job.contains("Id"))
                    continue;

                const auto idOpt = ParseId(job["Id"]);
                if (!idOpt)
                {
                    LogWarning(
                        std::string("[JobData] ID invalido em ") +
                        mod.uniqueId
                    );
                    continue;
                }

                const int id = *idOpt;

                // Test mods can expose a native special job without copying
                // an unrelated command if a different game build is loaded.
                // Compare before registering it in the menu or applying data.
                if (job.contains("ExpectedNativeJobCommandId"))
                {
                    const auto expected = ParseUnsigned(
                        job["ExpectedNativeJobCommandId"]);

                    if (id > kMaxJobId || !expected ||
                        *expected > 0xFF ||
                        g_table[id].jobCommandId != *expected)
                    {
                        LogWarning(
                            "[JobData] Job ID=" + std::to_string(id) +
                            " ignorado: ExpectedNativeJobCommandId nao "
                            "corresponde ao comando nativo desta build."
                        );
                        continue;
                    }
                }

                if (id <= kMaxJobId)
                    RegisterPresentation(id, job);

                if (id > kMaxJobId)
                {
                    LogWarning(
                        std::string("[JobData] Job ID ") +
                        std::to_string(id) +
                        " excede a tabela estatica 0..175; "
                        "reservado para futura virtualizacao."
                    );
                    continue;
                }

                const bool mutatesJobData =
                    HasJobDataPatchFields(job);

                if (mutatesJobData &&
                    job.contains("CloneFrom"))
                {
                    const auto cloneId =
                        ParseId(job["CloneFrom"]);

                    if (cloneId &&
                        *cloneId >= 0 &&
                        *cloneId <= kMaxJobId)
                    {
                        g_table[id] =
                            g_table[*cloneId];
                    }
                }

                if (mutatesJobData)
                {
                    ApplyObject(
                        g_table[id],
                        job
                    );

                    ++g_applied;
                }

                const bool expose =
                    job.value("ExposeInMenu", false);

                if (expose)
                    AddMenuJob(id);

                std::string displayMeta;

                if (const auto it =
                        g_presentations.find(
                            static_cast<std::uint16_t>(id));
                    it != g_presentations.end() &&
                    !it->second.displayNameMetadata.empty())
                {
                    displayMeta =
                        " | label=" +
                        it->second.displayNameMetadata;
                }

                std::string idText = [&]()
                {
                    char b[16]{};
                    std::snprintf(
                        b,
                        sizeof(b),
                        "%X",
                        id
                    );
                    return std::string(b);
                }();

                if (mutatesJobData)
                {
                    LogInfo(
                        std::string("[JobData] ") +
                        mod.uniqueId +
                        " aplicou Job ID=0x" +
                        idText +
                        (job.contains("CloneFrom")
                            ? " (clone+patch)"
                            : " (patch)") +
                        " | Command=" +
                        std::to_string(g_table[id].jobCommandId) +
                        displayMeta
                    );
                }
                else
                {
                    LogInfo(
                        std::string("[JobData] ") +
                        mod.uniqueId +
                        " registrou Job ID=0x" +
                        idText +
                        " (presentation/menu; JobData nativo preservado)" +
                        displayMeta
                    );
                }
            }
        }

        // GenericJobs is a Reloaded-II code mod and therefore does not ship a
        // loader-specific jobs.json.  Its original Mod.cs registers these two
        // native job IDs directly ("A0, A1").  Recognize the unmodified mod by
        // its official ModId and reproduce only that runtime registration here;
        // all data, text and artwork continue to come from the original mod.
        for (const auto& mod : GetLoadedMods())
        {
            if (mod.uniqueId == "ffttic.jobs.genericjobs")
            {
                AddMenuJob(0xA0);
                AddMenuJob(0xA1);
                LogInfo(
                    "[GenericJobsCompat] Mod original detectado; "
                    "jobs nativos A0/A1 registrados no menu."
                );
                break;
            }
        }

        LogInfo(
            std::string("[JobData] Patches aplicados: ") +
            std::to_string(g_applied) +
            " | jobs extras no menu: " +
            std::to_string(g_menuJobs.size())
        );

        if (g_table &&
            std::find(
                g_menuJobs.begin(),
                g_menuJobs.end(),
                static_cast<std::uint16_t>(0xA0)
            ) != g_menuJobs.end())
        {
            const JobData& dk =
                g_table[0xA0];

            char line[320]{};

            std::snprintf(
                line,
                sizeof(line),
                "[JobData] A0 runtime: Command=%u Move=%u Jump=%u "
                "Growth(H/M/S/PA/MA)=%u/%u/%u/%u/%u "
                "Multiplier(H/M/S/PA/MA)=%u/%u/%u/%u/%u "
                "Equip=%02X %02X %02X %02X %02X",
                static_cast<unsigned int>(dk.jobCommandId),
                static_cast<unsigned int>(dk.move),
                static_cast<unsigned int>(dk.jump),
                static_cast<unsigned int>(dk.hpGrowth),
                static_cast<unsigned int>(dk.mpGrowth),
                static_cast<unsigned int>(dk.speedGrowth),
                static_cast<unsigned int>(dk.paGrowth),
                static_cast<unsigned int>(dk.maGrowth),
                static_cast<unsigned int>(dk.hpMultiplier),
                static_cast<unsigned int>(dk.mpMultiplier),
                static_cast<unsigned int>(dk.speedMultiplier),
                static_cast<unsigned int>(dk.paMultiplier),
                static_cast<unsigned int>(dk.maMultiplier),
                static_cast<unsigned int>(dk.equippable1),
                static_cast<unsigned int>(dk.equippable2),
                static_cast<unsigned int>(dk.equippable3),
                static_cast<unsigned int>(dk.equippable4),
                static_cast<unsigned int>(dk.equippable5)
            );

            LogInfo(line);
        }

        if (g_table &&
            std::find(
                g_menuJobs.begin(),
                g_menuJobs.end(),
                static_cast<std::uint16_t>(0xA1)
            ) != g_menuJobs.end())
        {
            const JobData& onion = g_table[0xA1];
            char line[220]{};
            std::snprintf(
                line,
                sizeof(line),
                "[JobData] A1 runtime: Command=%u Move=%u Jump=%u Equip=%02X %02X %02X %02X %02X",
                static_cast<unsigned int>(onion.jobCommandId),
                static_cast<unsigned int>(onion.move),
                static_cast<unsigned int>(onion.jump),
                static_cast<unsigned int>(onion.equippable1),
                static_cast<unsigned int>(onion.equippable2),
                static_cast<unsigned int>(onion.equippable3),
                static_cast<unsigned int>(onion.equippable4),
                static_cast<unsigned int>(onion.equippable5)
            );
            LogInfo(line);
        }

        return true;
    }

    const std::vector<std::uint16_t>& GetMenuJobIds()
    {
        return g_menuJobs;
    }

    const JobPresentationInfo* GetJobPresentationInfo(
        std::uint16_t jobId
    )
    {
        const auto it =
            g_presentations.find(jobId);

        if (it == g_presentations.end())
            return nullptr;

        return &it->second;
    }

    JobMenuPresentation GetJobMenuPresentation(
        std::uint16_t jobId
    )
    {
        if (const auto* info =
                GetJobPresentationInfo(jobId))
        {
            return info->menuPresentation;
        }

        if (jobId == 0xA0)
            return JobMenuPresentation::DarkKnight;

        if (jobId == 0xA1)
            return JobMenuPresentation::OnionKnight;

        return JobMenuPresentation::Auto;
    }

    int GetJobRequirementSlot(
        std::uint16_t jobId
    )
    {
        if (const auto* info =
                GetJobPresentationInfo(jobId))
        {
            return info->requirementSlot;
        }

        return DefaultRequirementSlotForJob(jobId);
    }

    std::size_t AppliedJobPatchCount()
    {
        return g_applied;
    }

    bool JobDataTableFound()
    {
        return g_table != nullptr;
    }
}
