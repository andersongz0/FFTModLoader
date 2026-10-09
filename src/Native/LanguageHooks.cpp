#include "LanguageHooks.hpp"

#include "Logger.hpp"
#include "SignatureScanner.hpp"

#include <MinHook.h>

#include <atomic>
#include <cstdint>
#include <cstdio>

namespace
{
    using SetLanguageFn =
        void (__fastcall*)(void* self, int locale);

    SetLanguageFn g_originalSetLanguage = nullptr;
    void* g_target = nullptr;

    // FaithFramework / fftivc.utility.modloader:
    // faith::Locale::LanguageManager::SetLanguage
    constexpr const char* kSetLanguagePattern =
        "48 89 5C 24 ?? "
        "57 48 83 EC ?? "
        "48 8B 05 ?? ?? ?? ?? "
        "48 33 C4 "
        "48 89 44 24 ?? "
        "48 8B F9 3B 51";

    constexpr const char* kSetLanguagePatternDenuvo =
        "48 89 5C 24 ?? "
        "57 48 83 EC ?? "
        "48 8B 05 ?? ?? ?? ?? "
        "48 31 E0 "
        "48 89 44 24 ?? "
        "48 89 CF";

    // Source enum: Japanese=0, English=1, French=2, German=3, ...
    std::atomic<int> g_currentLanguage{1};

    void __fastcall SetLanguageHook(
        void* self,
        int locale
    )
    {
        g_currentLanguage.store(locale);

        char line[128]{};
        std::snprintf(
            line,
            sizeof(line),
            "[Language] Locale alterado para id=%d (%s)",
            locale,
            locale == 0 ? "jp" : "en"
        );

        fftml::LogInfo(line);

        g_originalSetLanguage(self, locale);
    }
}

namespace fftml
{
    bool InstallLanguageHooks()
    {
        auto address =
            FindPatternInMainModule(
                ParsePattern(kSetLanguagePattern)
            );

        if (!address)
        {
            address =
                FindPatternInMainModule(
                    ParsePattern(
                        kSetLanguagePatternDenuvo
                    )
                );
        }

        if (!address)
        {
            LogWarning(
                "[Language] SetLanguage signature nao encontrada; "
                "FFTPack usara locale en por padrao."
            );
            return false;
        }

        g_target = reinterpret_cast<void*>(address);

        if (MH_CreateHook(
                g_target,
                reinterpret_cast<void*>(&SetLanguageHook),
                reinterpret_cast<void**>(&g_originalSetLanguage)
            ) != MH_OK)
        {
            LogWarning(
                "[Language] Falha criando hook de SetLanguage."
            );
            g_target = nullptr;
            return false;
        }

        if (MH_EnableHook(g_target) != MH_OK)
        {
            MH_RemoveHook(g_target);
            g_target = nullptr;

            LogWarning(
                "[Language] Falha ativando hook de SetLanguage."
            );
            return false;
        }

        char line[128]{};
        std::snprintf(
            line,
            sizeof(line),
            "[Language] SetLanguage hook = 0x%llX",
            static_cast<unsigned long long>(address)
        );
        LogInfo(line);

        return true;
    }

    void RemoveLanguageHooks()
    {
        if (!g_target)
            return;

        MH_DisableHook(g_target);
        MH_RemoveHook(g_target);
        g_target = nullptr;
        g_originalSetLanguage = nullptr;
    }

    int CurrentLanguageId()
    {
        return g_currentLanguage.load();
    }

    std::string_view CurrentFFTPackLocale()
    {
        // The reference FFTPack override differentiates only JP vs EN.
        // All non-Japanese languages use the EN-side registry.
        return g_currentLanguage.load() == 0 ? "jp" : "en";
    }
}
