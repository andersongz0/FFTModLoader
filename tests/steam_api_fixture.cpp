// Test-only DLL. Never distribute this in a loader/game package.
// ZeroDS: records whether embedded runtime setup touches SteamAPI exports.
#include <windows.h>
static volatile LONG calls = 0;
extern "C" __declspec(dllexport) bool SteamAPI_RestartAppIfNecessary(unsigned int) {
    InterlockedIncrement(&calls);
    return false;
}
extern "C" __declspec(dllexport) bool SteamAPI_IsSteamRunning() {
    InterlockedIncrement(&calls);
    return false;
}
extern "C" __declspec(dllexport) int FFTFakeSteamCallCount() {
    return static_cast<int>(calls);
}
