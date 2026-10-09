using FF16Tools.Files.Nex;
using FF16Tools.Files.Nex.Entities;
using FF16Tools.Pack;
using FF16Tools.Pack.Crypto;

using Microsoft.Extensions.Logging.Abstractions;

internal static class JobVisualVerifier
{
    public static int Verify(string dataDir, Version gameVersion)
    {
        string enhancedDir = Path.Combine(dataDir, "enhanced");
        if (!Directory.Exists(enhancedDir))
        {
            Console.Error.WriteLine(
                "[FFTModPacker:GenericJobs] data/enhanced nao encontrada.");
            return 2;
        }

        using var manager = new FF16PackManager(NullLoggerFactory.Instance);
        manager.Open(enhancedDir, PackKeyStore.FFT_IVALICE_CODENAME);

        if (!manager.PackFiles.ContainsKey("modded.en"))
        {
            Console.Error.WriteLine(
                "[FFTModPacker:GenericJobs] modded.en ausente.");
            return 3;
        }

        byte[] bytes = manager.GetFileDataBytesFromPack(
            "nxd/job.en.nxd", "modded.en");
        var table = new NexDataFile();
        table.Read(bytes);
        NexTableLayout layout = TableMappingReader.ReadTableLayout(
            "job", gameVersion, "ffto");

        int nameIndex = layout.Columns.Keys
            .Select((name, index) => (name, index))
            .First(x => x.name.Equals(
                "Name", StringComparison.OrdinalIgnoreCase)).index;
        int descriptionIndex = layout.Columns.Keys
            .Select((name, index) => (name, index))
            .First(x => x.name.Equals(
                "Description", StringComparison.OrdinalIgnoreCase)).index;

        foreach (uint id in new uint[] { 0xA0, 0xA1 })
        {
            if (!table.RowManager!.TryGetRowInfo(out NexRowInfo? info, id))
            {
                Console.Error.WriteLine(
                    $"[FFTModPacker:GenericJobs] Job 0x{id:X2} ausente.");
                return 4;
            }

            List<object> cells = NexUtils.ReadRow(
                layout, table.Buffer!, info.RowDataOffset);
            string name = Convert.ToString(cells[nameIndex]) ?? string.Empty;
            string description = Convert.ToString(cells[descriptionIndex]) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(description))
            {
                Console.Error.WriteLine(
                    $"[FFTModPacker:GenericJobs] Job 0x{id:X2} sem texto.");
                return 5;
            }

            Console.WriteLine(
                $"[FFTModPacker:GenericJobs] Job 0x{id:X2}: {name}");
        }

        Console.WriteLine(
            "[FFTModPacker:GenericJobs] A0/A1 vieram diretamente do mod original.");
        return 0;
    }
}
