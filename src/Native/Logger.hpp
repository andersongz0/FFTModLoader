#pragma once
#include <string_view>

namespace fftml
{
    bool InitializeLogger();
    void ShutdownLogger();

    void Log(std::string_view text);
    void LogInfo(std::string_view text);
    void LogWarning(std::string_view text);
    void LogError(std::string_view text);
}
