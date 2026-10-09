#include "PathHook.hpp"

#include "ContentResolver.hpp"
#include "Logger.hpp"
#include "MesTextAPI.hpp"
#include "PackMountHooks.hpp"
#include "RuntimeConfig.hpp"
#include "SignatureScanner.hpp"

#include <MinHook.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <climits>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>

namespace fs = std::filesystem;

namespace
{
    using PathConstructorFn =
        void* (__fastcall*)(
            void* wrapper,
            const char* path
        );

    using ResolveResourceFn =
        int (__fastcall*)(
            void* wrapper
        );

    using ReadResourceFn =
        int (__fastcall*)(
            void* wrapper,
            void* destination,
            std::uint32_t offset,
            int size
        );

    PathConstructorFn g_originalPath = nullptr;
    ResolveResourceFn g_originalResolve = nullptr;
    ReadResourceFn g_originalRead = nullptr;

    void* g_pathTarget = nullptr;
    void* g_resolveTarget = nullptr;
    void* g_readTarget = nullptr;

    struct WrapperState
    {
        const fftml::ContentPlan* plan = nullptr;

        void* resource = nullptr;

        std::uint64_t originalSize = 0;
        bool sizeWasExpanded = false;

        std::shared_ptr<
            const std::vector<std::uint8_t>
        > baseBytes;
    };

    std::mutex g_mutex;

    std::unordered_map<
        void*,
        WrapperState
    > g_states;

    std::unordered_set<std::string> g_loggedCandidates;
    std::unordered_set<std::string> g_loggedServed;
    std::unordered_set<std::string> g_loggedEdits;
    std::unordered_set<std::string> g_loggedLarge;
    std::unordered_set<std::string> g_loggedLoadUnsupported;

    std::unordered_map<void*, std::string> g_observedPaths;
    std::unordered_set<std::string> g_dumpInitialized;
    std::unordered_set<std::string> g_dumpLogged;

    struct MesResourceState
    {
        void* resource = nullptr;
        std::uint64_t originalSize = 0;
        std::shared_ptr<
            const std::vector<std::uint8_t>
        > bytes;
        std::string virtualPath;
    };

    std::unordered_map<
        void*,
        MesResourceState
    > g_mesWrapperStates;

    std::unordered_set<std::string>
        g_mesServedLogged;

    std::unordered_set<std::string>
        g_textResolveLogged;

    std::unordered_set<std::string>
        g_textReadLogged;

    constexpr const char* kPathPattern =
        "48 89 5C 24 08 "
        "48 89 74 24 10 "
        "48 89 7C 24 18 "
        "55 41 54 41 55 41 56 41 57 "
        "48 89 E5 "
        "48 83 EC 40 "
        "45 31 E4";

    constexpr const char* kResolvePattern =
        "48 89 E0 "
        "48 89 58 10 "
        "48 89 70 18 "
        "48 89 78 20 "
        "55 "
        "48 8D 68 A1 "
        "48 81 EC B0 00 00 00 "
        "48 8B 05 ?? ?? ?? ?? "
        "48 31 E0 "
        "48 89 45 47 "
        "48 8B 51 18";

    constexpr const char* kReadPattern =
        "40 53 "
        "48 83 EC 20 "
        "48 8B 49 18 "
        "48 89 D3 "
        "45 89 C2 "
        "48 85 C9 "
        "75 10";

    bool IsJobTextDumpTarget(
        std::string_view path
    )
    {
        // 0.10.12 diagnostic:
        // capture every 9999_* system text resource that the game actually
        // loads. The previous curated list proved insufficient to locate the
        // literal <jobname(...)>/<jobhelp(...)> source.
        constexpr std::string_view prefix =
            "fftpack/text_sys/9999_";

        if (!path.starts_with(prefix))
            return false;

        return
            path.ends_with(".mes") ||
            path.ends_with(".lzw");
    }

