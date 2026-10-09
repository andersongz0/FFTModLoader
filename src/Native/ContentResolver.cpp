#include "ContentResolver.hpp"

#include "Logger.hpp"

#include <nlohmann/json.hpp>

#include <algorithm>
#include <cctype>
#include <fstream>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <unordered_map>
#include <unordered_set>

namespace fs = std::filesystem;

namespace
{
    std::vector<fftml::ModManifest> g_mods;

    std::unordered_map<
        std::string,
        fftml::ContentPlan
    > g_plans;

    std::mutex g_cacheMutex;

    std::unordered_map<
        std::string,
        std::shared_ptr<const std::vector<std::uint8_t>>
    > g_fileCache;

    fs::path g_gameDirectory;

    std::unordered_set<std::string> g_generatedPackTargets;

    std::string U8(const fs::path& path)
    {
        const auto s = path.generic_u8string();

        return std::string(
            reinterpret_cast<const char*>(s.data()),
            s.size()
        );
    }

    nlohmann::json ReadJson(
        const fs::path& path
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
        catch (const std::exception& ex)
        {
            fftml::LogError(
                std::string("[Content] JSON invalido: ") +
                U8(path) +
                " | " +
                ex.what()
            );
            return {};
        }
    }

    bool SafeRelativePath(
        const fs::path& path
    )
    {
        if (path.empty() || path.is_absolute())
            return false;

        for (const auto& part : path)
        {
            if (part == L"..")
                return false;
        }

        return true;
    }

    std::vector<std::uint8_t>
    ParseHexBytes(const std::string& text)
    {
        std::vector<std::uint8_t> result;

        std::string hex;
        hex.reserve(text.size());

        for (char c : text)
        {
            if (std::isxdigit(
                    static_cast<unsigned char>(c)))
            {
                hex.push_back(c);
            }
        }

        if ((hex.size() & 1) != 0)
            return {};

        for (std::size_t i = 0;
             i < hex.size();
             i += 2)
        {
            const std::string part = hex.substr(i, 2);

            try
            {
                const auto value =
                    static_cast<std::uint8_t>(
                        std::stoul(part, nullptr, 16)
                    );

                result.push_back(value);
            }
            catch (...)
            {
                return {};
            }
        }

        return result;
    }

    bool LoadManifest(
        const fs::path& modDirectory,
        fftml::ModManifest& manifest
    )
    {
        fs::path path = modDirectory / L"manifest.json";
        bool reloadedFormat = false;

        if (!fs::exists(path))
        {
            path = modDirectory / L"ModConfig.json";
            reloadedFormat = true;
        }

        const auto json = ReadJson(path);

        if (json.empty())
        {
            fftml::LogWarning(
                std::string("[Mods] Ignorando pasta sem manifest.json/ModConfig.json valido: ") +
                U8(modDirectory.filename())
            );
            return false;
        }

        if (reloadedFormat)
        {
            manifest.name = json.value("ModName", std::string{});
            manifest.author = json.value("ModAuthor", std::string{});
            manifest.version = json.value("ModVersion", std::string{});
            manifest.uniqueId = json.value("ModId", std::string{});
            manifest.priority = json.value("Priority", 10000);
            manifest.enabled = json.value("Enabled", true);
        }
        else
        {
            manifest.name = json.value("Name", std::string{});
            manifest.author = json.value("Author", std::string{});
            manifest.version = json.value("Version", std::string{});
            manifest.uniqueId = json.value("UniqueID", std::string{});
            manifest.priority = json.value("Priority", 0);
            manifest.enabled = json.value("Enabled", true);
        }

        manifest.directory = modDirectory;

        if (manifest.name.empty() ||
            manifest.version.empty() ||
            manifest.uniqueId.empty())
        {
            fftml::LogError(
                std::string("[Mods] metadados incompletos: ") +
                U8(path)
            );
            return false;
        }

        return true;
    }

