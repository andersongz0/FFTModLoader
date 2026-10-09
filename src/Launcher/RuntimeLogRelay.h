#pragma once
#include <filesystem>
#include <fstream>
#include <functional>
#include <map>
#include <string>

// Read only this launch's log data. Existing logs start at their previous EOF;
// a newly created log starts at zero. No global Reloaded installation is used.
class RuntimeLogRelay
{
    struct Cursor { std::uintmax_t offset = 0; std::string partial; };
    std::filesystem::path directory;
    std::filesystem::path sessionFile;
    std::map<std::filesystem::path, Cursor> cursors;
public:
    explicit RuntimeLogRelay(const std::filesystem::path& logs, const std::filesystem::path& session = {}) : directory(logs), sessionFile(session)
    {
        std::error_code ec;
        for (std::filesystem::directory_iterator it(directory,ec), end; !ec && it != end; it.increment(ec))
            if (it->is_regular_file(ec) && it->path().extension() == L".txt")
                cursors[it->path()].offset = it->file_size(ec);
    }

    static std::string ContentLine(std::string line)
    {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.find("Adding file '") != std::string::npos || line.find("mapping G2D file") != std::string::npos ||
            (line.find("[VisualExpansion]") != std::string::npos && line.find("SPR=Visuals/") != std::string::npos))
        {
            auto from = line.find(" from ");
            if (from != std::string::npos) line.resize(from);
            return "[Registrado] " + line;
        }
        if (line.find("DISABLED") != std::string::npos || line.find("NOT FOUND/EMPTY") != std::string::npos ||
            line.find("ERROR SPR") != std::string::npos || line.find("reporting failed") != std::string::npos || line.find("[ERROR]") != std::string::npos ||
            line.find("[Error]") != std::string::npos || line.find("[Warning]") != std::string::npos)
            return "[Aviso/Erro] " + line;
        if (line.find("Menu preview") != std::string::npos || line.find(" -> shape=") != std::string::npos || line.find("Sparse table resolved") != std::string::npos)
            return "[Resolvido] " + line;
        if (line.find("READY:") != std::string::npos || line.find("[RuntimeSession]") != std::string::npos)
            return "[Inicializacao] " + line;
        if (line.find("[VisualExpansion]") != std::string::npos || line.find("[NxdExpansion]") != std::string::npos || line.find("successfully loaded modded") != std::string::npos)
            return "[Carregado] " + line;
        if (line.find("Applying ") != std::string::npos || line.find("added rows for nex table") != std::string::npos ||
            line.find("cell changes for nex table") != std::string::npos || line.find("Writing 'modded") != std::string::npos)
            return "[Aplicando] " + line;
        if (line.find("[RuntimeSession]") != std::string::npos || line.find("[Reloaded] Loading:") != std::string::npos || line.find("- AppId ") != std::string::npos ||
            line.find("Mod loader initialized") != std::string::npos || line.find("Total Loader Initialization Time") != std::string::npos)
            return "[Inicializacao] " + line;
        return {};
    }

    void Pump(const std::function<void(const std::string&)>& emit)
    {
        std::error_code ec;
        for (std::filesystem::directory_iterator it(directory,ec), end; !ec && it != end; it.increment(ec))
        {
            if (!it->is_regular_file(ec) || it->path().extension() != L".txt") continue;
            if (!sessionFile.empty() && it->path() != sessionFile) continue;
            auto& cursor = cursors[it->path()];
            const auto size = it->file_size(ec);
            if (ec) break;
            if (size < cursor.offset) { cursor.offset = 0; cursor.partial.clear(); }
            if (size == cursor.offset) continue;
            std::ifstream input(it->path(),std::ios::binary);
            if (!input) continue;
            input.seekg(static_cast<std::streamoff>(cursor.offset));
            // Bounded reads prevent a noisy mod from blocking the launcher's UI.
            char buffer[65536];
            input.read(buffer,sizeof(buffer));
            const auto count = input.gcount();
            cursor.offset += static_cast<std::uintmax_t>(count);
            cursor.partial.append(buffer,static_cast<size_t>(count));
            size_t start = 0, newline;
            while ((newline = cursor.partial.find('\n',start)) != std::string::npos)
            {
                auto content = ContentLine(cursor.partial.substr(start,newline-start));
                if (!content.empty()) emit(content);
                start = newline + 1;
            }
            cursor.partial.erase(0,start);
            if (cursor.partial.size() > 1048576) cursor.partial.clear();
        }
    }
};