    void LogTextResolveOnce(
        const std::string& normalizedPath,
        int result,
        void* wrapper,
        void* resource,
        std::uint64_t size,
        bool hasPatch
    )
    {
        if (!IsJobTextDumpTarget(
                normalizedPath))
        {
            return;
        }

        std::lock_guard lock(g_mutex);

        if (!g_textResolveLogged.insert(
                normalizedPath).second)
        {
            return;
        }

        char line[420]{};

        std::snprintf(
            line,
            sizeof(line),
            "[TextDiag] Resolve path=%s result=%d wrapper=%p resource=%p size=%llu patch=%d",
            normalizedPath.c_str(),
            result,
            wrapper,
            resource,
            static_cast<unsigned long long>(
                size
            ),
            hasPatch ? 1 : 0
        );

        fftml::LogInfo(line);
    }

    void LogTextReadOnce(
        const std::string& normalizedPath,
        void* wrapper,
        std::uint32_t offset,
        int size,
        bool mesState
    )
    {
        if (!IsJobTextDumpTarget(
                normalizedPath))
        {
            return;
        }

        std::lock_guard lock(g_mutex);

        if (!g_textReadLogged.insert(
                normalizedPath).second)
        {
            return;
        }

        char line[360]{};

        std::snprintf(
            line,
            sizeof(line),
            "[TextDiag] Read path=%s wrapper=%p offset=%u size=%d mesState=%d",
            normalizedPath.c_str(),
            wrapper,
            static_cast<unsigned int>(
                offset
            ),
            size,
            mesState ? 1 : 0
        );

        fftml::LogInfo(line);
    }

    fs::path GetGameDirectory()
    {
        std::array<wchar_t, 32768> buffer{};

        const DWORD length =
            GetModuleFileNameW(
                nullptr,
                buffer.data(),
                static_cast<DWORD>(buffer.size())
            );

        if (length == 0 ||
            length >= buffer.size())
        {
            return fs::current_path();
        }

        return fs::path(
            std::wstring(
                buffer.data(),
                length
            )
        ).parent_path();
    }

    void SetObservedPath(
        void* wrapper,
        const std::string& normalizedPath
    )
    {
        std::lock_guard lock(g_mutex);

        if (!wrapper)
            return;

        g_observedPaths[wrapper] =
            normalizedPath;

        g_mesWrapperStates.erase(
            wrapper
        );
    }

    std::string GetObservedPath(
        void* wrapper
    )
    {
        std::lock_guard lock(g_mutex);

        const auto it =
            g_observedPaths.find(wrapper);

        if (it == g_observedPaths.end())
            return {};

        return it->second;
    }

    bool GetMesWrapperState(
        void* wrapper,
        MesResourceState& state
    )
    {
        std::lock_guard lock(g_mutex);

        const auto it =
            g_mesWrapperStates.find(
                wrapper
            );

        if (it ==
            g_mesWrapperStates.end())
        {
            return false;
        }

        state = it->second;
        return true;
    }

    void PutMesState(
        void* wrapper,
        const MesResourceState& state
    )
    {
        std::lock_guard lock(g_mutex);

        // IMPORTANT:
        // The game reuses the same wrapper and ResourceHandle addresses while
        // loading consecutive text resources. SetObservedPath() clears the
        // wrapper state for every new virtual path, so the safe cache lifetime
        // is the current wrapper+path only. Never cache by ResourceHandle ptr.
        g_mesWrapperStates[
            wrapper
        ] = state;
    }

    void LogMesServedOnce(
        const MesResourceState& state
    )
    {
        std::lock_guard lock(g_mutex);

        if (!g_mesServedLogged.insert(
                state.virtualPath).second)
        {
            return;
        }

        fftml::LogInfo(
            std::string("[MesText] BYTES SERVIDOS: ") +
            state.virtualPath +
            " | original=" +
            std::to_string(
                state.originalSize
            ) +
            " | mod=" +
            std::to_string(
                state.bytes
                    ? state.bytes->size()
                    : 0
            )
        );
    }

    // Forward declaration.
    // 0.10.7 started capturing the original MES bytes from inside
    // PrepareMesTextResource(), but DumpReadChunk() is defined later in this
    // translation unit. MSVC therefore emitted C3861.
    void DumpReadChunk(
        const std::string& normalizedPath,
        const void* data,
        std::uint32_t offset,
        int size
    );

