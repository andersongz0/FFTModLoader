#include "JobMenuExpansion.hpp"

#include "JobDataAPI.hpp"
#include "Logger.hpp"
#include "RuntimeConfig.hpp"
#include "SignatureScanner.hpp"

#include <windows.h>
#include <MinHook.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <initializer_list>
#include <vector>

namespace
{
    constexpr int MaxJobsPerPage = 19;
    constexpr int MaxJobs = 21;
    constexpr int PageTwoExtraJobStart = 6;
    constexpr int JobMenuSlotCount = 19;
    constexpr int JobMenuSlotStride = 0x100;
    constexpr int JobMenuSlotUnitOffset = 0x10;
    constexpr int UiObjectVisibleOffset = 0x1A0;
    constexpr int JobMenuSelectedIndexOffset = 0x4A48;
    constexpr int JobMenuPageDataOffset = 0x2270;
    constexpr int JobMenuPageDataSize = 0xB00;

    using RebuildJobMenuFn = std::uint8_t (__fastcall*)();
    using MoveJobSelectionFn = void (__fastcall*)(void*, void*, int, int);
    using UpdateJobListFn = void* (__fastcall*)(void*, int, void*);
    using PopulateJobMenuSlotFn = void (__fastcall*)(void*, void*, int);
    using SelectJobSlotFn = void (__fastcall*)(void*, int, std::uint8_t);
    using RefreshSelectionFn = void (__fastcall*)(void*);
    using SetJobSlotVisibilityFn = std::uint8_t (__fastcall*)(void*, void*, std::uint8_t, std::uint8_t);
    using UpdateRequirementsPopupFn = char (__fastcall*)(void*);
    using SetRequirementNameFn = void (__fastcall*)(void*, void*);
    using HandleJobMenuClickFn = void (__fastcall*)(void*, int, void*, void*);
    using HandleJobMenuStateFn = void (__fastcall*)(void*, void*, std::uint32_t, std::int16_t, int);
    using Sub286634Fn = std::uintptr_t (__fastcall*)(std::int16_t, std::uint16_t, int, void*, int);
    using HandleTeleportAnimationFn = std::uintptr_t (__fastcall*)(void*);
    using StartEffectFn = std::uintptr_t (__fastcall*)(void*, std::uintptr_t, void*);

    RebuildJobMenuFn g_originalRebuildJobMenu = nullptr;
    MoveJobSelectionFn g_originalMoveJobSelection = nullptr;
    UpdateJobListFn g_originalUpdateJobList = nullptr;
    PopulateJobMenuSlotFn g_originalPopulateJobMenuSlot = nullptr;
    UpdateRequirementsPopupFn g_originalUpdateRequirementsPopup = nullptr;
    SetRequirementNameFn g_originalSetRequirementName = nullptr;
    HandleJobMenuClickFn g_originalHandleJobMenuClick = nullptr;
    HandleJobMenuStateFn g_originalHandleJobMenuState = nullptr;
    Sub286634Fn g_originalSub286634 = nullptr;
    HandleTeleportAnimationFn g_originalHandleTeleportAnimation = nullptr;
    StartEffectFn g_originalStartEffect = nullptr;

    SelectJobSlotFn g_selectJobSlot = nullptr;
    RefreshSelectionFn g_refreshSelection = nullptr;
    SetJobSlotVisibilityFn g_setJobSlotVisibility = nullptr;

    void* g_tRebuildJobMenu = nullptr;
    void* g_tMoveJobSelection = nullptr;
    void* g_tUpdateJobList = nullptr;
    void* g_tPopulateJobMenuSlot = nullptr;
    void* g_tUpdateRequirementsPopup = nullptr;
    void* g_tSetRequirementName = nullptr;
    void* g_tHandleJobMenuClick = nullptr;
    void* g_tHandleJobMenuState = nullptr;
    void* g_tSub286634 = nullptr;
    void* g_tHandleTeleportAnimation = nullptr;
    void* g_tStartEffect = nullptr;

    std::uintptr_t g_jobPatch1 = 0;
    std::uintptr_t g_jobPatch2 = 0;
    std::uintptr_t g_jobPatch3 = 0;
    std::uintptr_t g_jobPatch4 = 0;
    std::uintptr_t g_jobPatch5 = 0;

    std::vector<std::uint16_t> g_extraJobs;
    std::array<std::uint16_t, MaxJobs> g_originalJobList{};
    std::array<std::uint8_t, JobMenuPageDataSize> g_pageOneData{};
    bool g_haveOriginalJobList = false;

    int g_pageOffset = 0;
    bool g_dontReset = true;
    int g_jobMenuState = 0;
    void* g_jobMenu = nullptr;
    void* g_jobMenuUI = nullptr;
    int g_jobMenuSlotRootsOffset = 0;
    std::uintptr_t g_jobList = 0;
    bool g_active = false;
    int g_teleportSpriteId = 0;

