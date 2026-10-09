#include "FFTPackHooks.hpp"

#include "ContentResolver.hpp"
#include "LanguageHooks.hpp"
#include "Logger.hpp"
#include "SignatureScanner.hpp"

#include <MinHook.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <limits>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>

namespace fs = std::filesystem;

namespace
{
    using FileReadRequestOffsetFn =
        int (__fastcall*)(
            int fileIndex,
            std::int64_t sectorOffset,
            std::int64_t size,
            void* outputPointer
        );

    FileReadRequestOffsetFn g_original = nullptr;
    void* g_target = nullptr;

    struct Mapping
    {
        const fftml::ContentPlan* plan = nullptr;
        int priority = 0;
        bool localeSpecific = false;
    };

    std::unordered_map<int, std::string> g_indexToName;
    std::unordered_map<std::string, int> g_nameToIndex;

    std::unordered_map<int, Mapping> g_en;
    std::unordered_map<int, Mapping> g_jp;

    std::mutex g_logMutex;
    std::unordered_set<std::string> g_loggedServed;
    std::unordered_set<std::string> g_loggedEdited;

    constexpr const char* kFileReadRequestOffsetPattern =
        "48 89 5C 24 ?? "
        "48 89 6C 24 ?? "
        "48 89 74 24 ?? "
        "57 48 83 EC ?? "
        "80 3D ?? ?? ?? ?? ?? "
        "49 8B F1";

    constexpr const char* kFileReadRequestOffsetPatternDenuvo =
        "48 89 5C 24 ?? "
        "48 89 6C 24 ?? "
        "48 89 74 24 ?? "
        "57 48 83 EC ?? "
        "80 3D ?? ?? ?? ?? ?? "
        "4C 89 CE";

    std::string Trim(std::string value)
    {
        while (!value.empty() &&
               (value.back() == '\r' ||
                value.back() == '\n' ||
                value.back() == ' ' ||
                value.back() == '\t'))
        {
            value.pop_back();
        }

        std::size_t first = 0;
        while (first < value.size() &&
               (value[first] == ' ' || value[first] == '\t'))
        {
            ++first;
        }

        return value.substr(first);
    }

    bool LoadFileList(const fs::path& path)
    {
        g_indexToName.clear();
        g_nameToIndex.clear();

        std::ifstream input(path);
        if (!input)
        {
            fftml::LogError(
                std::string("[FFTPack] File list nao encontrada: ") +
                path.generic_string()
            );
            return false;
        }

        std::string line;

        while (std::getline(input, line))
        {
            line = Trim(line);

            if (line.empty() || line.rfind("//", 0) == 0)
                continue;

            const auto sep = line.find('|');
            if (sep == std::string::npos)
                continue;

            int index = -1;
            try
            {
                index = std::stoi(line.substr(0, sep));
            }
            catch (...)
            {
                continue;
            }

            std::string pathName =
                fftml::NormalizeVirtualPath(
                    line.substr(sep + 1)
                );

            if (pathName.empty() || pathName == "<empty>")
                continue;

            g_indexToName[index] = pathName;
            g_nameToIndex[pathName] = index;
        }

        fftml::LogInfo(
            std::string("[FFTPack] File list carregada: ") +
            std::to_string(g_indexToName.size()) +
            " indices conhecidos."
        );

        return !g_indexToName.empty();
    }

    bool IsReservedGameOverrideIndex(int index)
    {
        return index == 17 || (index >= 741 && index <= 749);
    }

    struct LocalePath
    {
        std::string actualPath;
        std::string locale;
        bool localeSpecific = false;
    };

    LocalePath ParseLocalePath(std::string relative)
    {
        LocalePath result;
        result.actualPath = std::move(relative);

        const auto slash = result.actualPath.find_last_of('/');
        const auto dot2 = result.actualPath.find_last_of('.');

        if (dot2 == std::string::npos)
            return result;

        const auto dot1 = result.actualPath.find_last_of('.', dot2 - 1);

        if (dot1 == std::string::npos ||
            (slash != std::string::npos && dot1 < slash))
        {
            return result;
        }

        const std::string token =
            result.actualPath.substr(
                dot1 + 1,
                dot2 - dot1 - 1
            );

        if (token != "en" && token != "jp")
            return result;

        result.locale = token;
        result.localeSpecific = true;

        // foo.en.bin -> foo.bin
        result.actualPath.erase(
            dot1,
            dot2 - dot1
        );

        return result;
    }

