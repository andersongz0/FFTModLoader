using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace GenericJobs;

public partial class Mod
{
    // Enhanced 1.0.4.0. RVAs below are guarded against the exact clean image
    // AND live operands. Never apply this layout to another executable build.
    private const string VisualImageHash = "937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5";
    private const int OriginalShapes = ExpansionSettings.OriginalShapes;
    private int ShapeCapacity => _expansion.ShapeCapacity; // JSON capacity, checked against byte-safe ceiling.
    private readonly Dictionary<(byte Job, byte Sex), VisualEntry> _visualJobs = new();
    private readonly Dictionary<int, byte[]> _visualPages = new();
    private readonly Dictionary<int, byte[]> _visualSpr = new();
    private readonly Dictionary<int, nint> _visualPageMemory = new();
    private readonly HashSet<string> _visualTrace = new();
    private readonly object _visualLock = new();
    private nint _visualTables;
    private bool _visualReady;
    private IHook<ResolveShapeDelegate>? _resolveShape;
    private NativeJobRouteMemory? _shapeRoute;
    private IHook<ReadSpriteDelegate>? _readSprite;
    private IHook<DecodeVisualPageDelegate>? _decodeVisualPage;
    private IHook<UnloadVisualPageDelegate>? _unloadVisualPage;
    private IHook<OpenVisualResourceDelegate>? _openVisualResource;
    private IHook<MenuPortraitDelegate>? _menuPortrait;
    private WorldFaceDelegate? _worldFace;

    [Function(CallingConventions.Microsoft)]
    private delegate byte ResolveShapeDelegate(nint unit, nint palette, byte job);
    [Function(CallingConventions.Microsoft)]
    private delegate int ReadSpriteDelegate(int file, int sector, int count, nint output);
    [Function(CallingConventions.Microsoft)]
    private delegate int DecodeVisualPageDelegate(nint self, int page, nint output);
    [Function(CallingConventions.Microsoft)]
    private delegate void UnloadVisualPageDelegate(nint self);
    [Function(CallingConventions.Microsoft)]
    private delegate void OpenVisualResourceDelegate(nint manager, nint result);
    // RVA 32AB3C forwards its incoming R9 to Wfaceuvclut. Although the
    // decompiler only infers two arguments, palette is a fourth argument.
    [Function(CallingConventions.Microsoft)]
    private delegate uint MenuPortraitDelegate(uint index, nint primitive, nint unused, nint palette);
    [Function(CallingConventions.Microsoft)]
    private delegate uint WorldFaceDelegate(nint primitive, uint face, nint optionalShape, nint palette);

    private sealed class VisualManifest
    {
        public int SchemaVersion { get; set; }
        public VisualEntry[] Entries { get; set; } = [];
    }

    private sealed class VisualEntry
    {
        public int JobId { get; set; }
        public string Gender { get; set; } = "";
        public int ShapeId { get; set; }
        public int TemplateShape { get; set; }
        public string Spr { get; set; } = "";
        public string TopPage { get; set; } = "";
        public string BottomPage { get; set; } = "";
        public int PageId => 2000 + (ShapeId - OriginalShapes) * 2;
        public int SprId => 0x7000 + ShapeId;
        public byte Sex => Gender == "male" ? (byte)0x80 : (byte)0x81;
    }

    private sealed record VisualPatch(nuint Address, byte[] Before, byte[] After);

    private void RewriteVisualPreview(nint menu, nint jobData)
    {
        if (!_visualReady || menu == 0 || jobData == 0) return;
        int previousShape = Marshal.ReadInt32(jobData, MenuPreviewPolicy.ShapeOffset);
        // Only the job-grid descriptor is rewritten. Equipped named-unit
        // sprites and portraits still use their untouched native resolvers.
        if (!MenuPreviewPolicy.TryRewrite(menu, jobData, FindPreviewShape)) return;
        byte job = Marshal.ReadByte(jobData, MenuPreviewPolicy.JobOffset);
        byte sex = MenuPreviewPolicy.SexFromFlags(Marshal.ReadByte(menu, MenuPreviewPolicy.FlagsOffset));
        VisualTrace($"Menu preview job=0x{job:X2} sex=0x{sex:X2} shape={Marshal.ReadInt32(jobData, MenuPreviewPolicy.ShapeOffset)} (previous={previousShape})");
    }