    struct PermanentPatch
    {
        std::uintptr_t address = 0;
        std::vector<std::uint8_t> original;
    };

    std::vector<PermanentPatch> g_permanentPatches;

    constexpr const char* P_SelectJobSlot =
        "48 89 5C 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? "
        "48 33 C4 48 89 84 24 ?? ?? ?? ?? 80 B9 ?? ?? ?? ?? ?? 48 8B D9 4C 63 CA";
    constexpr const char* P_RefreshSelection =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B F1 "
        "48 8D 99 ?? ?? ?? ?? 33 FF 83 7B ?? 06";
    constexpr const char* P_SetJobSlotVisibility =
        "48 85 D2 0F 84 ?? ?? ?? ?? 48 8B C4 48 89 58 ?? 48 89 68 ?? "
        "48 89 70 ?? 48 89 78 ?? 41 56 48 83 EC ?? 48 8B DA 41 0F B6 E9 45 84 C0";
    constexpr const char* P_JobMenuSlotRootsOffset =
        "45 33 D2 4C 8D 99 ?? ?? ?? ?? 49 8B 03 4A 89 44 D4 ?? 4D 3B D1";
    constexpr const char* P_RebuildJobMenu =
        "48 8B C4 48 89 58 ?? 48 89 70 ?? 48 89 78 ?? 55 41 54 41 55 41 56 41 57 "
        "48 8D A8 ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 "
        "48 89 85 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 4C 8D 25";
    constexpr const char* P_MoveJobSelection =
        "48 8B C4 48 89 58 ?? 48 89 68 ?? 48 89 70 ?? 48 89 78 ?? "
        "41 54 41 56 41 57 48 83 EC ?? 45 8B F9";
    constexpr const char* P_UpdateJobList =
        "48 89 E0 48 89 58 ?? 48 89 68 ?? 48 89 70 ?? 48 89 48 ?? 57 41 54";
    constexpr const char* P_PopulateJobMenuSlot =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 55 41 56 41 57 "
        "48 83 EC ?? 49 63 F8 4C 8B EA 48 8B D9";
    constexpr const char* P_UpdateRequirementsPopup =
        "48 8B C4 48 89 58 ?? 48 89 70 ?? 48 89 78 ?? 55 41 54 41 55 41 56 41 57 "
        "48 8D A8 ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 "
        "48 89 85 ?? ?? ?? ?? 48 63 81";
    constexpr const char* P_SetRequirementName =
        "40 53 48 83 EC ?? 48 8B 81 ?? ?? ?? ?? 48 8B D9 48 89 81 ?? ?? ?? ?? 48 81 C1";
    constexpr const char* P_HandleJobMenuClick =
        "48 89 5C 24 ?? 55 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24 ?? "
        "48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 ?? 44 8B B9";
    constexpr const char* P_HandleJobMenuState =
        "48 8B C4 48 89 58 ?? 48 89 68 ?? 48 89 70 ?? 48 89 78 ?? "
        "41 56 48 83 EC ?? 48 8B 01 41 8B E9";
    constexpr const char* P_DKSanguineSword1 = "03 66 00 01 74";
    constexpr const char* P_DKSanguineSword2 = "F4 01 32 41 82 00 02 A0";
    constexpr const char* P_DKSanguineSword3 =
        "01 00 03 09 10 10 00 00 3C 00 00 00 00 00 00 00 FF FF FF FF 04";
    constexpr const char* P_DKRsmFixup1 =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 8B 74 24";
    constexpr const char* P_DKRsmFixup2 = "83 FB ?? 7C ?? BB";
    constexpr const char* P_DKVfxFixup1 =
        "48 8B C4 48 89 58 ?? 48 89 70 ?? 48 89 78 ?? 4C 89 70 ?? "
        "55 48 8D 68 ?? 48 81 EC ?? ?? ?? ?? 8B 91";
    constexpr const char* P_DKVfxFixup2 =
        "48 8B C4 48 89 58 ?? 48 89 68 ?? 48 89 70 ?? 48 89 78 ?? "
        "41 56 48 83 EC ?? 33 ED 48 8D 3D";
    constexpr const char* P_JobPatch1 = "89 83 ?? ?? ?? ?? 45 33 C0 8B D7";
    constexpr const char* P_JobPatch2 = "66 89 0E EB ?? 0F B7 CB";
    constexpr const char* P_JobPatch3 = "83 8F ?? ?? ?? ?? ?? 4C 8D 5C 24";
    constexpr const char* P_JobPatch4 = "66 89 1E 44 01 ED";
    constexpr const char* P_JobPatch5 = "75 ?? 41 09 C9 66 45 89 0F";
    constexpr const char* P_JobFlagsPatch1 =
        "48 8B 6C 24 ?? 89 D8 48 8B 5C 24 ?? 48 8B 74 24 ?? 48 83 C4 ?? 41 5F 41 5E 41 5D";
    constexpr const char* P_JobFlagsPatch2 = "89 F0 89 CF";
    constexpr const char* P_JobIconPatch = "76 ?? 33 D2 41 B8 ?? ?? ?? ?? 48 8B CB";

