#include <windows.h>

#include <filesystem>
#include <string>

#include "ContentResolver.hpp"
#include "FFTPackHooks.hpp"
#include "G2DHooks.hpp"
#include "JobDataAPI.hpp"
#include "JobCommandAPI.hpp"
#include "MesTextAPI.hpp"
#include "JobMenuExpansion.hpp"
#include "JobNeedLevelAPI.hpp"
#include "LanguageHooks.hpp"
#include "Logger.hpp"
#include "PackMountHooks.hpp"
#include "PathHook.hpp"
#include "RuntimeConfig.hpp"

namespace
{
    std::filesystem::path GameDirectory()
    {
        wchar_t path[32768]{};

        const DWORD len = GetModuleFileNameW(
            nullptr,
            path,
            static_cast<DWORD>(std::size(path))
        );

        if (!len)
            return {};

        return std::filesystem::path(
            std::wstring(path, len)
        ).parent_path();
    }

    DWORD WINAPI BootstrapThread(LPVOID)
    {
        if (!fftml::InitializeLogger())
            return 1;

        fftml::LogInfo("[FFTModLoader] Prototype 0.10.53");

        const auto gameDirectory = GameDirectory();

        if (gameDirectory.empty())
        {
            fftml::LogError(
                "[FFTModLoader] Nao foi possivel determinar a pasta do jogo."
            );
            return 2;
        }

        fftml::LoadRuntimeConfig(gameDirectory);

        if (!fftml::InitializeContentResolver(gameDirectory))
        {
            fftml::LogWarning(
                "[FFTModLoader] ContentResolver iniciou com erros; "
                "conteudo original sera usado quando necessario."
            );
        }

        fftml::LogInfo(
            std::string("[FFTModLoader] Mods ativos: ") +
            std::to_string(fftml::LoadedModCount())
        );

        fftml::LogInfo(
            std::string("[FFTModLoader] Content plans: ") +
            std::to_string(fftml::ContentPlanCount())
        );

        if (!fftml::InitializeJobDataAPI())
        {
            fftml::LogWarning(
                "[FFTModLoader] JobData API indisponivel nesta build; "
                "o restante do loader continuara funcionando."
            );
        }

        if (!fftml::InitializeJobNeedLevelAPI())
        {
            fftml::LogWarning(
                "[FFTModLoader] JobNeedLevel API indisponivel; "
                "requisitos originais serao mantidos."
            );
        }

        if (!fftml::InitializeJobCommandAPI())
        {
            fftml::LogWarning(
                "[FFTModLoader] JobCommand API indisponivel; "
                "skillsets originais serao mantidos."
            );
        }

        if (!fftml::InitializeMesTextAPI())
        {
            fftml::LogWarning(
                "[FFTModLoader] MesText API indisponivel; "
                "textos originais serao mantidos."
            );
        }

        fftml::LogInfo(
            "[FFTModLoader] Instalando hooks Path/Resolve/Read..."
        );

        if (!fftml::InstallPathHook())
        {
            fftml::LogError(
                "[FFTModLoader] Bootstrap interrompido: "
                "nao foi possivel instalar os hooks base."
            );
            return 3;
        }

        // Install before the game's first sprite requests.  The base g2d.dat
        // resource is opened very early during startup, while individual
        // entries are decoded on demand.
        if (!fftml::InstallG2DHooks())
        {
            fftml::LogWarning(
                "[FFTModLoader] G2D hook indisponivel; "
                "sprites externos usarao os dados originais."
            );
        }

        if (!fftml::InstallLanguageHooks())
        {
            fftml::LogWarning(
                "[FFTModLoader] Language hook indisponivel; "
                "FFTPack usara locale EN por padrao."
            );
        }

        if (!fftml::InstallPackMountHooks(gameDirectory))
        {
            fftml::LogWarning(
                "[FFTModLoader] ModPack mount indisponivel; "
                "fallback direto continuara ativo."
            );
        }

        if (!fftml::InitializeFFTPackOverrides(gameDirectory))
        {
            fftml::LogWarning(
                "[FFTModLoader] FFTPack index/overrides nao inicializados."
            );
        }
        else
        {
            fftml::LogInfo(
                std::string("[FFTModLoader] FFTPack overrides: ") +
                std::to_string(fftml::FFTPackOverrideCount())
            );

            if (!fftml::InstallFFTPackHooks())
            {
                fftml::LogWarning(
                    "[FFTModLoader] Hook FFTPack nao foi instalado."
                );
            }
        }

        if (!fftml::InstallExperimentalJobMenuExpansion())
        {
            fftml::LogWarning(
                "[FFTModLoader] JobMenu experimental nao foi instalado; "
                "patches de JobData continuam validos."
            );
        }

        fftml::LogInfo(
            "[FFTModLoader] Inicializacao 0.10.53 concluida."
        );

        return 0;
    }
}

BOOL APIENTRY DllMain(
    HMODULE module,
    DWORD reason,
    LPVOID reserved
)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);

        HANDLE thread = CreateThread(
            nullptr,
            0,
            BootstrapThread,
            nullptr,
            0,
            nullptr
        );

        if (thread)
            CloseHandle(thread);
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        if (reserved == nullptr)
        {
            fftml::RemoveG2DHooks();
            fftml::RemoveExperimentalJobMenuExpansion();
            fftml::RemoveFFTPackHooks();
            fftml::RemoveLanguageHooks();
            fftml::RemovePackMountHooks();
            fftml::RemovePathHook();
        }

        fftml::ShutdownLogger();
    }

    return TRUE;
}
