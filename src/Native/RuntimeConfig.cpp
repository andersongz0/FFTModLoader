#include "RuntimeConfig.hpp"
#include "Logger.hpp"

#include <nlohmann/json.hpp>

#include <fstream>
#include <string>

namespace
{
    fftml::RuntimeConfig g_config;

    nlohmann::json ReadJson(
        const std::filesystem::path& path
    )
    {
        std::ifstream input(path);

        if (!input)
            return {};

        try
        {
            nlohmann::json json;
            input >> json;
            return json;
        }
        catch (...)
        {
            return {};
        }
    }
}

namespace fftml
{
    const RuntimeConfig& GetRuntimeConfig()
    {
        return g_config;
    }

    void LoadRuntimeConfig(
        const std::filesystem::path& gameDirectory
    )
    {
        const auto path =
            gameDirectory / L"FFTModLoader.config.json";

        const auto json = ReadJson(path);

        if (json.empty())
        {
            LogWarning(
                "[Config] FFTModLoader.config.json ausente/invalido; "
                "usando valores padrao."
            );
            return;
        }

        if (json.contains("LogAllResourcePaths"))
            g_config.logAllResourcePaths =
                json.value(
                    "LogAllResourcePaths",
                    g_config.logAllResourcePaths
                );

        // Backward compatibility with 0.2.x.
        if (json.contains("EnableContentOverrides"))
        {
            g_config.enableContentOverrides =
                json.value(
                    "EnableContentOverrides",
                    g_config.enableContentOverrides
                );
        }
        else if (json.contains("EnableLooseContentOverrides"))
        {
            g_config.enableContentOverrides =
                json.value(
                    "EnableLooseContentOverrides",
                    g_config.enableContentOverrides
                );
        }

        g_config.enableExperimentalLargerOverrides =
            json.value(
                "EnableExperimentalLargerOverrides",
                g_config.enableExperimentalLargerOverrides
            );

        g_config.enableExperimentalJobMenuExpansion =
            json.value(
                "EnableExperimentalJobMenuExpansion",
                g_config.enableExperimentalJobMenuExpansion
            );

        g_config.dumpJobTextResources =
            json.value(
                "DumpJobTextResources",
                g_config.dumpJobTextResources
            );

        LogInfo(
            std::string("[Config] LogAllResourcePaths=") +
            (g_config.logAllResourcePaths ? "true" : "false")
        );

        LogInfo(
            std::string("[Config] EnableContentOverrides=") +
            (g_config.enableContentOverrides ? "true" : "false")
        );

        LogInfo(
            std::string("[Config] EnableExperimentalLargerOverrides=") +
            (g_config.enableExperimentalLargerOverrides ? "true" : "false")
        );

        LogInfo(
            std::string("[Config] EnableExperimentalJobMenuExpansion=") +
            (g_config.enableExperimentalJobMenuExpansion ? "true" : "false")
        );

        LogInfo(
            std::string("[Config] DumpJobTextResources=") +
            (g_config.dumpJobTextResources ? "true" : "false")
        );
    }
}