    bool SafeWrite(std::uintptr_t address, const std::uint8_t* data, std::size_t length)
    {
        if (!address || !data || !length)
            return false;

        DWORD oldProtection = 0;
        if (!VirtualProtect(reinterpret_cast<void*>(address), length, PAGE_EXECUTE_READWRITE, &oldProtection))
            return false;

        std::memcpy(reinterpret_cast<void*>(address), data, length);
        FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<void*>(address), length);
        DWORD ignored = 0;
        VirtualProtect(reinterpret_cast<void*>(address), length, oldProtection, &ignored);
        return true;
    }

    bool SafeWrite(std::uintptr_t address, std::initializer_list<std::uint8_t> bytes)
    {
        const std::vector<std::uint8_t> data(bytes);
        return SafeWrite(address, data.data(), data.size());
    }

    bool ApplyPermanentPatch(std::uintptr_t address, std::initializer_list<std::uint8_t> bytes)
    {
        if (!address || bytes.size() == 0)
            return false;

        PermanentPatch patch;
        patch.address = address;
        patch.original.resize(bytes.size());
        std::memcpy(patch.original.data(), reinterpret_cast<void*>(address), patch.original.size());
        const std::vector<std::uint8_t> replacement(bytes);
        if (!SafeWrite(address, replacement.data(), replacement.size()))
            return false;

        g_permanentPatches.push_back(std::move(patch));
        return true;
    }

    void RestorePermanentPatches()
    {
        for (auto it = g_permanentPatches.rbegin(); it != g_permanentPatches.rend(); ++it)
            SafeWrite(it->address, it->original.data(), it->original.size());
        g_permanentPatches.clear();
    }

    void ApplyTempPatches(bool disable)
    {
        if (!g_jobPatch1 || !g_jobPatch2 || !g_jobPatch3 || !g_jobPatch4 || !g_jobPatch5)
            return;

        if (disable)
        {
            SafeWrite(g_jobPatch1, {0x90,0x90,0x90,0x90,0x90,0x90});
            SafeWrite(g_jobPatch2, {0x90,0x90,0x90});
            SafeWrite(g_jobPatch3, {0x90,0x90,0x90,0x90,0x90,0x90,0x90});
            SafeWrite(g_jobPatch4, {0x90,0x90,0x90});
            SafeWrite(g_jobPatch5, {0x90,0x90,0x90,0x90});
        }
        else
        {
            SafeWrite(g_jobPatch1, {0x89,0x83,0x38,0x4A,0x00,0x00});
            SafeWrite(g_jobPatch2, {0x66,0x89,0x0E});
            SafeWrite(g_jobPatch3, {0x83,0x8F,0x38,0x4A,0x00,0x00,0xFF});
            SafeWrite(g_jobPatch4, {0x66,0x89,0x1E});
            SafeWrite(g_jobPatch5, {0x66,0x45,0x89,0x0F});
        }
    }

    std::uint16_t NativeTaggedJobId(std::uint16_t requested)
    {
        if (!g_haveOriginalJobList)
            return requested;

        // GenericJobs does not put the literal IDs in the second-page list.
        // The game has already decorated entries 19/20 with its runtime flags;
        // preserve those exact 16-bit values.  Checking only the low byte (the
        // old adapter behaviour) discarded the flags on this game build and
        // made A0/A1 resolve as unrelated jobs such as Mime.
        if (requested == 0xA0)
            return g_originalJobList[19];
        if (requested == 0xA1)
            return g_originalJobList[20];
        return requested;
    }

    void UpdateJobListDisplay()
    {
        if (!g_jobList || !g_haveOriginalJobList)
            return;

        auto* jobList = reinterpret_cast<std::uint16_t*>(g_jobList);
        if (g_pageOffset == 0)
        {
            std::copy(g_originalJobList.begin(), g_originalJobList.end(), jobList);
            return;
        }

        for (int i = 0; i < PageTwoExtraJobStart; ++i)
            jobList[i] = g_originalJobList[MaxJobsPerPage - PageTwoExtraJobStart + i];

        for (int i = PageTwoExtraJobStart; i < MaxJobsPerPage; ++i)
        {
            const int extraIndex = i - PageTwoExtraJobStart;
            jobList[i] = extraIndex < static_cast<int>(g_extraJobs.size())
                ? NativeTaggedJobId(g_extraJobs[extraIndex])
                : static_cast<std::uint16_t>(0xFFFF);
        }
    }

    void UpdateJobSlotVisibility(void* jobMenu, bool showAll = false)
    {
        if (!jobMenu || !g_setJobSlotVisibility || !g_jobMenuSlotRootsOffset)
            return;

        const int extraJobEnd = std::min(
            PageTwoExtraJobStart + static_cast<int>(g_extraJobs.size()), MaxJobsPerPage);
        auto* base = static_cast<std::uint8_t*>(jobMenu);

        for (int i = 0; i < JobMenuSlotCount; ++i)
        {
            const bool visible = showAll || g_pageOffset == 0 || i < extraJobEnd;
            auto* slotData = base + g_jobMenuSlotRootsOffset + i * JobMenuSlotStride;
            void* unitRoot = *reinterpret_cast<void**>(slotData + JobMenuSlotUnitOffset);
            if (!unitRoot)
                continue;

            g_setJobSlotVisibility(jobMenu, unitRoot, visible ? 1 : 0, 1);
            *(static_cast<std::uint8_t*>(unitRoot) + UiObjectVisibleOffset) = visible ? 1 : 0;
        }
    }

    void UpdateJobSlotVisibility(bool showAll = false)
    {
        UpdateJobSlotVisibility(g_jobMenu, showAll);
    }

    int GetSecondPageEntryIndex(int column)
    {
        const int count = std::min(
            static_cast<int>(g_extraJobs.size()), MaxJobsPerPage - PageTwoExtraJobStart);
        if (count <= 0)
            return PageTwoExtraJobStart;
        return PageTwoExtraJobStart + std::clamp(column, 0, count - 1);
    }

    void SetPageData(void* a1, void* a2, int pageOffset, int newIndex)
    {
        ApplyTempPatches(true);
        g_pageOffset = pageOffset;
        g_jobMenu = a1;
        UpdateJobListDisplay();

        auto* menu = static_cast<std::uint8_t*>(a1);
        *reinterpret_cast<int*>(menu + JobMenuSelectedIndexOffset) = newIndex;
        g_dontReset = true;
        g_originalRebuildJobMenu();
        g_dontReset = false;

        g_selectJobSlot(a1, newIndex, 0);
        *reinterpret_cast<int*>(menu + JobMenuSelectedIndexOffset) = newIndex;
        *reinterpret_cast<int*>(static_cast<std::uint8_t*>(a2) + 4) = newIndex;
        *(static_cast<std::uint8_t*>(a2) + 8) = 1;
        UpdateJobSlotVisibility(a1);
        ApplyTempPatches(false);
    }

    void SetJobMenuSelection(void* a1, void* a2, int index)
    {
        auto* menu = static_cast<std::uint8_t*>(a1);
        *reinterpret_cast<int*>(menu + JobMenuSelectedIndexOffset) = index;
        g_selectJobSlot(a1, index, 0);
        *reinterpret_cast<int*>(menu + JobMenuSelectedIndexOffset) = index;
        *reinterpret_cast<int*>(static_cast<std::uint8_t*>(a2) + 4) = index;
        *(static_cast<std::uint8_t*>(a2) + 8) = 1;
        g_refreshSelection(a1);
        UpdateJobSlotVisibility(a1);
    }

    std::uint8_t __fastcall RebuildJobMenuHook()
    {
        // Faithful port of GenericJobs Mod.Sub363718Hook.  The upstream mod
        // only preserves the extra page while changing job (1402) or learning
        // abilities (1404); every other rebuild follows the native reset path.
        if (g_pageOffset > 0 &&
            (g_jobMenuState == 1402 || g_jobMenuState == 1404))
        {
            ApplyTempPatches(true);
            UpdateJobListDisplay();
            g_originalRebuildJobMenu();
            UpdateJobSlotVisibility();
            ApplyTempPatches(false);
            return 0;
        }

        if (!g_dontReset && g_pageOffset > 0)
            UpdateJobSlotVisibility(true);

        const std::uint8_t result = g_originalRebuildJobMenu();
        if (!g_dontReset)
        {
            g_pageOffset = 0;
            g_originalJobList.fill(0);
            g_haveOriginalJobList = false;
            g_jobMenu = nullptr;
        }
        return result;
    }

    void __fastcall MoveJobSelectionHook(void* a1, void* a2, int a3, int a4)
    {
        g_jobMenu = a1;
        auto* menu = static_cast<std::uint8_t*>(a1);
        const int currentIndex = *reinterpret_cast<int*>(menu + JobMenuSelectedIndexOffset);
        auto* jobList = reinterpret_cast<std::uint16_t*>(g_jobList);

        if (g_pageOffset == 0 && jobList && jobList[1] == 0xFFFF)
        {
            g_originalMoveJobSelection(a1, a2, a3, a4);
            return;
        }

        if (!g_haveOriginalJobList && jobList)
        {
            std::copy(jobList, jobList + MaxJobs, g_originalJobList.begin());
            g_haveOriginalJobList = true;
        }

        g_originalMoveJobSelection(a1, a2, a3, a4);
        const int newIndex = *reinterpret_cast<int*>(menu + JobMenuSelectedIndexOffset);

        if (currentIndex >= 13 && currentIndex <= 18 && newIndex >= 0 && newIndex < 6)
        {
            if (g_pageOffset == 0 && !g_extraJobs.empty())
            {
                std::memcpy(g_pageOneData.data(), menu + JobMenuPageDataOffset, g_pageOneData.size());
                SetPageData(a1, a2, 1, GetSecondPageEntryIndex(newIndex));
                return;
            }
            if (g_pageOffset == 1)
            {
                SetPageData(a1, a2, 0, newIndex);
                return;
            }
        }

        if (currentIndex >= 0 && currentIndex <= 5 && newIndex >= 13 && newIndex <= 18)
        {
            if (g_pageOffset == 1)
            {
                SetPageData(a1, a2, 0, newIndex);
                return;
            }
            if (g_pageOffset == 0 && !g_extraJobs.empty())
            {
                std::memcpy(g_pageOneData.data(), menu + JobMenuPageDataOffset, g_pageOneData.size());
                SetPageData(a1, a2, 1, GetSecondPageEntryIndex(currentIndex));
                return;
            }
        }

        const int firstInvalidIndex = PageTwoExtraJobStart + static_cast<int>(g_extraJobs.size());
        const bool currentIsExtra = currentIndex >= PageTwoExtraJobStart && currentIndex < firstInvalidIndex;
        if (g_pageOffset > 0 && currentIsExtra && newIndex >= 13)
        {
            SetPageData(a1, a2, 0, currentIndex - PageTwoExtraJobStart);
            return;
        }

        const bool currentIsTopRow = currentIndex >= 0 && currentIndex < PageTwoExtraJobStart;
        const bool newIsEmptyMiddle = newIndex >= firstInvalidIndex && newIndex < 13;
        if (g_pageOffset > 0 && currentIsTopRow && newIsEmptyMiddle)
        {
            SetJobMenuSelection(a1, a2, firstInvalidIndex - 1);
            return;
        }
        if (g_pageOffset > 0 && newIndex >= firstInvalidIndex)
        {
            SetJobMenuSelection(a1, a2, currentIndex);
            return;
        }
        if (g_pageOffset > 0)
            UpdateJobSlotVisibility(a1);
    }

    void* __fastcall UpdateJobListHook(void* jobList, int a2, void* a3)
    {
        g_jobList = reinterpret_cast<std::uintptr_t>(jobList);
        return g_originalUpdateJobList(jobList, a2, a3);
    }

    void __fastcall PopulateJobMenuSlotHook(void* a1, void* jobData, int index)
    {
        const int extraEnd = std::min(
            PageTwoExtraJobStart + static_cast<int>(g_extraJobs.size()), MaxJobsPerPage);
        const bool isExtra = g_pageOffset > 0 && index >= PageTwoExtraJobStart && index < extraEnd;
        if (isExtra)
        {
            auto* data = static_cast<std::uint8_t*>(jobData);
            if (*reinterpret_cast<int*>(data + 0x44) < 1)
                *reinterpret_cast<int*>(data + 0x44) = 1;
        }
        g_originalPopulateJobMenuSlot(a1, jobData, index);
    }

    char __fastcall UpdateRequirementsPopupHook(void* a1)
    {
        g_jobMenuUI = a1;
        std::array<std::uint8_t, JobMenuPageDataSize> temp{};
        auto* data = static_cast<std::uint8_t*>(a1) + JobMenuPageDataOffset;
        std::memcpy(temp.data(), data, temp.size());
        const char result = g_originalUpdateRequirementsPopup(a1);
        std::memcpy(data, temp.data(), temp.size());
        g_jobMenuUI = nullptr;
        return result;
    }

    void __fastcall SetRequirementNameHook(void* a1, void* a2)
    {
        g_originalSetRequirementName(a1, a2);
        if (g_jobMenuUI && g_pageOffset > 0)
        {
            std::memcpy(static_cast<std::uint8_t*>(g_jobMenuUI) + JobMenuPageDataOffset,
                        g_pageOneData.data(), g_pageOneData.size());
        }
    }

    void __fastcall HandleJobMenuClickHook(void* a1, int a2, void* a3, void* a4)
    {
        const int selected = *reinterpret_cast<int*>(
            static_cast<std::uint8_t*>(a1) + JobMenuSelectedIndexOffset);
        if (g_pageOffset == 1 && selected >= PageTwoExtraJobStart + static_cast<int>(g_extraJobs.size()))
            return;
        g_originalHandleJobMenuClick(a1, a2, a3, a4);
    }

    void __fastcall HandleJobMenuStateHook(
        void* a1, void* a2, std::uint32_t a3, std::int16_t a4, int state)
    {
        void* ui = *reinterpret_cast<void**>(static_cast<std::uint8_t*>(a1) + 0x78);
        g_jobMenuState = state;

        // Faithful port of GenericJobs Mod.HandleJobMenuStateHook.  Job
        // Progress is a stock first-page view, so upstream restores page one,
        // performs the native rebuild, then lets the native state handler run.
        if (ui && g_pageOffset > 0 && state == 1409)
        {
            g_pageOffset = 0;
            UpdateJobListDisplay();
            g_originalRebuildJobMenu();
            UpdateJobSlotVisibility(a1);
        }
        g_originalHandleJobMenuState(a1, a2, a3, a4, state);
    }

    std::uintptr_t __fastcall Sub286634Hook(
        std::int16_t a1, std::uint16_t jobId, int a3, void* a4, int a5)
    {
        auto* jobList = reinterpret_cast<std::uint16_t*>(g_jobList);
        if (jobList && jobList[0] != 0x5E && jobId == 0x5E)
            jobId = 0xA0;
        return g_originalSub286634(a1, jobId, a3, a4, a5);
    }

    std::uintptr_t __fastcall HandleTeleportAnimationHook(void* a1)
    {
        g_teleportSpriteId = *(static_cast<std::uint8_t*>(a1) + 10);
        const auto result = g_originalHandleTeleportAnimation(a1);
        g_teleportSpriteId = 0;
        return result;
    }

    std::uintptr_t __fastcall StartEffectHook(
        void* a1, std::uintptr_t effectId, void* a3)
    {
        const bool ultimaTeleporting =
            g_teleportSpriteId == 65 || g_teleportSpriteId == 73;
        if (!ultimaTeleporting)
        {
            if (effectId == 0xB8) effectId = 0xA4;
            else if (effectId == 0x2D) effectId = 0xA5;
            else if (effectId == 0xDB) effectId = 0x100;
            else if (effectId == 0xDC) effectId = 0x67;
        }
        else
        {
            if (effectId == 0xB8) effectId = 0x28;
            if (effectId == 0xDB) effectId = 0xDC;
        }
        return g_originalStartEffect(a1, effectId, a3);
    }

    bool CreateHook(std::uintptr_t address, void* detour, void** original, void*& target)
    {
        if (!address)
            return false;
        target = reinterpret_cast<void*>(address);
        return MH_CreateHook(target, detour, original) == MH_OK;
    }

    bool Enable(void* target)
    {
        return target && MH_EnableHook(target) == MH_OK;
    }

    void Remove(void*& target)
    {
        if (!target)
            return;
        MH_DisableHook(target);
        MH_RemoveHook(target);
        target = nullptr;
    }
}