    void RegisterReplaceOrLoad(
        const fftml::ModManifest& mod,
        fftml::ContentAction action,
        const std::string& targetRaw,
        const fs::path& sourceRelative
    )
    {
        if (!SafeRelativePath(sourceRelative))
        {
            fftml::LogError(
                std::string("[Content] FromFile inseguro em ") +
                mod.uniqueId +
                ": " +
                U8(sourceRelative)
            );
            return;
        }

        const fs::path source =
            mod.directory / sourceRelative;

        if (!fs::exists(source) ||
            !fs::is_regular_file(source))
        {
            fftml::LogError(
                std::string("[Content] Arquivo nao encontrado: ") +
                U8(source)
            );
            return;
        }

        const std::string target =
            fftml::NormalizeVirtualPath(targetRaw);

        if (target.empty())
            return;

        auto& plan = g_plans[target];

        // Replace/Load resets prior edits in the lower-priority base.
        if (plan.baseAction != fftml::ContentAction::None)
        {
            fftml::LogWarning(
                std::string("[Content] Base substituida: ") +
                target +
                " | " +
                plan.baseModUniqueId +
                " -> " +
                mod.uniqueId
            );
        }

        plan.virtualPath = target;
        plan.kind = fftml::DetectContentKind(target);
        plan.baseAction = action;
        plan.baseFile = source;
        plan.baseModName = mod.name;
        plan.baseModUniqueId = mod.uniqueId;
        plan.basePriority = mod.priority;
        plan.patches.clear();
        plan.mayRequireSyntheticResource =
            action == fftml::ContentAction::Load;

        fftml::LogInfo(
            std::string("[Content] ") +
            (action == fftml::ContentAction::Load
                ? "Load"
                : "Replace") +
            ": " +
            target +
            " <- " +
            U8(sourceRelative) +
            " | " +
            mod.uniqueId
        );
    }

    void RegisterEdit(
        const fftml::ModManifest& mod,
        const std::string& targetRaw,
        const nlohmann::json& change
    )
    {
        const std::string target =
            fftml::NormalizeVirtualPath(targetRaw);

        if (target.empty())
            return;

        if (!change.contains("Patches") ||
            !change["Patches"].is_array())
        {
            fftml::LogError(
                std::string("[Content] Edit sem Patches: ") +
                target +
                " | " +
                mod.uniqueId
            );
            return;
        }

        auto& plan = g_plans[target];

        plan.virtualPath = target;
        plan.kind = fftml::DetectContentKind(target);

        std::size_t added = 0;

        for (const auto& patchJson :
             change["Patches"])
        {
            if (!patchJson.is_object())
                continue;

            const auto offset =
                patchJson.value(
                    "Offset",
                    std::uint64_t{0}
                );

            const std::string bytesText =
                patchJson.value(
                    "Bytes",
                    std::string{}
                );

            auto bytes =
                ParseHexBytes(bytesText);

            if (bytes.empty())
            {
                fftml::LogError(
                    std::string("[Content] Patch sem bytes validos: ") +
                    target +
                    " | " +
                    mod.uniqueId
                );
                continue;
            }

            fftml::BinaryPatch patch;
            patch.offset = offset;
            patch.bytes = std::move(bytes);
            patch.modName = mod.name;
            patch.modUniqueId = mod.uniqueId;

            plan.patches.push_back(
                std::move(patch)
            );

            ++added;
        }

        if (added != 0)
        {
            fftml::LogInfo(
                std::string("[Content] Edit: ") +
                target +
                " | patches=" +
                std::to_string(added) +
                " | " +
                mod.uniqueId
            );
        }
    }

    void LoadContentJson(
        const fftml::ModManifest& mod
    )
    {
        const fs::path path =
            mod.directory / L"content.json";

        if (!fs::exists(path))
            return;

        const auto json = ReadJson(path);

        if (json.empty())
            return;

        if (!json.contains("Changes") ||
            !json["Changes"].is_array())
        {
            fftml::LogError(
                std::string("[Content] content.json sem Changes[]: ") +
                U8(path)
            );
            return;
        }

        for (const auto& change :
             json["Changes"])
        {
            if (!change.is_object())
                continue;

            const std::string action =
                change.value(
                    "Action",
                    std::string{}
                );

            const std::string target =
                change.value(
                    "Target",
                    std::string{}
                );

            if (target.empty())
            {
                fftml::LogError(
                    std::string("[Content] Change sem Target: ") +
                    mod.uniqueId
                );
                continue;
            }

            if (action == "Replace" ||
                action == "Load")
            {
                const std::string fromFile =
                    change.value(
                        "FromFile",
                        std::string{}
                    );

                if (fromFile.empty())
                {
                    fftml::LogError(
                        std::string("[Content] ") +
                        action +
                        " sem FromFile: " +
                        target
                    );
                    continue;
                }

                RegisterReplaceOrLoad(
                    mod,
                    action == "Load"
                        ? fftml::ContentAction::Load
                        : fftml::ContentAction::Replace,
                    target,
                    fs::u8path(fromFile)
                );
            }
            else if (action == "Edit")
            {
                RegisterEdit(
                    mod,
                    target,
                    change
                );
            }
            else
            {
                fftml::LogWarning(
                    std::string("[Content] Action desconhecida: ") +
                    action +
                    " | " +
                    mod.uniqueId
                );
            }
        }
    }

