#include "G2DHooks.hpp"

#include "ContentResolver.hpp"
#include "Logger.hpp"
#include "SignatureScanner.hpp"

#include <MinHook.h>
#include <windows.h>

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

namespace
{
    // Layout used by the game's CFILE_DAT object. These offsets match the
    // Reloaded-II utility loader used by GenericJobs.
    constexpr std::size_t kWorkBufferOffset = 0x18;
    constexpr std::size_t kOutputBufferOffset = 0x20;
    constexpr std::size_t kCurrentFileIndexOffset = 0x13C;

    using DecodeG2DFn = int (__fastcall*)(void*, int, void*);
    using LoadG2DFn = void (__fastcall*)(void*, int, int, int);
    using UnloadG2DFn = void (__fastcall*)(void*);

    DecodeG2DFn g_originalDecode = nullptr;
    LoadG2DFn g_originalLoad = nullptr;
    UnloadG2DFn g_originalUnload = nullptr;

    void* g_decodeTarget = nullptr;
    void* g_loadTarget = nullptr;
    void* g_unloadTarget = nullptr;

    struct OverrideEntry
    {
        std::shared_ptr<const std::vector<std::uint8_t>> bytes;
        void* buffer = nullptr;
    };

    std::mutex g_mutex;
    std::unordered_map<int, OverrideEntry> g_overrides;

    constexpr const char* kDecodePattern =
        "40 53 56 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? "
        "48 31 E0 48 89 84 24 ?? ?? ?? ?? 89 D7";

    constexpr const char* kDecodeAltPattern =
        "40 53 56 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? "
        "48 33 C4 48 89 84 24 ?? ?? ?? ?? 8B FA 49 8B F0";

    constexpr const char* kLoadPattern =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 56 "
        "41 57 48 83 EC ?? 44 89 C0";

    constexpr const char* kLoadAltPattern =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 56 "
        "41 57 48 83 EC ?? 41 8B C0";

    constexpr const char* kUnloadPattern =
        "40 53 48 83 EC ?? 48 89 CB 48 8B 49 ?? 48 85 C9 74 ?? "
        "45 31 C0 31 D2 E8 ?? ?? ?? ?? 48 83 63 ?? ?? 48 83 63";

    constexpr const char* kUnloadAltPattern =
        "40 53 48 83 EC ?? 48 8B D9 48 8B 49 ?? 48 85 C9 74 ?? "
        "45 33 C0 33 D2 E8 ?? ?? ?? ?? 48 83 63 ?? ?? 48 83 63";

    std::uintptr_t FindEither(const char* primary, const char* alternate)
    {
        auto address = fftml::FindPatternInMainModule(
            fftml::ParsePattern(primary)
        );
        if (!address)
        {
            address = fftml::FindPatternInMainModule(
                fftml::ParsePattern(alternate)
            );
        }
        return address;
    }

    std::shared_ptr<const std::vector<std::uint8_t>> ResolveOverride(int fileIndex)
    {
        if (fileIndex < 0)
            return {};

        {
            std::scoped_lock lock(g_mutex);
            const auto found = g_overrides.find(fileIndex);
            if (found != g_overrides.end())
                return found->second.bytes;
        }

        char path[96]{};
        std::snprintf(
            path,
            sizeof(path),
            "system/ffto/g2d/tex_%d.bin",
            fileIndex
        );

        const auto* plan = fftml::ResolveContentPlan(path);
        if (!plan)
            return {};

        auto bytes = fftml::GetBaseBytes(*plan);
        if (!bytes || bytes->empty())
            return {};

        std::scoped_lock lock(g_mutex);
        auto [it, inserted] = g_overrides.try_emplace(
            fileIndex,
            OverrideEntry{bytes, nullptr}
        );
        if (inserted)
        {
            fftml::LogInfo(
                std::string("[G2D] Override localizado: ") + path +
                " (" + std::to_string(bytes->size()) + " bytes)"
            );
        }
        return it->second.bytes;
    }

    void* EnsurePersistentBuffer(int fileIndex)
    {
        auto bytes = ResolveOverride(fileIndex);
        if (!bytes)
            return nullptr;

        std::scoped_lock lock(g_mutex);
        auto& entry = g_overrides.at(fileIndex);
        if (!entry.buffer)
        {
            entry.buffer = VirtualAlloc(
                nullptr,
                entry.bytes->size(),
                MEM_COMMIT | MEM_RESERVE,
                PAGE_READWRITE
            );
            if (!entry.buffer)
            {
                fftml::LogError("[G2D] Falha ao alocar buffer persistente.");
                return nullptr;
            }
            std::memcpy(entry.buffer, entry.bytes->data(), entry.bytes->size());
        }
        return entry.buffer;
    }