    bool PrepareMesTextResource(
        void* wrapper,
        void* resource,
        const std::string& normalizedPath
    )
    {
        if (!wrapper ||
            !resource ||
            !fftml::HasMesTextPatches(
                normalizedPath))
        {
            return false;
        }

        auto* resourceBytes =
            static_cast<std::uint8_t*>(
                resource
            );

        const std::uint64_t originalSize =
            *reinterpret_cast<
                std::uint64_t*>(
                    resourceBytes + 0x40
                );

        if (originalSize == 0 ||
            originalSize >
                static_cast<std::uint64_t>(
                    INT_MAX
                ))
        {
            fftml::LogWarning(
                std::string("[MesText] Tamanho invalido para ") +
                normalizedPath
            );

            return false;
        }

        std::vector<std::uint8_t>
            original(
                static_cast<std::size_t>(
                    originalSize
                )
            );

        fftml::LogInfo(
            std::string("[MesText] Preparando por PATH: ") +
            normalizedPath +
            " | wrapper=" +
            [&]()
            {
                char b[32]{};
                std::snprintf(
                    b,
                    sizeof(b),
                    "%p",
                    wrapper
                );
                return std::string(b);
            }() +
            " | resource=" +
            [&]()
            {
                char b[32]{};
                std::snprintf(
                    b,
                    sizeof(b),
                    "%p",
                    resource
                );
                return std::string(b);
            }()
        );

        const int readResult =
            g_originalRead(
                wrapper,
                original.data(),
                0,
                static_cast<int>(
                    originalSize
                )
            );

        if (readResult != 0)
        {
            fftml::LogWarning(
                std::string("[MesText] Nao foi possivel ler o resource original: ") +
                normalizedPath +
                " | result=" +
                std::to_string(readResult)
            );

            return false;
        }

        // Previous diagnostic ZIPs contained zero-filled placeholders for
        // patched resources because HookRead served the rebuilt bytes before
        // DumpReadChunk could see the original stream. Capture the exact game
        // bytes here, immediately after calling the original Read trampoline.
        if (fftml::GetRuntimeConfig()
                .dumpJobTextResources &&
            IsJobTextDumpTarget(
                normalizedPath))
        {
            DumpReadChunk(
                normalizedPath,
                original.data(),
                0,
                static_cast<int>(
                    original.size()
                )
            );
        }

        const auto patched =
            fftml::BuildPatchedMes(
                normalizedPath,
                original
            );

        if (!patched)
            return false;

        MesResourceState state;
        state.resource = resource;
        state.originalSize =
            originalSize;
        state.bytes = patched;
        state.virtualPath =
            normalizedPath;

        *reinterpret_cast<
            std::uint64_t*>(
                resourceBytes + 0x40
            ) =
            static_cast<std::uint64_t>(
                patched->size()
            );

        PutMesState(
            wrapper,
            state
        );

        fftml::LogInfo(
            std::string("[MesText] Resource preparado: ") +
            normalizedPath +
            " | original=" +
            std::to_string(originalSize) +
            " | mod=" +
            std::to_string(
                patched->size()
            )
        );

        return true;
    }

    fs::path DumpPathFor(
        const std::string& normalizedPath
    )
    {
        fs::path relative;

        for (const auto& part :
             fs::path(normalizedPath))
        {
            if (part == "..")
                continue;

            relative /= part;
        }

        return
            GetGameDirectory() /
            L"FFTModLoader_Dumps" /
            relative;
    }

    void InitializeDumpFile(
        const std::string& normalizedPath,
        std::uint64_t size
    )
    {
        if (!IsJobTextDumpTarget(normalizedPath))
            return;

        std::lock_guard lock(g_mutex);

        if (!g_dumpInitialized.insert(
                normalizedPath).second)
        {
            return;
        }

        try
        {
            const fs::path output =
                DumpPathFor(normalizedPath);

            fs::create_directories(
                output.parent_path()
            );

            std::ofstream file(
                output,
                std::ios::binary |
                std::ios::trunc
            );

            if (!file)
            {
                fftml::LogWarning(
                    std::string("[TextDump] Nao foi possivel criar: ") +
                    output.generic_string()
                );
                return;
            }

            if (size > 0)
            {
                file.seekp(
                    static_cast<std::streamoff>(
                        size - 1
                    )
                );

                const char zero = 0;
                file.write(&zero, 1);
            }

            fftml::LogInfo(
                std::string("[TextDump] Preparado: ") +
                normalizedPath +
                " | size=" +
                std::to_string(size) +
                " -> " +
                output.generic_string()
            );
        }
        catch (const std::exception& ex)
        {
            fftml::LogWarning(
                std::string("[TextDump] Falha preparando ") +
                normalizedPath +
                ": " +
                ex.what()
            );
        }
    }

