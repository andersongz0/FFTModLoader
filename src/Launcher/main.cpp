#include <windows.h>
#include <tlhelp32.h>

#include <nlohmann/json.hpp>

#include <filesystem>
#include <cctype>
#include <fstream>
#include <functional>
#include <iostream>
#include <map>
#include <regex>
#include <set>
#include <sstream>
#include <string>
#include <vector>
#include "RuntimeLogRelay.h"
#include "ModLoadOrder.h"

namespace fs = std::filesystem;
using json = nlohmann::json;

namespace
{
    struct LauncherConfig
    {
        bool keepLauncherOpen = true;
        bool waitForGameExit = true;
    };

    std::string ReadAllText(const fs::path& path)
    {
        std::ifstream input(path, std::ios::binary);

        if (!input)
            return {};

        std::ostringstream out;
        out << input.rdbuf();
        return out.str();
    }

    bool ReadBool(
        const std::string& json,
        const char* key,
        bool fallback
    )
    {
        try
        {
            const std::regex rx(
                std::string("\"") + key +
                "\"\\s*:\\s*(true|false)",
                std::regex_constants::icase
            );

            std::smatch match;

            if (!std::regex_search(json, match, rx))
                return fallback;

            std::string value = match[1].str();

            for (char& c : value)
                c = static_cast<char>(
                    std::tolower(
                        static_cast<unsigned char>(c)
                    )
                );

            return value == "true";
        }
        catch (...)
        {
            return fallback;
        }
    }

    LauncherConfig LoadLauncherConfig(
        const fs::path& baseDir
    )
    {
        LauncherConfig config;

        const std::string json =
            ReadAllText(
                baseDir / L"FFTModLoader.config.json"
            );

        if (json.empty())
            return config;

        config.keepLauncherOpen =
            ReadBool(
                json,
                "KeepLauncherOpen",
                config.keepLauncherOpen
            );

        config.waitForGameExit =
            ReadBool(
                json,
                "WaitForGameExit",
                config.waitForGameExit
            );

        return config;
    }

    std::wstring GetLastErrorMessage(DWORD error)
    {
        LPWSTR buffer = nullptr;

        const DWORD flags =
            FORMAT_MESSAGE_ALLOCATE_BUFFER |
            FORMAT_MESSAGE_FROM_SYSTEM |
            FORMAT_MESSAGE_IGNORE_INSERTS;

        FormatMessageW(
            flags,
            nullptr,
            error,
            0,
            reinterpret_cast<LPWSTR>(&buffer),
            0,
            nullptr
        );

        std::wstring message =
            buffer ? buffer : L"Unknown error";

        if (buffer)
            LocalFree(buffer);

        return message;
    }

    uintptr_t FindRemoteModuleBase(
        DWORD pid,
        const wchar_t* moduleName
    )
    {
        for (int attempt = 0; attempt < 8; ++attempt)
        {
            HANDLE snapshot =
                CreateToolhelp32Snapshot(
                    TH32CS_SNAPMODULE |
                    TH32CS_SNAPMODULE32,
                    pid
                );

            if (snapshot == INVALID_HANDLE_VALUE)
            {
                if (GetLastError() == ERROR_BAD_LENGTH)
                {
                    Sleep(10);
                    continue;
                }

                return 0;
            }

            MODULEENTRY32W entry{};
            entry.dwSize = sizeof(entry);

            uintptr_t base = 0;

            if (Module32FirstW(snapshot, &entry))
            {
                do
                {
                    if (_wcsicmp(
                            entry.szModule,
                            moduleName) == 0)
                    {
                        base =
                            reinterpret_cast<uintptr_t>(
                                entry.modBaseAddr
                            );
                        break;
                    }
                }
                while (Module32NextW(snapshot, &entry));
            }

            CloseHandle(snapshot);

            if (base)
                return base;

            Sleep(10);
        }

        return 0;
    }

    bool IsProcessAlive(HANDLE process)
    {
        DWORD exitCode = 0;

        if (!GetExitCodeProcess(process, &exitCode))
            return false;

        return exitCode == STILL_ACTIVE;
    }

    bool IsProcessRunning(const wchar_t* executableName)
    {
        HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE)
            return false;

        PROCESSENTRY32W entry{};
        entry.dwSize = sizeof(entry);
        bool found = false;

        if (Process32FirstW(snapshot, &entry))
        {
            do
            {
                if (_wcsicmp(entry.szExeFile, executableName) == 0)
                {
                    found = true;
                    break;
                }
            }
            while (Process32NextW(snapshot, &entry));
        }

