#include "../src/Launcher/ModLoadOrder.h"
#include <iostream>
#include <stdexcept>

int main()
{
    using namespace fftloader;
    std::map<std::string, ModInfo> mods;
    auto add = [&](std::string id, std::vector<std::string> deps = {}, bool library = false) {
        mods[id] = {id, deps, library};
    };
    add("reloaded.sharedlib.hooks");
    add("Reloaded.Memory.SigScan.ReloadedII");
    add("fftivc.utility.modloader", {"reloaded.sharedlib.hooks", "Reloaded.Memory.SigScan.ReloadedII"});
    add("ffttic.jobs.genericjobs", {"fftivc.utility.modloader"});
    add("fftmodloader.jobexpansion", {"ffttic.jobs.genericjobs", "fftivc.utility.modloader"});
    add("fftmodloader.contentexpansion", {"fftmodloader.jobexpansion"});
    add("ffttic.jobs.genericknights", {"fftmodloader.jobexpansion"});
    add(ChemistId, {"fftmodloader.contentexpansion"});
    std::vector<std::string> sorted;
    std::string error;
    auto check = [](bool ok) { if (!ok) throw std::runtime_error("Load-order test failed"); };
    check(SortMods(mods, sorted, error));
    check(sorted == std::vector<std::string>{"Reloaded.Memory.SigScan.ReloadedII", "reloaded.sharedlib.hooks",
        "fftivc.utility.modloader", "ffttic.jobs.genericjobs", "fftmodloader.jobexpansion",
        "fftmodloader.contentexpansion", "ffttic.jobs.genericknights", ChemistId});
    check(CanonicalModId(LegacyChemistId) == ChemistId);
    check(CanonicalModId("thirdparty.id") == "thirdparty.id");
    check(CanonicalModId(ChemistId) == ChemistId);
    add("aaa.consumer", {ChemistId});
    check(SortMods(mods, sorted, error) && sorted.back() == "aaa.consumer");
    // Declared dependency overrides the preferred infrastructure stage.
    mods["reloaded.sharedlib.hooks"].dependencies = {"zzz.library"};
    add("zzz.library", {}, true);
    check(SortMods(mods, sorted, error));
    check(std::find(sorted.begin(), sorted.end(), "zzz.library") < std::find(sorted.begin(), sorted.end(), "reloaded.sharedlib.hooks"));
    mods["zzz.library"].dependencies = {"reloaded.sharedlib.hooks"};
    check(!SortMods(mods, sorted, error) && sorted.empty() && error.find("circular") != std::string::npos);
    mods.erase("zzz.library");
    check(!SortMods(mods, sorted, error) && error.find("ausente") != std::string::npos);
    mods.clear();
    check(SortMods(mods, sorted, error) && sorted.empty());
    std::cout << "PASS: deterministic infrastructure order, dependency precedence, aliases, missing dependencies, cycles and empty host\n";
}