    void DumpReadChunk(
        const std::string& normalizedPath,
        const void* data,
        std::uint32_t offset,
        int size
    )
    {
        if (!data ||
            size <= 0 ||
            !IsJobTextDumpTarget(normalizedPath))
        {
            return;
        }

        std::lock_guard lock(g_mutex);

        try
        {
            const fs::path output =
                DumpPathFor(normalizedPath);

            fs::create_directories(
                output.parent_path()
            );

            std::fstream file(
                output,
                std::ios::binary |
                std::ios::in |
                std::ios::out
            );

            if (!file)
            {
                std::ofstream create(
                    output,
                    std::ios::binary
                );
                create.close();

                file.open(
                    output,
                    std::ios::binary |
                    std::ios::in |
                    std::ios::out
                );
            }

            if (!file)
                return;

            file.seekp(
                static_cast<std::streamoff>(
                    offset
                )
            );

            file.write(
                static_cast<const char*>(data),
                static_cast<std::streamsize>(size)
            );

            file.flush();

            if (g_dumpLogged.insert(
                    normalizedPath).second)
            {
                fftml::LogInfo(
                    std::string("[TextDump] Capturando bytes: ") +
                    normalizedPath
                );
            }
        }
        catch (const std::exception& ex)
        {
            fftml::LogWarning(
                std::string("[TextDump] Falha gravando ") +
                normalizedPath +
                ": " +
                ex.what()
            );
        }
    }

    std::string PlanKey(
        const fftml::ContentPlan& plan
    )
    {
        return plan.virtualPath +
            "|" +
            plan.baseModUniqueId;
    }

    void SetPlan(
        void* wrapper,
        const fftml::ContentPlan* plan
    )
    {
        std::lock_guard lock(g_mutex);

        if (!plan)
        {
            g_states.erase(wrapper);
            return;
        }

        WrapperState state;
        state.plan = plan;

        g_states[wrapper] =
            std::move(state);
    }

    bool GetState(
        void* wrapper,
        WrapperState& state
    )
    {
        std::lock_guard lock(g_mutex);

        const auto it =
            g_states.find(wrapper);

        if (it == g_states.end())
            return false;

        state = it->second;
        return true;
    }

    void UpdateState(
        void* wrapper,
        const WrapperState& state
    )
    {
        std::lock_guard lock(g_mutex);
        g_states[wrapper] = state;
    }

    void LogCandidateOnce(
        const fftml::ContentPlan& plan
    )
    {
        std::lock_guard lock(g_mutex);

        const std::string key =
            plan.virtualPath;

        if (!g_loggedCandidates.insert(key).second)
            return;

        fftml::LogInfo(
            std::string("[ContentAPI] Candidate: ") +
            plan.virtualPath +
            " | type=" +
            fftml::ContentKindName(plan.kind) +
            " | base=" +
            (plan.baseAction == fftml::ContentAction::Replace
                ? "Replace"
                : plan.baseAction == fftml::ContentAction::Load
                    ? "Load"
                    : "Original") +
            " | edits=" +
            std::to_string(plan.patches.size())
        );
    }

    void LogServedOnce(
        const fftml::ContentPlan& plan,
        std::uint64_t originalSize,
        std::uint64_t replacementSize
    )
    {
        std::lock_guard lock(g_mutex);

        const std::string key =
            plan.virtualPath;

        if (!g_loggedServed.insert(key).second)
            return;

        fftml::LogInfo(
            std::string("[ContentAPI] Replace BYTES SERVIDOS: ") +
            plan.virtualPath +
            " | original=" +
            std::to_string(originalSize) +
            " | mod=" +
            std::to_string(replacementSize) +
            " | " +
            plan.baseModUniqueId
        );
    }

    void LogEditOnce(
        const fftml::ContentPlan& plan
    )
    {
        std::lock_guard lock(g_mutex);

        if (!g_loggedEdits.insert(
                plan.virtualPath).second)
        {
            return;
        }

        fftml::LogInfo(
            std::string("[ContentAPI] Edit aplicado: ") +
            plan.virtualPath +
            " | patches=" +
            std::to_string(plan.patches.size())
        );
    }

