using FF16Tools.Files;
using FF16Tools.Files.Nex;
using FF16Tools.Files.Nex.Entities;
using FF16Tools.Files.Nex.Managers;

internal static class JobNexCloneBuilder
{
    public sealed record Directive(
        string ModId,
        uint TargetId,
        uint SourceId,
        uint? OverviewSourceId,
        uint SourceJobTypeId);

    private static readonly HashSet<string> OverviewColumns =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Name",
            "Description",
            "jobcommand+Id",
            "TexturePartsIndex",
            "uijobabilityhelp+Id",
            "Unknown15"
        };

    public static IReadOnlyDictionary<uint, uint> ResolveSourceJobTypes(
        byte[] jobBytes,
        Version gameVersion,
        IReadOnlyList<Directive> directives)
    {
        NexTableLayout layout = TableMappingReader.ReadTableLayout(
            "job", gameVersion, "ffto");
        var table = new NexDataFile();
        table.Read(jobBytes);

        int jobTypeColumn = layout.Columns.Keys
            .Select((name, index) => (name, index))
            .First(x => x.name.Equals(
                "jobtype+Id",
                StringComparison.OrdinalIgnoreCase)).index;

        var result = new Dictionary<uint, uint>();
        foreach (Directive directive in directives)
        {
            if (!table.RowManager!.TryGetRowInfo(
                    out NexRowInfo? source, directive.SourceId))
            {
                throw new InvalidDataException(
                    $"job: fonte 0x{directive.SourceId:X2} ausente.");
            }

            List<object> cells = NexUtils.ReadRow(
                layout, table.Buffer!, source.RowDataOffset);
            result[directive.TargetId] = Convert.ToUInt32(cells[jobTypeColumn]);
        }

        return result;
    }

    public static void Build(
        byte[] originalBytes,
        string tableName,
        Version gameVersion,
        IReadOnlyList<Directive> directives,
        string outputPath)
    {
        NexTableLayout layout = TableMappingReader.ReadTableLayout(
            tableName,
            gameVersion,
            "ffto");

        var table = new NexDataFile();
        table.Read(originalBytes);

        var builder = new NexDataFileBuilder(layout);

        if (table.Type == NexTableType.TripleKeyed)
        {
            var manager = (NexTripleKeyedRowTableManager)table.RowManager!;

            foreach (var set in manager.GetRowSets())
            {
                builder.AddTripleKeyedSet(set.Key);

                foreach (var subset in set.Value.SubSets)
                    builder.AddTripleKeyedSubset(set.Key, subset.Key);
            }
        }
        else if (table.Type == NexTableType.DoubleKeyed)
        {
            var manager = (NexDoubleKeyedRowTableManager)table.RowManager!;

            foreach (var set in manager.GetRowSets())
                builder.AddDoubleKeyedSet(set.Key);
        }

        foreach (NexRowInfo row in table.RowManager!.GetAllRowInfos())
        {
            builder.AddRow(
                row.Key,
                row.Key2,
                row.Key3,
                NexUtils.ReadRow(layout, table.Buffer!, row.RowDataOffset));
        }

        int jobTypeColumn = -1;

        if (tableName.Equals("job", StringComparison.OrdinalIgnoreCase))
        {
            int index = 0;

            foreach (string column in layout.Columns.Keys)
            {
                if (column.Equals("jobtype+Id", StringComparison.OrdinalIgnoreCase))
                {
                    jobTypeColumn = index;
                    break;
                }

                ++index;
            }

            if (jobTypeColumn < 0)
                throw new InvalidDataException("Coluna jobtype+Id nao encontrada.");
        }

        foreach (Directive directive in directives)
        {
            uint sourceId = tableName.Equals(
                "jobtype", StringComparison.OrdinalIgnoreCase)
                ? directive.SourceJobTypeId
                : directive.SourceId;

            if (!table.RowManager.TryGetRowInfo(
                    out NexRowInfo? source,
                    sourceId))
            {
                throw new InvalidDataException(
                    $"{tableName}: fonte 0x{sourceId:X2} ausente.");
            }

            List<object> cells = NexUtils.ReadRow(
                layout,
                table.Buffer!,
                source.RowDataOffset);

            if (tableName.Equals("job", StringComparison.OrdinalIgnoreCase) &&
                directive.OverviewSourceId is uint overviewSourceId)
            {
                if (!table.RowManager.TryGetRowInfo(
                        out NexRowInfo? overviewSource,
                        overviewSourceId))
                {
                    throw new InvalidDataException(
                        $"job: fonte de Overview 0x{overviewSourceId:X2} ausente.");
                }

                List<object> overviewCells = NexUtils.ReadRow(
                    layout,
                    table.Buffer!,
                    overviewSource.RowDataOffset);
                int columnIndex = 0;
                foreach (string columnName in layout.Columns.Keys)
                {
                    if (OverviewColumns.Contains(columnName))
                        cells[columnIndex] = overviewCells[columnIndex];
                    ++columnIndex;
                }
            }

            // JobType is not an extensible identity in every native graphics
            // path. The table reader accepts a new row, but several render and
            // formation routines still use this value as a bounded native
            // index. Keep the custom Job id while routing presentation through
            // the source generic JobType (Squire = 1).
            if (jobTypeColumn >= 0)
                cells[jobTypeColumn] = unchecked((int)directive.SourceJobTypeId);

            builder.RemoveRow(directive.TargetId, 0, 0);
            builder.AddRow(
                directive.TargetId,
                0,
                0,
                cells,
                overwriteIfExists: true);

            Console.WriteLine(
                $"[FFTModPacker:JobClone] {tableName}: " +
                $"0x{directive.TargetId:X2} <- 0x{sourceId:X2} " +
                $"({directive.ModId})");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var output = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        builder.Write(output);
    }
}