        CloseHandle(snapshot);
        return found;
    }

    bool EnsureSteamRunning()
    {
        if (IsProcessRunning(L"steam.exe"))
            return true;

        wchar_t programFilesX86[32768]{};
        const DWORD length = GetEnvironmentVariableW(
            L"ProgramFiles(x86)",
            programFilesX86,
            static_cast<DWORD>(std::size(programFilesX86))
        );

        fs::path steamExe = length > 0
            ? fs::path(programFilesX86) / L"Steam" / L"steam.exe"
            : fs::path(L"C:\\Program Files (x86)\\Steam\\steam.exe");

        if (!fs::exists(steamExe))
            return false;

        std::wstring commandLine = L"\"" + steamExe.wstring() + L"\" -silent";
        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        PROCESS_INFORMATION processInfo{};

        std::wcout << L"[FFTModLoader] Iniciando o Steam...\n";

        if (!CreateProcessW(
                steamExe.c_str(),
                commandLine.data(),
                nullptr,
                nullptr,
                FALSE,
                0,
                nullptr,
                steamExe.parent_path().c_str(),
                &startup,
                &processInfo))
        {
            return false;
        }

        CloseHandle(processInfo.hThread);
        CloseHandle(processInfo.hProcess);

        for (int attempt = 0; attempt < 300; ++attempt)
        {
            if (IsProcessRunning(L"steam.exe"))
            {
                Sleep(2000);
                return true;
            }

            Sleep(100);
        }

        return false;
    }

    std::string ToUtf8(const std::wstring& value)
    {
        if (value.empty())
            return {};

        const int bytes = WideCharToMultiByte(
            CP_UTF8, 0, value.c_str(), -1,
            nullptr, 0, nullptr, nullptr);

        if (bytes <= 1)
            return {};

        std::string result(static_cast<size_t>(bytes), '\0');
        WideCharToMultiByte(
            CP_UTF8, 0, value.c_str(), -1,
            result.data(), bytes, nullptr, nullptr);
        result.pop_back();
        return result;
    }

    bool InjectSuspendedProcess(
        DWORD pid,
        const fs::path& dllPath,
        const fs::path& syringePath
    )
    {
        using SyringeForSuspendedProcess = void* (__cdecl*)(uint32_t);
        using SyringeInject = bool (__cdecl*)(void*, const uint8_t*);
        using SyringeFree = void (__cdecl*)(void*);

        std::wcout
            << L"[FFTModLoader] Injetando o runtime no processo suspenso...\n";

        HMODULE syringeModule = LoadLibraryW(syringePath.c_str());
        if (!syringeModule)
        {
            std::wcerr
                << L"[ERRO] Nao foi possivel carregar dll_syringe.dll: "
                << GetLastErrorMessage(GetLastError()) << L"\n";
            return false;
        }

        const auto forSuspended = reinterpret_cast<SyringeForSuspendedProcess>(
            GetProcAddress(syringeModule, "syringe_for_suspended_process"));
        const auto inject = reinterpret_cast<SyringeInject>(
            GetProcAddress(syringeModule, "syringe_inject"));
        const auto freeSyringe = reinterpret_cast<SyringeFree>(
            GetProcAddress(syringeModule, "syringe_free"));

        if (!forSuspended || !inject || !freeSyringe)
        {
            std::wcerr
                << L"[ERRO] dll_syringe.dll nao possui a API esperada.\n";
            FreeLibrary(syringeModule);
            return false;
        }

        void* syringe = forSuspended(static_cast<uint32_t>(pid));
        if (!syringe)
        {
            std::wcerr
                << L"[ERRO] O injetor nao reconheceu o processo suspenso.\n";
            FreeLibrary(syringeModule);
            return false;
        }

        const std::string bootstrapperUtf8 =
            ToUtf8(fs::absolute(dllPath).wstring());
        const bool success = !bootstrapperUtf8.empty() &&
            inject(
                syringe,
                reinterpret_cast<const uint8_t*>(bootstrapperUtf8.c_str()));

        freeSyringe(syringe);
        FreeLibrary(syringeModule);

        if (!success)
            std::wcerr << L"[ERRO] dll_syringe recusou a injecao.\n";

        return success;
    }

    bool WaitForRuntimeInitialization(
        HANDLE process,
        const fs::path& bootstrapperLog
    )
    {
        constexpr int timeoutMilliseconds = 30000;
        constexpr int pollMilliseconds = 25;

        std::wcout
            << L"[FFTModLoader] Aguardando os mods terminarem a inicializacao...\n";

        for (int elapsed = 0;
             elapsed < timeoutMilliseconds;
             elapsed += pollMilliseconds)
        {
            if (!IsProcessAlive(process))
                return false;

            const std::string log = ReadAllText(bootstrapperLog);
            if (log.find("Reloaded managed entry point completed") !=
                std::string::npos)
            {
                std::wcout
                    << L"[FFTModLoader] Runtime e mods inicializados.\n";
                return true;
            }

            Sleep(pollMilliseconds);
        }

        std::wcerr
            << L"[ERRO] O runtime nao concluiu a inicializacao em 30 segundos.\n";
        return false;
    }

    bool WriteJson(const fs::path& path, const json& value)
    {
        std::error_code error;
        fs::create_directories(path.parent_path(), error);
        if (error)
            return false;

        std::ofstream output(path, std::ios::binary | std::ios::trunc);
        if (!output)
            return false;

        output << value.dump(2);
        return output.good();
    }

    bool LinkOrCopyFile(
        const fs::path& source,
        const fs::path& destination,
        std::wstring& errorMessage)
    {
        std::error_code error;
        fs::create_directories(destination.parent_path(), error);
        if (error)
        {
            errorMessage = L"Nao foi possivel criar a pasta resolvida de mods: " +
                destination.parent_path().wstring();
            return false;
        }

        fs::create_hard_link(source, destination, error);
        if (!error)
            return true;

        error.clear();
        fs::copy_file(
            source,
            destination,
            fs::copy_options::overwrite_existing,
            error);
        if (!error)
            return true;

        errorMessage = L"Nao foi possivel preparar o arquivo do mod: " +
            source.wstring();
        return false;
    }

    bool CopyResolvedMod(
        const fs::path& manifestPath,
        const fs::path& resolvedRoot,
        std::set<std::string>& resolvedIds,
        std::wstring& errorMessage)
    {
        json manifest;
        try
        {
            std::ifstream input(manifestPath, std::ios::binary);
            manifest = json::parse(input);
        }
        catch (...)
        {
            errorMessage = L"ModConfig.json invalido: " + manifestPath.wstring();
            return false;
        }

        const std::string id = fftloader::CanonicalModId(manifest.value("ModId", ""));
        manifest["ModId"] = id;
        if (id == fftloader::ChemistId) manifest["ModName"] = "Reworked Chemist";
        for (const char* key : {"ModDependencies", "OptionalDependencies"})
            if (manifest.contains(key) && manifest[key].is_array())
                for (auto& dependency : manifest[key])
                    if (dependency.is_string())
                        dependency = fftloader::CanonicalModId(dependency.get<std::string>());
        // Upgrade published consumers in the generated view only. Original
        // third-party manifests and old user packages remain untouched.
        if (id == "ffttic.jobs.genericknights")
        {
            auto& dependencies = manifest["ModDependencies"];
            if (dependencies.is_null()) dependencies = json::array();
            if (!dependencies.is_array()) return false;
            if (std::find(dependencies.begin(), dependencies.end(), "fftmodloader.jobexpansion") == dependencies.end())
                dependencies.push_back("fftmodloader.jobexpansion");
        }
        if (id.empty())
            return true;

        if (!resolvedIds.insert(id).second)
        {
            errorMessage = L"ModId duplicado: " + std::wstring(id.begin(), id.end());
            return false;
        }

        std::wstring safeName(id.begin(), id.end());
        for (wchar_t& character : safeName)
        {
            if (character == L'<' || character == L'>' || character == L':' ||
                character == L'"' || character == L'/' || character == L'\\' ||
                character == L'|' || character == L'?' || character == L'*')
            {
                character = L'_';
            }
        }

        const fs::path sourceRoot = manifestPath.parent_path();
        const fs::path destinationRoot = resolvedRoot / safeName;
        std::error_code error;
        fs::create_directories(destinationRoot, error);
        if (error)
        {
            errorMessage = L"Nao foi possivel criar a copia resolvida de " +
                std::wstring(id.begin(), id.end());
            return false;
        }

        for (fs::recursive_directory_iterator it(
                 sourceRoot,
                 fs::directory_options::skip_permission_denied,
                 error), end;
             it != end;
             it.increment(error))
        {
            if (error)
            {
                error.clear();
                continue;
            }

            const fs::path relative = fs::relative(it->path(), sourceRoot, error);
            if (error)
            {
                errorMessage = L"Falha ao resolver o caminho do mod " +
                    std::wstring(id.begin(), id.end());
                return false;
            }

            const fs::path destination = destinationRoot / relative;
            if (it->is_directory())
            {
                fs::create_directories(destination, error);
                if (error)
                {
                    errorMessage = L"Falha ao preparar a pasta do mod " +
                        std::wstring(id.begin(), id.end());
                    return false;
                }
                continue;
            }

            if (!it->is_regular_file())
                continue;

            if (_wcsicmp(it->path().filename().c_str(), L"ModConfig.json") == 0 ||
                (id == "ffttic.jobs.genericjobs" &&
                 _wcsicmp(it->path().filename().c_str(), L"FFTModLoader.jobs.json") == 0))
                continue;

            if (!LinkOrCopyFile(it->path(), destination, errorMessage))
                return false;
        }

        // GenericJobs is built-in content: its Dark Knight and Onion Knight
        // resources always load. The loader owns their menu hooks, so suppress
        // only the original DLL in the resolved view, never its gameplay data.
        if (id == "ffttic.jobs.genericjobs")
        {
            manifest["ModDll"] = "";
            manifest["ModR2RManagedDll32"] = "";
            manifest["ModR2RManagedDll64"] = "";
            manifest["ModNativeDll32"] = "";
            manifest["ModNativeDll64"] = "";
            if (!WriteJson(destinationRoot / L"FFTModLoader.jobs.json", {
                    {"SchemaVersion", 1}, {"JobIds", {0xA0, 0xA1}},
                    {"RequiresGenericJobsContent", true}}))
                return false;
        }

        // Backward-compatible registration for already published Knights
        // packages. Future consumers can ship this manifest themselves.
        if (id == "ffttic.jobs.genericknights" &&
            !fs::exists(destinationRoot / L"FFTModLoader.jobs.json"))
        {
            if (!WriteJson(destinationRoot / L"FFTModLoader.jobs.json", {
                    {"SchemaVersion", 1}, {"JobIds", {0xA2, 0xA3}},
                    {"RequiresGenericJobsContent", true}}))
                return false;
        }
        return WriteJson(destinationRoot / L"ModConfig.json", manifest);
    }

    bool BuildResolvedMods(
        const fs::path& sourceMods,
        const fs::path& internalMods,
        const fs::path& resolvedMods,
        std::wstring& errorMessage)
    {
        std::error_code error;
        fs::remove_all(resolvedMods, error);
        if (error)
        {
            errorMessage = L"Nao foi possivel limpar FFTModLoader.Runtime\\ResolvedMods.";
            return false;
        }

        fs::create_directories(resolvedMods, error);
        if (error)
        {
            errorMessage = L"Nao foi possivel criar FFTModLoader.Runtime\\ResolvedMods.";
            return false;
        }

        std::set<std::string> resolvedIds;
        std::vector<fs::path> manifests;
        for (const fs::path& root : {sourceMods, internalMods})
        {
            if (!fs::exists(root))
                continue;

            for (fs::recursive_directory_iterator it(
                     root,
                     fs::directory_options::skip_permission_denied,
                     error), end;
                 it != end;
                 it.increment(error))
            {
                if (error)
                {
                    error.clear();
                    continue;
                }

                if (!it->is_regular_file() ||
                    _wcsicmp(it->path().filename().c_str(), L"ModConfig.json") != 0)
                {
                    continue;
                }

                manifests.push_back(it->path());
                try
                {
                    std::ifstream input(it->path(), std::ios::binary);
                    const json config = json::parse(input);
                    const auto jobsPath = it->path().parent_path() / L"FFTModLoader.jobs.json";
                    if (fs::exists(jobsPath))
                    {
                        std::ifstream jobsInput(jobsPath, std::ios::binary);
                        const json jobs = json::parse(jobsInput);
                        if (jobs.at("SchemaVersion") != 1 || !jobs.at("JobIds").is_array())
                            throw std::runtime_error("Unsupported job registration");
                        for (const auto& job : jobs.at("JobIds"))
                            if (!job.is_number_integer() || job.get<int64_t>() < 0xA0 || job.get<int64_t>() > 0xFF)
                                throw std::runtime_error("Invalid registered job ID");
                    }
                }
                catch (...)
                {
                    errorMessage = L"Registro de mod/classes invalido: " + it->path().wstring();
                    return false;
                }
            }
        }
        for (const auto& path : manifests)
            if (!CopyResolvedMod(path, resolvedMods, resolvedIds, errorMessage))
                return false;

        if (!resolvedIds.contains("fftmodloader.jobexpansion"))
        {
            errorMessage = L"Runtime interno de expansao de Jobs nao foi encontrado.";
            return false;
        }

        if (!resolvedIds.contains("fftmodloader.contentexpansion"))
        {
            errorMessage = L"Runtime interno de expansao de conteudo nao foi encontrado. Reinstale o pacote completo do FFTModLoader.";
            return false;
        }
        return true;
    }

    bool DiscoverAndSortMods(
        const fs::path& modsDirectory,
        std::vector<std::string>& sortedMods,
        std::wstring& errorMessage)
    {
        std::map<std::string, fftloader::ModInfo> mods;
        std::error_code error;

        if (!fs::exists(modsDirectory))
            fs::create_directories(modsDirectory, error);

        if (error)
        {
            errorMessage = L"Nao foi possivel criar a pasta Mods.";
            return false;
        }

        for (fs::recursive_directory_iterator it(
                 modsDirectory,
                 fs::directory_options::skip_permission_denied,
                 error), end;
             it != end;
             it.increment(error))
        {
            if (error)
            {
                error.clear();
                continue;
            }

            if (!it->is_regular_file() ||
                _wcsicmp(it->path().filename().c_str(), L"ModConfig.json") != 0)
            {
                continue;
            }

            try
            {
                std::ifstream input(it->path(), std::ios::binary);
                const json manifest = json::parse(input);
                const std::string id = manifest.value("ModId", "");

                if (id.empty())
                    continue;

                fftloader::ModInfo info;
                info.id = id;
                info.library = manifest.value("IsLibrary", false);

                if (manifest.contains("ModDependencies") &&
                    manifest["ModDependencies"].is_array())
                {
                    for (const auto& dependency : manifest["ModDependencies"])
                    {
                        if (dependency.is_string())
                            info.dependencies.push_back(dependency.get<std::string>());
                    }
                }

                if (!mods.emplace(id, std::move(info)).second)
                    throw std::runtime_error("Duplicate mod ID");
            }
            catch (const std::exception& ex)
            {
                errorMessage = L"ModConfig.json invalido: " + it->path().wstring();
                return false;
            }
        }

        std::string errorText;
        if (!fftloader::SortMods(mods, sortedMods, errorText))
        {
            errorMessage.assign(errorText.begin(), errorText.end());
            return false;
        }

        return true;
    }

    bool PrepareReloadedRuntime(
        const fs::path& baseDir,
        const fs::path& gameExe,
        fs::path& bootstrapper,
        fs::path& loaderConfig,
        std::vector<std::string>& sortedMods,
        std::wstring& errorMessage)
    {
        const fs::path runtimeRoot = baseDir / L"FFTModLoader.Runtime";
        const fs::path loaderRoot = runtimeRoot / L"Loader" / L"X64";
        const fs::path appsRoot = runtimeRoot / L"Apps";
        const fs::path userRoot = runtimeRoot / L"User";
        const fs::path sourceModsRoot = baseDir / L"Mods";
        const fs::path internalModsRoot = runtimeRoot / L"InternalMods";
        const fs::path modsRoot = runtimeRoot / L"ResolvedMods";

        bootstrapper = loaderRoot / L"Bootstrapper" /
            L"Reloaded.Mod.Loader.Bootstrapper.dll";
        const fs::path managedLoader = loaderRoot / L"Reloaded.Mod.Loader.dll";
        loaderConfig = runtimeRoot / L"ReloadedII.json";

        if (!fs::exists(bootstrapper) || !fs::exists(managedLoader))
        {
            errorMessage = L"Runtime integrado incompleto em FFTModLoader.Runtime.";
            return false;
        }

        if (!BuildResolvedMods(
                sourceModsRoot,
                internalModsRoot,
                modsRoot,
                errorMessage))
        {
            return false;
        }

        if (!DiscoverAndSortMods(modsRoot, sortedMods, errorMessage))
            return false;

        if (sortedMods.empty())
        {
            errorMessage = L"Nenhum ModConfig.json foi encontrado dentro de Mods.";
            return false;
        }

        json loader = {
            {"LoaderPath32", ""},
            {"LoaderPath64", managedLoader.string()},
            {"LauncherPath", (baseDir / L"FFTModLoader.exe").string()},
            {"Bootstrapper32Path", ""},
            {"Bootstrapper64Path", bootstrapper.string()},
            {"ApplicationConfigDirectory", appsRoot.string()},
            {"ModUserConfigDirectory", (userRoot / L"Mods").string()},
            {"MiscConfigDirectory", (userRoot / L"Misc").string()},
            {"PluginConfigDirectory", (runtimeRoot / L"Plugins").string()},
            {"ModConfigDirectory", modsRoot.string()},
            {"EnabledPlugins", json::array()},
            {"LanguageFile", ""},
            {"ThemeFile", ""},
            {"FirstLaunch", false},
            {"ShowConsole", false},
            {"LogFileCompressTimeHours", 6},
            {"LogFileDeleteHours", 336},
            {"CrashDumpDeleteHours", 24},
            {"NuGetFeeds", json::array()},
            {"ForceModPrereleases", false},
            {"ReloadedProcessListRefreshInterval", 1000},
            {"LoaderSetupTimeout", 30000},
            {"LoaderSetupSleeptime", 32},
            {"ProcessRefreshInterval", 200},
            {"SkipWineLaunchWarning", false},
            {"DisableDInput", false}
        };

        json app = {
            {"AppId", "fft_enhanced.exe"},
            {"AppName", "FINAL FANTASY TACTICS - The Ivalice Chronicles"},
            {"AppLocation", gameExe.string()},
            {"AppArguments", ""},
            {"AppIcon", ""},
            {"AutoInject", false},
            {"EnabledMods", sortedMods},
            {"WorkingDirectory", baseDir.string()},
            {"PluginData", json::object()},
            {"SortedMods", sortedMods},
            {"PreserveDisabledModOrder", true},
            {"DontInject", false},
            {"IsMsStore", false}
        };

        if (!WriteJson(loaderConfig, loader) ||
            !WriteJson(appsRoot / L"fft_enhanced.exe" / L"AppConfig.json", app))
        {
            errorMessage = L"Nao foi possivel gravar a configuracao local do runtime.";
            return false;
        }

        return true;
    }
}