    void LogLargeOnce(
        const fftml::ContentPlan& plan,
        std::uint64_t originalSize,
        std::uint64_t replacementSize,
        bool enabled
    )
    {
        std::lock_guard lock(g_mutex);

        if (!g_loggedLarge.insert(
                plan.virtualPath).second)
        {
            return;
        }

        if (enabled)
        {
            fftml::LogWarning(
                std::string("[ContentAPI] EXPERIMENTAL size override: ") +
                plan.virtualPath +
                " | " +
                std::to_string(originalSize) +
                " -> " +
                std::to_string(replacementSize)
            );
        }
        else
        {
            fftml::LogWarning(
                std::string("[ContentAPI] Replace maior rejeitado: ") +
                plan.virtualPath +
                " | original=" +
                std::to_string(originalSize) +
                " | mod=" +
                std::to_string(replacementSize) +
                " | habilite EnableExperimentalLargerOverrides para testar."
            );
        }
    }

    void LogLoadUnsupportedOnce(
        const fftml::ContentPlan& plan
    )
    {
        std::lock_guard lock(g_mutex);

        if (!g_loggedLoadUnsupported.insert(
                plan.virtualPath).second)
        {
            return;
        }

        fftml::LogWarning(
            std::string("[ContentAPI] Load aguardando SyntheticResource API: ") +
            plan.virtualPath +
            ". O caminho nao existe no ResourceManager original."
        );
    }

    void* __fastcall HookPath(
        void* wrapper,
        const char* path
    )
    {
        if (!path || !*path)
        {
            SetPlan(wrapper, nullptr);

            return g_originalPath(
                wrapper,
                path
            );
        }

        const auto& config =
            fftml::GetRuntimeConfig();

        if (config.logAllResourcePaths)
        {
            fftml::LogInfo(
                std::string("[ResourcePath] ") +
                path
            );
        }

        const fftml::ContentPlan* plan = nullptr;

        const std::string normalizedPath =
            fftml::NormalizeVirtualPath(path);

        SetObservedPath(
            wrapper,
            normalizedPath
        );

        // FFTPack has its own indexed/sector reader. Let that specialized
        // hook own fftpack/* completely so the generic Resource Read hook
        // cannot impose the normal Resource-size assumptions on it.
        const bool specializedFFTPack =
            normalizedPath.rfind("fftpack/", 0) == 0;

        if (config.enableContentOverrides &&
            !specializedFFTPack)
        {
            plan =
                fftml::ResolveContentPlan(
                    normalizedPath
                );
        }

        SetPlan(wrapper, plan);

        if (plan)
            LogCandidateOnce(*plan);

        // The original virtual path is always preserved.
        return g_originalPath(
            wrapper,
            path
        );
    }

