#include "SignatureScanner.hpp"

#include <windows.h>
#include <psapi.h>

#include <charconv>
#include <string>

namespace fftml
{
    std::vector<PatternByte> ParsePattern(std::string_view pattern)
    {
        std::vector<PatternByte> result;

        std::size_t i = 0;

        while (i < pattern.size())
        {
            while (i < pattern.size() && pattern[i] == ' ')
                ++i;

            if (i >= pattern.size())
                break;

            if (pattern[i] == '?')
            {
                ++i;
                if (i < pattern.size() && pattern[i] == '?')
                    ++i;

                result.push_back({0, true});
                continue;
            }

            if (i + 2 > pattern.size())
                break;

            unsigned int value = 0;

            const char* begin = pattern.data() + i;
            const char* end = begin + 2;

            auto [ptr, ec] =
                std::from_chars(begin, end, value, 16);

            if (ec != std::errc{})
                break;

            result.push_back({
                static_cast<std::uint8_t>(value),
                false
            });

            i += 2;
        }

        return result;
    }

    std::uintptr_t FindPatternInMainModule(
        const std::vector<PatternByte>& pattern
    )
    {
        if (pattern.empty())
            return 0;

        HMODULE module = GetModuleHandleW(nullptr);
        if (!module)
            return 0;

        MODULEINFO info{};

        if (!GetModuleInformation(
                GetCurrentProcess(),
                module,
                &info,
                sizeof(info)))
        {
            return 0;
        }

        auto* base =
            static_cast<std::uint8_t*>(info.lpBaseOfDll);

        const std::size_t size =
            static_cast<std::size_t>(info.SizeOfImage);

        if (size < pattern.size())
            return 0;

        for (std::size_t i = 0;
             i <= size - pattern.size();
             ++i)
        {
            bool match = true;

            for (std::size_t j = 0;
                 j < pattern.size();
                 ++j)
            {
                if (!pattern[j].wildcard &&
                    base[i + j] != pattern[j].value)
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return reinterpret_cast<std::uintptr_t>(base + i);
        }

        return 0;
    }
}
