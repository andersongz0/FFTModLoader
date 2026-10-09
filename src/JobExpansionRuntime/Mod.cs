using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Mod.Interfaces;
using GenericJobs.Configuration;
using GenericJobs.Template;
using System.Diagnostics;
using Reloaded.Memory.Interfaces;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using System.Runtime.InteropServices;
using Reloaded.Memory.Sigscan;
using System;
using Reloaded.Memory.Sigscan.Definitions.Structs;

namespace GenericJobs
{
    /// <summary>
    /// Job selection mod that adds vertical paging for hidden jobs (Dark Knight)
    /// </summary>
    public partial class Mod : ModBase
    {
        private readonly IModLoader _modLoader;
        private readonly IReloadedHooks? _hooks;
        private readonly ILogger _logger;
        private readonly IMod _owner;
        private Config _configuration;
        private readonly IModConfig _modConfig;
        private static IStartupScanner? _startupScanner = null!;
        private const int MaxJobsPerPage = 19;
        private const int MaxJobs = 21;
        private const int PageTwoExtraJobStart = 6;
        private const int ExtraJobsPerPage = MaxJobsPerPage - PageTwoExtraJobStart;
        private ExpansionSettings _expansion = new();
        private const int JobMenuSlotCount = 19;
        private const int JobMenuSlotStride = 0x100;
        private const int JobMenuSlotUnitOffset = 0x10;
        private const int UiObjectVisibleOffset = 0x1A0;

        // Game base address
        private nuint _gameBase;

        // Cached extra jobs
        private ushort[] _extraJobs = Array.Empty<ushort>();
        private ushort[] _registeredJobs = Array.Empty<ushort>();

        // State tracking
        private ushort[] _originalJobList = new ushort[MaxJobs]; // Store original jobs
        private int _pageOffset = 0; // 0 = page 1, 1 = page 2
        private bool _dontReset = true;
        private Dictionary<int, string> _jobNameLookup = new();
        private nint _jobMenuUI = 0;
        private byte[] _jobMenuUIData = new byte[0xB00];
        private byte[] _jobMenuPageOneData = new byte[0xB00];
        private int _jobMenuState = 0;
        private int _teleportSpriteId = 0;
        private nint _jobMenu = 0;
        private int _jobMenuSlotRootsOffset = 0;
        private ushort _pendingVisualJob = 0xFFFF;

        // Hook objects
        private IHook<Sub363718Delegate>? _sub363718Hook;
        private IHook<Sub12FBB8Delegate>? _sub12FBB8Hook;
        private IHook<PopulateJobMenuSlotDelegate>? _populateJobMenuSlot;
        private IHook<UpdateLevelRequirementsPopupDelegate>? _updateLevelRequirementsPopup;
        private IHook<SetJobRequirementNameDelegate>? _setJobRequirementName;
        private IHook<HandleJobMenuClickDelegate>? _handleJobMenuClick;
        private IHook<HandleJobMenuStateDelegate>? _handleJobMenuState;
        private IHook<Sub286634Delegate>? _sub286634;
        private IHook<StartEffectDelegate>? _startEffect;
        private IHook<HandleTeleportAnimationDelegate>? _handleTeleportAnimation;
        private IHook<LoadG2DDelegate>? _loadG2D;
        private readonly object _g2DLogLock = new();
        private int _pendingVisualG2DCalls;


        private readonly List<(nuint address, byte[] original)> _tempPatches = new();

        // CurChrChgDisp builds the job-change menu into this permanent buffer.
        // FUN_1402864ac also builds combat command lists into caller stack
        // buffers: NEVER capture those addresses for later menu writes.
        private const nuint JobMenuListRva = 0x3C51890;
        private long _jobList = 0;

        // Function delegates
        [Function(CallingConventions.Microsoft)]
        private delegate void Sub363718Delegate();

        [Function(CallingConventions.Microsoft)]
        private delegate void Sub12FBB8Delegate(nint a1, nint a2, int a3, int a4);

        [Function(CallingConventions.Microsoft)]
        private delegate void PopulateJobMenuSlotDelegate(nint a1, nint jobData, int index);

        [Function(CallingConventions.Microsoft)]
        private delegate nint Sub3CEE14Delegate(nint a1, uint a2);

        [Function(CallingConventions.Microsoft)]
        private delegate void Sub1309C0Delegate(nint a1, int a2, byte a3);

        [Function(CallingConventions.Microsoft)]
        private delegate byte SetJobSlotVisibilityDelegate(nint a1, nint slotRoot, byte visible, byte force);

        [Function(CallingConventions.Microsoft)]
        private delegate byte SubF5E3CDelegate(nint a1, nint a2, int a3, int a4);