    // Backward compatibility with 0.2.x:
    // no content.json -> every file under content/ is Replace.
    void AutoIndexLegacyContent(
        const fftml::ModManifest& mod
    )
    {
        const std::vector<fs::path> roots =
        {
            mod.directory / L"content",
            mod.directory / L"FFTIVC" / L"data" / L"enhanced"
        };

        for (const fs::path& contentDir : roots)
        {
            if (!fs::exists(contentDir))
                continue;

            std::error_code ec;
            for (fs::recursive_directory_iterator it(
                     contentDir,
                     fs::directory_options::skip_permission_denied,
                     ec), end;
                 it != end;
                 it.increment(ec))
            {
                if (ec)
                {
                    ec.clear();
                    continue;
                }

                const auto& entry = *it;
                if (entry.is_symlink(ec))
                {
                    if (entry.is_directory(ec))
                        it.disable_recursion_pending();
                    continue;
                }
                if (!entry.is_regular_file(ec))
                    continue;

                const fs::path target = fs::relative(entry.path(), contentDir, ec);
                if (ec)
                {
                    ec.clear();
                    continue;
                }
                const fs::path source = fs::relative(entry.path(), mod.directory, ec);
                if (ec)
                {
                    ec.clear();
                    continue;
                }

                RegisterReplaceOrLoad(
                    mod,
                    fftml::ContentAction::Replace,
                    U8(target),
                    source
                );
            }
        }
    }

    std::shared_ptr<const std::vector<std::uint8_t>>
    ReadFileCached(const fs::path& path)
    {
        const std::string key = U8(path);

        {
            std::lock_guard lock(g_cacheMutex);

            const auto it = g_fileCache.find(key);

            if (it != g_fileCache.end())
                return it->second;
        }

        std::ifstream input(
            path,
            std::ios::binary
        );

        if (!input)
            return {};

        input.seekg(0, std::ios::end);
        const auto length = input.tellg();
        input.seekg(0, std::ios::beg);

        if (length <= 0)
            return {};

        auto data =
            std::make_shared<
                std::vector<std::uint8_t>
            >(
                static_cast<std::size_t>(length)
            );

        input.read(
            reinterpret_cast<char*>(
                data->data()
            ),
            length
        );

        if (!input)
            return {};

        std::string reason;

        const auto kind =
            fftml::DetectContentKind(key);

        // Target kind is validated later from the plan, not disk extension.
        (void)kind;

        {
            std::lock_guard lock(g_cacheMutex);
            g_fileCache[key] = data;
        }

        return data;
    }

    void LoadGeneratedPackIndex()
    {
        g_generatedPackTargets.clear();

        const fs::path path =
            g_gameDirectory /
            L"FFTModLoader.pack-index.json";

        if (!fs::exists(path))
            return;

        const auto json = ReadJson(path);

        if (!json.is_object() ||
            !json.contains("Entries") ||
            !json["Entries"].is_array())
        {
            fftml::LogWarning(
                "[ModPack] pack-index.json invalido."
            );
            return;
        }

        for (const auto& entry :
             json["Entries"])
        {
            if (!entry.is_object())
                continue;

            const std::string target =
                fftml::NormalizeVirtualPath(
                    entry.value(
                        "Target",
                        std::string{}
                    )
                );

            if (!target.empty())
                g_generatedPackTargets.insert(
                    target
                );
        }

        fftml::LogInfo(
            std::string("[ModPack] Targets no pack gerado: ") +
            std::to_string(
                g_generatedPackTargets.size()
            )
        );
    }

}