    int __fastcall DecodeG2DHook(void* self, int fileIndex, void* output)
    {
        auto bytes = ResolveOverride(fileIndex);
        if (!bytes || !output)
            return g_originalDecode(self, fileIndex, output);

        std::memcpy(output, bytes->data(), bytes->size());
        return static_cast<int>(bytes->size());
    }

    void __fastcall LoadG2DHook(void* self, int fileIndex, int nextFileIndex, int a4)
    {
        void* buffer = EnsurePersistentBuffer(fileIndex);
        if (!buffer)
        {
            g_originalLoad(self, fileIndex, nextFileIndex, a4);
            return;
        }

        auto* object = static_cast<std::uint8_t*>(self);
        *reinterpret_cast<void**>(object + kWorkBufferOffset) = buffer;
        *reinterpret_cast<void**>(object + kOutputBufferOffset) = buffer;
        *reinterpret_cast<int*>(object + kCurrentFileIndexOffset) = fileIndex;
    }

    void __fastcall UnloadG2DHook(void* self)
    {
        auto* object = static_cast<std::uint8_t*>(self);
        const int fileIndex =
            *reinterpret_cast<int*>(object + kCurrentFileIndexOffset);

        if (!ResolveOverride(fileIndex))
        {
            g_originalUnload(self);
            return;
        }

        *reinterpret_cast<void**>(object + kWorkBufferOffset) = nullptr;
        *reinterpret_cast<void**>(object + kOutputBufferOffset) = nullptr;
        *reinterpret_cast<int*>(object + kCurrentFileIndexOffset) = -1;
    }

    bool CreateAndEnable(
        void* target,
        void* detour,
        void** original,
        const char* name)
    {
        if (MH_CreateHook(target, detour, original) != MH_OK)
        {
            fftml::LogError(std::string("[G2D] Falha ao criar hook ") + name + ".");
            return false;
        }
        if (MH_EnableHook(target) != MH_OK)
        {
            MH_RemoveHook(target);
            fftml::LogError(std::string("[G2D] Falha ao ativar hook ") + name + ".");
            return false;
        }
        return true;
    }
}

namespace fftml
{
    bool InstallG2DHooks()
    {
        const auto decode = FindEither(kDecodePattern, kDecodeAltPattern);
        const auto load = FindEither(kLoadPattern, kLoadAltPattern);
        const auto unload = FindEither(kUnloadPattern, kUnloadAltPattern);

        if (!decode || !load || !unload)
        {
            LogWarning("[G2D] Assinaturas Decode/Load/Unload incompletas.");
            return false;
        }

        g_decodeTarget = reinterpret_cast<void*>(decode);
        g_loadTarget = reinterpret_cast<void*>(load);
        g_unloadTarget = reinterpret_cast<void*>(unload);

        if (!CreateAndEnable(g_decodeTarget, reinterpret_cast<void*>(&DecodeG2DHook),
                            reinterpret_cast<void**>(&g_originalDecode), "Decode"))
            return false;
        if (!CreateAndEnable(g_loadTarget, reinterpret_cast<void*>(&LoadG2DHook),
                            reinterpret_cast<void**>(&g_originalLoad), "Load"))
        {
            MH_DisableHook(g_decodeTarget);
            MH_RemoveHook(g_decodeTarget);
            g_decodeTarget = nullptr;
            return false;
        }
        if (!CreateAndEnable(g_unloadTarget, reinterpret_cast<void*>(&UnloadG2DHook),
                            reinterpret_cast<void**>(&g_originalUnload), "Unload"))
        {
            MH_DisableHook(g_loadTarget);
            MH_RemoveHook(g_loadTarget);
            MH_DisableHook(g_decodeTarget);
            MH_RemoveHook(g_decodeTarget);
            g_loadTarget = nullptr;
            g_decodeTarget = nullptr;
            return false;
        }

        char message[220]{};
        std::snprintf(message, sizeof(message),
            "[G2D] Hooks ativos: Decode=0x%llX Load=0x%llX Unload=0x%llX",
            static_cast<unsigned long long>(decode),
            static_cast<unsigned long long>(load),
            static_cast<unsigned long long>(unload));
        LogInfo(message);
        return true;
    }

    void RemoveG2DHooks()
    {
        void** targets[] = {&g_unloadTarget, &g_loadTarget, &g_decodeTarget};
        for (void** target : targets)
        {
            if (*target)
            {
                MH_DisableHook(*target);
                MH_RemoveHook(*target);
                *target = nullptr;
            }
        }

        std::scoped_lock lock(g_mutex);
        for (auto& [_, entry] : g_overrides)
        {
            if (entry.buffer)
                VirtualFree(entry.buffer, 0, MEM_RELEASE);
        }
        g_overrides.clear();
    }
}