        [Function(CallingConventions.Microsoft)]
        private delegate void Sub131A6CDelegate(nint a1);

        [Function(CallingConventions.Microsoft)]
        private delegate void UpdateLevelRequirementsPopupDelegate(nint a1);

        [Function(CallingConventions.Microsoft)]
        private delegate void SetJobRequirementNameDelegate(nint a1, nint a2);

        [Function(CallingConventions.Microsoft)]
        private delegate void HandleJobMenuClickDelegate(nint a1, nint a2, byte a3);

        [Function(CallingConventions.Microsoft)]
        private delegate void HandleJobMenuStateDelegate(nint a1, nint a2, uint a3, int a4, int a5);

        [Function(CallingConventions.Microsoft)]
        private delegate void Sub286634Delegate(short a1, ushort jobId, int a3, nint a4, int a5);

        [Function(CallingConventions.Microsoft)]
        private delegate void StartEffectDelegate(uint a1, uint a2, nint a3);

        [Function(CallingConventions.Microsoft)]
        private delegate int HandleTeleportAnimationDelegate(nint a1);

        // The game requests individual pages from system/ffto/g2d.dat through
        // this routine.  We only observe requests here; the hook does not
        // provide or replace resources.
        [Function(CallingConventions.Microsoft)]
        private delegate int LoadG2DDelegate(nint self, int fileIndex, int nextFileIndex, int flags);

        // Wrapper for original functions
        private Sub1309C0Delegate? _sub1309C0;
        private Sub131A6CDelegate? _sub131A6C;
        private SetJobSlotVisibilityDelegate? _setJobSlotVisibility;
        private SubF5E3CDelegate? _subF5E3C;

        public Mod(ModContext context)
        {
            _modLoader = context.ModLoader;
            _hooks = context.Hooks;
            _logger = context.Logger;
            _owner = context.Owner;
            _configuration = context.Configuration;
            _modConfig = context.ModConfig;
            StartSessionContentLog();

#if DEBUG
            Debugger.Launch();
#endif
            Initialize();
        }

        private void Initialize()
        {
            try
            {
                _logger.WriteLine($"[{_modConfig.ModId}] Initializing...");

                var process = Process.GetCurrentProcess();
                var mainModule = process.MainModule;
                if (mainModule == null)
                {
                    _logger.WriteLine($"[{_modConfig.ModId}] Failed to get main module!");
                    return;
                }

                _gameBase = (nuint)mainModule.BaseAddress;
                _logger.WriteLine($"[{_modConfig.ModId}] Game base: 0x{_gameBase:X}");

                if (!mainModule.ModuleName.Equals("FFT_enhanced.exe", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.WriteLine($"[{_modConfig.ModId}] Wrong executable: {mainModule.ModuleName}");
                    return;
                }

                string configPath = Path.Combine(Path.GetDirectoryName(mainModule.FileName)!, "FFTModLoader.config.json");
                _logger.WriteLine($"[{_modConfig.ModId}] [ExpansionConfig] File: {configPath}");
                _expansion = ExpansionSettings.Load(configPath);
                _logger.WriteLine($"[{_modConfig.ModId}] [ExpansionConfig] Requested: extra-jobs={_expansion.MaxExtraJobs}, new sprite sets={_expansion.MaxNewSpriteSets}. Safe maxima: {ExpansionSettings.SafeMaxExtraJobs}/{ExpansionSettings.SafeMaxNewSpriteSets}.");
                var enabledMods = _modLoader.GetAppConfig().EnabledMods;
                string gameDirectory = Path.GetDirectoryName(mainModule.FileName)!;
                _registeredJobs = JobActivationPolicy.ReadRegisteredJobs(enabledMods.Select(id =>
                    ModDirectoryResolver.Resolve(gameDirectory, id, _modLoader.GetDirectoryForModId)));
                _extraJobs = ParseExtraJobs();
                ValidateVisualCapacity(Path.GetDirectoryName(mainModule.FileName)!); // Validate staged mods BEFORE hooks; later mods are not registered yet.
                _logger.WriteLine($"[{_modConfig.ModId}] [ExpansionConfig] Validated capacities: menu={_expansion.MaxExtraJobs} (configured={_extraJobs.Length}), sprites={_expansion.MaxNewSpriteSets} new sets (total shapes={_expansion.ShapeCapacity}). Restart the game after changing JSON.");
                _logger.WriteLine($"[{_modConfig.ModId}] Loaded {_extraJobs.Length} extra jobs");

                // A host with no registered jobs must not create menu slots.
                // The bundled GenericJobs always registers Dark/Onion; external
                // hosts can use Chemist's sparse lookup without job-menu hooks.
                if (_registeredJobs.Length == 0 &&
                    !enabledMods.Contains("ffttic.tests.reworkedchemist.venom"))
                {
                    _logger.WriteLine($"[{_modConfig.ModId}] Dormant: no expansion consumer; original job menu preserved.");
                    return;
                }

                // Fixed addresses require the same executable guard as visual
                // expansion. Do not touch another game build's memory.
                using (var image = File.OpenRead(mainModule.FileName))
                    if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image)) != VisualImageHash)
                        throw new NotSupportedException("Unsupported executable; menu/command patches not installed.");
                _jobList = (long)(_gameBase + JobMenuListRva);
                _logger.WriteLine($"[{_modConfig.ModId}] [CombatSafety] Job menu uses permanent buffer RVA 0x{JobMenuListRva:X}; battle command-list builder is not intercepted.");