    int __fastcall HookResolve(
        void* wrapper
    )
    {
        const int result =
            g_originalResolve(wrapper);

        {
            const std::string observedPath =
                GetObservedPath(wrapper);

            if (IsJobTextDumpTarget(
                    observedPath))
            {
                void* resource = nullptr;
                std::uint64_t resourceSize = 0;

                if (wrapper)
                {
                    auto* wrapperBytes =
                        static_cast<std::uint8_t*>(
                            wrapper
                        );

                    resource =
                        *reinterpret_cast<void**>(
                            wrapperBytes + 0x18
                        );

                    if (resource)
                    {
                        auto* resourceBytes =
                            static_cast<std::uint8_t*>(
                                resource
                            );

                        resourceSize =
                            *reinterpret_cast<std::uint64_t*>(
                                resourceBytes + 0x40
                            );
                    }
                }

                LogTextResolveOnce(
                    observedPath,
                    result,
                    wrapper,
                    resource,
                    resourceSize,
                    fftml::HasMesTextPatches(
                        observedPath
                    )
                );
            }
        }

        if (result == 0 &&
            fftml::GetRuntimeConfig()
                .dumpJobTextResources)
        {
            const std::string observedPath =
                GetObservedPath(wrapper);

            if (IsJobTextDumpTarget(
                    observedPath))
            {
                auto* wrapperBytes =
                    static_cast<std::uint8_t*>(
                        wrapper
                    );

                void* resource =
                    *reinterpret_cast<void**>(
                        wrapperBytes + 0x18
                    );

                if (resource)
                {
                    auto* resourceBytes =
                        static_cast<std::uint8_t*>(
                            resource
                        );

                    const std::uint64_t resourceSize =
                        *reinterpret_cast<std::uint64_t*>(
                            resourceBytes + 0x40
                        );

                    InitializeDumpFile(
                        observedPath,
                        resourceSize
                    );
                }
            }
        }

        if (result == 0)
        {
            const std::string observedPath =
                GetObservedPath(wrapper);

            if (fftml::HasMesTextPatches(
                    observedPath))
            {
                auto* wrapperBytes =
                    static_cast<std::uint8_t*>(
                        wrapper
                    );

                void* resource =
                    *reinterpret_cast<void**>(
                        wrapperBytes + 0x18
                    );

                PrepareMesTextResource(
                    wrapper,
                    resource,
                    observedPath
                );
            }
        }

        WrapperState state;

        if (!GetState(wrapper, state) ||
            !state.plan)
        {
            return result;
        }

        const auto& plan = *state.plan;

        // If FFTModPacker placed this target in modded.pac and the pack is
        // mounted, the engine already received the replacement through its
        // normal ResourceManager. Do not apply the old byte-level fallback.
        if (fftml::IsGeneratedPackTarget(
                plan.virtualPath) &&
            fftml::IsGeneratedModPackMounted())
        {
            return result;
        }

        if (result != 0)
        {
            if (plan.baseAction ==
                fftml::ContentAction::Load)
            {
                LogLoadUnsupportedOnce(plan);
            }

            return result;
        }

        auto* wrapperBytes =
            static_cast<std::uint8_t*>(
                wrapper
            );

        void* resource =
            *reinterpret_cast<void**>(
                wrapperBytes + 0x18
            );

        if (!resource)
            return result;

        auto* resourceBytes =
            static_cast<std::uint8_t*>(
                resource
            );

        const std::uint64_t originalSize =
            *reinterpret_cast<std::uint64_t*>(
                resourceBytes + 0x40
            );

        state.resource = resource;
        state.originalSize = originalSize;

        if (plan.baseAction !=
                fftml::ContentAction::None)
        {
            state.baseBytes =
                fftml::GetBaseBytes(plan);

            if (state.baseBytes)
            {
                const std::uint64_t replacementSize =
                    static_cast<std::uint64_t>(
                        state.baseBytes->size()
                    );

                if (replacementSize > originalSize)
                {
                    const bool allow =
                        fftml::GetRuntimeConfig()
                            .enableExperimentalLargerOverrides;

                    LogLargeOnce(
                        plan,
                        originalSize,
                        replacementSize,
                        allow
                    );

                    if (allow)
                    {
                        // The known wrapper API reads size after
                        // FUN_151C5FEE0 and then reads bytes through
                        // FUN_151C62D70. Updating +0x40 enables larger
                        // buffers for that path.
                        //
                        // This is experimental because unknown engine
                        // code may bypass the Read Resource function.
                        *reinterpret_cast<std::uint64_t*>(
                            resourceBytes + 0x40
                        ) = replacementSize;

                        state.sizeWasExpanded = true;
                    }
                }
            }
        }

        UpdateState(
            wrapper,
            state
        );

        return result;
    }

