#pragma once

#include <string_view>

namespace fftml
{
    bool InstallLanguageHooks();
    void RemoveLanguageHooks();

    int CurrentLanguageId();
    std::string_view CurrentFFTPackLocale();
}