    void PutMapping(
        std::unordered_map<int, Mapping>& table,
        int index,
        const fftml::ContentPlan* plan,
        bool localeSpecific
    )
    {
        const Mapping incoming{
            plan,
            plan ? plan->basePriority : 0,
            localeSpecific
        };

        auto it = table.find(index);

        if (it != table.end())
        {
            const bool replace =
                incoming.priority > it->second.priority ||
                (incoming.priority == it->second.priority &&
                 incoming.localeSpecific &&
                 !it->second.localeSpecific);

            if (!replace)
                return;

            if (it->second.plan && plan)
            {
                fftml::LogWarning(
                    std::string("[FFTPack] Conflito index ") +
                    std::to_string(index) +
                    ": " +
                    it->second.plan->baseModUniqueId +
                    " -> " +
                    plan->baseModUniqueId
                );
            }
        }

        table[index] = incoming;
    }

    const Mapping* FindMapping(int fileIndex)
    {
        const auto locale = fftml::CurrentFFTPackLocale();
        const auto& table = locale == "jp" ? g_jp : g_en;

        const auto it = table.find(fileIndex);
        return it == table.end() ? nullptr : &it->second;
    }

    void LogServedOnce(
        int fileIndex,
        std::string_view locale,
        const fftml::ContentPlan& plan,
        std::int64_t byteOffset,
        std::int64_t size
    )
    {
        std::lock_guard lock(g_logMutex);

        const std::string key =
            std::to_string(fileIndex) +
            "|" + std::string(locale) +
            "|" + plan.virtualPath;

        if (!g_loggedServed.insert(key).second)
            return;

        fftml::LogInfo(
            std::string("[FFTPack] Override SERVIDO index=") +
            std::to_string(fileIndex) +
            " locale=" + std::string(locale) +
            " path=" + plan.virtualPath +
            " offset=0x" + [&]()
            {
                char b[32]{};
                std::snprintf(
                    b,
                    sizeof(b),
                    "%llX",
                    static_cast<unsigned long long>(byteOffset)
                );
                return std::string(b);
            }() +
            " size=" + std::to_string(size)
        );
    }

    void LogEditOnce(
        int fileIndex,
        const fftml::ContentPlan& plan
    )
    {
        std::lock_guard lock(g_logMutex);

        const std::string key =
            std::to_string(fileIndex) + "|" + plan.virtualPath;

        if (!g_loggedEdited.insert(key).second)
            return;

        fftml::LogInfo(
            std::string("[FFTPack] Edit aplicado index=") +
            std::to_string(fileIndex) +
            " path=" + plan.virtualPath +
            " patches=" + std::to_string(plan.patches.size())
        );
    }

    int __fastcall FileReadRequestOffsetHook(
        int fileIndex,
        std::int64_t sectorOffset,
        std::int64_t size,
        void* outputPointer
    )
    {
        if (!outputPointer || size <= 0 || sectorOffset < 0)
        {
            return g_original(
                fileIndex,
                sectorOffset,
                size,
                outputPointer
            );
        }

        // The game itself reroutes these entries to other file systems.
        if (IsReservedGameOverrideIndex(fileIndex))
        {
            return g_original(
                fileIndex,
                sectorOffset,
                size,
                outputPointer
            );
        }

        const Mapping* mapping = FindMapping(fileIndex);

        if (!mapping || !mapping->plan)
        {
            return g_original(
                fileIndex,
                sectorOffset,
                size,
                outputPointer
            );
        }

        const auto& plan = *mapping->plan;

        if (size > static_cast<std::int64_t>(
                std::numeric_limits<std::size_t>::max()))
        {
            return g_original(
                fileIndex,
                sectorOffset,
                size,
                outputPointer
            );
        }

        const std::uint64_t byteOffset =
            static_cast<std::uint64_t>(sectorOffset) * 0x800ull;

        const std::size_t requested =
            static_cast<std::size_t>(size);

        // Edit-only: preserve original reader then patch requested range.
        if (plan.baseAction == fftml::ContentAction::None)
        {
            const int result = g_original(
                fileIndex,
                sectorOffset,
                size,
                outputPointer
            );

            if (result == 0 && !plan.patches.empty())
            {
                fftml::ApplyBinaryPatches(
                    plan,
                    byteOffset,
                    outputPointer,
                    requested
                );

                LogEditOnce(fileIndex, plan);
            }

            return result;
        }

        const auto bytes = fftml::GetBaseBytes(plan);

        if (!bytes)
        {
            return g_original(
                fileIndex,
                sectorOffset,
                size,
                outputPointer
            );
        }

        std::memset(outputPointer, 0, requested);

        if (byteOffset < bytes->size())
        {
            const std::size_t available =
                bytes->size() -
                static_cast<std::size_t>(byteOffset);

            const std::size_t copySize =
                std::min(requested, available);

            std::memcpy(
                outputPointer,
                bytes->data() + byteOffset,
                copySize
            );
        }

        if (!plan.patches.empty())
        {
            fftml::ApplyBinaryPatches(
                plan,
                byteOffset,
                outputPointer,
                requested
            );
        }

        LogServedOnce(
            fileIndex,
            fftml::CurrentFFTPackLocale(),
            plan,
            static_cast<std::int64_t>(byteOffset),
            size
        );

        return 0;
    }
}