                var scanner = new Scanner(process, process.MainModule);

                if (_extraJobs.Length != 0) InstallHooks(scanner);
                _modLoader.OnModLoaderInitialized += InstallNxdExpansion;
                _modLoader.OnModLoaderInitialized += InstallVisualExpansion;
            }
            catch (Exception ex)
            {
                _logger.WriteLine($"[{_modConfig.ModId}] [Error] Expansion initialization stopped; no hooks installed if configuration validation failed: {ex}");
            }
        }

        void InstallHooks(Scanner scanner)
        {
            if (_hooks == null)
            {
                _logger.WriteLine($"[{_modConfig.ModId}] Hooks interface is null!");
                return;
            }

            var startupScannerController = _modLoader.GetController<IStartupScanner>();
            if (startupScannerController == null || !startupScannerController.TryGetTarget(out _startupScanner))
            {
                _logger.WriteLine($"[{_modConfig.ModId}] Could not fetch IStartupScanner", _logger.ColorRed);
                return;
            }

            var patterns = new Dictionary<string, (string pattern, Action<PatternScanResult> handler)>
            {
                ["Sub1309C0"] = (
                    "48 89 5C 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 80 B9 ?? ?? ?? ?? ?? 48 8B D9 4C 63 CA",
                    e => _sub1309C0 = _hooks.CreateWrapper<Sub1309C0Delegate>((long)_gameBase + e.Offset, out _)
                ),
                ["Sub131A6C"] = (
                    "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B F1 48 8D 99 ?? ?? ?? ?? 33 FF 83 7B ?? 06",
                    e => _sub131A6C = _hooks.CreateWrapper<Sub131A6CDelegate>((long)_gameBase + e.Offset, out _)
                ),
                ["SetJobSlotVisibility"] = (
                    "48 85 D2 0F 84 ?? ?? ?? ?? 48 8B C4 48 89 58 ?? 48 89 68 ?? 48 89 70 ?? 48 89 78 ?? 41 56 48 83 EC ?? 48 8B DA 41 0F B6 E9 45 84 C0",
                    e => _setJobSlotVisibility = _hooks.CreateWrapper<SetJobSlotVisibilityDelegate>((long)_gameBase + e.Offset, out _)
                ),
                ["JobMenuSlotRootsOffset"] = (
                    "45 33 D2 4C 8D 99 ?? ?? ?? ?? 49 8B 03 4A 89 44 D4 ?? 4D 3B D1",
                    e => _jobMenuSlotRootsOffset = Marshal.ReadInt32((nint)(_gameBase + (nuint)e.Offset + 6))
                ),
                ["SubF5E3C"] = (
                    "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 41 8B D9 41 8B E8 48 8B FA 48 8B F1 E8 ?? ?? ?? ?? 84 C0 75 ?? BA 00 80 00 00",
                    e => _subF5E3C = _hooks.CreateWrapper<SubF5E3CDelegate>((long)_gameBase + e.Offset, out _)
                ),
                ["Sub363718Hook"] = (
                    "48 8B C4 48 89 58 ?? 48 89 70 ?? 48 89 78 ?? 55 41 54 41 55 41 56 41 57 48 8D A8 ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 4C 8D 25",
                    e => InstallMenuHook(ref _sub363718Hook, Sub363718Hook, (long)_gameBase + e.Offset)
                ),
                ["Sub12FBB8Hook"] = (
                    "48 8B C4 48 89 58 ?? 48 89 68 ?? 48 89 70 ?? 48 89 78 ?? 41 54 41 56 41 57 48 83 EC ?? 45 8B F9 45 8B E0",
                    e => InstallMenuHook(ref _sub12FBB8Hook, Sub12FBB8Hook, (long)_gameBase + e.Offset)
                ),
                ["PopulateJobMenuSlot"] = (
                    "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 55 41 56 41 57 48 83 EC ?? 49 63 F8 4C 8B EA 48 8B D9",
                    e => InstallMenuHook(ref _populateJobMenuSlot, PopulateJobMenuSlotHook, (long)_gameBase + e.Offset)
                ),
                ["UpdateLevelRequirementsPopup"] = (
                    "48 8B C4 48 89 58 ?? 48 89 70 ?? 48 89 78 ?? 55 41 54 41 55 41 56 41 57 48 8D A8 ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 48 63 81",
                    e => InstallMenuHook(ref _updateLevelRequirementsPopup, UpdateLevelRequirementsPopupHook, (long)_gameBase + e.Offset)
                ),
                ["SetJobRequirementName"] = (
                    "40 53 48 83 EC ?? 48 8B 81 ?? ?? ?? ?? 48 8B D9 48 89 81 ?? ?? ?? ?? 48 81 C1",
                    e => InstallMenuHook(ref _setJobRequirementName, SetJobRequirementNameHook, (long)_gameBase + e.Offset)
                ),
                ["HandleJobMenuClick"] = (
                    "48 89 5C 24 ?? 55 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24 ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 ?? 44 8B B9",
                    e => InstallMenuHook(ref _handleJobMenuClick, HandleJobMenuClickHook, (long)_gameBase + e.Offset)
                ),
                ["HandleJobMenuState"] = (
                    "48 8B C4 48 89 58 ?? 48 89 68 ?? 48 89 70 ?? 48 89 78 ?? 41 56 48 83 EC ?? 48 8B 01 41 8B E9",
                    e => InstallMenuHook(ref _handleJobMenuState, HandleJobMenuStateHook, (long)_gameBase + e.Offset)
                ),
                ["JobPatch1"] = (
                    "89 83 ?? ?? ?? ?? 45 33 C0 8B D7",
                    e => AddTempPatch(_gameBase + (nuint)e.Offset, 6)
                ),
                ["JobPatch2"] = (
                    "66 89 0E EB ?? 0F B7 CB",
                    e => AddTempPatch(_gameBase + (nuint)e.Offset, 3)
                ),
                ["JobPatch3"] = (
                    "83 8F ?? ?? ?? ?? ?? 4C 8D 5C 24",
                    e => AddTempPatch(_gameBase + (nuint)e.Offset, 7)
                ),
                ["JobPatch4"] = (
                    "66 89 1E 41 03 ED",
                    e => AddTempPatch(_gameBase + (nuint)e.Offset, 3)
                ),
                ["JobPatch5"] = (
                    "75 ?? 44 0B C9 66 45 89 0F",
                    e => AddTempPatch(_gameBase + (nuint)e.Offset + 5, 4)
                ),
                ["JobFlagsPatch1"] = (
                    "83 E3 F7 48 8B 6C 24 ?? 8B C3 48 8B 5C 24",
                    e => WriteMemory(_gameBase + (nuint)e.Offset, [0x90, 0x90, 0x90])
                ),
                ["JobFlagsPatch2"] = (
                    "41 BF 13 00 00 00 8B C6 8B F9",
                    e => WriteMemory(_gameBase + (nuint)e.Offset + 2, [0x15])
                ),
                ["JobIconPatch"] = (
                    "76 ?? 33 D2 41 B8 ?? ?? ?? ?? 48 8B CB",
                    e => WriteMemory(_gameBase + (nuint)e.Offset, [0xEB, 0x02])
                )
            };

            var darkKnightPatterns = new Dictionary<string, (string pattern, Action<PatternScanResult> handler)>
            {
                ["DK_SanguineSword1"] = (
                    "03 66 00 01 74",
                    e => WriteMemory(_gameBase + (nuint)e.Offset, [0x07, 0x00, 0x00])
                ),
                ["DK_SanguineSword2"] = (
                    "F4 01 32 41 82 00 02 A0",
                    e => WriteMemory(_gameBase + (nuint)e.Offset, [0xF4, 0x01, 0x32, 0x41, 0x82, 0x00, 0x02, 0xA0])
                ),
                ["DK_SanguineSword3"] = (
                    "01 00 03 09 10 10 00 00 3C 00 00 00 00 00 00 00 FF FF FF FF 04",
                    e => WriteMemory(_gameBase + (nuint)e.Offset, [0x03, 0x00, 0x00, 0x05, 0x11, 0x12, 0x08, 0x01, 0x65, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00])
                ),
                ["DK_RSMFixup1"] = (
                    "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 8B 74 24",
                    e => InstallMenuHook(ref _sub286634, Sub286634Hook, (long)_gameBase + e.Offset)
                ),
                ["DK_RSMFixup2"] = (
                    "83 FB ?? 7C ?? BB",
                    e => WriteMemory(_gameBase + (nuint)e.Offset, [0x83, 0xFB, 0x15])
                ),
                ["DK_VFXFixup1"] = (
                    "48 8B C4 48 89 58 ?? 48 89 70 ?? 48 89 78 ?? 4C 89 70 ?? 55 48 8D 68 ?? 48 81 EC ?? ?? ?? ?? 8B 91",
                    e => InstallMenuHook(ref _handleTeleportAnimation, HandleTeleportAnimationHook, (long)_gameBase + e.Offset)
                ),
                ["DK_VFXFixup2"] = (
                    "48 8B C4 48 89 58 ?? 48 89 68 ?? 48 89 70 ?? 48 89 78 ?? 41 56 48 83 EC ?? 33 ED 48 8D 3D",
                    e => InstallMenuHook(ref _startEffect, StartEffectHook, (long)_gameBase + e.Offset)
                )
            };

            if (_extraJobs.Contains((ushort)0xA0))
            {
                patterns = patterns.Concat(darkKnightPatterns).ToDictionary(x => x.Key, x => x.Value);
            }

            _logger.WriteLine($"[{_modConfig.ModId}] Scanning for {patterns.Count} patterns...");

            foreach (var kvp in patterns)
            {
                var name = kvp.Key;
                var (pattern, handler) = kvp.Value;

                _startupScanner.AddMainModuleScan(pattern, result =>
                {
                    if (result.Found)
                    {
                        _logger.WriteLine($"[{_modConfig.ModId}] {name,-30} -> 0x{(_gameBase + (nuint)result.Offset):X}");
                        handler(result);
                    }
                    else
                    {
                        _logger.WriteLine($"[{_modConfig.ModId}] {name,-30} -> NOT FOUND", _logger.ColorRed);
                    }
                });
            }
        }

        private void InstallMenuHook<T>(ref IHook<T>? field, T callback, long address) where T : Delegate
        {
            // Native code may enter immediately after activation. Publish the
            // owner first so its callback can always reach OriginalFunction.
            field = _hooks!.CreateHook<T>(callback, address);
            field.Activate();
        }

        private unsafe void WriteMemory(nuint address, byte[] data)
        {
            fixed (byte* dataPtr = data)
            {
                Reloaded.Memory.Memory.Instance.SafeWrite(address, new Span<byte>(dataPtr, data.Length));
            }
        }

        private void Sub363718Hook()
        {
            // if changing job or learning an ability while on the second page
            // make sure to reset job menu properly
            if (_pageOffset > 0 && (_jobMenuState == 1402 || _jobMenuState == 1404))
            {
                ApplyTempPatches(true);
                UpdateJobListDisplay();
                _sub363718Hook!.OriginalFunction();
                UpdateJobSlotVisibility();
                ApplyTempPatches(false);
                return;
            }

            if (!_dontReset && _pageOffset > 0)
                UpdateJobSlotVisibility(showAll: true);

            _sub363718Hook!.OriginalFunction();

            if (!_dontReset)
            {
                _pageOffset = 0;
                Array.Clear(_originalJobList, 0, _originalJobList.Length);
                _jobMenu = 0;
            }

        }

        private ushort[] ParseExtraJobs()
        {
            ushort[] jobs = JobActivationPolicy.SelectJobs(_registeredJobs, _configuration.ExtraJobsHex);
            _expansion.EnsureMenuFits(jobs.Length);
            return jobs;
        }

        private int ExtraPageCount =>
            (_extraJobs.Length + ExtraJobsPerPage - 1) / ExtraJobsPerPage;

        private int CurrentExtraStart =>
            Math.Max(0, _pageOffset - 1) * ExtraJobsPerPage;

        private int CurrentExtraCount =>
            _pageOffset <= 0
                ? 0
                : Math.Min(
                    ExtraJobsPerPage,
                    Math.Max(0, _extraJobs.Length - CurrentExtraStart));

        private unsafe void UpdateJobListDisplay()
        {
            ushort* joblist = (ushort*)(_jobList);

            if (_pageOffset == 0)
            {
                for (int i = 0; i < MaxJobs; i++)
                {
                    joblist[i] = _originalJobList[i];
                }
            }
            else
            {
                for (int i = 0; i < PageTwoExtraJobStart; i++)
                {
                    joblist[i] = _originalJobList[MaxJobsPerPage - PageTwoExtraJobStart + i];
                }

                for (int i = PageTwoExtraJobStart; i < MaxJobsPerPage; i++)
                {
                    int extraIndex = CurrentExtraStart + i - PageTwoExtraJobStart;
                    if (extraIndex < _extraJobs.Length)
                    {
                        joblist[i] = _extraJobs[extraIndex];

                        // temp fix until actual generics
                        if (joblist[i] == 0xa0)
                        {
                            joblist[i] += (ushort)(_originalJobList[19] - 0xa0);
                        }

                        if (joblist[i] == 0xa1)
                        {
                            joblist[i] += (ushort)(_originalJobList[20] - 0xa1);
                        }
                    }
                    else
                    {
                        joblist[i] = 0xFFFF; // End of list
                    }
                }
            }
        }

        private void AddTempPatch(nuint address, int length)
        {
            var original = new byte[length];
            Marshal.Copy((nint)address, original, 0, length);
            _tempPatches.Add((address, original));
        }

        private unsafe void ApplyTempPatches(bool disable)
        {
            foreach (var (address, original) in _tempPatches)
            {
                if (disable)
                    WriteMemory(address, Enumerable.Repeat((byte)0x90, original.Length).ToArray());
                else
                    WriteMemory(address, original);
            }
        }

        private unsafe void SetPageData(nint a1, nint a2, int pageOffet, int newIndex)
        {
            ApplyTempPatches(true);
            _pageOffset = pageOffet;
            _jobMenu = a1;
            UpdateJobListDisplay();

            *(int*)(a1 + 0x4A48) = newIndex;

            _dontReset = true;
            _sub363718Hook!.OriginalFunction();
            _dontReset = false;

            _sub1309C0!(a1, newIndex, 0);
            *(int*)(a1 + 0x4A48) = newIndex;
            *(int*)(a2 + 4) = newIndex;
            *(byte*)(a2 + 8) = 1;

            UpdateJobSlotVisibility(a1);

            ApplyTempPatches(false);
        }

        private int GetExtraPageEntryIndex(int column, int pageOffset)
        {
            int extraStart = Math.Max(0, pageOffset - 1) * ExtraJobsPerPage;
            int extraJobCount = Math.Min(
                ExtraJobsPerPage,
                Math.Max(0, _extraJobs.Length - extraStart));
            if (extraJobCount <= 0)
                return PageTwoExtraJobStart;

            return PageTwoExtraJobStart + Math.Clamp(column, 0, extraJobCount - 1);
        }

        private unsafe void SetJobMenuSelection(nint a1, nint a2, int index)
        {
            *(int*)(a1 + 0x4A48) = index;
            _sub1309C0!(a1, index, 0);
            *(int*)(a1 + 0x4A48) = index;
            *(int*)(a2 + 4) = index;
            *(byte*)(a2 + 8) = 1;
            _sub131A6C!(a1);
            UpdateJobSlotVisibility(a1);
        }

        private unsafe void Sub12FBB8Hook(nint a1, nint a2, int a3, int a4)
        {
            _jobMenu = a1;
            int currentIndex = *(int*)(a1 + 0x4A48);
            ushort* joblist = (ushort*)(_jobList);

            // if viewing a job tree with only 1 job (monsters, guests), do nothing
            if (_pageOffset == 0 && joblist[1] == 0xFFFF)
            {
                _sub12FBB8Hook!.OriginalFunction(a1, a2, a3, a4);
                return;
            }

            if (_originalJobList[0] == 0)
            {
                for (int i = 0; i < MaxJobs; i++)
                    _originalJobList[i] = joblist[i];
            }

            _sub12FBB8Hook!.OriginalFunction(a1, a2, a3, a4);

            int newIndex = *(int*)(a1 + 0x4A48);

            // Detect scrolling down: was on bottom row (13-18), now wrapped to first row (0-5)
            if (currentIndex >= 13 && currentIndex <= 18 && newIndex >= 0 && newIndex < 6)
            {
                if (_pageOffset == 0 && _extraJobs.Length > 0)
                {
                    Marshal.Copy(IntPtr.Add(a1, 8816), _jobMenuPageOneData, 0, 0xB00);
                    SetPageData(a1, a2, 1, GetExtraPageEntryIndex(newIndex, 1));
                    return;
                }
                else if (_pageOffset > 0 && _pageOffset < ExtraPageCount)
                {
                    int nextPage = _pageOffset + 1;
                    SetPageData(a1, a2, nextPage, GetExtraPageEntryIndex(newIndex, nextPage));
                    return;
                }
                else if (_pageOffset > 0)
                {
                    SetPageData(a1, a2, 0, newIndex);
                    return;
                }
            }

            // Detect scrolling up: was on top row (0-5), now wrapped to bottom rows (13-18)
            if (currentIndex >= 0 && currentIndex <= 5 && newIndex >= 13 && newIndex <= 18)
            {
                if (_pageOffset == 1)
                {
                    SetPageData(a1, a2, 0, newIndex);
                    return;
                }
                else if (_pageOffset > 1)
                {
                    int previousPage = _pageOffset - 1;
                    SetPageData(
                        a1,
                        a2,
                        previousPage,
                        GetExtraPageEntryIndex(currentIndex, previousPage));
                    return;
                }
                else if (_pageOffset == 0 && _extraJobs.Length > 0)
                {
                    Marshal.Copy(IntPtr.Add(a1, 8816), _jobMenuPageOneData, 0, 0xB00);
                    int lastPage = ExtraPageCount;
                    SetPageData(
                        a1,
                        a2,
                        lastPage,
                        GetExtraPageEntryIndex(currentIndex, lastPage));
                    return;
                }
            }

            int firstInvalidIndex = PageTwoExtraJobStart + CurrentExtraCount;
            bool currentIsExtraJob = currentIndex >= PageTwoExtraJobStart && currentIndex < firstInvalidIndex;

            if (_pageOffset > 0 && currentIsExtraJob && newIndex >= 13)
            {
                SetPageData(a1, a2, 0, currentIndex - PageTwoExtraJobStart);
                return;
            }

            bool currentIsTopRow = currentIndex >= 0 && currentIndex < PageTwoExtraJobStart;
            bool newIndexIsEmptyMiddleSlot = newIndex >= firstInvalidIndex && newIndex < 13;
            if (_pageOffset > 0 && currentIsTopRow && newIndexIsEmptyMiddleSlot)
            {
                SetJobMenuSelection(a1, a2, firstInvalidIndex - 1);
                return;
            }

            if (_pageOffset > 0 && newIndex >= firstInvalidIndex)
            {
                SetJobMenuSelection(a1, a2, currentIndex);
                return;
            }

            if (_pageOffset > 0)
                UpdateJobSlotVisibility(a1);
        }

        private unsafe void PopulateJobMenuSlotHook(nint a1, nint jobData, int index)
        {
            RewriteVisualPreview(a1, jobData);
            int extraJobEnd = PageTwoExtraJobStart + CurrentExtraCount;
            bool isExtraJob = _pageOffset > 0 && index >= PageTwoExtraJobStart && index < extraJobEnd;

            if (isExtraJob && *(int*)(jobData + 0x44) < 1)
                *(int*)(jobData + 0x44) = 1;

            _populateJobMenuSlot!.OriginalFunction(a1, jobData, index);
        }

        private unsafe void HandleJobMenuClickHook(nint a1, nint a2, byte a3)
        {
            int selectedIndex = *(int*)(a1 + 19016);

            if (_pageOffset > 0 && selectedIndex >= PageTwoExtraJobStart + CurrentExtraCount)
                return;

            // Keep the selected job only as diagnostic context for resource
            // requests that happen immediately after this confirmation.
            if (_jobList != 0 && selectedIndex >= 0 && selectedIndex < MaxJobsPerPage)
            {
                ushort selectedJob = ((ushort*)_jobList)[selectedIndex];
                _pendingVisualJob = selectedJob;
                if (selectedJob is >= 0xA0 and <= 0xA3)
                {
                    // Capture the next resource requests for this precise
                    // selection.  A global "seen page" list hid the useful
                    // evidence after a page had been requested once.
                    _pendingVisualG2DCalls = 24;
                    _logger.WriteLine($"[{_modConfig.ModId}] [G2DLoadProbe] selecting job=0x{selectedJob:X2}; tracing next {_pendingVisualG2DCalls} G2D requests");
                }
            }

            _handleJobMenuClick?.OriginalFunction(a1, a2, a3);
        }

        private void HandleJobMenuStateHook(nint a1, nint a2, uint a3, int a4, int state)
        {
            var ui = Marshal.ReadIntPtr(IntPtr.Add(a1, 0x78));

            _jobMenuState = state;

            // temp fix, move back to page 1 when viewing job tree
            if (ui > 0 && _pageOffset > 0)
            {
                if (state == 1409)
                {
                    _pageOffset = 0;
                    UpdateJobListDisplay();
                    _sub363718Hook!.OriginalFunction();
                    UpdateJobSlotVisibility(a1);
                }
            }

            _handleJobMenuState?.OriginalFunction(a1, a2, a3, a4, state);
        }

        private unsafe void UpdateJobSlotVisibility(bool showAll = false)
        {
            if (_jobMenu == 0)
                return;

            UpdateJobSlotVisibility(_jobMenu, showAll);
        }

        private unsafe void UpdateJobSlotVisibility(nint jobMenu, bool showAll = false)
        {
            if (_setJobSlotVisibility == null || _jobMenuSlotRootsOffset == 0)
                return;

            int extraJobEnd = PageTwoExtraJobStart + CurrentExtraCount;

            for (int i = 0; i < JobMenuSlotCount; i++)
            {
                bool visible = showAll || _pageOffset == 0 || i < extraJobEnd;
                nint slotData = jobMenu + _jobMenuSlotRootsOffset + i * JobMenuSlotStride;
                nint unitRoot = *(nint*)(slotData + JobMenuSlotUnitOffset);

                if (unitRoot != 0)
                {
                    _setJobSlotVisibility(jobMenu, unitRoot, visible ? (byte)1 : (byte)0, 1);
                    *(byte*)(unitRoot + UiObjectVisibleOffset) = visible ? (byte)1 : (byte)0;
                }
            }
        }

        private unsafe void UpdateLevelRequirementsPopupHook(nint a1)
        {
            _jobMenuUI = a1;
            byte[] tempJobData = new byte[0xB00];
            Marshal.Copy(IntPtr.Add(a1, 8816), tempJobData, 0, 0xB00);
            _updateLevelRequirementsPopup!.OriginalFunction(a1);
            Marshal.Copy(tempJobData, 0, IntPtr.Add(a1, 8816), 0xB00);
            _jobMenuUI = 0;
        }

        private void SetJobRequirementNameHook(nint a1, nint a2)
        {
            _setJobRequirementName?.OriginalFunction(a1, a2);

            if (_jobMenuUI != 0 && _pageOffset > 0)
            {
                Marshal.Copy(_jobMenuPageOneData, 0, IntPtr.Add(_jobMenuUI, 8816), 0xB00);
            }
        }

        private unsafe void Sub286634Hook(short a1, ushort jobId, int a3, nint a4, int a5)
        {
            ushort* joblist = (ushort*)(_jobList);

            // in order to equip dark knight reaction/support, we needed to +1 a > 19 loop
            // which ended at 0x5E
            if (joblist != null && joblist[0] != 0x5E && jobId == 0x5E)
                jobId = 0xA0;

            _sub286634!.OriginalFunction(a1, jobId, a3, a4, a5);
        }

        private unsafe int HandleTeleportAnimationHook(nint a1)
        {
            _teleportSpriteId = *(byte*)(a1 + 10);
            var result = _handleTeleportAnimation!.OriginalFunction(a1);
            _teleportSpriteId = 0;
            return result;
        }

        private void StartEffectHook(uint a1, uint a2, nint a3)
        {
            bool isUltimaTeleporting = _teleportSpriteId == 65 || _teleportSpriteId == 73;

            if (!isUltimaTeleporting)
            {
                // yes, this is basically how the psp version handles 
                // dark knight vfx
                if (a2 == 0xB8)
                    a2 = 0xA4;
                else if (a2 == 0x2D)
                    a2 = 0xA5;
                else if (a2 == 0xDB)
                    a2 = 0x100;
                else if (a2 == 0xDC)
                    a2 = 0x67;
            }
            else
            {
                if (a2 == 0xB8)
                    a2 = 0x28;
                if (a2 == 0xDB)
                    a2 = 0xDC;
            }

            _startEffect!.OriginalFunction(a1, a2, a3);
        }

        private int LoadG2DHook(nint self, int fileIndex, int nextFileIndex, int flags)
        {
            if (TryLoadVisualPage(self, fileIndex, out int result))
                return result;
            // Capture a bounded per-selection trace.  This identifies the
            // native spriteset-to-page route without replacing any resource.
            if (_pendingVisualG2DCalls > 0)
            {
                lock (_g2DLogLock)
                {
                    int call = 25 - _pendingVisualG2DCalls--;
                    string jobContext = _pendingVisualJob == 0xFFFF
                        ? "unknown"
                        : $"0x{_pendingVisualJob:X2}";
                    _logger.WriteLine(
                        $"[{_modConfig.ModId}] [G2DLoadProbe] job={jobContext} " +
                        $"call={call}/24 page={fileIndex} next={nextFileIndex} flags={flags}");
                }
            }

            return _loadG2D!.OriginalFunction(self, fileIndex, nextFileIndex, flags);
        }

        #region Standard Overrides
        public override void ConfigurationUpdated(Config configuration)
        {
            _configuration = configuration;
            _logger.WriteLine($"[{_modConfig.ModId}] Config Updated: Applying");

            _logger.WriteLine($"[{_modConfig.ModId}] Restart required to apply registered job menu changes safely.");
        }
        #endregion

        #region For Exports, Serialization etc.
#pragma warning disable CS8618
        public Mod() { }
#pragma warning restore CS8618
        #endregion
    }
}