static bool RunModPacker(
    const fs::path& baseDir
)
{
    const fs::path packer =
        baseDir /
        L"PackBuilder" /
        L"FFTModPacker.exe";

    if (!fs::exists(packer))
    {
        std::wcerr
            << L"[ERRO] FFTModPacker.exe nao encontrado; "
               L"nao e possivel validar o plano dos mods.\n";
        return false;
    }

    std::wstring commandLine =
        L"\"" +
        packer.wstring() +
        L"\" \"" +
        baseDir.wstring() +
        L"\"";

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);

    PROCESS_INFORMATION processInfo{};

    std::wcout
        << L"[FFTModLoader] Gerando packs dos mods...\n";

    if (!CreateProcessW(
            packer.c_str(),
            commandLine.data(),
            nullptr,
            nullptr,
            FALSE,
            0,
            nullptr,
            baseDir.c_str(),
            &startup,
            &processInfo))
    {
        std::wcerr
            << L"[ERRO] Nao foi possivel iniciar FFTModPacker.exe.\n";

        return false;
    }

    CloseHandle(
        processInfo.hThread
    );

    WaitForSingleObject(
        processInfo.hProcess,
        INFINITE
    );

    DWORD exitCode = 1;

    GetExitCodeProcess(
        processInfo.hProcess,
        &exitCode
    );

    CloseHandle(
        processInfo.hProcess
    );

    if (exitCode != 0)
    {
        std::wcerr
            << L"[ERRO] FFTModPacker falhou. ExitCode="
            << exitCode
            << L"\n";

        return false;
    }

    std::wcout
        << L"[FFTModLoader] Packs preparados.\n";

    return true;
}

