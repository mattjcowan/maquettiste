using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The rules of a seed whose target is a table (2026-10-07, table seeds): MQ7107 (a table file and its own columns), MQ7108 (no
/// column the database or the engine fills), MQ7109 (each cell fits its column), MQ7111 (a foreign key names a seeded row, in every environment the
/// row is loaded in), MQ7112 (one row per key), MQ7113 (one source for a table's rows), MQ7114 (a row key), MQ7121 (every seed of
/// the table finds its rows by the same key) and MQ7014 (a cell of a column typed by an enum or a reference type holds one of its
/// stored values or codes, a code wherever the row is loaded; next-phase-2-design.md section 3.2), or MQ7019 in its place for a former
/// code of the reference type (section 3.5). A stable id column takes no cell
/// (MQ7108), and the first seed's row key can derive stable ids (MQ7125; section 3.4). A foreign key of the table may stand for its
/// columns (section 3.4, decisions 1 to 4): the entry's cell names the referenced row by that table's row key in seed terms
/// (<see cref="NaturalRowsOf"/>), its shape and parts fit that key (MQ7109), it finds a row wherever the row is loaded (MQ7111), the
/// entry is the target's, alone for its columns, names a table with rows, and the row key holds all its columns or none (MQ7126),
/// and outside PostgreSQL a value the database fills cannot be looked up (MQ4056). The rows of the reference types
/// bound to a table count as its rows for MQ7111 and MQ7112 (2026-10-11). The seeds of a table come in merge order
/// (<see cref="Resolution.SeedOrder"/>: package, name, id), which decides which of two is the later one a finding goes on
/// (next-phase-1-design.md section 4.3). Cells are column values: a text column
/// takes any scalar (a number or a boolean read from a rows file stands for its text), numbers fit their type, a NOT NULL column
/// without a default needs a value. CHECK constraints are left to the database. The columns, primary key and uniques a table inherits
/// from its stereotypes count as its own, by keyed id (next-phase-2-design.md section 3.1).
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

        // The members the table inherits from its stereotypes count as its own, by keyed id (next-phase-2-design.md section 3.1).
        var members = TableMembers.Of(model, table);
        table = MemberRules.WithMembers(model, table);

        var columns = new Column?[seed.Columns.Count];
        var entries = new ForeignKey?[seed.Columns.Count];
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
                // A foreign key of the table stands for its columns (next-phase-2-design.md section 3.4, decision 1).
                if (table.ForeignKeys.FirstOrDefault(f => f.Id == id) is { } fk)
                {
                    entries[k] = fk;
                    continue;
                }

                if (OtherForeignKey(context, id, table) is { } other)
                {
                    report.Add("MQ7126", $"'{id}' is foreign key '{other.Key}' of table '{other.Table}', not of table '{table.Name}': a seed names foreign keys of its own target only.", pointer);
                    continue;
                }

                var hint = MemberRules.KeyedHint(members, id);
                report.Add("MQ7107", hint.Length > 0 ? $"The column '{id}' is not a column of table '{table.Name}'.{hint}"
                    : $"'{id}' is not a column or foreign key of table '{table.Name}'; remove the stale column.", pointer);
                continue;
            }

            if (column.Identity is not null || column.Generated is not null || column.Computed is not null)
            {
                report.Add("MQ7108", $"Column '{column.Name}' of table '{table.Name}' is filled by the database ({(column.Computed is not null ? "computed" : "identity or generated")}); leave it out of the seed.", pointer);
                continue;
            }

            if (column.StableId)
            {
                report.Add("MQ7108", $"Column '{column.Name}' of table '{table.Name}' takes a stable id, which the engine derives from the row key; leave it out of the seed.", pointer);
                continue;
            }

            columns[k] = column;
        }

        // A foreign-key entry's columns count as given; its natural key names a row of the referenced table's rows (MQ7126 when it has
        // none), and outside PostgreSQL a value the database fills there cannot be looked up (MQ4056).
        var natural = new NaturalRows?[seed.Columns.Count];
        for (var k = 0; k < entries.Length; k++)
        {
            if (entries[k] is not { } fk)
                continue;
            var pointer = "/columns/" + Str(k);
            var name = fk.Name ?? fk.Id;
            seen.UnionWith(fk.Columns);
            if (fk.Columns.FirstOrDefault(c => seed.Columns.Contains(c, StringComparer.Ordinal)) is { } own)
                report.Add("MQ7126", $"Seed '{seed.Name}' lists foreign key '{name}' beside its own column '{table.Columns.FirstOrDefault(c => c.Id == own)?.Name ?? own}': give the key's columns either through the foreign key or one by one.", pointer);
            if (NaturalRowsOf(context, fk.ReferencesTable) is not { } rows)
            {
                var referenced = context.Model.GetDocument(fk.ReferencesTable)?.Element.Name ?? fk.ReferencesTable;
                report.Add("MQ7126", $"Foreign key '{name}' references table '{referenced}', which has no rows in the model (no table seed and no reference type bound to it), so a natural key cannot name one: list the key's columns and give their values.", pointer);
                continue;
            }

            natural[k] = rows;
            if (rows.KeyIds.Count == 0)
                report.Add("MQ7126", $"Foreign key '{name}' references table '{rows.Table.Name}', whose rows have no row key (no primary key, and no seed names a unique constraint), so a natural key cannot name one.", pointer);
            if (context.Model.GetDocument(table.Database)?.Element is Database { Dialect: not Dialect.PostgreSql } database
                && FilledReferencedValue(rows, fk) is { } filled)
            {
                report.Add("MQ4056", $"Foreign key '{name}' names rows of table '{rows.Table.Name}' whose {filled}, which {DialectInfo.Name(database.Dialect)} (database '{database.Name}') cannot look up in a seed script: the script writes NULL with a TODO comment for it.", pointer);
            }
        }

        // Columns the seed leaves out need a default or NULL.
        foreach (var column in table.Columns)
        {
            if (seen.Contains(column.Id) || column.Nullable != false || column.Default is not null || column.DefaultSql.Count > 0
                || column.Identity is not null || column.Generated is not null || column.Computed is not null || column.Sequence is not null
                || column.StableId)
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
                report.Add("MQ7114", $"The row key '{keyId}' is not a unique constraint of table '{table.Name}'.{MemberRules.KeyedUniqueHint(members, keyId)}", "/key");
        }
        else if (table.PrimaryKey is { } primaryKey)
        {
            keyIds = primaryKey.Columns;
        }
        else
        {
            report.Add("MQ7114", $"Table '{table.Name}' has no primary key: name a unique constraint as the seed's row key (key), so a row is found again on the next run.", "/key");
        }

        // A row key that holds some but not all columns of a foreign-key entry cannot be given in seed terms (MQ7126).
        for (var k = 0; keyIds is not null && k < entries.Length; k++)
        {
            if (entries[k] is { } fk && fk.Columns.Any(keyIds.Contains) && !fk.Columns.All(keyIds.Contains))
                report.Add("MQ7126", $"The row key of seed '{seed.Name}' holds some but not all columns of foreign key '{fk.Name ?? fk.Id}': a key column the entry fills takes the entry's cell, so the key holds all of its columns or none.", "/columns/" + Str(k));
        }

        CheckStableIdKey(context, seed, table, keyIds, report);
        var keyPositions = keyIds?.Select(id => Position(seed, table, id)).ToList();
        if (keyIds is not null && keyPositions!.Any(p => p < 0))
            report.Add("MQ7114", $"Seed '{seed.Name}' has no column for every column of its row key.", "/columns");

        var keys = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var r = 0; r < seed.Rows.Count; r++)
        {
            var row = seed.Rows[r];
            var rowPointer = "/rows/" + Str(r);
            for (var k = 0; k < columns.Length; k++)
            {
                var cell = k < row.Values.Count ? row.Values[k] : default;
                if (entries[k] is { } fk)
                {
                    CheckEntryCell(seed, table, fk, natural[k], row, cell, rowPointer + "/values/" + Str(k), report);
                    continue;
                }

                if (columns[k] is not { } column)
                    continue;
                if (Fit(cell, column) is { } problem)
                    report.Add("MQ7109", $"Row {row.Id}, column '{column.Name}': {problem}.", rowPointer + "/values/" + Str(k), row.Id);
                if (ColumnTypeRules.FormerCodeProblem(context, column, cell, seed) is { } former)
                    report.Add("MQ7019", $"Row {row.Id}, column '{column.Name}' names a former code: {former}.", rowPointer + "/values/" + Str(k), row.Id);
                else if (ColumnTypeRules.ValueProblem(context, column, cell, seed) is { } wrong)
                    report.Add("MQ7014", $"Row {row.Id}, column '{column.Name}': {wrong}.", rowPointer + "/values/" + Str(k), row.Id);
            }

            if (keyPositions is not null && keyPositions.All(p => p >= 0))
            {
                var parts = keyPositions.Select(p => p < row.Values.Count ? row.Values[p] : default).ToList();
                if (parts.Any(v => v.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null))
                {
                    report.Add("MQ7114", $"Row {row.Id} has no value for its row key.", rowPointer, row.Id);
                    continue;
                }

                var key = string.Join("\u0000", parts.Select(Canonical));
                if (!keys.TryAdd(key, r))
                    report.Add("MQ7112", $"Row {row.Id} repeats the row key of row {seed.Rows[keys[key]].Id}.", rowPointer, row.Id);
            }
        }

        CheckKeyAgreement(context, seed, table, report);
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
        if (BuiltinTypes.IsRange(type))
            return cell.ValueKind == JsonValueKind.String ? null : $"not a text value, the {type}'s text form ({Kind(cell)})";
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

    /// <summary>
    /// MQ7125 (next-phase-2-design.md section 3.4): the first seed of a table in merge order decides its rows' key, so on that seed,
    /// when the table takes stable ids, the key may hold neither a stable id column nor a value the database fills. Reported at
    /// <c>/key</c>, or at <c>/target</c> when the seed names none (the key is then the primary key).
    /// </summary>
    private static void CheckStableIdKey(ValidationContext context, Seed seed, Table table, IReadOnlyList<string>? keyIds, Report report)
    {
        if (keyIds is null || !table.Columns.Any(c => c.StableId) || context.SeedsOf(table.Id) is not [var first, ..] || first.Id != seed.Id
            || (PhysicalRules.StableIdKeyProblem(table, keyIds) ?? EntryKeyProblem(context, seed, table, keyIds)) is not { } problem)
            return;
        report.Add("MQ7125", $"Table '{table.Name}' takes stable ids from the row key of seed '{seed.Name}', which comes first, but {problem}: name a unique constraint of values the rows give as the seed's key.",
            seed.Key is null ? "/target" : "/key");
    }

    /// <summary>
    /// Why a row key cannot derive stable ids through a foreign-key entry (MQ7125, decision 9): it holds a column that an entry of the
    /// seed fills with a value the database fills in the referenced table; <see langword="null"/> when none does.
    /// </summary>
    private static string? EntryKeyProblem(ValidationContext context, Seed seed, Table table, IReadOnlyList<string> keyIds)
    {
        foreach (var id in seed.Columns)
        {
            if (table.ForeignKeys.FirstOrDefault(f => f.Id == id) is not { } fk || fk.Columns.FirstOrDefault(keyIds.Contains) is not { } held
                || NaturalRowsOf(context, fk.ReferencesTable) is not { } rows || FilledReferencedValue(rows, fk) is not { } filled)
                continue;
            return $"its row key holds column '{table.Columns.FirstOrDefault(c => c.Id == held)?.Name ?? held}', which foreign key '{fk.Name ?? fk.Id}' fills from rows of table '{rows.Table.Name}' whose {filled} and the model never knows";
        }

        return null;
    }

    /// <summary>
    /// MQ7121: the first seed of a table in merge order decides the table's row key (its <c>key</c>, else the primary key), as the
    /// resolver does, so a later seed that names another <c>key</c>, or none beside a seed that names one, is reported at its
    /// <c>/key</c>, or at <c>/target</c> when it names none. Environments do not matter: a table has one row key.
    /// </summary>
    private static void CheckKeyAgreement(ValidationContext context, Seed seed, Table table, Report report)
    {
        var seeds = context.SeedsOf(table.Id);
        if (seeds.Length < 2 || seeds[0].Id == seed.Id || string.Equals(seeds[0].Key, seed.Key, StringComparison.Ordinal))
            return;
        var first = seeds[0];
        report.Add(
            "MQ7121",
            $"Seed '{seed.Name}' finds its rows by {KeyName(table, seed.Key)}, but seed '{first.Name}' of table '{table.Name}', which comes first, finds them by {KeyName(table, first.Key)}{ReferenceDataRules.BothPackages(context, seed, first)}: the first seed decides the table's row key, so give every seed of the table the same key.",
            seed.Key is null ? "/target" : "/key");
    }

    private static string KeyName(Table table, string? key) => key is null
        ? "the primary key"
        : table.Uniques.FirstOrDefault(u => u.Id == key) is { } unique ? $"the unique constraint '{unique.Name ?? unique.Id}'" : $"the key '{key}'";

    /// <summary>
    /// Two seeds of one table with overlapping environments that give the same row key (MQ7112, reported on the later seed in merge
    /// order, naming both packages when they differ), and a row whose key is a row of a reference type bound to the table where both
    /// are loaded (reported on the table seed).
    /// </summary>
    private static void CheckOtherSeeds(ValidationContext context, Seed seed, Table table, IReadOnlyList<string>? keyIds, Report report)
    {
        if (keyIds is null)
            return;
        foreach (var (listSeed, keys) in ReferenceBindingRules.KeysOf(context, table, keyIds))
        {
            if (!Overlap(seed, listSeed))
                continue;
            var type = context.Model.GetDocument(listSeed.Target)?.Element.Name ?? listSeed.Target;
            for (var r = 0; r < seed.Rows.Count; r++)
            {
                if (KeyOf(seed, seed.Rows[r], keyIds, table) is { } key && keys.Contains(key))
                    report.Add("MQ7112", $"Row {seed.Rows[r].Id} repeats the row key of a row of reference type '{type}' (seed '{listSeed.Name}'), which is bound to table '{table.Name}'.", "/rows/" + Str(r), seed.Rows[r].Id);
            }
        }

        foreach (var other in context.SeedsOf(table.Id).TakeWhile(o => o.Id != seed.Id))
        {
            if (!Overlap(seed, other))
                continue;
            var theirs = KeysOf(context, other, keyIds, table);
            for (var r = 0; r < seed.Rows.Count; r++)
            {
                if (KeyOf(seed, seed.Rows[r], keyIds, table) is { } key && theirs.Contains(key))
                    report.Add("MQ7112", $"Row {seed.Rows[r].Id} repeats a row key of seed '{other.Name}' of the same table{ReferenceDataRules.BothPackages(context, seed, other)}.", "/rows/" + Str(r), seed.Rows[r].Id);
            }
        }
    }

    /// <summary>Whether two seeds are loaded together somewhere: either is for every environment, or they share one.</summary>
    internal static bool Overlap(Seed a, Seed b) =>
        a.Environments.Count == 0 || b.Environments.Count == 0 || a.Environments.Intersect(b.Environments, StringComparer.Ordinal).Any();

    /// <summary>
    /// A row's key over some columns as raw JSON text joined by U+0000, or <see langword="null"/> when a cell is missing. With the
    /// seed's <paramref name="table"/>, a column a foreign-key entry fills takes the entry's cell: the key in seed terms (decision 2).
    /// </summary>
    private static string? KeyOf(Seed seed, SeedRow row, IReadOnlyList<string> columnIds, Table? table = null)
    {
        var parts = new List<string>(columnIds.Count);
        foreach (var id in columnIds)
        {
            var k = table is null ? IndexOf(seed.Columns, id) : Position(seed, table, id);
            if (k < 0 || k >= row.Values.Count || row.Values[k].ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                return null;
            parts.Add(Canonical(row.Values[k]));
        }

        return string.Join("\u0000", parts);
    }

    /// <summary>The keys a seed's rows give over some columns; a seed with a rows script gives its scripted rows (<see cref="ValidationContext.RowsOf"/>).</summary>
    private static HashSet<string> KeysOf(ValidationContext context, Seed seed, IReadOnlyList<string> columnIds, Table? table = null) =>
        [.. context.RowsOf(seed).Select(r => KeyOf(seed, r, columnIds, table)).OfType<string>()];

    private static int IndexOf(IReadOnlyList<string> list, string id)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], id, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Where a seed gives a column of its table: the column's own entry, else the foreign-key entry whose key holds it; -1 when it gives
    /// neither.
    /// </summary>
    private static int Position(Seed seed, Table table, string columnId)
    {
        var at = IndexOf(seed.Columns, columnId);
        if (at >= 0)
            return at;
        for (var k = 0; k < seed.Columns.Count; k++)
        {
            if (table.ForeignKeys.FirstOrDefault(f => f.Id == seed.Columns[k]) is { } fk && fk.Columns.Contains(columnId, StringComparer.Ordinal))
                return k;
        }

        return -1;
    }

    /// <summary>A cell's text for key comparisons: its raw JSON, a list's items in canonical form (a composite natural key).</summary>
    internal static string Canonical(JsonElement cell) => cell.ValueKind == JsonValueKind.Array
        ? "[" + string.Join(",", cell.EnumerateArray().Select(Canonical)) + "]"
        : cell.GetRawText();

    /// <summary>
    /// The rows of a table that a foreign-key entry's natural key is matched against (next-phase-2-design.md section 3.4, decision 2):
    /// the table with its inherited members, its row key (<see cref="NaturalKeyIds"/>), the keys in seed terms each of its table seeds
    /// and each seed of a reference type bound to it holds, and the columns a foreign-key entry of its seeds fills (their key parts are
    /// natural keys of another table).
    /// </summary>
    internal sealed record NaturalRows(Table Table, IReadOnlyList<string> KeyIds, List<(Seed Seed, HashSet<string> Keys)> Keys, HashSet<string> EntryFilled)
    {
        /// <summary>
        /// Whether some of the rows are not judged: a seed with a rows script outside a save's scope (next-phase-2-design.md section
        /// 3.4), so a key no judged row holds is not reported (MQ7111) until whole-model validation.
        /// </summary>
        public bool Partial { get; init; }
    }

    /// <summary>The rows a foreign-key entry naming a table finds, or <see langword="null"/> when the model holds none for it (MQ7126).</summary>
    internal static NaturalRows? NaturalRowsOf(ValidationContext context, string tableId)
    {
        if (context.Model.GetDocument(tableId)?.Element is not Table file)
            return null;
        var table = MemberRules.WithMembers(context.Model, file);
        var seeds = context.SeedsOf(table.Id);
        var bindings = context.BoundTypesOf(table.Id).Where(b => string.Equals(b.Binding.Database, table.Database, StringComparison.Ordinal))
            .Select(b => b.Binding).ToList();
        if (seeds.Length == 0 && bindings.Count == 0)
            return null;
        var keyIds = NaturalKeyIds(table, seeds, bindings) ?? [];
        var entryFilled = new HashSet<string>(seeds.SelectMany(s => s.Columns).Select(id => table.ForeignKeys.FirstOrDefault(f => f.Id == id))
            .OfType<ForeignKey>().SelectMany(f => f.Columns), StringComparer.Ordinal);
        var keys = keyIds.Count == 0 ? []
            : seeds.Select(s => (Seed: s, Keys: KeysOf(context, s, keyIds, table))).Concat(ReferenceBindingRules.KeysOf(context, table, keyIds)).ToList();
        return new NaturalRows(table, keyIds, keys, entryFilled) { Partial = keys.Any(k => !context.Judges(k.Seed)) };
    }

    /// <summary>
    /// The row key a table's rows are found by, as the resolver gives it: its first table seed's key in merge order (the unique
    /// constraint it names, else the primary key), else the key a binding of a reference type gives its rows
    /// (<see cref="ReferenceBindingRules.RowKeyOf"/>), else its primary key; <see langword="null"/> when there is none.
    /// </summary>
    /// <param name="table">The table, with its inherited members.</param>
    /// <param name="seeds">Its table seeds, in merge order.</param>
    /// <param name="bindings">The bindings of the reference types bound to it in its database.</param>
    /// <returns>The key's column ids.</returns>
    internal static IReadOnlyList<string>? NaturalKeyIds(Table table, IEnumerable<Seed> seeds, IEnumerable<ReferenceTypeBinding> bindings)
    {
        foreach (var seed in seeds)
        {
            if (seed.Key is { } id && table.Uniques.FirstOrDefault(u => u.Id == id) is { } unique)
                return unique.Columns;
            if (table.PrimaryKey is { } primaryKey)
                return primaryKey.Columns;
        }

        foreach (var binding in bindings)
        {
            if (ReferenceBindingRules.RowKeyOf(table, binding) is { } key)
                return key;
        }

        return table.PrimaryKey?.Columns;
    }

    /// <summary>
    /// What the database fills among the values a foreign key references in a table's rows, in words ("column 'id' the database fills
    /// (identity)"): a column it always fills, or a default the rows do not give; <see langword="null"/> when the model knows every one.
    /// </summary>
    internal static string? FilledReferencedValue(NaturalRows rows, ForeignKey fk)
    {
        var referenced = fk.ReferencesColumns.Count > 0 ? fk.ReferencesColumns : rows.Table.PrimaryKey?.Columns ?? [];
        foreach (var id in referenced)
        {
            if (rows.Table.Columns.FirstOrDefault(c => c.Id == id) is not { } column)
                continue;
            if (PhysicalRules.FilledBy(column) is { } how)
                return $"column '{column.Name}' the database fills ({how})";
            if ((column.Default is not null || column.DefaultSql.Count > 0) && !column.StableId && !rows.EntryFilled.Contains(column.Id)
                && !rows.Keys.Any(h => h.Seed.Columns.Contains(column.Id, StringComparer.Ordinal)))
                return $"column '{column.Name}' the database fills (its default)";
        }

        return null;
    }

    /// <summary>A foreign key of another table that an id names, as (its name, its table's name); <see langword="null"/> otherwise.</summary>
    private static (string Key, string Table)? OtherForeignKey(ValidationContext context, string id, Table table)
    {
        if (!context.Model.TryGetEntry(id, out var entry) || entry.Kind != "key" || entry.OwnerId == table.Id
            || !entry.JsonPointer.StartsWith("/foreignKeys/", StringComparison.Ordinal)
            || context.Model.GetDocument(entry.OwnerId)?.Element is not Table owner || owner.ForeignKeys.FirstOrDefault(f => f.Id == id) is not { } fk)
            return null;
        return (fk.Name ?? fk.Id, owner.Name);
    }

    /// <summary>
    /// A foreign-key entry's cell (decisions 1 to 4): NULL only where every column of the key takes it (MQ7109), else the shape of the
    /// referenced row key, each part fitting its key column (MQ7109), naming a row of the referenced table's rows wherever this row is
    /// loaded (MQ7111).
    /// </summary>
    private static void CheckEntryCell(Seed seed, Table table, ForeignKey fk, NaturalRows? rows, SeedRow row, JsonElement cell, string pointer, Report report)
    {
        var name = fk.Name ?? fk.Id;
        if (cell.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            if (fk.Columns.Select(id => table.Columns.FirstOrDefault(c => c.Id == id))
                .FirstOrDefault(c => c is { Nullable: false, Default: null, DefaultSql.Count: 0 }) is { } required)
                report.Add("MQ7109", $"Row {row.Id}, foreign key '{name}': NULL, but its column '{required.Name}' is NOT NULL without a default.", pointer, row.Id);
            return;
        }

        if (rows is null || rows.KeyIds.Count == 0)
            return;
        if (ShapeProblem(rows, cell) is { } problem)
        {
            report.Add("MQ7109", $"Row {row.Id}, foreign key '{name}': {problem}.", pointer, row.Id);
            return;
        }

        if (rows.Partial)
            return; // a scripted seed of the table is outside this save's scope: whole-model validation judges the key

        var key = rows.KeyIds.Count == 1 ? Canonical(cell) : string.Join("\u0000", cell.EnumerateArray().Select(Canonical));
        var having = rows.Keys.Where(h => h.Keys.Contains(key)).Select(h => h.Seed).ToList();
        var text = Canonical(cell);
        if (having.Count == 0)
        {
            report.Add("MQ7111", $"Row {row.Id}: foreign key '{name}' names no row of table '{rows.Table.Name}' by {KeyWords(rows)} {text}.", pointer, row.Id);
        }
        else if (MissingIn(seed, having) is { } missing)
        {
            report.Add("MQ7111", $"Row {row.Id}: foreign key '{name}' names a row of table '{rows.Table.Name}' ({text}) that its rows do not hold {missing}, where this row is loaded.", pointer, row.Id);
        }
    }

    /// <summary>The referenced row key in words: "its column 'email'" or "its columns ('region', 'code')".</summary>
    private static string KeyWords(NaturalRows rows)
    {
        var names = rows.KeyIds.Select(id => rows.Table.Columns.FirstOrDefault(c => c.Id == id)?.Name ?? id).ToList();
        return names.Count == 1 ? $"its column '{names[0]}'" : $"its columns ('{string.Join("', '", names)}')";
    }

    /// <summary>Why a natural key cell does not have the referenced row key's shape, or a part does not fit its key column.</summary>
    private static string? ShapeProblem(NaturalRows rows, JsonElement cell)
    {
        var count = rows.KeyIds.Count;
        if (count == 1)
        {
            if (cell.ValueKind is JsonValueKind.Array or JsonValueKind.Object && !rows.EntryFilled.Contains(rows.KeyIds[0]))
                return $"{Kind(cell)}, but table '{rows.Table.Name}' finds its rows by {KeyWords(rows)}: give that value";
            return PartProblem(rows, 0, cell);
        }

        if (cell.ValueKind != JsonValueKind.Array || cell.GetArrayLength() != count)
            return $"{Kind(cell)}, but table '{rows.Table.Name}' finds its rows by {KeyWords(rows)}: give a list of {Str(count)} values in that order";
        var i = 0;
        foreach (var part in cell.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Null)
                return $"part {Str(i + 1)} of the key has no value";
            if (PartProblem(rows, i, part) is { } problem)
                return problem;
            i++;
        }

        return null;
    }

    /// <summary>Why one part of a natural key does not fit its key column as a cell of that column would (a part an entry fills is not judged).</summary>
    private static string? PartProblem(NaturalRows rows, int index, JsonElement part)
    {
        var id = rows.KeyIds[index];
        if (rows.EntryFilled.Contains(id) || rows.Table.Columns.FirstOrDefault(c => c.Id == id) is not { } column)
            return null;
        return Fit(part, column) is { } problem ? $"the key's column '{column.Name}': {problem}" : null;
    }

    /// <summary>
    /// A foreign key whose columns the seed gives must name a row of the referenced table's seeds, when that table has seeds (MQ7111),
    /// and a row that is there in every environment the row is loaded in: a seed of every environment needs the row in a seed of every
    /// environment; a seed of named environments needs it, for each of them, in a seed of every environment or of that one.
    /// </summary>
    private static void CheckForeignKeys(ValidationContext context, Seed seed, Table table, Report report)
    {
        foreach (var fk in table.ForeignKeys)
        {
            if (fk.Columns.Count == 0 || fk.Columns.Any(c => !seed.Columns.Contains(c, StringComparer.Ordinal)))
                continue;
            if (HoldersOf(context, fk) is not { } holders)
                continue;
            for (var r = 0; r < seed.Rows.Count; r++)
            {
                if (KeyOf(seed, seed.Rows[r], fk.Columns) is { } key && ForeignKeyProblem(context, seed, fk, holders, key) is { } problem)
                    report.Add("MQ7111", $"Row {seed.Rows[r].Id}: {problem}.", "/rows/" + Str(r), seed.Rows[r].Id);
            }
        }
    }

    /// <summary>
    /// The rows the table a foreign key references holds, by their key over the referenced columns, per seed: its table seeds' rows and
    /// the rows of the reference types bound to it (2026-10-11); <see langword="null"/> when it has neither (its rows come from
    /// elsewhere) or the key's columns do not line up.
    /// </summary>
    internal static (Table Table, List<(Seed Seed, HashSet<string> Keys)> Keys)? HoldersOf(ValidationContext context, ForeignKey fk)
    {
        if (context.Model.GetDocument(fk.ReferencesTable)?.Element is not Table file)
            return null;
        var referenced = MemberRules.WithMembers(context.Model, file);
        var seeds = context.SeedsOf(referenced.Id);
        if (seeds.Length == 0 && context.BoundTypesOf(referenced.Id).Length == 0)
            return null;
        IReadOnlyList<string>? referencedColumns = fk.ReferencesColumns is { Count: > 0 } named ? named : referenced.PrimaryKey?.Columns;
        if (referencedColumns is null || referencedColumns.Count != fk.Columns.Count)
            return null;
        return (referenced, seeds.Select(s => (Seed: s, Keys: KeysOf(context, s, referencedColumns)))
            .Concat(ReferenceBindingRules.KeysOf(context, referenced, referencedColumns)).ToList());
    }

    /// <summary>
    /// Why a row of <paramref name="seed"/> whose foreign key cells give <paramref name="key"/> breaks MQ7111 (no such row, or not in
    /// every environment the row is loaded in), or <see langword="null"/>.
    /// </summary>
    internal static string? ForeignKeyProblem(ValidationContext context, Seed seed, ForeignKey fk, (Table Table, List<(Seed Seed, HashSet<string> Keys)> Keys) holders, string key)
    {
        var referenced = holders.Table;
        if (holders.Keys.Any(h => !context.Judges(h.Seed)))
            return null; // a scripted seed of the table is outside this save's scope: whole-model validation judges the key
        var having = holders.Keys.Where(s => s.Keys.Contains(key)).Select(s => s.Seed).ToList();
        if (having.Count == 0)
            return $"foreign key '{fk.Name}' names no row of the seeds of table '{referenced.Name}'{(context.BoundTypesOf(referenced.Id).Length > 0 ? " or of the reference types bound to it" : "")}";
        return MissingIn(seed, having) is { } missing
            ? $"foreign key '{fk.Name}' names a row of table '{referenced.Name}' that its seeds do not hold {missing}, where this row is loaded"
            : null;
    }

    /// <summary>Where a row of <paramref name="seed"/> is loaded and none of <paramref name="holders"/> is, in words, or <see langword="null"/>.</summary>
    internal static string? MissingIn(Seed seed, IEnumerable<Seed> holders)
    {
        var list = holders.ToList();
        if (list.Any(h => h.Environments.Count == 0))
            return null;
        if (seed.Environments.Count == 0)
            return "in every environment";
        var missing = seed.Environments.Where(e => !list.Any(h => h.Environments.Contains(e, StringComparer.Ordinal))).ToList();
        return missing.Count == 0 ? null : "in the environment" + (missing.Count == 1 ? " '" : "s '") + string.Join("', '", missing) + "'";
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