namespace fftml
{
    bool InstallExperimentalJobMenuExpansion()
    {
        g_active = false;
        if (!GetRuntimeConfig().enableExperimentalJobMenuExpansion)
        {
            LogInfo("[JobMenu] Expansao experimental desativada na configuracao.");
            return true;
        }

        g_extraJobs = GetMenuJobIds();
        if (g_extraJobs.empty())
        {
            LogInfo("[JobMenu] Nenhum MenuJob solicitado; hook nao necessario.");
            return true;
        }
        if (g_extraJobs.size() > static_cast<std::size_t>(MaxJobsPerPage - PageTwoExtraJobStart))
            g_extraJobs.resize(MaxJobsPerPage - PageTwoExtraJobStart);

        const auto aSelect = FindPatternInMainModule(ParsePattern(P_SelectJobSlot));
        const auto aRefresh = FindPatternInMainModule(ParsePattern(P_RefreshSelection));
        const auto aVisibility = FindPatternInMainModule(ParsePattern(P_SetJobSlotVisibility));
        const auto aSlotRoots = FindPatternInMainModule(ParsePattern(P_JobMenuSlotRootsOffset));
        const auto aRebuild = FindPatternInMainModule(ParsePattern(P_RebuildJobMenu));
        const auto aMove = FindPatternInMainModule(ParsePattern(P_MoveJobSelection));
        const auto aUpdateList = FindPatternInMainModule(ParsePattern(P_UpdateJobList));
        const auto aPopulate = FindPatternInMainModule(ParsePattern(P_PopulateJobMenuSlot));
        const auto aUpdateReq = FindPatternInMainModule(ParsePattern(P_UpdateRequirementsPopup));
        const auto aSetReq = FindPatternInMainModule(ParsePattern(P_SetRequirementName));
        const auto aClick = FindPatternInMainModule(ParsePattern(P_HandleJobMenuClick));
        const auto aState = FindPatternInMainModule(ParsePattern(P_HandleJobMenuState));
        const auto aPatch1 = FindPatternInMainModule(ParsePattern(P_JobPatch1));
        const auto aPatch2 = FindPatternInMainModule(ParsePattern(P_JobPatch2));
        const auto aPatch3 = FindPatternInMainModule(ParsePattern(P_JobPatch3));
        const auto aPatch4 = FindPatternInMainModule(ParsePattern(P_JobPatch4));
        const auto aPatch5 = FindPatternInMainModule(ParsePattern(P_JobPatch5));
        const auto aFlags1 = FindPatternInMainModule(ParsePattern(P_JobFlagsPatch1));
        const auto aFlags2 = FindPatternInMainModule(ParsePattern(P_JobFlagsPatch2));
        const auto aIcon = FindPatternInMainModule(ParsePattern(P_JobIconPatch));
        const bool hasDarkKnight = std::find(
            g_extraJobs.begin(), g_extraJobs.end(), 0xA0) != g_extraJobs.end();

        const auto aSanguine1 = hasDarkKnight
            ? FindPatternInMainModule(ParsePattern(P_DKSanguineSword1)) : 1;
        const auto aSanguine2 = hasDarkKnight
            ? FindPatternInMainModule(ParsePattern(P_DKSanguineSword2)) : 1;
        const auto aSanguine3 = hasDarkKnight
            ? FindPatternInMainModule(ParsePattern(P_DKSanguineSword3)) : 1;
        const auto aRsm1 = hasDarkKnight
            ? FindPatternInMainModule(ParsePattern(P_DKRsmFixup1)) : 1;
        const auto aRsm2 = hasDarkKnight
            ? FindPatternInMainModule(ParsePattern(P_DKRsmFixup2)) : 1;
        const auto aVfx1 = hasDarkKnight
            ? FindPatternInMainModule(ParsePattern(P_DKVfxFixup1)) : 1;
        const auto aVfx2 = hasDarkKnight
            ? FindPatternInMainModule(ParsePattern(P_DKVfxFixup2)) : 1;

        const bool complete = aSelect && aRefresh && aVisibility && aSlotRoots && aRebuild &&
            aMove && aUpdateList && aPopulate && aUpdateReq && aSetReq && aClick && aState &&
            aPatch1 && aPatch2 && aPatch3 && aPatch4 && aPatch5 &&
            aFlags1 && aFlags2 && aIcon && aSanguine1 && aSanguine2 &&
            aSanguine3 && aRsm1 && aRsm2 && aVfx1 && aVfx2;
        if (!complete)
        {
            LogError("[JobMenu] Uma ou mais signatures do GenericJobs nao foram encontradas.");
            return false;
        }

        g_selectJobSlot = reinterpret_cast<SelectJobSlotFn>(aSelect);
        g_refreshSelection = reinterpret_cast<RefreshSelectionFn>(aRefresh);
        g_setJobSlotVisibility = reinterpret_cast<SetJobSlotVisibilityFn>(aVisibility);
        g_jobMenuSlotRootsOffset = *reinterpret_cast<const std::int32_t*>(aSlotRoots + 6);
        g_jobPatch1 = aPatch1;
        g_jobPatch2 = aPatch2;
        g_jobPatch3 = aPatch3;
        g_jobPatch4 = aPatch4;
        g_jobPatch5 = aPatch5 + 5;

        if (!CreateHook(aRebuild, reinterpret_cast<void*>(&RebuildJobMenuHook), reinterpret_cast<void**>(&g_originalRebuildJobMenu), g_tRebuildJobMenu) ||
            !CreateHook(aMove, reinterpret_cast<void*>(&MoveJobSelectionHook), reinterpret_cast<void**>(&g_originalMoveJobSelection), g_tMoveJobSelection) ||
            !CreateHook(aUpdateList, reinterpret_cast<void*>(&UpdateJobListHook), reinterpret_cast<void**>(&g_originalUpdateJobList), g_tUpdateJobList) ||
            !CreateHook(aPopulate, reinterpret_cast<void*>(&PopulateJobMenuSlotHook), reinterpret_cast<void**>(&g_originalPopulateJobMenuSlot), g_tPopulateJobMenuSlot) ||
            !CreateHook(aUpdateReq, reinterpret_cast<void*>(&UpdateRequirementsPopupHook), reinterpret_cast<void**>(&g_originalUpdateRequirementsPopup), g_tUpdateRequirementsPopup) ||
            !CreateHook(aSetReq, reinterpret_cast<void*>(&SetRequirementNameHook), reinterpret_cast<void**>(&g_originalSetRequirementName), g_tSetRequirementName) ||
            !CreateHook(aClick, reinterpret_cast<void*>(&HandleJobMenuClickHook), reinterpret_cast<void**>(&g_originalHandleJobMenuClick), g_tHandleJobMenuClick) ||
            !CreateHook(aState, reinterpret_cast<void*>(&HandleJobMenuStateHook), reinterpret_cast<void**>(&g_originalHandleJobMenuState), g_tHandleJobMenuState))
        {
            LogError("[JobMenu] Falha criando hooks do fluxo GenericJobs.");
            RemoveExperimentalJobMenuExpansion();
            return false;
        }

        if (!Enable(g_tRebuildJobMenu) || !Enable(g_tMoveJobSelection) || !Enable(g_tUpdateJobList) ||
            !Enable(g_tPopulateJobMenuSlot) || !Enable(g_tUpdateRequirementsPopup) ||
            !Enable(g_tSetRequirementName) || !Enable(g_tHandleJobMenuClick) ||
            !Enable(g_tHandleJobMenuState))
        {
            LogError("[JobMenu] Falha ativando hooks do fluxo GenericJobs.");
            RemoveExperimentalJobMenuExpansion();
            return false;
        }

        if (!ApplyPermanentPatch(aFlags1 - 0xD, {0x90,0x90,0x90,0x90,0x90,0x90,0x90,0x90,0x90,0x90,0x90}))
        {
            RemoveExperimentalJobMenuExpansion();
            return false;
        }

        const bool denuvoLayout = *reinterpret_cast<const std::uint8_t*>(aFlags2 - 7) == 0x44;
        const bool flags2Ok = denuvoLayout
            ? ApplyPermanentPatch(aFlags2 - 7, {0x41,0xBF,0x15,0x00,0x00,0x00,0x90})
            : ApplyPermanentPatch(aFlags2 - 6, {0x41,0xBF,0x15});
        if (!flags2Ok || !ApplyPermanentPatch(aIcon, {0xEB,0x02}))
        {
            RemoveExperimentalJobMenuExpansion();
            return false;
        }

        if (hasDarkKnight)
        {
            if (!CreateHook(aRsm1, reinterpret_cast<void*>(&Sub286634Hook),
                            reinterpret_cast<void**>(&g_originalSub286634), g_tSub286634) ||
                !CreateHook(aVfx1, reinterpret_cast<void*>(&HandleTeleportAnimationHook),
                            reinterpret_cast<void**>(&g_originalHandleTeleportAnimation), g_tHandleTeleportAnimation) ||
                !CreateHook(aVfx2, reinterpret_cast<void*>(&StartEffectHook),
                            reinterpret_cast<void**>(&g_originalStartEffect), g_tStartEffect) ||
                !Enable(g_tSub286634) || !Enable(g_tHandleTeleportAnimation) ||
                !Enable(g_tStartEffect) ||
                !ApplyPermanentPatch(aSanguine1, {0x07,0x00,0x00}) ||
                !ApplyPermanentPatch(aSanguine2, {0xF4,0x01,0x32,0x41,0x82,0x00,0x02,0xA0}) ||
                !ApplyPermanentPatch(aSanguine3,
                    {0x03,0x00,0x00,0x05,0x11,0x12,0x08,0x01,
                     0x65,0x00,0x00,0x00,0x00,0x00,0x00,0x00}) ||
                !ApplyPermanentPatch(aRsm2, {0x83,0xFB,0x15}))
            {
                LogError("[JobMenu] Falha instalando correcoes Dark Knight do GenericJobs.");
                RemoveExperimentalJobMenuExpansion();
                return false;
            }
            LogInfo("[JobMenu] Correcoes Dark Knight: R/S/M, Sanguine Sword e VFX ativas.");
        }

        g_active = true;
        LogInfo("[JobMenu] Fluxo nativo GenericJobs ativo: A0/A1 preservam flags e Overview real.");
        return true;
    }

