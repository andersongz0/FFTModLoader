#include "../src/Launcher/RuntimeLogRelay.h"
#include "../src/Launcher/LauncherConsole.h"
#include <chrono>
#include <iostream>
#include <stdexcept>
#include <vector>
#include <windows.h>

void Require(bool condition) { if (!condition) throw std::runtime_error("Relay regression"); }
int main(int argc,char** argv)
{
    namespace fs = std::filesystem;
    if(argc==3 && std::string(argv[1])=="--inspect-session")
    {
        const fs::path session=argv[2];
        RuntimeLogRelay recorded(session.parent_path(),session);
        std::vector<std::string> messages;
        recorded.Drain([&](const std::string& line){messages.push_back(line);});
        Require(recorded.HasReceivedContent());
        for(const std::string id:{"ffttic.jobs.genericjobs","ffttic.jobs.genericknights","ffttic.jobs.bestialwarrior","ReworkedChemist"})
        {
            size_t count=0;for(const auto& line:messages)if(line.find(id)!=std::string::npos)++count;
            Require(count>0);
            std::cout << "PASS: recorded session relays " << count << " details for " << id << std::endl;
        }
        return 0;
    }
    if(argc==4 && std::string(argv[1])=="--watch")
    {
        const fs::path session=argv[2];
        const auto expected=static_cast<size_t>(std::stoul(argv[3]));
        RuntimeLogRelay liveRelay(session.parent_path(),session);
        std::vector<std::string> received;
        const auto until=std::chrono::steady_clock::now()+std::chrono::seconds(10);
        while(std::chrono::steady_clock::now()<until)
        {
            liveRelay.Drain([&](const std::string& line){received.push_back(line);});
            if(received.size()>=expected)
            {
                if(received.size()!=expected || !liveRelay.HasReceivedContent())return 2;
                std::cout << "PASS: cross-process live relay received " << received.size() << " complete messages." << std::endl;
                return 0;
            }
            Sleep(25);
        }
        std::cerr << "Cross-process live relay timeout; received=" << received.size() << std::endl;
        return 3;
    }
    const auto dir = fs::temp_directory_path() / ("fft-content-relay-test-" +
        std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()));
    fs::create_directory(dir);
    const auto old = dir / "old.txt", fresh = dir / "new.txt";
    auto write = [](const fs::path& path,const std::string& value,bool append) {
        std::ofstream out(path,std::ios::binary | (append ? std::ios::app : std::ios::trunc)); out << value;
    };
    write(old,"Game successfully loaded modded pack.\n",false);
    RuntimeLogRelay relay(dir);
    std::vector<std::string> lines;
    auto emit = [&](const std::string& line) { lines.push_back(line); };
    relay.Pump(emit); Require(lines.empty());
    write(fresh,"Adding file 'face.tex' from 'path'\r\n[VisualExpansion] SPR file=28842",false);
    relay.Pump(emit); Require(lines.size() == 1 && lines[0].starts_with("[Registrado]") && lines[0].find("path") == std::string::npos);
    write(fresh," served\n[VisualExpansion] DISABLED failed\n",true);
    relay.Pump(emit); Require(lines.size() == 3 && lines[1].starts_with("[Carregado]") && lines[2].starts_with("[Aviso/Erro]"));
    relay.Pump(emit); Require(lines.size() == 3);
    write(old,"Applying JobData with 20 change(s)\n",true);
    relay.Pump(emit); Require(lines.size() == 4 && lines.back().starts_with("[Aplicando]"));
    write(fresh,"[Error] x\n",false); relay.Pump(emit);
    Require(lines.size() == 5 && lines.back().starts_with("[Aviso/Erro]"));
    Require(RuntimeLogRelay::ContentLine("[VisualExpansion] Portrait resource NOT FOUND/EMPTY: face.tex").starts_with("[Aviso/Erro]"));
    Require(RuntimeLogRelay::ContentLine("[VisualExpansion] Portrait resource loaded: face.tex, bytes=1024").starts_with("[Carregado]"));
    Require(RuntimeLogRelay::ContentLine("[VisualExpansion] Menu preview job=0xA2 shape=170").starts_with("[Resolvido]"));
    Require(RuntimeLogRelay::ContentLine("[VisualExpansion] job=0xA2 -> shape=170").starts_with("[Resolvido]"));
    Require(RuntimeLogRelay::ContentLine("unrelated noisy scanner message").empty());
    Require(RuntimeLogRelay::ContentLine("[ExpansionConfig] Validated capacities").starts_with("[Configuracao]"));
    Require(RuntimeLogRelay::ContentLine("[BestialWarrior] [Runtime] installed").starts_with("[Carregado]"));
    Require(RuntimeLogRelay::ContentLine("[BestialWarrior] [Form] Wolf").starts_with("[Forma]"));
    Require(RuntimeLogRelay::ContentLine("[RuntimeSession] [ReworkedChemist] [Save] saved").starts_with("[Save]"));
    Require(RuntimeLogRelay::ContentLine("Registered Sword Limit").starts_with("[Registrado]"));
    const DWORD originalMode=ENABLE_QUICK_EDIT_MODE|ENABLE_LINE_INPUT|ENABLE_ECHO_INPUT|ENABLE_PROCESSED_INPUT;
    const DWORD liveMode=LiveConsoleInputMode(originalMode);
    Require(!(liveMode&ENABLE_QUICK_EDIT_MODE) && (liveMode&ENABLE_EXTENDED_FLAGS));
    Require((liveMode&(ENABLE_LINE_INPUT|ENABLE_ECHO_INPUT|ENABLE_PROCESSED_INPUT)) ==
        (originalMode&(ENABLE_LINE_INPUT|ENABLE_ECHO_INPUT|ENABLE_PROCESSED_INPUT)));
    const auto active = dir / "active.txt";
    HANDLE writer = CreateFileW(active.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
    Require(writer != INVALID_HANDLE_VALUE);
    const std::string live = "[VisualExpansion] G2D page=2000 loaded\n";
    DWORD written;
    Require(WriteFile(writer,live.data(),static_cast<DWORD>(live.size()),&written,nullptr) && written == live.size());
    FlushFileBuffers(writer);
    relay.Pump(emit);
    Require(lines.size() == 6 && lines.back().starts_with("[Carregado]"));
    CloseHandle(writer); fs::remove(active);
    const auto session = dir / "Content-session-test.txt";
    RuntimeLogRelay isolated(dir,session);
    lines.clear();
    write(old,"[VisualExpansion] SPR file=999 served\n",true);
    write(session,"[RuntimeSession] Live content channel ready.\n[NxdExpansion] READY: sparse key lookup\n[NxdExpansion] Sparse table resolved by actual keys: rows=167, requested=170, found=True\n[VisualExpansion] SPR file=28842 served\n",false);
    isolated.Pump(emit);
    Require(lines.size() == 4 && lines[0].starts_with("[Inicializacao]") && lines[1].starts_with("[Inicializacao]") && lines[2].starts_with("[Resolvido]") && lines[3].starts_with("[Carregado]"));
    isolated.Pump(emit); Require(lines.size() == 4);
    const auto liveFile=dir/"Content-session-no-disk-flush.txt";
    RuntimeLogRelay noDiskFlushRelay(dir,liveFile);
    HANDLE liveWriter=CreateFileW(liveFile.c_str(),GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
    Require(liveWriter!=INVALID_HANDLE_VALUE);
    const std::string liveMessage="[RuntimeSession] Live content channel ready.\n";
    Require(WriteFile(liveWriter,liveMessage.data(),static_cast<DWORD>(liveMessage.size()),&written,nullptr) && written==liveMessage.size());
    // StreamWriter.AutoFlush writes bytes, but does not flush NTFS metadata to
    // disk. Directory-entry sizes can remain zero while this handle is open.
    std::uintmax_t cachedLength=0;
    for(fs::directory_iterator it(dir),end;it!=end;++it)
        if(it->path()==liveFile)cachedLength=it->file_size();
    std::vector<std::string> unflushed;
    noDiskFlushRelay.Drain([&](const std::string& line){unflushed.push_back(line);});
    LARGE_INTEGER actualLength{};
    Require(GetFileSizeEx(liveWriter,&actualLength));
    std::cout << "Live writer: cached directory size=" << cachedLength
        << ", handle size=" << actualLength.QuadPart << ", delivered=" << unflushed.size() << std::endl;
    if(unflushed.size()!=1) {
        CloseHandle(liveWriter);
        throw std::runtime_error("Live log unavailable before writer close without FlushFileBuffers");
    }
    CloseHandle(liveWriter); fs::remove(liveFile);
    const auto lockedFile=dir/"Content-session-locked.txt";
    RuntimeLogRelay lockedRelay(dir,lockedFile);
    HANDLE lockedWriter=CreateFileW(lockedFile.c_str(),GENERIC_WRITE,0,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
    Require(lockedWriter!=INVALID_HANDLE_VALUE);
    Require(WriteFile(lockedWriter,liveMessage.data(),static_cast<DWORD>(liveMessage.size()),&written,nullptr));
    std::vector<std::string> warnings;
    auto warn=[&](const std::string& line){warnings.push_back(line);};
    lockedRelay.Drain(warn); lockedRelay.Drain(warn);
    Require(warnings.size()==1 && warnings.front().starts_with("[Aviso/Erro]") && !lockedRelay.HasReceivedContent());
    CloseHandle(lockedWriter);
    lockedRelay.Drain(warn);
    Require(warnings.size()==2 && warnings.back().starts_with("[Inicializacao]") && lockedRelay.HasReceivedContent());
    fs::remove(lockedFile);
    const auto burstFile=dir/"Content-session-burst.txt";
    RuntimeLogRelay burstRelay(dir,burstFile);
    lines.clear();
    HANDLE burstWriter=CreateFileW(burstFile.c_str(),GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
    Require(burstWriter!=INVALID_HANDLE_VALUE);
    std::string burst;
    for(int i=0;i<4000;++i) burst+="Applying JobData row="+std::to_string(i)+"\n";
    Require(burst.size()>65536);
    Require(WriteFile(burstWriter,burst.data(),static_cast<DWORD>(burst.size()),&written,nullptr) && written==burst.size());
    FlushFileBuffers(burstWriter);
    burstRelay.Drain(emit);
    Require(lines.size()==4000 && lines.front().ends_with("row=0") && lines.back().ends_with("row=3999"));
    lines.push_back("ready");
    burstRelay.Drain(emit); Require(lines.size()==4001 && lines.back()=="ready");
    const std::string later="[BestialWarrior] [Form] Wolf active\n";
    Require(WriteFile(burstWriter,later.data(),static_cast<DWORD>(later.size()),&written,nullptr) && written==later.size());
    FlushFileBuffers(burstWriter);
    burstRelay.Drain(emit); Require(lines.size()==4002 && lines.back().starts_with("[Forma]"));
    CloseHandle(burstWriter); fs::remove(burstFile);
    fs::remove(old); fs::remove(fresh); fs::remove(session); fs::remove(dir);
    std::cout << "Runtime console relay: live stages, startup burst before readiness, no duplicates, historical logs excluded and QuickEdit disabled OK.\n";
}