    private int? FindPreviewShape(byte job, byte sex) =>
        _visualJobs.TryGetValue((job, sex), out var entry) ? entry.ShapeId : null;

    private static byte[] VisualFile(string root, string relative, int? exactSize = null)
    {
        // Do not allow a manifest to read outside its installed mod directory.
        string path = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (string.IsNullOrWhiteSpace(relative) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Visual asset outside mod: {relative}");
        byte[] bytes = File.ReadAllBytes(path);
        if (exactSize.HasValue && bytes.Length != exactSize.Value)
            throw new InvalidDataException($"{relative}: expected {exactSize} bytes, found {bytes.Length}");
        return bytes;
    }

    private unsafe void InstallVisualExpansion()
    {
        var applied = new List<VisualPatch>();
        try
        {
            if (_hooks == null || _visualReady) return;
            if (!_nxdExpansionReady)
                throw new NotSupportedException("Sparse NXD lookup unavailable; extra visual resources cannot be enabled safely.");
            var shapes = new HashSet<int>();
            foreach (string id in _modLoader.GetAppConfig().EnabledMods)
            {
                string root = _modLoader.GetDirectoryForModId(id);
                string manifestPath = Path.Combine(root, "FFTModLoader.visuals.json");
                if (!File.Exists(manifestPath)) continue;
                var manifest = JsonSerializer.Deserialize<VisualManifest>(File.ReadAllText(manifestPath))
                    ?? throw new InvalidDataException($"Empty visual manifest: {id}");
                if (manifest.SchemaVersion != 1) throw new InvalidDataException($"Unsupported visual schema: {id}");
                foreach (var entry in manifest.Entries)
                {
                    if (entry.JobId is < 0xA2 or > 255 || entry.Gender is not ("male" or "female") ||
                        entry.TemplateShape is < 96 or > 162)
                        throw new InvalidDataException($"Invalid visual registration: {id}, job={entry.JobId}, shape={entry.ShapeId}");
                    _expansion.EnsureShapeFits(entry.ShapeId, id);
                    if (!shapes.Add(entry.ShapeId) || !_visualJobs.TryAdd(((byte)entry.JobId, entry.Sex), entry))
                        throw new InvalidDataException($"Conflicting visual registration: {id}, shape={entry.ShapeId}");
                    byte[] spr = VisualFile(root, entry.Spr);
                    // Native SPR staging memory for human units is 22 sectors.
                    // Reject oversized/partial assets instead of overrunning it.
                    if (spr.Length < 37376 || spr.Length > 45056)
                        throw new InvalidDataException($"SPR must contain complete frames and fit 45056 bytes: {entry.Spr}");
                    Array.Resize(ref spr, 45056);
                    _visualSpr.Add(entry.SprId, spr);
                    _visualPages.Add(entry.PageId, VisualFile(root, entry.TopPage, 131072));
                    _visualPages.Add(entry.PageId + 1, VisualFile(root, entry.BottomPage, 131072));
                    // Validate that the corresponding enhanced portrait is also installed.
                    VisualFile(root, $"FFTIVC/data/enhanced/ui/ffto/common/face/texture/wldface_{entry.ShapeId}_08_uitx.tex");
                    VisualFile(root, $"FFTIVC/data/enhanced/ui/ffto/common/face/textureparts/wldface_{entry.ShapeId}_08_uitx.utexpt");
                    _logger.WriteLine($"[{_modConfig.ModId}] [VisualExpansion] {id} job=0x{entry.JobId:X2} {entry.Gender}: shape={entry.ShapeId}, pages={entry.PageId}/{entry.PageId + 1}, SPR={entry.Spr}");
                }
            }
            if (shapes.Count == 0) return;
            string exe = Process.GetCurrentProcess().MainModule!.FileName;
            using (var stream = File.OpenRead(exe))
                if (Convert.ToHexString(SHA256.HashData(stream)) != VisualImageHash)
                    throw new NotSupportedException("Visual expansion: unsupported game executable. No native tables changed.");

            _visualTables = AllocateVisualTables();
            nint pageTable = _visualTables;
            nint infoTable = _visualTables + 0x1000;
            nint sprEnhanced = _visualTables + 0x2000;
            nint sprClassic = _visualTables + 0x3000;
            CopyVisualTable(0x7151F0, pageTable, 4);
            CopyVisualTable(0x673300, infoTable, 4);
            CopyVisualTable(0x784690, sprEnhanced, 8);
            CopyVisualTable(0x784BE0, sprClassic, 8);
            foreach (var entry in _visualJobs.Values)
            {
                Marshal.WriteInt32(pageTable, entry.ShapeId * 4, entry.PageId);
                Marshal.WriteInt32(infoTable, entry.ShapeId * 4, Marshal.ReadInt32(infoTable, entry.TemplateShape * 4));
                foreach (nint table in new[] { sprEnhanced, sprClassic })
                {
                    Marshal.WriteInt32(table, entry.ShapeId * 8, entry.SprId);
                    Marshal.WriteInt32(table, entry.ShapeId * 8 + 4, 45056);
                }
            }
            // Immutable native lookup; zero means leave the native shape alone.
            // No CLR transition/dictionary access is allowed on this battle leaf.
            nint shapeRegistrations = _visualTables + 0x4000;
            foreach (var entry in _visualJobs.Values)
                Marshal.WriteByte(shapeRegistrations, entry.JobId * 2 + entry.Sex - 0x80, checked((byte)entry.ShapeId));
            // Every known indexed/RIP-relative consumer, including individual
            // animation-record fields (+1,+2,+3), must follow the expanded table.
            var plan = new List<VisualPatch>();
            RedirectVisualTable(plan, 0x7151F0, pageTable,
                [(0xCE26C,3,7), (0x261722,3,0), (0x2617B2,3,0), (0x2618FC,4,0),
                 (0x26C33F,4,0), (0x26F014,3,0), (0x26F03B,3,0)]);
            RedirectVisualTable(plan, 0x673300, infoTable,
                [(0xCD8EB,4,0), (0x1D4D17,3,7), (0x1D4EDD,3,7), (0x1D5148,3,7),
                 (0x201BEA,3,7), (0x201D3C,3,7), (0x266353,4,0), (0x268E83,3,7),
                 (0x269C66,3,7), (0x26A45C,3,7), (0x26BA94,3,7), (0x26CDE9,4,0),
                 (0x26F452,4,0), (0x26FE09,5,0)]);
            RedirectVisualTable(plan, 0x673301, infoTable + 1,
                [(0xCC6D4,3,7), (0x268EB3,3,7), (0x2699A8,4,0), (0x269E5E,3,7), (0x26FDA7,5,0)]);
            RedirectVisualTable(plan, 0x673302, infoTable + 2, [(0xEDBAB,3,7), (0x1F4FB5,3,7)]);
            RedirectVisualTable(plan, 0x673303, infoTable + 3, [(0x202159,3,7), (0x2736BE,3,7), (0x2736F6,3,7)]);
            RedirectVisualTable(plan, 0x784690, sprEnhanced, [(0x26E792,3,7), (0x26FD69,3,7)]);
            RedirectVisualTable(plan, 0x784BE0, sprClassic, [(0x26E78B,3,7), (0x26FD5C,3,7)]);
            AddVisualPatch(plan, 0xCE266, BitConverter.GetBytes(169), BitConverter.GetBytes(ShapeCapacity - 1));
            foreach (uint site in new uint[] {0x261717,0x2617A7,0x2618EF,0x26C338})
                AddVisualPatch(plan, site, [170], [(byte)ShapeCapacity]);
            foreach (uint site in new uint[] {0x26F007,0x26F02E})
                AddVisualPatch(plan, site, BitConverter.GetBytes(169), BitConverter.GetBytes(ShapeCapacity - 1));
            AddVisualPatch(plan, 0x26FD06, BitConverter.GetBytes(170), BitConverter.GetBytes(ShapeCapacity - 1));
            // Battle's leaf queue builder has no unwind metadata and its own
            // exclusive bound. Otherwise it silently replaces every new shape
            // with shape 1 (Ramza), even after successful resource resolution.
            AddVisualPatch(plan, 0x304208, BitConverter.GetBytes(170), BitConverter.GetBytes(ShapeCapacity));

            // Create all detours before mutating anything. Keep their lifetimes
            // for the entire game session; these resources cannot be unloaded.
            _shapeRoute = new NativeJobRouteMemory();
            _resolveShape = _hooks.CreateHook<ResolveShapeDelegate>((void*)_shapeRoute.Address, (long)_gameBase + 0x278EDC);
            _shapeRoute.CompleteShape(NativeJobRoutes.BuildShape(_resolveShape.OriginalFunctionAddress, (long)shapeRegistrations));
            _readSprite = _hooks.CreateHook<ReadSpriteDelegate>(ReadVisualSprite, (long)_gameBase + 0x2F40C4);
            _loadG2D = _hooks.CreateHook<LoadG2DDelegate>(LoadG2DHook, (long)_gameBase + 0x3D2450);
            _decodeVisualPage = _hooks.CreateHook<DecodeVisualPageDelegate>(DecodeVisualPage, (long)_gameBase + 0x3D25D8);
            _unloadVisualPage = _hooks.CreateHook<UnloadVisualPageDelegate>(UnloadVisualPage, (long)_gameBase + 0x3D25A4);
            // Same exact FileResult ABI and signature used by Nenkai's optional
            // ResourceManagerHooks. Observe portrait reads without replacing
            // files, modifying unit identity, or claiming an unrequested face.
            _openVisualResource = _hooks.CreateHook<OpenVisualResourceDelegate>(ReportVisualResource, (long)_gameBase + 0x92D84);
            // Generic menu faces use a different route from battle portraits:
            // (job * 2 - 0x34/0x33) indexes the old 211-entry face table.
            // A2/A3 overflow that bound and become face 0. Do not expand that
            // old formula or replace Ramza's assets; resolve registered units.
            _worldFace = Marshal.GetDelegateForFunctionPointer<WorldFaceDelegate>((nint)(_gameBase + 0x2E16A8));
            _menuPortrait = _hooks.CreateHook<MenuPortraitDelegate>(ResolveMenuPortrait, (long)_gameBase + 0x32AB3C);
            foreach (var patch in plan) { WriteMemory(patch.Address, patch.After); applied.Add(patch); }
            _readSprite.Activate();
            _unloadVisualPage.Activate();
            _decodeVisualPage.Activate();
            _loadG2D.Activate();
            _openVisualResource.Activate();
            _visualReady = true;
            _menuPortrait.Activate();
            _resolveShape.Activate(); // Resolver is last: only expose fully ready new shapes.
            _logger.WriteLine($"[{_modConfig.ModId}] [VisualExpansion] Native shape resolver: original private registers (including battle R11) preserved; no managed callback.");
            _logger.WriteLine($"[{_modConfig.ModId}] [VisualExpansion] Menu portraits: registered generic units routed to custom faces; native identities preserved.");
            _logger.WriteLine($"[{_modConfig.ModId}] [VisualExpansion] READY: {OriginalShapes} -> {ShapeCapacity} shapes; {shapes.Count} registered; {plan.Count} table/bounds redirects. Original shapes 0..169 preserved.");
        }
        catch (Exception ex)
        {
            _visualReady = false;
            _resolveShape?.Disable(); _readSprite?.Disable(); _loadG2D?.Disable();
            _decodeVisualPage?.Disable(); _unloadVisualPage?.Disable();
            _openVisualResource?.Disable();
            _menuPortrait?.Disable();
            foreach (var patch in applied.AsEnumerable().Reverse()) WriteMemory(patch.Address, patch.Before);
            _logger.WriteLine($"[{_modConfig.ModId}] [VisualExpansion] DISABLED (rolled back): {ex}", _logger.ColorRed);
        }
    }

    private void ValidateVisualCapacity(string gameDirectory)
    {
        var shapes = new HashSet<int>();
        foreach (string id in _modLoader.GetAppConfig().EnabledMods)
        {
            string directory = ModDirectoryResolver.Resolve(gameDirectory, id, _modLoader.GetDirectoryForModId);
            string path = Path.Combine(directory, "FFTModLoader.visuals.json");
            if (!File.Exists(path)) continue;
            var manifest = JsonSerializer.Deserialize<VisualManifest>(File.ReadAllText(path))
                ?? throw new InvalidDataException($"Empty visual manifest: {id}");
            if (manifest.SchemaVersion != 1) throw new InvalidDataException($"Unsupported visual schema: {id}");
            foreach (var entry in manifest.Entries)
            {
                _expansion.EnsureShapeFits(entry.ShapeId, id);
                shapes.Add(entry.ShapeId);
            }
        }
        _logger.WriteLine($"[{_modConfig.ModId}] [ExpansionConfig] Active manifests: {shapes.Count} sprite sets; highest shape={(shapes.Count == 0 ? "none" : shapes.Max().ToString())}; capacity checked before native patches.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);

    private nint AllocateVisualTables()
    {
        // Native indexed instructions use image-base + signed disp32. Allocate
        // near the image, NOT arbitrary unmanaged memory beyond that range.
        for (long delta = 0x4000000; delta < 0x40000000; delta += 0x10000)
        {
            nint address = VirtualAlloc((nint)((long)_gameBase + delta), 0x10000, 0x3000, 0x04);
            if (address != 0) return address;
        }
        throw new OutOfMemoryException("Could not allocate expanded sprite tables within native addressing range.");
    }

    private void CopyVisualTable(uint source, nint destination, int stride)
    {
        var data = new byte[ShapeCapacity * stride];
        Marshal.Copy((nint)(_gameBase + source), data, 0, OriginalShapes * stride);
        // Unregistered entries are safe fallbacks, never adjacent native data.
        for (int i = OriginalShapes; i < ShapeCapacity; i++)
            Array.Copy(data, 100 * stride, data, i * stride, stride);
        Marshal.Copy(data, 0, destination, data.Length);
    }

    private void RedirectVisualTable(List<VisualPatch> plan, uint source, nint destination,
        (uint Site, int DispOffset, int RipLength)[] sites)
    {
        foreach (var site in sites)
        {
            int before = site.RipLength == 0 ? (int)source : checked((int)(source - (long)(site.Site + site.RipLength)));
            long origin = site.RipLength == 0 ? (long)_gameBase : (long)_gameBase + site.Site + site.RipLength;
            int after = checked((int)((long)destination - origin));
            AddVisualPatch(plan, site.Site + (uint)site.DispOffset, BitConverter.GetBytes(before), BitConverter.GetBytes(after));
        }
    }

    private void AddVisualPatch(List<VisualPatch> plan, uint offset, byte[] before, byte[] after)
    {
        nuint address = _gameBase + offset;
        byte[] live = new byte[before.Length];
        Marshal.Copy((nint)address, live, 0, live.Length);
        if (!live.SequenceEqual(before)) throw new NotSupportedException($"Sprite operand changed at RVA {offset:X}; no patches applied.");
        plan.Add(new(address, before, after));
    }

    private uint ResolveMenuPortrait(uint index, nint primitive, nint unused, nint palette)
    {
        if (_visualReady && index < MenuPortraitPolicy.UnitCount && primitive != 0 && palette != 0)
        {
            nint unit = (nint)(_gameBase + MenuPortraitPolicy.SaveDataRva) + checked((int)index * MenuPortraitPolicy.RecordSize);
            byte identity = Marshal.ReadByte(unit);
            byte job = Marshal.ReadByte(unit, 2);
            if (MenuPortraitPolicy.IsEligible(index, identity, job) &&
                _visualJobs.TryGetValue((job, identity), out var visual))
            {
                // Initialize PSX primitive UVs and CLUT through the native
                // function too, rather than returning a bare enhanced face ID.
                uint face = _worldFace!(primitive, (uint)visual.ShapeId, 0, palette);
                VisualTrace($"Menu portrait unit={index} job=0x{job:X2} {visual.Gender} -> face={face}, palette={Marshal.ReadInt32(palette)}");
                return face;
            }
        }
        return _menuPortrait!.OriginalFunction(index, primitive, unused, palette);
    }

    private int ReadVisualSprite(int file, int sector, int count, nint output)
    {
        if (!_visualSpr.TryGetValue(file, out var bytes)) return _readSprite!.OriginalFunction(file, sector, count, output);
        long start = (long)sector * 2048;
        if (output == 0 || start < 0 || count < 0 || start + count > bytes.Length)
        {
            VisualTrace($"ERROR SPR range: file={file}, sector={sector}, count={count}");
            return 1;
        }
        Marshal.Copy(bytes, (int)start, output, count);
        // Same synchronous completion flag cleared by the original reader.
        Marshal.WriteInt32((nint)(_gameBase + 0x1856AB4), 0);
        VisualTrace($"SPR file={file}, bytes={count} served from registered mod asset");
        return 0;
    }

    private void ReportVisualResource(nint manager, nint result)
    {
        _openVisualResource!.OriginalFunction(manager, result);
        if (result == 0) return;
        try
        {
            string? path = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(result, 0x18));
            if (path == null || !path.Contains("wldface_", StringComparison.OrdinalIgnoreCase)) return;
            long size = Marshal.ReadInt64(result, 0x40);
            VisualTrace($"Portrait resource {(size > 0 ? "loaded" : "NOT FOUND/EMPTY")}: {path}, bytes={size}");
        }
        catch (Exception ex)
        {
            VisualTrace($"Portrait reporting failed: {ex.Message}");
        }
    }

    private bool TryLoadVisualPage(nint self, int page, out int result)
    {
        result = 0;
        if (!_visualPages.TryGetValue(page, out byte[]? bytes)) return false;
        lock (_visualLock)
        {
            UnloadVisualPage(self); // Release/clear the previous page using its owner.
            if (!_visualPageMemory.TryGetValue(page, out nint memory))
            {
                memory = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, memory, bytes.Length);
                _visualPageMemory.Add(page, memory);
            }
            Marshal.WriteIntPtr(self, 0x18, memory);
            Marshal.WriteIntPtr(self, 0x20, memory);
            Marshal.WriteInt32(self, 0x13C, page);
        }
        VisualTrace($"G2D page={page}, bytes={bytes.Length} loaded (native success=0)");
        return true;
    }

    private int DecodeVisualPage(nint self, int page, nint output)
    {
        if (!_visualPages.TryGetValue(page, out var bytes)) return _decodeVisualPage!.OriginalFunction(self, page, output);
        Marshal.Copy(bytes, 0, output, bytes.Length);
        return bytes.Length;
    }

    private void UnloadVisualPage(nint self)
    {
        if (!_visualPages.ContainsKey(Marshal.ReadInt32(self, 0x13C)))
        {
            _unloadVisualPage!.OriginalFunction(self);
            return;
        }
        Marshal.WriteIntPtr(self, 0x18, 0);
        Marshal.WriteIntPtr(self, 0x20, 0);
        Marshal.WriteInt32(self, 0x13C, -1);
    }

    private void VisualTrace(string text)
    {
        lock (_visualLock)
            if (_visualTrace.Add(text)) _logger.WriteLine($"[{_modConfig.ModId}] [VisualExpansion] {text}");
    }
}