    int __fastcall HookRead(
        void* wrapper,
        void* destination,
        std::uint32_t offset,
        int size
    )
    {
        if (!wrapper ||
            !destination ||
            size <= 0)
        {
            return g_originalRead(
                wrapper,
                destination,
                offset,
                size
            );
        }

        MesResourceState mesState;

        const bool hasMesState =
            GetMesWrapperState(
                wrapper,
                mesState
            ) &&
            mesState.bytes;

        const std::string observedReadPath =
            GetObservedPath(wrapper);

        LogTextReadOnce(
            observedReadPath,
            wrapper,
            offset,
            size,
            hasMesState
        );

        if (hasMesState)
        {
            const std::uint64_t replacementSize =
                static_cast<std::uint64_t>(
                    mesState.bytes->size()
                );

            if (static_cast<std::uint64_t>(
                    offset) >=
                replacementSize)
            {
                std::memset(
                    destination,
                    0,
                    static_cast<std::size_t>(
                        size
                    )
                );

                return 0;
            }

            const std::uint64_t available =
                replacementSize -
                static_cast<std::uint64_t>(
                    offset
                );

            const std::size_t effective =
                static_cast<std::size_t>(
                    std::min<std::uint64_t>(
                        static_cast<std::uint64_t>(
                            size
                        ),
                        available
                    )
                );

            std::memcpy(
                destination,
                mesState.bytes->data() +
                    offset,
                effective
            );

            if (effective <
                static_cast<std::size_t>(
                    size
                ))
            {
                std::memset(
                    static_cast<std::uint8_t*>(
                        destination
                    ) + effective,
                    0,
                    static_cast<std::size_t>(
                        size
                    ) - effective
                );
            }

            LogMesServedOnce(
                mesState
            );

            return 0;
        }

        WrapperState state;

        if (!GetState(wrapper, state) ||
            !state.plan)
        {
            const int result =
                g_originalRead(
                    wrapper,
                    destination,
                    offset,
                    size
                );

            if (result == 0 &&
                fftml::GetRuntimeConfig()
                    .dumpJobTextResources)
            {
                const std::string observedPath =
                    GetObservedPath(wrapper);

                DumpReadChunk(
                    observedPath,
                    destination,
                    offset,
                    size
                );
            }

            return result;
        }

        const auto& plan = *state.plan;

        if (fftml::IsGeneratedPackTarget(
                plan.virtualPath) &&
            fftml::IsGeneratedModPackMounted())
        {
            // Pack delivery path: the original Resource already points at
            // the data selected from modded.pac.
            return g_originalRead(
                wrapper,
                destination,
                offset,
                size
            );
        }

        // Edit-only path: let the engine read original bytes first.
        if (plan.baseAction ==
            fftml::ContentAction::None)
        {
            const int result =
                g_originalRead(
                    wrapper,
                    destination,
                    offset,
                    size
                );

            if (result == 0 &&
                !plan.patches.empty())
            {
                fftml::ApplyBinaryPatches(
                    plan,
                    offset,
                    destination,
                    static_cast<std::size_t>(size)
                );

                LogEditOnce(plan);
            }

            return result;
        }

        if (!state.baseBytes)
        {
            state.baseBytes =
                fftml::GetBaseBytes(plan);

            UpdateState(
                wrapper,
                state
            );
        }

        if (!state.baseBytes)
        {
            return g_originalRead(
                wrapper,
                destination,
                offset,
                size
            );
        }

        const std::uint64_t replacementSize =
            static_cast<std::uint64_t>(
                state.baseBytes->size()
            );

        // Safe default: larger replacement falls back to original.
        if (replacementSize >
                state.originalSize &&
            !state.sizeWasExpanded)
        {
            const int result =
                g_originalRead(
                    wrapper,
                    destination,
                    offset,
                    size
                );

            if (result == 0 &&
                !plan.patches.empty())
            {
                fftml::ApplyBinaryPatches(
                    plan,
                    offset,
                    destination,
                    static_cast<std::size_t>(size)
                );

                LogEditOnce(plan);
            }

            return result;
        }

        if (static_cast<std::uint64_t>(offset) >=
            replacementSize)
        {
            std::memset(
                destination,
                0,
                static_cast<std::size_t>(size)
            );

            return 0;
        }

        const std::uint64_t available =
            replacementSize -
            static_cast<std::uint64_t>(
                offset
            );

        const std::size_t effective =
            static_cast<std::size_t>(
                std::min<std::uint64_t>(
                    static_cast<std::uint64_t>(size),
                    available
                )
            );

        std::memcpy(
            destination,
            state.baseBytes->data() + offset,
            effective
        );

        if (effective <
            static_cast<std::size_t>(size))
        {
            std::memset(
                static_cast<std::uint8_t*>(
                    destination
                ) + effective,
                0,
                static_cast<std::size_t>(size) -
                    effective
            );
        }

        if (!plan.patches.empty())
        {
            fftml::ApplyBinaryPatches(
                plan,
                offset,
                destination,
                static_cast<std::size_t>(size)
            );

            LogEditOnce(plan);
        }

        LogServedOnce(
            plan,
            state.originalSize,
            replacementSize
        );

        return 0;
    }