namespace fftml
{
    bool InitializeFFTPackOverrides(
        const fs::path& gameDirectory
    )
    {
        g_en.clear();
        g_jp.clear();
        g_loggedServed.clear();
        g_loggedEdited.clear();

        const fs::path listPath =
            gameDirectory /
            L"Runtime" /
            L"fftpack.txt";

        if (!LoadFileList(listPath))
            return false;

        std::size_t accepted = 0;

        for (const ContentPlan* plan : GetAllContentPlans())
        {
            if (!plan)
                continue;

            const std::string normalized =
                NormalizeVirtualPath(plan->virtualPath);

            constexpr std::string_view prefix = "fftpack/";

            if (normalized.rfind(prefix, 0) != 0)
                continue;

            LocalePath parsed =
                ParseLocalePath(
                    normalized.substr(prefix.size())
                );

            const auto known =
                g_nameToIndex.find(parsed.actualPath);

            if (known == g_nameToIndex.end())
            {
                LogWarning(
                    std::string("[FFTPack] Target desconhecido; ") +
                    "nao existe em fftpack.txt: " +
                    normalized
                );
                continue;
            }

            const int index = known->second;

            if (IsReservedGameOverrideIndex(index))
            {
                LogWarning(
                    std::string("[FFTPack] Target usa indice reservado pelo jogo (") +
                    std::to_string(index) +
                    "): " + normalized
                );
                continue;
            }

            if (parsed.localeSpecific)
            {
                if (parsed.locale == "jp")
                    PutMapping(g_jp, index, plan, true);
                else
                    PutMapping(g_en, index, plan, true);
            }
            else
            {
                PutMapping(g_en, index, plan, false);
                PutMapping(g_jp, index, plan, false);
            }

            ++accepted;

            LogInfo(
                std::string("[FFTPack] Mapping index=") +
                std::to_string(index) +
                " file=" + parsed.actualPath +
                (parsed.localeSpecific
                    ? " locale=" + parsed.locale
                    : " locale=en+jp") +
                " <- " + plan->virtualPath
            );
        }

        LogInfo(
            std::string("[FFTPack] Content plans aceitos: ") +
            std::to_string(accepted)
        );

        return true;
    }

    bool InstallFFTPackHooks()
    {
        if (g_en.empty() && g_jp.empty())
        {
            LogInfo(
                "[FFTPack] Nenhum override FFTPack ativo; hook nao necessario."
            );
            return true;
        }

        auto address =
            FindPatternInMainModule(
                ParsePattern(kFileReadRequestOffsetPattern)
            );

        if (!address)
        {
            address =
                FindPatternInMainModule(
                    ParsePattern(
                        kFileReadRequestOffsetPatternDenuvo
                    )
                );
        }

        if (!address)
        {
            LogError(
                "[FFTPack] fileReadRequestOffset signature nao encontrada."
            );
            return false;
        }

        g_target = reinterpret_cast<void*>(address);

        if (MH_CreateHook(
                g_target,
                reinterpret_cast<void*>(&FileReadRequestOffsetHook),
                reinterpret_cast<void**>(&g_original)
            ) != MH_OK)
        {
            LogError(
                "[FFTPack] Falha criando fileReadRequestOffset hook."
            );
            g_target = nullptr;
            return false;
        }

        if (MH_EnableHook(g_target) != MH_OK)
        {
            MH_RemoveHook(g_target);
            g_target = nullptr;

            LogError(
                "[FFTPack] Falha ativando fileReadRequestOffset hook."
            );
            return false;
        }

        char line[160]{};
        std::snprintf(
            line,
            sizeof(line),
            "[FFTPack] fileReadRequestOffset hook = 0x%llX",
            static_cast<unsigned long long>(address)
        );
        LogInfo(line);

        return true;
    }

    void RemoveFFTPackHooks()
    {
        if (!g_target)
            return;

        MH_DisableHook(g_target);
        MH_RemoveHook(g_target);
        g_target = nullptr;
        g_original = nullptr;
    }

    std::size_t FFTPackOverrideCount()
    {
        std::unordered_set<int> indices;

        for (const auto& [index, mapping] : g_en)
            indices.insert(index);

        for (const auto& [index, mapping] : g_jp)
            indices.insert(index);

        return indices.size();
    }
}
