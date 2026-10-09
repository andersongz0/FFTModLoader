using FF16Tools.Files;
using FF16Tools.Files.Nex;
using FF16Tools.Files.Nex.Entities;
using FF16Tools.Files.Nex.Managers;

internal static class NexMergeEngine
{
    public sealed record Input(string ModId, string Path);

    public static void Merge(
        byte[] originalBytes,
        string tableName,
        Version gameVersion,
        IReadOnlyList<Input> inputs,
        string outputPath)
    {
        NexTableLayout layout =
            TableMappingReader.ReadTableLayout(
                tableName,
                gameVersion,
                "ffto"
            );

        var original = new NexDataFile();
        original.Read(originalBytes);

        var builder = new NexDataFileBuilder(layout);

        if (original.Type == NexTableType.TripleKeyed)
        {
            var manager =
                (NexTripleKeyedRowTableManager)original.RowManager!;

            foreach (var set in manager.GetRowSets())
            {
                builder.AddTripleKeyedSet(set.Key);

                foreach (var subset in set.Value.SubSets)
                    builder.AddTripleKeyedSubset(set.Key, subset.Key);
            }
        }
        else if (original.Type == NexTableType.DoubleKeyed)
        {
            var manager =
                (NexDoubleKeyedRowTableManager)original.RowManager!;

            foreach (var set in manager.GetRowSets())
                builder.AddDoubleKeyedSet(set.Key);
        }

        foreach (NexRowInfo row in original.RowManager!.GetAllRowInfos())
        {
            List<object> cells =
                NexUtils.ReadRow(
                    layout,
                    original.Buffer!,
                    row.RowDataOffset
                );

            builder.AddRow(
                row.Key,
                row.Key2,
                row.Key3,
                cells
            );
        }

        foreach (Input input in inputs)
        {
            var mod = NexDataFile.FromFile(input.Path);

            ApplyDiff(
                input.ModId,
                layout,
                original,
                mod,
                builder
            );
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(outputPath)!
        );

        using var stream =
            new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None
            );

        builder.Write(stream);
    }

    private static void ApplyDiff(
        string modId,
        NexTableLayout layout,
        NexDataFile original,
        NexDataFile mod,
        NexDataFileBuilder builder
    )
    {
        int removed = 0;
        int changed = 0;
        int inserted = 0;

        foreach (NexRowInfo originalInfo in
                 original.RowManager!.GetAllRowInfos())
        {
            if (!mod.RowManager!.TryGetRowInfo(
                    out NexRowInfo? modInfo,
                    originalInfo.Key,
                    originalInfo.Key2,
                    originalInfo.Key3))
            {
                builder.RemoveRow(
                    originalInfo.Key,
                    originalInfo.Key2,
                    originalInfo.Key3
                );

                ++removed;
                continue;
            }

            List<object> oldRow =
                NexUtils.ReadRow(
                    layout,
                    original.Buffer!,
                    originalInfo.RowDataOffset
                );

            List<object> newRow =
                NexUtils.ReadRow(
                    layout,
                    mod.Buffer!,
                    modInfo.RowDataOffset
                );

            var current = builder.GetRow(
                originalInfo.Key,
                originalInfo.Key2,
                originalInfo.Key3
            );

            if (current is null)
                continue;

            int count = Math.Min(oldRow.Count, newRow.Count);

            for (int i = 0; i < count; ++i)
            {
                var column = layout.Columns.ElementAt(i);

                if (column.Key.StartsWith(
                        "Comment",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (CellsEqual(
                        layout,
                        column.Value,
                        oldRow[i],
                        newRow[i]))
                {
                    continue;
                }

                current.Cells[i] = newRow[i];
                ++changed;
            }
        }

        foreach (NexRowInfo newInfo in
                 mod.RowManager!.GetAllRowInfos())
        {
            if (original.RowManager!.TryGetRowInfo(
                    out _,
                    newInfo.Key,
                    newInfo.Key2,
                    newInfo.Key3))
            {
                continue;
            }

            List<object> row =
                NexUtils.ReadRow(
                    layout,
                    mod.Buffer!,
                    newInfo.RowDataOffset
                );

            builder.AddRow(
                newInfo.Key,
                newInfo.Key2,
                newInfo.Key3,
                row,
                overwriteIfExists: true
            );

            ++inserted;
        }

        Console.WriteLine(
            $"[FFTModPacker:NEX] {modId}: " +
            $"removed={removed}, cells={changed}, inserted={inserted}"
        );
    }

    private static bool CellsEqual(
        NexTableLayout layout,
        NexStructColumn column,
        object left,
        object right
    )
    {
        switch (column.Type)
        {
            case NexColumnType.Byte:
                return (byte)left == (byte)right;
            case NexColumnType.SByte:
                return (sbyte)left == (sbyte)right;
            case NexColumnType.Short:
                return (short)left == (short)right;
            case NexColumnType.UShort:
                return (ushort)left == (ushort)right;
            case NexColumnType.Int:
                return (int)left == (int)right;
            case NexColumnType.UInt:
            case NexColumnType.HexUInt:
                return (uint)left == (uint)right;
            case NexColumnType.Float:
                return (float)left == (float)right;
            case NexColumnType.Double:
                return (double)left == (double)right;
            case NexColumnType.Int64:
                // FF16Tools has used both signed/unsigned 64-bit backing
                // values across revisions; compare without assuming the box type.
                return Convert.ToUInt64(left) == Convert.ToUInt64(right);
            case NexColumnType.String:
                return string.Equals((string)left, (string)right);
            case NexColumnType.NexUnionKey32:
            case NexColumnType.NexUnionKey16:
            {
                var a = (NexUnionKey)left;
                var b = (NexUnionKey)right;
                return a.Type == b.Type && a.Value == b.Value;
            }
            case NexColumnType.ByteArray:
                return ((byte[])left).SequenceEqual((byte[])right);
            case NexColumnType.ShortArray:
                return ((short[])left).SequenceEqual((short[])right);
            case NexColumnType.IntArray:
                return ((int[])left).SequenceEqual((int[])right);
            case NexColumnType.UIntArray:
                return ((uint[])left).SequenceEqual((uint[])right);
            case NexColumnType.FloatArray:
                return ((float[])left).SequenceEqual((float[])right);
            case NexColumnType.StringArray:
                return ((string[])left).SequenceEqual((string[])right);
            case NexColumnType.NexUnionKey32Array:
            {
                var a = (NexUnionKey[])left;
                var b = (NexUnionKey[])right;

                if (a.Length != b.Length)
                    return false;

                for (int i = 0; i < a.Length; ++i)
                {
                    if (a[i].Type != b[i].Type ||
                        a[i].Value != b[i].Value)
                    {
                        return false;
                    }
                }

                return true;
            }
            case NexColumnType.CustomStructArray:
            {
                var a = (object[])left;
                var b = (object[])right;

                if (a.Length != b.Length)
                    return false;

                NexTableColumnStruct fields =
                    layout.CustomStructDefinitions[
                        column.StructTypeName!
                    ];

                for (int i = 0; i < a.Length; ++i)
                {
                    var sa = (object[])a[i];
                    var sb = (object[])b[i];

                    if (sa.Length != sb.Length)
                        return false;

                    for (int j = 0;
                         j < fields.Columns.Count;
                         ++j)
                    {
                        if (!CellsEqual(
                                layout,
                                fields.Columns[j],
                                sa[j],
                                sb[j]))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }
            default:
                return Equals(left, right);
        }
    }
}
