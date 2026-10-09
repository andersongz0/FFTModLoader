using FF16Tools.Files;
using FF16Tools.Files.Nex;
using FF16Tools.Files.Nex.Entities;
using FF16Tools.Pack;
using FF16Tools.Pack.Crypto;

using Microsoft.Extensions.Logging.Abstractions;

internal static class JobCatalogDumper
{
    public static int DumpSpriteAssets(string dataDir)
    {
        string extractionDir = Path.Combine(
            Directory.GetParent(dataDir)!.FullName,
            "FFTModLoader_Dumps",
            "sprite-assets");
        Directory.CreateDirectory(extractionDir);

        foreach (string mode in new[] { "enhanced", "classic" })
        {
            string modeDir = Path.Combine(dataDir, mode);
            if (!Directory.Exists(modeDir))
                continue;

            using var manager = new FF16PackManager(NullLoggerFactory.Instance);
            manager.Open(modeDir, PackKeyStore.FFT_IVALICE_CODENAME);
            if (mode == "enhanced")
            {
                foreach (string target in new[]
                {
                    "fftpack/unit/battle_aguri_spr.bin",
                    "fftpack/unit/battle_cloud_spr.bin",
                    "fftpack/unit/battle_knight_m_spr.bin",
                    "fftpack/unit/battle_knight_w_spr.bin"
                })
                {
                    File.WriteAllBytes(
                        Path.Combine(extractionDir, Path.GetFileName(target)),
                        manager.GetFileDataBytes(target));
                }
            }
            foreach (var packEntry in manager.PackFiles.OrderBy(x => x.Key))
            {
                foreach (var fileEntry in packEntry.Value.Files
                             .Where(x =>
                                 x.Key.Contains("spr", StringComparison.OrdinalIgnoreCase) ||
                                 x.Key.Contains("unit", StringComparison.OrdinalIgnoreCase) ||
                                 x.Key.Contains("wlduni", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(x => x.Key))
                {
                    Console.WriteLine(
                        $"{mode}|{packEntry.Key}|{fileEntry.Value.DecompressedFileSize}|{fileEntry.Key}");
                }
            }
        }

        return 0;
    }

    public static int Dump(string dataDir, Version gameVersion)
    {
        string enhancedDir = Path.Combine(dataDir, "enhanced");
        using var manager = new FF16PackManager(NullLoggerFactory.Instance);
        manager.Open(enhancedDir, PackKeyStore.FFT_IVALICE_CODENAME);

        bool HasPackedFile(string pack, string path) =>
            manager.PackFiles.TryGetValue(pack, out var info) &&
            info.Files.ContainsKey(path);

        byte[] jobBytes = HasPackedFile("modded.en", "nxd/job.en.nxd")
            ? manager.GetFileDataBytesFromPack("nxd/job.en.nxd", "modded.en")
            : manager.GetFileDataBytes("nxd/job.en.nxd");
        byte[] typeBytes = HasPackedFile("modded", "nxd/jobtype.nxd")
            ? manager.GetFileDataBytesFromPack("nxd/jobtype.nxd", "modded")
            : manager.GetFileDataBytes("nxd/jobtype.nxd");

        var jobs = new NexDataFile();
        jobs.Read(jobBytes);
        var types = new NexDataFile();
        types.Read(typeBytes);

        NexTableLayout jobLayout = TableMappingReader.ReadTableLayout(
            "job", gameVersion, "ffto");
        NexTableLayout typeLayout = TableMappingReader.ReadTableLayout(
            "jobtype", gameVersion, "ffto");

        Console.WriteLine("ID|Name|JobType|Command|MaleIcon|FemaleIcon");
        foreach (NexRowInfo info in jobs.RowManager!.GetAllRowInfos()
                     .OrderBy(x => x.Key))
        {
            List<object> cells = NexUtils.ReadRow(
                jobLayout, jobs.Buffer!, info.RowDataOffset);
            uint typeId = Convert.ToUInt32(cells[6]);
            int male = 0;
            int female = 0;

            if (types.RowManager!.TryGetRowInfo(
                    out NexRowInfo? typeInfo, typeId))
            {
                List<object> typeCells = NexUtils.ReadRow(
                    typeLayout, types.Buffer!, typeInfo.RowDataOffset);
                male = Convert.ToInt32(typeCells[2]);
                female = Convert.ToInt32(typeCells[3]);
            }

            string name = Convert.ToString(cells[2]) ?? "";
            name = name.Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
            Console.WriteLine(
                $"0x{info.Key:X2}|{name}|0x{typeId:X2}|" +
                $"0x{Convert.ToUInt32(cells[8]):X2}|{male}|{female}");
        }

        Console.WriteLine();
        Console.WriteLine("Detailed Job rows used by the generic-class test:");
        uint[] diagnosticIds = [0x1E, 0x32, 0x34, 0xA0, 0xA1];
        foreach (uint id in diagnosticIds)
        {
            if (!jobs.RowManager!.TryGetRowInfo(out NexRowInfo? info, id))
                continue;

            List<object> cells = NexUtils.ReadRow(
                jobLayout, jobs.Buffer!, info.RowDataOffset);
            Console.WriteLine($"[Job 0x{id:X2}]");
            int columnIndex = 0;
            foreach (string columnName in jobLayout.Columns.Keys)
            {
                string value = Convert.ToString(cells[columnIndex]) ?? "";
                value = value.Replace("\r", "\\r").Replace("\n", "\\n");
                Console.WriteLine($"  {columnName}={value}");
                ++columnIndex;
            }
        }

        return 0;
    }
}
