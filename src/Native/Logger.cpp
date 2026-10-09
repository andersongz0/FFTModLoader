#include "Logger.hpp"

#include <windows.h>

#include <filesystem>
#include <fstream>
#include <iomanip>
#include <mutex>
#include <sstream>
#include <string>

namespace
{
    std::ofstream g_log;
    std::mutex g_mutex;

    std::string Timestamp()
    {
        SYSTEMTIME st{};
        GetLocalTime(&st);

        char buffer[64]{};

        std::snprintf(
            buffer,
            sizeof(buffer),
            "%04u-%02u-%02u %02u:%02u:%02u.%03u",
            st.wYear,
            st.wMonth,
            st.wDay,
            st.wHour,
            st.wMinute,
            st.wSecond,
            st.wMilliseconds
        );

        return buffer;
    }

    void Write(const char* level, std::string_view text)
    {
        std::lock_guard lock(g_mutex);

        if (!g_log.is_open())
            return;

        g_log
            << '[' << Timestamp() << "] "
            << '[' << level << "] "
            << text
            << '\n';

        g_log.flush();
    }
}

namespace fftml
{
    bool InitializeLogger()
    {
        wchar_t pathBuffer[32768]{};

        const DWORD len = GetModuleFileNameW(
            nullptr,
            pathBuffer,
            static_cast<DWORD>(std::size(pathBuffer))
        );

        if (!len)
            return false;

        const std::filesystem::path exePath(
            std::wstring(pathBuffer, len)
        );

        const auto logPath =
            exePath.parent_path() / L"FFTModLoader.log";

        std::lock_guard lock(g_mutex);

        g_log.open(
            logPath,
            std::ios::out | std::ios::trunc
        );

        if (!g_log)
            return false;

        g_log
            << '[' << Timestamp() << "] "
            << "[INFO] "
            << "[FFTModLoader] Native bootstrap initialized.\n";

        g_log.flush();
        return true;
    }

    void ShutdownLogger()
    {
        std::lock_guard lock(g_mutex);

        if (!g_log.is_open())
            return;

        g_log
            << '[' << Timestamp() << "] "
            << "[INFO] "
            << "[FFTModLoader] Shutdown.\n";

        g_log.flush();
        g_log.close();
    }

    void Log(std::string_view text)
    {
        Write("INFO", text);
    }

    void LogInfo(std::string_view text)
    {
        Write("INFO", text);
    }

    void LogWarning(std::string_view text)
    {
        Write("WARN", text);
    }

    void LogError(std::string_view text)
    {
        Write("ERROR", text);
    }
}