    bool CreateHook(
        void* target,
        void* detour,
        void** original,
        const char* label
    )
    {
        if (MH_CreateHook(
                target,
                detour,
                original) != MH_OK)
        {
            fftml::LogError(
                std::string("[Hook] Create falhou: ") +
                label
            );

            return false;
        }

        return true;
    }
}

namespace fftml
{
    bool InstallPathHook()
    {
        const auto pathAddress =
            FindPatternInMainModule(
                ParsePattern(kPathPattern)
            );

        const auto resolveAddress =
            FindPatternInMainModule(
                ParsePattern(kResolvePattern)
            );

        const auto readAddress =
            FindPatternInMainModule(
                ParsePattern(kReadPattern)
            );

        if (!pathAddress ||
            !resolveAddress ||
            !readAddress)
        {
            LogError(
                "[Hook] Uma ou mais signatures "
                "(Path/Resolve/Read) nao foram encontradas."
            );

            char buffer[220]{};

            std::snprintf(
                buffer,
                sizeof(buffer),
                "[Hook] Path=0x%llX Resolve=0x%llX Read=0x%llX",
                static_cast<unsigned long long>(pathAddress),
                static_cast<unsigned long long>(resolveAddress),
                static_cast<unsigned long long>(readAddress)
            );

            LogError(buffer);
            return false;
        }

        g_pathTarget =
            reinterpret_cast<void*>(
                pathAddress
            );

        g_resolveTarget =
            reinterpret_cast<void*>(
                resolveAddress
            );

        g_readTarget =
            reinterpret_cast<void*>(
                readAddress
            );

        {
            char buffer[220]{};

            std::snprintf(
                buffer,
                sizeof(buffer),
                "[Hook] Path=0x%llX | Resolve=0x%llX | Read=0x%llX",
                static_cast<unsigned long long>(pathAddress),
                static_cast<unsigned long long>(resolveAddress),
                static_cast<unsigned long long>(readAddress)
            );

            LogInfo(buffer);
        }

        if (MH_Initialize() != MH_OK)
        {
            LogError(
                "[Hook] MH_Initialize falhou."
            );
            return false;
        }

        if (!CreateHook(
                g_pathTarget,
                reinterpret_cast<void*>(
                    &HookPath
                ),
                reinterpret_cast<void**>(
                    &g_originalPath
                ),
                "Path"))
        {
            MH_Uninitialize();
            return false;
        }

        if (!CreateHook(
                g_resolveTarget,
                reinterpret_cast<void*>(
                    &HookResolve
                ),
                reinterpret_cast<void**>(
                    &g_originalResolve
                ),
                "Resolve"))
        {
            MH_RemoveHook(g_pathTarget);
            MH_Uninitialize();
            return false;
        }

        if (!CreateHook(
                g_readTarget,
                reinterpret_cast<void*>(
                    &HookRead
                ),
                reinterpret_cast<void**>(
                    &g_originalRead
                ),
                "Read"))
        {
            MH_RemoveHook(
                g_resolveTarget
            );
            MH_RemoveHook(
                g_pathTarget
            );
            MH_Uninitialize();
            return false;
        }

        if (MH_EnableHook(
                MH_ALL_HOOKS) != MH_OK)
        {
            LogError(
                "[Hook] Enable ALL falhou."
            );

            MH_RemoveHook(
                g_readTarget
            );
            MH_RemoveHook(
                g_resolveTarget
            );
            MH_RemoveHook(
                g_pathTarget
            );
            MH_Uninitialize();
            return false;
        }

        LogInfo(
            "[Hook] Path + Resolve + Read hooks ATIVOS."
        );

        return true;
    }

    void RemovePathHook()
    {
        MH_DisableHook(
            MH_ALL_HOOKS
        );

        if (g_readTarget)
            MH_RemoveHook(
                g_readTarget
            );

        if (g_resolveTarget)
            MH_RemoveHook(
                g_resolveTarget
            );

        if (g_pathTarget)
            MH_RemoveHook(
                g_pathTarget
            );

        g_readTarget = nullptr;
        g_resolveTarget = nullptr;
        g_pathTarget = nullptr;

        MH_Uninitialize();
    }
}
