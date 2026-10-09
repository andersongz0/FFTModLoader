#include "../src/Launcher/RuntimeLogRelay.h"
#include <chrono>
#include <iostream>
#include <stdexcept>
#include <vector>
#include <windows.h>

void Require(bool condition) { if (!condition) throw std::runtime_error("Relay regression"); }
int main()
{
    namespace fs = std::filesystem;
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
    fs::remove(old); fs::remove(fresh); fs::remove(session); fs::remove(dir);
    std::cout << "Runtime console relay: historical logs excluded, partial lines, append, truncation, stages and no duplicates OK.\n";
}
