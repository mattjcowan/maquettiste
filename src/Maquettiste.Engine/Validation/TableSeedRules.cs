using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The rules of a seed whose target is a table (2026-10-07, table seeds): MQ7107 (a table file and its own columns), MQ7108 (no
/// column the database fills), MQ7109 (each cell fits its column), MQ7111 (a foreign key names a seeded row), MQ7112 (one row per key),
/// MQ7113 (one source for a table's rows) and MQ7114 (a row key). Cells are column values: a text column takes any scalar (a number
/// or a boolean read from a rows file stands for its text), numbers fit their type, a NOT NULL column without a default needs a value.
/// CHECK constraints are left to the database.
/// </summary>
internal static class TableSeedRules
{
    private static readonly HashSet<string> Integers = new(["int16", "int32", "int64"], StringComparer.Ordinal);
    private static readonly HashSet<string> Texts = new(["string", "text"], StringComparer.Ordinal);

    private static string Str(int i) => i.ToString(CultureInfo.InvariantCulture);

    public static void Check(ValidationContext context, Seed seed, Table table, Report report)
    {
        var model = context.Model;
        if (table.Origin == TableOrigin.Synthesized)
        {
            report.Add("MQ7107", $"Seed '{seed.Name}' targets '{table.Name}', a projected table's overlay: seed a table file of the database (store the table as a file first).", "/target");
            return;
        }

        var columns = new Column?[seed.Columns.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var k = 0; k < seed.Columns.Count; k++)
        {
            var id = seed.Columns[k];
            var pointer = "/columns/" + Str(k);
            if (!seen.Add(id))
            {
                report.Add("MQ7107", $"Seed '{seed.Name}' lists the column '{id}' twice.", pointer);
                continue;
            }

            if (table.Columns.FirstOrDefault(c => c.Id == id) is not { } column)
            {
                report.Add("MQ7107", $"The column '{id}' is not a column of table '{table.Name}'; remove the stale column.", pointer);
                continue;
            }

            if (column.Identity is not null || column.Generated is not null || column.Computed is not null)
            {
                report.Add("MQ7108", $"Column '{column.Name}' of table '{table.Name}' is filled by the database ({(column.Computed is not null ? "computed" : "identity or generated")}); leave it out of the seed.", pointer);
                continue;
            }

            columns[k] = column;
        }

        // Columns the seed leaves out need a default or NULL.
        foreach (var column in table.Columns)
        {
            if (seen.Contains(column.Id) || column.Nullable != false || column.Default is not null || column.DefaultSql.Count > 0
                || column.Identity is not null || column.Generated is not null || column.Computed is not null || column.Sequence is not null)
                continue;
            report.Add("MQ7109", $"Seed '{seed.Name}' has no column for '{column.Name}', which is NOT NULL without a default.", "/columns");
        }

        // The row key: the unique constraint named, else the primary key.
        IReadOnlyList<string>? keyIds = null;
        if (seed.Key is { } keyId)
        {
            if (table.Uniques.FirstOrDefault(u => u.Id == keyId) is { } unique)
                keyIds = unique.Columns;
            else
                report.Add("MQ7114", $"The row key '{keyId}' is not a unique constraint of table '{table.Name}'.", "/key");
        }
        else if (table.PrimaryKey is { } primaryKey)
        {
            keyIds = primaryKey.Columns;
        }
        else
        {
            report.Add("MQ7114", $"Table '{table.Name}' has no primary key: name a unique constraint as the seed's row key (key), so a row is found again on the next run.", "/key");
        }

        var keyPositions = keyIds?.Select(id => seed.Columns.ToList().IndexOf(id)).ToList();
        if (keyIds is not null && keyPositions!.Any(p => p < 0))
            report.Add("MQ7114", $"Seed '{seed.Name}' has no column for every column of its row key.", "/columns");

        var keys = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var r = 0; r < seed.Rows.Count; r++)
        {
            var row = seed.Rows[r];
            var rowPointer = "/rows/" + Str(r);
            for (var k = 0; k < columns.Length; k++)
            {
                if (columns[k] is not { } column)
                    continue;
                var cell = k < row.Values.Count ? row.Values[k] : default;
                if (Fit(cell, column) is { } problem)
                    report.Add("MQ7109", $"Row {row.Id}, column '{column.Name}': {problem}.", rowPointer + "/values/" + Str(k), row.Id);
            }

            if (keyPositions is not null && keyPositions.All(p => p >= 0))
            {
                var parts = keyPositions.Select(p => p < row.Values.Count ? row.Values[p] : default).ToList();
                if (parts.Any(v => v.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null))
                {
                    report.Add("MQ7114", $"Row {row.Id} has no value for its row key.", rowPointer, row.Id);
                    continue;
                }

                var key = string.Join("\u0000", parts.Select(v => v.GetRawText()));
                if (!keys.TryAdd(key, r))
                    report.Add("MQ7112", $"Row {row.Id} repeats the row key of row {seed.Rows[keys[key]].Id}.", rowPointer, row.Id);
            }
        }

        CheckOtherSeeds(context, seed, table, keyIds, report);
        CheckForeignKeys(context, seed, table, report);
        CheckOneSource(context, seed, table, report);
    }

    /// <summary>Why a cell does not fit its column, or <see langword="null"/>.</summary>
    private static string? Fit(JsonElement cell, Column column)
    {
        var type = column.Type ?? "string";
        if (cell.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return column.Nullable == false && column.Default is null && column.DefaultSql.Count == 0 ? "NULL in a NOT NULL column without a default" : null;
        if (Texts.Contains(type) || type is "char")
        {
            if (cell.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                return $"a {Kind(cell)} in a text column";
            var text = cell.ValueKind == JsonValueKind.String ? cell.GetString()! : cell.GetRawText();
            return column.Length is { } length && new StringInfo(text).LengthInTextElements > length ? $"{new StringInfo(text).LengthInTextElements} characters, more than its length {length}" : null;
        }

        if (Integers.Contains(type))
        {
            if (cell.ValueKind != JsonValueKind.Number || !cell.TryGetInt64(out var value))
                return $"not an integer ({Kind(cell)})";
            return type switch
            {
                "int16" when value is < short.MinValue or > short.MaxValue => "outside the range of int16",
                "int32" when value is < int.MinValue or > int.MaxValue => "outside the range of int32",
                _ => null,
            };
        }

        if (type is "decimal")
        {
            if (cell.ValueKind != JsonValueKind.Number)
                return $"not a number ({Kind(cell)})";
            var text = cell.GetRawText().TrimStart('-');
            var dot = text.IndexOf('.', StringComparison.Ordinal);
            var whole = (dot < 0 ? text : text[..dot]).TrimStart('0').Length;
            var fraction = dot < 0 ? 0 : text.Length - dot - 1;
            if (column.Scale is { } scale && fraction > scale)
                return $"{fraction} decimals, more than its scale {scale}";
            if (column.Precision is { } precision && whole + Math.Max(fraction, column.Scale ?? 0) > precision)
                return $"more digits than its precision {precision}";
            return null;
        }

        if (type is "float" or "double")
            return cell.ValueKind == JsonValueKind.Number ? null : $"not a number ({Kind(cell)})";
        if (type is "bool")
            return cell.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : $"not true or false ({Kind(cell)})";
        if (type is "uuid")
            return cell.ValueKind == JsonValueKind.String && Guid.TryParse(cell.GetString(), out _) ? null : "not a UUID";
        if (type is "date" or "datetime" or "datetimeoffset" or "time" or "duration" or "binary")
            return cell.ValueKind == JsonValueKind.String ? null : $"not a text value ({Kind(cell)})";
        return null; // json and anything else take any value
    }

    private static string Kind(JsonElement cell) => cell.ValueKind switch
    {
        JsonValueKind.String => "a text",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Array => "a list",
        JsonValueKind.Object => "an object",
        _ => "no value",
    };

    /// <summary>Two seeds of one table with overlapping environments that give the same row key (MQ7112, reported on the later seed by name).</summary>
    private static void CheckOtherSeeds(ValidationContext context, Seed seed, Table table, IReadOnlyList<string>? keyIds, Report report)
    {
        if (keyIds is null)
            return;
        foreach (var other in context.SeedsOf(table.Id).Where(o => o.Id != seed.Id && string.CompareOrdinal(o.Name, seed.Name) < 0))
        {
            if (seed.Environments.Count > 0 && other.Environments.Count > 0 && !seed.Environments.Intersect(other.Environments, StringComparer.Ordinal).Any())
                continue;
            var theirs = KeysOf(other, keyIds);
            for (var r = 0; r < seed.Rows.Count; r++)
            {
                if (KeyOf(seed, seed.Rows[r], keyIds) is { } key && theirs.Contains(key))
                    report.Add("MQ7112", $"Row {seed.Rows[r].Id} repeats a row key of seed '{other.Name}' of the same table.", "/rows/" + Str(r), seed.Rows[r].Id);
            }
        }
    }

    private static string? KeyOf(Seed seed, SeedRow row, IReadOnlyList<string> columnIds)
    {
        var parts = new List<string>(columnIds.Count);
        foreach (var id in columnIds)
        {
            var k = seed.Columns.ToList().IndexOf(id);
            if (k < 0 || k >= row.Values.Count || row.Values[k].ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                return null;
            parts.Add(row.Values[k].GetRawText());
        }

        return string.Join("\u0000", parts);
    }

    private static HashSet<string> KeysOf(Seed seed, IReadOnlyList<string> columnIds) =>
        [.. seed.Rows.Select(r => KeyOf(seed, r, columnIds)).OfType<string>()];

    /// <summary>A foreign key whose columns the seed gives must name a row of the referenced table's seeds, when that table has seeds (MQ7111).</summary>
    private static void CheckForeignKeys(ValidationContext context, Seed seed, Table table, Report report)
    {
        foreach (var fk in table.ForeignKeys)
        {
            if (fk.Columns.Count == 0 || fk.Columns.Any(c => !seed.Columns.Contains(c, StringComparer.Ordinal)))
                continue;
            if (context.Model.GetDocument(fk.ReferencesTable)?.Element is not Table referenced)
                continue;
            var seeds = context.SeedsOf(referenced.Id);
            if (seeds.Length == 0)
                continue; // the referenced rows come from elsewhere
            IReadOnlyList<string>? referencedColumns = fk.ReferencesColumns is { Count: > 0 } named ? named : referenced.PrimaryKey?.Columns;
            if (referencedColumns is null || referencedColumns.Count != fk.Columns.Count)
                continue;
            var known = new HashSet<string>(seeds.SelectMany(s => KeysOf(s, referencedColumns)), StringComparer.Ordinal);
            for (var r = 0; r < seed.Rows.Count; r++)
            {
                if (KeyOf(seed, seed.Rows[r], fk.Columns) is { } key && !known.Contains(key))
                    report.Add("MQ7111", $"Row {seed.Rows[r].Id}: foreign key '{fk.Name}' names no row of the seeds of table '{referenced.Name}'.", "/rows/" + Str(r), seed.Rows[r].Id);
            }
        }
    }

    /// <summary>A table seeded by its own seeds and by the seeds of an entity bound to it (MQ7113, once per seed).</summary>
    private static void CheckOneSource(ValidationContext context, Seed seed, Table table, Report report)
    {
        foreach (var entity in context.Model.All<Entity>())
        {
            if (!entity.Bindings.Any(b => b.Source == table.Id || b.Write?.Table == table.Id) || context.SeedsOf(entity.Id).Length == 0)
                continue;
            report.Add("MQ7113", $"Table '{table.Name}' also receives the seed rows of entity '{entity.Name}', which is bound to it: give each row one source.", "/target");
        }
    }
}
