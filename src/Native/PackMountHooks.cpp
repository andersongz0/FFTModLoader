#include "PackMountHooks.hpp"

#include "LanguageHooks.hpp"
#include "Logger.hpp"
#include "SignatureScanner.hpp"

#include <MinHook.h>

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <string>
#include <vector>

namespace fs = std::filesystem;

namespace
{
    // Reference signatures/ABI were cross-checked against the uploaded
    // fftivc.utility.modloader and FaithFramework sources.

    using RegisterPackListFn =
        int (__fastcall*)(void* resourceManager);

    using RegisterPackFn =
        int (__fastcall*)(
            void* resourceManager,
            unsigned char* packName
        );

    struct ResourceFactory
    {
        void* createWrapCb;
        void* createCb;
        void* field10;
        void* field18;
    };

    using GetOrCreateResourceFn =
        std::intptr_t (__fastcall*)(
            void* resourceManager,
            void** outResource,
            std::uint32_t pathHash,
            std::uint32_t bucketIndex,
            unsigned char* path,
            unsigned char* fileExtension,
            std::uint8_t a7,
            std::uint8_t a8,
            ResourceFactory* outFactory,
            std::intptr_t factoryArg
        );

    RegisterPackListFn g_originalRegisterPackList = nullptr;
    GetOrCreateResourceFn g_originalGetOrCreate = nullptr;
    RegisterPackFn g_registerPack = nullptr;

    void* g_registerPackListTarget = nullptr;
    void* g_getOrCreateTarget = nullptr;

    fs::path g_gameDirectory;

    struct GeneratedPack
    {
        fs::path path;
        std::string name;
    };

    std::vector<GeneratedPack> g_generatedPacks;

    // 0 = not attempted
    // 1 = mounting
    // 2 = mounted
    // 3 = failed
    std::atomic<int> g_mountState{0};

    constexpr const char* kRegisterPackListPattern =
        "48 89 5C 24 ?? "
        "48 89 74 24 ?? "
        "48 89 7C 24 ?? "
        "55 48 8B EC "
        "48 81 EC ?? ?? ?? ?? "
        "48 8D 05";

    constexpr const char* kRegisterPackPattern =
        "48 89 5C 24 ?? "
        "55 56 57 "
        "48 81 EC ?? ?? ?? ?? "
        "48 8B 05 ?? ?? ?? ?? "
        "48 33 C4 "
        "48 89 44 24 ?? "
        "48 8B F9";

    constexpr const char* kGetOrCreatePattern =
        "40 55 53 56 57 "
        "41 54 41 55 41 56 41 57 "
        "48 8D 6C 24 ?? "
        "48 81 EC ?? ?? ?? ?? "
        "48 8B 05 ?? ?? ?? ?? "
        "48 33 C4 "
        "48 89 45 ?? "
        "4C 8B 7D ?? "
        "45 8B F0";

    bool TryMount(void* resourceManager)
    {
        if (!resourceManager ||
            !g_registerPack ||
            g_generatedPacks.empty())
        {
            return false;
        }

        int expected = 0;

        if (!g_mountState.compare_exchange_strong(
                expected,
                1))
        {
            // Re-entrant RegisterPack calls can pass through here.
            return g_mountState.load() == 2;
        }

        std::size_t mounted = 0;

        for (const GeneratedPack& pack : g_generatedPacks)
        {
            fftml::LogInfo(
                "[ModPack] Tentando montar data/enhanced/" +
                pack.name + "..."
            );

            std::vector<unsigned char> mutableName(
                pack.name.begin(),
                pack.name.end());
            mutableName.push_back(0);

            const int result = g_registerPack(
                resourceManager,
                mutableName.data());

            if (result < 0)
            {
                g_mountState.store(3);

                char message[240]{};

                std::snprintf(
                    message,
                    sizeof(message),
                    "[ModPack] RegisterPack %s falhou: 0x%08X",
                    pack.name.c_str(),
                    static_cast<unsigned int>(result)
                );

                fftml::LogError(message);
                return false;
            }

            ++mounted;
            fftml::LogInfo(
                "[ModPack] " + pack.name +
                " montado com sucesso."
            );
        }

        g_mountState.store(2);

        fftml::LogInfo(
            "[ModPack] Pacotes gerados montados: " +
            std::to_string(mounted)
        );

        return true;
    }

    int __fastcall RegisterPackListHook(
        void* resourceManager
    )
    {
        const int result =
            g_originalRegisterPackList(
                resourceManager
            );

        if (result >= 0)
            TryMount(resourceManager);

        return result;
    }

    std::intptr_t __fastcall GetOrCreateHook(
        void* resourceManager,
        void** outResource,
        std::uint32_t pathHash,
        std::uint32_t bucketIndex,
        unsigned char* path,
        unsigned char* fileExtension,
        std::uint8_t a7,
        std::uint8_t a8,
        ResourceFactory* outFactory,
        std::intptr_t factoryArg
    )
    {
        // The standalone launcher injects after the Windows loader is ready.
        // If RegisterPackList already ran, this is our reliable late fallback:
        // the first resource request gives us a valid ResourceManager instance.
        if (g_mountState.load() == 0)
            TryMount(resourceManager);

        return g_originalGetOrCreate(
            resourceManager,
            outResource,
            pathHash,
            bucketIndex,
            path,
            fileExtension,
            a7,
            a8,
            outFactory,
            factoryArg
        );
    }
}