namespace fftml
{
    std::string NormalizeVirtualPath(
        std::string_view path
    )
    {
        std::string result;
        result.reserve(path.size());

        bool slash = false;

        for (char ch : path)
        {
            char c = ch;

            if (c == '\\')
                c = '/';

            if (c == '/')
            {
                if (slash)
                    continue;

                slash = true;
                result.push_back('/');
                continue;
            }

            slash = false;

            if (c >= 'A' && c <= 'Z')
                c = static_cast<char>(
                    c + ('a' - 'A')
                );

            result.push_back(c);
        }

        while (result.rfind("./", 0) == 0)
            result.erase(0, 2);

        while (!result.empty() &&
               result.front() == '/')
        {
            result.erase(result.begin());
        }

        return result;
    }

    bool InitializeContentResolver(
        const fs::path& gameDirectory
    )
    {
        g_gameDirectory = gameDirectory;
        g_mods.clear();
        g_plans.clear();
        LoadGeneratedPackIndex();

        {
            std::lock_guard lock(g_cacheMutex);
            g_fileCache.clear();
        }

        const fs::path modsDir =
            gameDirectory / L"Mods";

        std::error_code ec;
        fs::create_directories(modsDir, ec);

        if (ec)
        {
            LogError(
                "[Mods] Nao foi possivel criar/acessar Mods."
            );
            return false;
        }

        // The packer writes this plan before generating packs. The native layer
        // consumes exactly the same ordered, dependency-validated mod set.
        const fs::path planPath =
            gameDirectory / L"FFTModLoader.modplan.json";
        const auto plan = ReadJson(planPath);
        if (!plan.is_object() ||
            !plan.contains("SchemaVersion") ||
            !plan["SchemaVersion"].is_number_integer() ||
            plan["SchemaVersion"].get<int>() != 1 ||
            !plan.contains("Mods") ||
            !plan["Mods"].is_array())
        {
            LogError("[Mods] Plano de carregamento ausente/invalido; nenhum mod ativado.");
            return false;
        }

        std::unordered_set<std::string> uniqueIds;
        for (const auto& item : plan["Mods"])
        {
            if (!item.is_object())
            {
                LogError("[Mods] Entrada invalida no plano; abortando carregamento.");
                g_mods.clear();
                return false;
            }

            std::string folder;
            std::string id;
            std::string version;
            int priority = 0;
            try
            {
                folder = item.at("Directory").get<std::string>();
                id = item.at("UniqueId").get<std::string>();
                version = item.at("Version").get<std::string>();
                priority = item.at("Priority").get<int>();
            }
            catch (const std::exception&)
            {
                LogError("[Mods] Entrada incompleta no plano; abortando carregamento.");
                g_mods.clear();
                return false;
            }
            const fs::path folderPath = fs::u8path(folder);
            if (!SafeRelativePath(folderPath) ||
                folderPath.filename() != folderPath ||
                folder == "." ||
                folder.empty() ||
                id.empty() ||
                !uniqueIds.insert(id).second)
            {
                LogError("[Mods] Pasta/UniqueID invalido ou duplicado no plano.");
                g_mods.clear();
                return false;
            }

            ModManifest manifest;
            if (!LoadManifest(modsDir / folderPath, manifest) ||
                !manifest.enabled ||
                manifest.uniqueId != id ||
                manifest.version != version ||
                manifest.priority != priority)
            {
                LogError("[Mods] Plano diverge do manifesto: " + id);
                g_mods.clear();
                return false;
            }
            g_mods.push_back(std::move(manifest));
        }

        LogInfo(
            std::string("[Mods] Mods ativos encontrados: ") +
            std::to_string(g_mods.size())
        );

        for (std::size_t i = 0;
             i < g_mods.size();
             ++i)
        {
            const auto& mod = g_mods[i];

            LogInfo(
                std::string("[Mods] #") +
                std::to_string(i + 1) +
                " P=" +
                std::to_string(mod.priority) +
                " " +
                mod.name +
                " " +
                mod.version +
                " (" +
                mod.uniqueId +
                ")"
            );

            const auto contentJson =
                mod.directory / L"content.json";

            if (fs::exists(contentJson))
                LoadContentJson(mod);
            else
                AutoIndexLegacyContent(mod);
        }

        LogInfo(
            std::string("[Content] Planos ativos: ") +
            std::to_string(g_plans.size())
        );

        for (auto& [path, plan] : g_plans)
        {
            plan.kind = DetectContentKind(path);

            LogInfo(
                std::string("[Content] Plan: ") +
                path +
                " | type=" +
                ContentKindName(plan.kind) +
                " | patches=" +
                std::to_string(plan.patches.size())
            );
        }

        return true;
    }

