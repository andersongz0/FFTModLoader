#pragma once
#include <windows.h>
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
    std::map<std::filesystem::path,DWORD> readErrors;
    bool receivedContent=false;
public:
    explicit RuntimeLogRelay(const std::filesystem::path& logs, const std::filesystem::path& session = {}) : directory(logs), sessionFile(session)
    {
        // A named session is exclusively owned by this launch. Do not tail it
        // through directory metadata, which can lag until the writer closes.
        if(!sessionFile.empty())return;
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
        if (line.find("[ExpansionConfig]") != std::string::npos)
            return "[Configuracao] " + line;
        if (line.find("[BestialWarrior]") != std::string::npos || line.find("[ReworkedChemist]") != std::string::npos)
        {
            if (line.find("[Runtime]") != std::string::npos) return "[Carregado] " + line;
            if (line.find("[Form]") != std::string::npos) return "[Forma] " + line;
            if (line.find("[Action]") != std::string::npos) return "[Acao] " + line;
            if (line.find("[Learning]") != std::string::npos) return "[Habilidades] " + line;
            if (line.find("[Save]") != std::string::npos) return "[Save] " + line;
            return "[Conteudo] " + line;
        }
        if (line.find("Registered Sword Limit") != std::string::npos)
            return "[Registrado] " + line;
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

    bool Pump(const std::function<void(const std::string&)>& emit)
    {
        bool progress=false;
        const auto reportError=[&](const std::filesystem::path& path,DWORD error)
        {
            if(error==ERROR_FILE_NOT_FOUND || error==ERROR_PATH_NOT_FOUND)return;
            if(readErrors[path]!=error)
                emit("[Aviso/Erro] Leitura do canal de conteudo indisponivel; codigo="+std::to_string(error)+". Nova tentativa automatica.");
            readErrors[path]=error;
        };
        const auto pumpFile=[&](const std::filesystem::path& path)
        {
            struct SharedReadHandle
            {
                HANDLE value;
                ~SharedReadHandle(){if(value!=INVALID_HANDLE_VALUE)CloseHandle(value);}
            } input{CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,
                nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr)};
            if(input.value==INVALID_HANDLE_VALUE){reportError(path,GetLastError());return;}
            LARGE_INTEGER length{};
            // Query the open handle, never directory_entry::file_size. The
            // .NET writer flushes user buffers, not the NTFS directory cache.
            if(!GetFileSizeEx(input.value,&length)){reportError(path,GetLastError());return;}
            auto& cursor=cursors[path];
            const auto size=static_cast<std::uintmax_t>(length.QuadPart);
            if (size < cursor.offset) { cursor.offset = 0; cursor.partial.clear(); }
            if(size==cursor.offset){readErrors.erase(path);return;}
            LARGE_INTEGER position{};position.QuadPart=static_cast<LONGLONG>(cursor.offset);
            if(!SetFilePointerEx(input.value,position,nullptr,FILE_BEGIN)){reportError(path,GetLastError());return;}
            // Bounded reads prevent a noisy mod from blocking the launcher's UI.
            char buffer[65536];
            DWORD count=0;
            if(!ReadFile(input.value,buffer,sizeof(buffer),&count,nullptr)){reportError(path,GetLastError());return;}
            readErrors.erase(path);
            if(count>0)progress=true;
            cursor.offset += static_cast<std::uintmax_t>(count);
            cursor.partial.append(buffer,static_cast<size_t>(count));
            size_t start = 0, newline;
            while ((newline = cursor.partial.find('\n',start)) != std::string::npos)
            {
                auto content = ContentLine(cursor.partial.substr(start,newline-start));
                if (!content.empty()){receivedContent=true;emit(content);}
                start = newline + 1;
            }
            cursor.partial.erase(0,start);
            if (cursor.partial.size() > 1048576) cursor.partial.clear();
        };
        if(!sessionFile.empty())pumpFile(sessionFile);
        else
        {
            std::error_code ec;
            for(std::filesystem::directory_iterator it(directory,ec),end;!ec && it!=end;it.increment(ec))
                if(it->is_regular_file(ec) && it->path().extension()==L".txt")pumpFile(it->path());
        }
        return progress;
    }

    bool HasReceivedContent()const{return receivedContent;}

    void Drain(const std::function<void(const std::string&)>& emit)
    {
        // Consume a startup burst before reporting readiness, without an
        // unbounded loop if an unrelated mod floods its logger continuously.
        for(int chunk=0;chunk<256 && Pump(emit);++chunk) { }
    }
};