    void RemoveExperimentalJobMenuExpansion()
    {
        ApplyTempPatches(false);
        Remove(g_tStartEffect);
        Remove(g_tHandleTeleportAnimation);
        Remove(g_tSub286634);
        Remove(g_tHandleJobMenuState);
        Remove(g_tHandleJobMenuClick);
        Remove(g_tSetRequirementName);
        Remove(g_tUpdateRequirementsPopup);
        Remove(g_tPopulateJobMenuSlot);
        Remove(g_tUpdateJobList);
        Remove(g_tMoveJobSelection);
        Remove(g_tRebuildJobMenu);
        RestorePermanentPatches();

        g_originalRebuildJobMenu = nullptr;
        g_originalMoveJobSelection = nullptr;
        g_originalUpdateJobList = nullptr;
        g_originalPopulateJobMenuSlot = nullptr;
        g_originalUpdateRequirementsPopup = nullptr;
        g_originalSetRequirementName = nullptr;
        g_originalHandleJobMenuClick = nullptr;
        g_originalHandleJobMenuState = nullptr;
        g_originalSub286634 = nullptr;
        g_originalHandleTeleportAnimation = nullptr;
        g_originalStartEffect = nullptr;
        g_selectJobSlot = nullptr;
        g_refreshSelection = nullptr;
        g_setJobSlotVisibility = nullptr;
        g_jobMenu = nullptr;
        g_jobMenuUI = nullptr;
        g_jobList = 0;
        g_pageOffset = 0;
        g_jobMenuState = 0;
        g_teleportSpriteId = 0;
        g_haveOriginalJobList = false;
        g_originalJobList.fill(0);
        g_extraJobs.clear();
        g_active = false;
    }

    bool JobMenuExpansionActive()
    {
        return g_active;
    }
}