    const ContentPlan* ResolveContentPlan(
        std::string_view virtualPath
    )
    {
        const std::string normalized =
            NormalizeVirtualPath(virtualPath);

        const auto it = g_plans.find(normalized);

        if (it == g_plans.end())
            return nullptr;

        return &it->second;
    }

    std::shared_ptr<const std::vector<std::uint8_t>>
    GetBaseBytes(
        const ContentPlan& plan
    )
    {
        if (plan.baseAction == ContentAction::None ||
            plan.baseFile.empty())
        {
            return {};
        }

        const auto data =
            ReadFileCached(plan.baseFile);

        if (!data)
        {
            LogError(
                std::string("[Content] Nao foi possivel ler: ") +
                U8(plan.baseFile)
            );
            return {};
        }

        std::string reason;

        if (!ValidateContentBytes(
                plan.kind,
                *data,
                reason))
        {
            LogError(
                std::string("[Content] Arquivo invalido para ") +
                plan.virtualPath +
                " (" +
                ContentKindName(plan.kind) +
                "): " +
                reason
            );
            return {};
        }

        return data;
    }

    void ApplyBinaryPatches(
        const ContentPlan& plan,
        std::uint64_t readOffset,
        void* destination,
        std::size_t length
    )
    {
        if (!destination ||
            length == 0 ||
            plan.patches.empty())
        {
            return;
        }

        auto* dest =
            static_cast<std::uint8_t*>(
                destination
            );

        const std::uint64_t readEnd =
            readOffset +
            static_cast<std::uint64_t>(length);

        for (const auto& patch :
             plan.patches)
        {
            const std::uint64_t patchStart =
                patch.offset;

            const std::uint64_t patchEnd =
                patch.offset +
                static_cast<std::uint64_t>(
                    patch.bytes.size()
                );

            const std::uint64_t overlapStart =
                std::max(
                    readOffset,
                    patchStart
                );

            const std::uint64_t overlapEnd =
                std::min(
                    readEnd,
                    patchEnd
                );

            if (overlapStart >= overlapEnd)
                continue;

            const std::size_t destinationOffset =
                static_cast<std::size_t>(
                    overlapStart - readOffset
                );

            const std::size_t patchOffset =
                static_cast<std::size_t>(
                    overlapStart - patchStart
                );

            const std::size_t copyLength =
                static_cast<std::size_t>(
                    overlapEnd - overlapStart
                );

            std::copy_n(
                patch.bytes.data() + patchOffset,
                copyLength,
                dest + destinationOffset
            );
        }
    }

    std::size_t LoadedModCount()
    {
        return g_mods.size();
    }

    std::size_t ContentPlanCount()
    {
        return g_plans.size();
    }

    const std::vector<ModManifest>& GetLoadedMods()
    {
        return g_mods;
    }

    std::vector<const ContentPlan*> GetAllContentPlans()
    {
        std::vector<const ContentPlan*> result;
        result.reserve(g_plans.size());

        for (const auto& [path, plan] : g_plans)
            result.push_back(&plan);

        std::sort(
            result.begin(),
            result.end(),
            [](const ContentPlan* a, const ContentPlan* b)
            {
                if (a->basePriority != b->basePriority)
                    return a->basePriority < b->basePriority;

                return a->virtualPath < b->virtualPath;
            }
        );

        return result;
    }

    bool IsGeneratedPackTarget(
        std::string_view virtualPath
    )
    {
        const std::string normalized =
            NormalizeVirtualPath(
                virtualPath
            );

        return g_generatedPackTargets.contains(
            normalized
        );
    }
}