namespace fftml
{
    bool InstallPackMountHooks(
        const fs::path& gameDirectory
    )
    {
        g_gameDirectory = gameDirectory;
        const fs::path enhancedDirectory =
            gameDirectory /
            L"data" /
            L"enhanced";

        g_generatedPacks.clear();

        const auto addIfPresent =
            [&](const char* name)
            {
                const fs::path path = enhancedDirectory / name;
                if (fs::exists(path))
                    g_generatedPacks.push_back({path, name});
            };

        addIfPresent("modded.pac");

        // Match fftivc.utility.modloader exactly: only the base pack is
        // registered. RegisterPack("modded.pac") makes the resource manager
        // associate and mount the matching modded.<language>.pac itself.
        // Registering modded.en.pac as a second independent base pack corrupts
        // the pack lookup order: GenericJobs UI resources then resolve against
        // the stock 0x20/0x21 entries (black/Mime sprites and empty progress).
        LogInfo(
            "[ModPack] Pacotes localizados serao associados pelo jogo ao modded.pac."
        );

        g_mountState.store(0);

        if (g_generatedPacks.empty())
        {
            LogInfo(
                "[ModPack] Nenhum data/enhanced/modded*.pac gerado; "
                "hooks de montagem nao sao necessarios."
            );

            return true;
        }

        const auto registerPackAddress =
            FindPatternInMainModule(
                ParsePattern(
                    kRegisterPackPattern
                )
            );

        const auto registerPackListAddress =
            FindPatternInMainModule(
                ParsePattern(
                    kRegisterPackListPattern
                )
            );

        const auto getOrCreateAddress =
            FindPatternInMainModule(
                ParsePattern(
                    kGetOrCreatePattern
                )
            );

        if (!registerPackAddress)
        {
            LogError(
                "[ModPack] Signature RegisterPack nao encontrada."
            );
            return false;
        }

        g_registerPack =
            reinterpret_cast<RegisterPackFn>(
                registerPackAddress
            );

        char addressLog[260]{};

        std::snprintf(
            addressLog,
            sizeof(addressLog),
            "[ModPack] RegisterPack=0x%llX | RegisterPackList=0x%llX | GetOrCreate=0x%llX",
            static_cast<unsigned long long>(
                registerPackAddress
            ),
            static_cast<unsigned long long>(
                registerPackListAddress
            ),
            static_cast<unsigned long long>(
                getOrCreateAddress
            )
        );

        LogInfo(addressLog);

        // PathHook initializes MinHook first.
        // We intentionally tolerate missing RegisterPackList because the
        // GetOrCreate fallback can mount the pack later.
        if (registerPackListAddress)
        {
            g_registerPackListTarget =
                reinterpret_cast<void*>(
                    registerPackListAddress
                );

            if (MH_CreateHook(
                    g_registerPackListTarget,
                    reinterpret_cast<void*>(
                        &RegisterPackListHook
                    ),
                    reinterpret_cast<void**>(
                        &g_originalRegisterPackList
                    )) != MH_OK)
            {
                LogWarning(
                    "[ModPack] Nao foi possivel hookar RegisterPackList; "
                    "usando fallback GetOrCreate."
                );

                g_registerPackListTarget = nullptr;
            }
            else if (MH_EnableHook(
                         g_registerPackListTarget
                     ) != MH_OK)
            {
                MH_RemoveHook(
                    g_registerPackListTarget
                );

                g_registerPackListTarget = nullptr;

                LogWarning(
                    "[ModPack] RegisterPackList hook nao ativou; "
                    "usando fallback GetOrCreate."
                );
            }
        }

        if (getOrCreateAddress)
        {
            g_getOrCreateTarget =
                reinterpret_cast<void*>(
                    getOrCreateAddress
                );

            if (MH_CreateHook(
                    g_getOrCreateTarget,
                    reinterpret_cast<void*>(
                        &GetOrCreateHook
                    ),
                    reinterpret_cast<void**>(
                        &g_originalGetOrCreate
                    )) != MH_OK)
            {
                LogWarning(
                    "[ModPack] GetOrCreate hook nao foi criado."
                );

                g_getOrCreateTarget = nullptr;
            }
            else if (MH_EnableHook(
                         g_getOrCreateTarget
                     ) != MH_OK)
            {
                MH_RemoveHook(
                    g_getOrCreateTarget
                );

                g_getOrCreateTarget = nullptr;

                LogWarning(
                    "[ModPack] GetOrCreate hook nao foi ativado."
                );
            }
        }

        if (!g_registerPackListTarget &&
            !g_getOrCreateTarget)
        {
            LogError(
                "[ModPack] Nenhum ponto de montagem foi hookado."
            );
            return false;
        }

        LogInfo(
            "[ModPack] Sistema de montagem preparado."
        );

        return true;
    }

    void RemovePackMountHooks()
    {
        if (g_getOrCreateTarget)
        {
            MH_DisableHook(
                g_getOrCreateTarget
            );

            MH_RemoveHook(
                g_getOrCreateTarget
            );

            g_getOrCreateTarget = nullptr;
        }

        if (g_registerPackListTarget)
        {
            MH_DisableHook(
                g_registerPackListTarget
            );

            MH_RemoveHook(
                g_registerPackListTarget
            );

            g_registerPackListTarget = nullptr;
        }

        g_registerPack = nullptr;
        g_generatedPacks.clear();
        g_mountState.store(0);
    }

    bool IsGeneratedModPackPresent()
    {
        return !g_generatedPacks.empty();
    }

    bool IsGeneratedModPackMounted()
    {
        return g_mountState.load() == 2;
    }
}