int wmain(int argc, wchar_t** argv)
{
    std::vector<wchar_t> modulePath(32768);

    const DWORD len =
        GetModuleFileNameW(
            nullptr,
            modulePath.data(),
            static_cast<DWORD>(
                modulePath.size()
            )
        );

    if (len == 0)
        return 1;

    const fs::path baseDir =
        fs::path(
            std::wstring(
                modulePath.data(),
                len
            )
        ).parent_path();

    const LauncherConfig config =
        LoadLauncherConfig(baseDir);

    const fs::path gameExe =
        baseDir / L"FFT_enhanced.exe";

    std::wcout
        << L"FFTModLoader 0.11.7-rc.6 - ZeroDS - Runtime independente integrado\n"
        << L"Projeto: https://github.com/andersongz0/FFTModLoader\n";

    std::wcout
        << L"Diretorio: "
        << baseDir.wstring()
        << L"\n\n";

    if (!fs::exists(gameExe))
    {
        std::wcerr
            << L"[ERRO] FFT_enhanced.exe nao encontrado.\n";
        return 2;
    }

    fs::path bootstrapper;
    fs::path loaderConfig;
    std::vector<std::string> sortedMods;
    std::wstring preparationError;

    if (!PrepareReloadedRuntime(
            baseDir,
            gameExe,
            bootstrapper,
            loaderConfig,
            sortedMods,
            preparationError))
    {
        std::wcerr
            << L"[ERRO] " << preparationError << L"\n";
        return 3;
    }

    std::wcout
        << L"[FFTModLoader] " << sortedMods.size()
        << L" componentes na ordem de carregamento (dependencias antes dos mods):\n";

    for (const std::string& id : sortedMods)
    {
        std::ifstream input(baseDir / L"FFTModLoader.Runtime" / L"ResolvedMods" / id / "ModConfig.json");
        const json manifest = json::parse(input);
        const std::string name = manifest.value("ModName", id);
        const int length = MultiByteToWideChar(CP_UTF8, 0, name.data(), static_cast<int>(name.size()), nullptr, 0);
        std::wstring wideName(length, L'\0');
        if (length > 0) MultiByteToWideChar(CP_UTF8, 0, name.data(), static_cast<int>(name.size()), wideName.data(), length);
        else wideName.assign(id.begin(), id.end());
        std::wcout << L"  - " << wideName << L"\n";
    }

    if (!SetEnvironmentVariableW(
            L"FFTMODLOADER_RELOADED_CONFIG",
            loaderConfig.c_str()))
    {
        std::wcerr
            << L"[ERRO] Nao foi possivel ativar a configuracao local.\n";
        return 4;
    }

    const fs::path bundledDotNet =
        baseDir / L"FFTModLoader.Runtime" / L"DotNet";

    if (!fs::exists(
            bundledDotNet / L"host" / L"fxr") ||
        !fs::exists(
            bundledDotNet / L"shared" / L"Microsoft.NETCore.App") ||
        !fs::exists(
            bundledDotNet / L"shared" / L"Microsoft.WindowsDesktop.App"))
    {
        std::wcerr
            << L"[ERRO] O runtime .NET integrado esta ausente ou incompleto.\n";
        return 5;
    }

    if (!SetEnvironmentVariableW(
            L"DOTNET_ROOT",
            bundledDotNet.c_str()) ||
        !SetEnvironmentVariableW(
            L"DOTNET_ROOT_X64",
            bundledDotNet.c_str()))
    {
        std::wcerr
            << L"[ERRO] Nao foi possivel ativar o runtime .NET integrado.\n";
        return 5;
    }

    if (argc > 1 && _wcsicmp(argv[1], L"--prepare-only") == 0)
    {
        std::wcout
            << L"[FFTModLoader] Configuracao local validada sem iniciar o jogo.\n";
        return 0;
    }

    if (!EnsureSteamRunning())
    {
        std::wcerr
            << L"[ERRO] O Steam nao esta aberto e nao foi possivel inicia-lo.\n"
            << L"[ERRO] Abra o Steam, entre na sua conta e tente novamente.\n";
        return 5;
    }

    std::wcout << L"[FFTModLoader] Steam pronto.\n";

    std::wstring commandLine =
        L"\"" + gameExe.wstring() + L"\"";

    const fs::path bootstrapperLog =
        baseDir / L"FFTModLoader.Runtime" / L"Bootstrapper.log";
    {
        std::ofstream clearLog(
            bootstrapperLog,
            std::ios::binary | std::ios::trunc);
    }

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);

    PROCESS_INFORMATION processInfo{};
    const fs::path contentLogs = baseDir / L"FFTModLoader.Runtime" / L"Logs";
    std::error_code contentError;
    fs::create_directories(contentLogs,contentError);
    if (contentError)
    {
        std::wcerr << L"[ERRO] Nao foi possivel preparar a pasta de logs: " << contentLogs << L"\n";
        return 5;
    }
    const fs::path sessionLog = contentLogs / (L"Content-session-" + std::to_wstring(GetCurrentProcessId()) +
        L"-" + std::to_wstring(GetTickCount64()) + L".txt");
    if (!SetEnvironmentVariableW(L"FFTMODLOADER_SESSION_LOG",sessionLog.c_str()))
    {
        std::wcerr << L"[ERRO] Nao foi possivel configurar o log desta sessao.\n";
        return 5;
    }
    std::wcout << L"[FFTModLoader] Log de conteudo desta sessao: " << sessionLog << L"\n";
    RuntimeLogRelay contentLog(contentLogs,sessionLog);
    const auto showContent = [](const std::string& line)
    {
        const int length = MultiByteToWideChar(CP_UTF8,0,line.data(),static_cast<int>(line.size()),nullptr,0);
        if (length <= 0) return;
        std::wstring wide(static_cast<size_t>(length),L'\0');
        MultiByteToWideChar(CP_UTF8,0,line.data(),static_cast<int>(line.size()),wide.data(),length);
        std::wcout << wide << L"\n" << std::flush;
    };

    if (!CreateProcessW(
            gameExe.c_str(),
            commandLine.data(),
            nullptr,
            nullptr,
            FALSE,
            CREATE_SUSPENDED,
            nullptr,
            baseDir.c_str(),
            &startup,
            &processInfo))
    {
        const DWORD error = GetLastError();

        std::wcerr
            << L"[ERRO] CreateProcessW falhou: "
            << GetLastErrorMessage(error)
            << L"\n";

        return 4;
    }

    std::wcout
        << L"[FFTModLoader] FFT_enhanced.exe iniciado.\n"
        << L"[FFTModLoader] PID: "
        << processInfo.dwProcessId
        << L"\n";

    const fs::path syringePath =
        baseDir / L"FFTModLoader.Runtime" / L"Injector" /
        L"dll_syringe.dll";

    const bool injected =
        InjectSuspendedProcess(
            processInfo.dwProcessId,
            bootstrapper,
            syringePath
        );

    if (!injected)
    {
        std::wcerr
            << L"[ERRO] O jogo nao sera iniciado sem o runtime de mods.\n";
        TerminateProcess(processInfo.hProcess, 1);
        CloseHandle(processInfo.hThread);
        CloseHandle(processInfo.hProcess);
        return 6;
    }

    if (!WaitForRuntimeInitialization(
            processInfo.hProcess,
            bootstrapperLog))
    {
        std::wcerr
            << L"[ERRO] O jogo nao sera liberado com os mods incompletos.\n";
        TerminateProcess(processInfo.hProcess, 1);
        CloseHandle(processInfo.hThread);
        CloseHandle(processInfo.hProcess);
        return 7;
    }

    const DWORD resumeResult = ResumeThread(processInfo.hThread);
    std::wcout << L"[Conteudo] Registro = recurso encontrado; Aplicando = alteracao em processamento; "
                  L"Carregado = confirmacao recebida do runtime.\n";
    contentLog.Pump(showContent);

    if (resumeResult == static_cast<DWORD>(-1))
    {
        std::wcerr
            << L"[ERRO] O runtime foi preparado, mas a thread principal "
               L"do jogo nao pode ser retomada: "
            << GetLastErrorMessage(GetLastError())
            << L"\n";
    }
    else
    {
        std::wcout
            << L"[FFTModLoader] Jogo liberado para iniciar.\n";
    }

    std::wcout
        << L"[FFTModLoader] Runtime integrado injetado.\n"
        << L"[FFTModLoader] Inicializacao concluida. O conteudo sera confirmado abaixo conforme utilizado.\n";

    CloseHandle(processInfo.hThread);

    if (config.keepLauncherOpen)
    {
        std::wcout
            << L"\n[FFTModLoader] Console mantido aberto.\n";

        if (config.waitForGameExit)
        {
            std::wcout
                << L"[FFTModLoader] Aguardando o jogo fechar...\n";

            DWORD waitResult;
            do
            {
                contentLog.Pump(showContent);
                waitResult = WaitForSingleObject(processInfo.hProcess,500);
            } while (waitResult == WAIT_TIMEOUT);
            contentLog.Pump(showContent);

            DWORD gameExitCode = 0;

            GetExitCodeProcess(
                processInfo.hProcess,
                &gameExitCode
            );

            std::wcout
                << L"[FFTModLoader] Jogo encerrado. ExitCode="
                << gameExitCode
                << L"\n";
        }

        // A successful normal run follows the game's lifetime and closes with it.
        // Startup failures still return earlier and keep their diagnostics visible.
    }

    CloseHandle(processInfo.hProcess);

    return injected ? 0 : 5;
}
