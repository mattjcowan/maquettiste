using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The rules of a reference type's bindings (2026-10-11, optional): MQ7117 (a table file of the binding's database, one binding per
/// database), MQ7118 (the field map and the constants), MQ7119 (the code column, with the constant columns, is a key of the table, and
/// no other type bound to the table has the same constants or puts rows of the same key there), MQ7120 (every NOT NULL column without
/// a default is filled), MQ7111 (a foreign key of the table that the binding fills names a seeded row of the referenced table, through
/// the constants on the type and through a field on each seed row; <see cref="CheckSeed"/>) and MQ7113 (a table bound to a type and
/// to an entity with seeds). The rows a binding puts in its table also count for the table seed rules (MQ7111, MQ7112) through
/// <see cref="KeysOf"/>. A bound table's inherited columns and keys count as its own, by keyed id (next-phase-2-design.md section 3.1).
/// A field may not fill a column that names its own type (MQ4082), and a constant written into a column typed by an enum or a reference
/// type is one of its values (MQ7014); <see cref="CodeFit"/> judges a code column here and a typed column alike (section 3.2).
/// </summary>
internal static class ReferenceBindingRules
{
    /// <summary>The built-in fields a binding may map besides the type's user fields.</summary>
    private static readonly string[] Builtins = ["code", "label", "description"];

    private static readonly HashSet<string> Texts = new(["string", "text", "char"], StringComparer.Ordinal);
    private static readonly HashSet<string> Integers = new(["int16", "int32", "int64"], StringComparer.Ordinal);

    private static string Str(int i) => i.ToString(CultureInfo.InvariantCulture);

    /// <summary>A column of a table by id, else by physical name; pass the table with its inherited members
    /// (<see cref="MemberRules.WithMembers"/>) to find those too, by keyed id or name.</summary>
    public static Column? ColumnOf(Table table, string reference) =>
        table.Columns.FirstOrDefault(c => string.Equals(c.Id, reference, StringComparison.Ordinal))
        ?? table.Columns.FirstOrDefault(c => string.Equals(c.Name, reference, StringComparison.Ordinal));

    public static void Check(ValidationContext context, ReferenceType type, Report report)
    {
        var databases = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < type.Bindings.Count; i++)
        {
            var binding = type.Bindings[i];
            var pointer = "/bindings/" + Str(i);
            if (!databases.TryAdd(binding.Database, i))
            {
                report.Add("MQ7117", $"Reference type '{type.Name}' has a second binding to one database (the first is at index {Str(databases[binding.Database])}): a type has one binding per database.", pointer + "/database", binding.Id);
                continue;
            }

            if (context.Model.GetDocument(binding.Table)?.Element is not Table table)
                continue; // MQ2001 or MQ2002
            if (!string.Equals(table.Database, binding.Database, StringComparison.Ordinal))
            {
                var database = context.Model.GetDocument(binding.Database)?.Element.Name ?? binding.Database;
                report.Add("MQ7117", $"Table '{table.Name}' is not a table of database '{database}': a binding names a table of its own database.", pointer + "/table", binding.Id);
                continue;
            }

            if (table.Origin == TableOrigin.Synthesized)
            {
                report.Add("MQ7117", $"Table '{table.Name}' is a projected table's overlay: bind a table file of the database (store the table as a file first).", pointer + "/table", binding.Id);
                continue;
            }

            CheckBinding(context, type, binding, MemberRules.WithMembers(context.Model, table), pointer, report);
        }
    }

    /// <summary>The bindings whose rows go in a table file of their own database (the first per database), with their tables.</summary>
    private static IEnumerable<(ReferenceTypeBinding Binding, Table Table)> TablesOf(ValidationContext context, ReferenceType type)
    {
        var databases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in type.Bindings)
        {
            if (!databases.Add(binding.Database) || context.Model.GetDocument(binding.Table)?.Element is not Table table
                || !string.Equals(table.Database, binding.Database, StringComparison.Ordinal) || table.Origin == TableOrigin.Synthesized)
                continue;
            yield return (binding, MemberRules.WithMembers(context.Model, table));
        }
    }

    /// <summary>
    /// MQ7111 on a seed of a bound reference type: a foreign key of a bound table that the binding fills, through a field at least,
    /// names a row of the referenced table's seeds (or of the types bound to it) wherever the row is loaded. A foreign key the
    /// constants alone fill is checked once on the type.
    /// </summary>
    public static void CheckSeed(ValidationContext context, Seed seed, ReferenceType type, Report report)
    {
        foreach (var (binding, table) in TablesOf(context, type))
        {
            foreach (var fk in table.ForeignKeys)
            {
                if (fk.Columns.Count == 0 || Sources(table, binding, fk.Columns) is not { } sources || sources.All(x => x.Cell is null)
                    || TableSeedRules.HoldersOf(context, fk) is not { } holders)
                    continue;
                var positions = Positions(seed, sources);
                for (var r = 0; r < seed.Rows.Count; r++)
                {
                    if (KeyOf(seed.Rows[r], sources, positions) is { } key && TableSeedRules.ForeignKeyProblem(context, seed, fk, holders, key) is { } problem)
                        report.Add("MQ7111", $"Row {seed.Rows[r].Id}: through the binding to table '{table.Name}', {problem}.", "/rows/" + Str(r), seed.Rows[r].Id);
                }
            }
        }
    }

    private static void CheckBinding(ValidationContext context, ReferenceType type, ReferenceTypeBinding binding, Table table, string pointer, Report report)
    {
        var filled = new Dictionary<string, string>(StringComparer.Ordinal); // column id -> what fills it
        var fields = new HashSet<string>(StringComparer.Ordinal);
        Column? codeColumn = null;
        for (var j = 0; j < binding.Fields.Count; j++)
        {
            var field = binding.Fields[j];
            var at = pointer + "/fields/" + Str(j);
            var attribute = type.Attributes.FirstOrDefault(a => string.Equals(a.Id, field.Attribute, StringComparison.Ordinal));
            if (attribute is null && !Builtins.Contains(field.Attribute, StringComparer.Ordinal))
            {
                report.Add("MQ7118", $"'{field.Attribute}' is not a field of reference type '{type.Name}': map code, label, description or a user field.", at + "/attribute", binding.Id);
                continue;
            }

            var name = attribute?.Name ?? field.Attribute;
            if (attribute is { Collection: true })
            {
                report.Add("MQ7118", $"Field '{name}' of reference type '{type.Name}' holds a list of values; a binding maps single-valued fields to columns.", at + "/attribute", binding.Id);
                continue;
            }

            if (!fields.Add(field.Attribute))
            {
                report.Add("MQ7118", $"The binding of reference type '{type.Name}' maps field '{name}' twice.", at + "/attribute", binding.Id);
                continue;
            }

            if (ColumnOf(table, field.Column) is not { } column)
            {
                report.Add("MQ7118", $"'{field.Column}' is not a column of table '{table.Name}'.", at + "/column", binding.Id);
                continue;
            }

            // A binding's field says what the column holds, so the column states no type of its own (next-phase-2-design.md section 3.2).
            if (column.TypeRef is not null)
                report.Add("MQ4082", $"Column '{column.Name}' of table '{table.Name}' names the type it holds (typeRef), and the binding of reference type '{type.Name}' fills it with field '{name}': the binding says what the column holds, so remove the column's typeRef.", at + "/column", binding.Id);
            if (Fill(column, "field '" + name + "'", filled, table, at + "/column", binding, report) is false)
                continue;
            if (field.Attribute == "code")
                codeColumn = column;
            if (Fit(type, field.Attribute, column) is { } problem)
                report.Add("MQ7118", $"Field '{name}' of reference type '{type.Name}' does not fit column '{column.Name}' of table '{table.Name}': {problem}.", at + "/column", binding.Id);
        }

        var constants = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        for (var j = 0; j < binding.Constants.Count; j++)
        {
            var constant = binding.Constants[j];
            var at = pointer + "/constants/" + Str(j) + "/column";
            if (ColumnOf(table, constant.Column) is not { } column)
            {
                report.Add("MQ7118", $"'{constant.Column}' is not a column of table '{table.Name}'.", at, binding.Id);
                continue;
            }

            if (Fill(column, "a constant", filled, table, at, binding, report) is not false)
                constants[column.Id] = constant.Value;
            if (constant.Value is { } value && ColumnTypeRules.ValueProblem(context, column, value, null) is { } problem)
                report.Add("MQ7014", $"The constant the binding of reference type '{type.Name}' writes into column '{column.Name}' of table '{table.Name}' is not one of the values the column's type holds: {problem}.", pointer + "/constants/" + Str(j), binding.Id);
        }

        if (codeColumn is null)
        {
            if (!fields.Contains("code"))
                report.Add("MQ7118", $"The binding of reference type '{type.Name}' maps no column to code: the code is the row's key in table '{table.Name}'.", pointer + "/fields", binding.Id);
            return;
        }

        // The code, with the constants, finds one row: a key of the table made of those columns, the code among them.
        var keyColumns = new HashSet<string>(constants.Keys.Append(codeColumn.Id), StringComparer.Ordinal);
        var keys = (table.PrimaryKey is { } pk ? [pk.Columns] : Array.Empty<IReadOnlyList<string>>()).Concat(table.Uniques.Select(u => u.Columns)).ToList();
        if (!keys.Any(k => k.Count > 0 && k.Contains(codeColumn.Id, StringComparer.Ordinal) && k.All(keyColumns.Contains)))
            report.Add("MQ7119", $"No primary key or unique constraint of table '{table.Name}' is made of its code column '{codeColumn.Name}'{(constants.Count > 0 ? " and constant columns" : "")}: a foreign key cannot name one of the type's rows.", pointer + "/fields", binding.Id);

        foreach (var column in table.Columns)
        {
            if (filled.ContainsKey(column.Id) || column.Nullable != false || column.Default is not null || column.DefaultSql.Count > 0
                || column.Identity is not null || column.Generated is not null || column.Computed is not null || column.Sequence is not null
                || column.StableId) // the engine derives a stable id (next-phase-2-design.md section 3.4)
                continue;
            report.Add("MQ7120", $"The binding of reference type '{type.Name}' fills no value for column '{column.Name}' of table '{table.Name}', which is NOT NULL without a default: map a field to it or give it a constant.", pointer, binding.Id);
        }

        // Another type bound to the same table with the same constants would put rows of the same keys there.
        var sameConstants = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (other, otherBinding) in context.BoundTypesOf(table.Id))
        {
            if (other.Id == type.Id || string.CompareOrdinal(other.Name, type.Name) > 0 || (other.Name == type.Name && string.CompareOrdinal(other.Id, type.Id) > 0))
                continue;
            var theirs = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
            foreach (var constant in otherBinding.Constants)
            {
                if (ColumnOf(table, constant.Column) is { } c)
                    theirs.TryAdd(c.Id, constant.Value);
            }

            if (constants.Count == theirs.Count && constants.All(kv => theirs.TryGetValue(kv.Key, out var v) && Raw(v) == Raw(kv.Value)))
            {
                report.Add("MQ7119", $"Reference type '{other.Name}' is bound to table '{table.Name}' with the same constants: give each type a constant value of its own (a column that names the type), so their codes do not share keys.", pointer + "/constants", binding.Id);
                sameConstants.Add(other.Id);
            }
        }

        CheckSharedKeys(context, type, table, keys.Where(k => k.Count > 0 && k.All(filled.ContainsKey)), sameConstants, pointer, binding, report);
        CheckConstantForeignKeys(context, type, binding, table, pointer, report);

        foreach (var entity in context.Model.All<Entity>())
        {
            if (!entity.Bindings.Any(b => b.Source == table.Id || b.Write?.Table == table.Id) || context.SeedsOf(entity.Id).Length == 0)
                continue;
            report.Add("MQ7113", $"Table '{table.Name}' also receives the seed rows of entity '{entity.Name}', which is bound to it: give each row one source.", pointer + "/table", binding.Id);
        }
    }

    /// <summary>
    /// MQ7119 when the seeds of this type and of another type bound to the table (earlier by name, so the pair is reported once) put
    /// rows of the same key there: over a key of the table the binding fills, their constants do not tell the rows apart and a code
    /// repeats where both seeds load.
    /// </summary>
    private static void CheckSharedKeys(ValidationContext context, ReferenceType type, Table table, IEnumerable<IReadOnlyList<string>> keys,
        HashSet<string> reported, string pointer, ReferenceTypeBinding binding, Report report)
    {
        foreach (var key in keys)
        {
            var holders = KeysOf(context, table, key);
            var mine = holders.Where(h => h.Seed.Target == type.Id).ToList();
            if (mine.Count == 0)
                continue;
            foreach (var (seed, theirs) in holders)
            {
                if (seed.Target == type.Id || reported.Contains(seed.Target) || context.Model.GetDocument(seed.Target)?.Element is not ReferenceType other
                    || string.CompareOrdinal(other.Name, type.Name) > 0 || (other.Name == type.Name && string.CompareOrdinal(other.Id, type.Id) > 0))
                    continue;
                foreach (var (own, ours) in mine)
                {
                    if (!TableSeedRules.Overlap(own, seed) || ours.Where(theirs.Contains).Order(StringComparer.Ordinal).ToList() is not { Count: > 0 } shared)
                        continue;
                    var columns = string.Join(", ", key.Select(id => ColumnOf(table, id)?.Name ?? id));
                    report.Add("MQ7119", $"Seed '{own.Name}' and seed '{seed.Name}' of reference type '{other.Name}' put {Str(shared.Count)} row(s) of the same key ({columns}) in table '{table.Name}', the first {shared[0].Replace("\u0000", ", ", StringComparison.Ordinal)}: give each type a constant value of its own in a column of the key.", pointer, binding.Id);
                }
            }
        }
    }

    /// <summary>MQ7111 once on the type for a foreign key of the table that the binding's constants alone fill.</summary>
    private static void CheckConstantForeignKeys(ValidationContext context, ReferenceType type, ReferenceTypeBinding binding, Table table, string pointer, Report report)
    {
        foreach (var fk in table.ForeignKeys)
        {
            if (fk.Columns.Count == 0 || Sources(table, binding, fk.Columns) is not { } sources || sources.Any(x => x.Cell is not null)
                || TableSeedRules.HoldersOf(context, fk) is not { } holders || KeyOf(null, sources, []) is not { } key)
                continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var seed in context.SeedsOf(type.Id))
            {
                if (TableSeedRules.ForeignKeyProblem(context, seed, fk, holders, key) is { } problem && seen.Add(problem))
                    report.Add("MQ7111", $"The constants of the binding to table '{table.Name}' (rows of seed '{seed.Name}'): {problem}.", pointer + "/constants", binding.Id);
            }
        }
    }

    /// <summary>
    /// What fills each of some columns of a bound table: a field (its seed column) or a constant; <see langword="null"/> when the
    /// binding fills not every one of them.
    /// </summary>
    private static List<(string? Cell, JsonElement? Constant)>? Sources(Table table, ReferenceTypeBinding binding, IReadOnlyList<string> columnIds)
    {
        var sources = new List<(string? Cell, JsonElement? Constant)>(columnIds.Count);
        foreach (var id in columnIds)
        {
            if (binding.Fields.FirstOrDefault(f => ColumnOf(table, f.Column)?.Id == id) is { } field)
                sources.Add((field.Attribute, null));
            else if (binding.Constants.FirstOrDefault(c => ColumnOf(table, c.Column)?.Id == id) is { } constant)
                sources.Add((null, constant.Value));
            else
                return null;
        }

        return sources;
    }

    private static int[] Positions(Seed seed, List<(string? Cell, JsonElement? Constant)> sources)
    {
        var columns = seed.Columns.ToList();
        return [.. sources.Select(s => s.Cell is null ? -1 : columns.IndexOf(s.Cell))];
    }

    /// <summary>A row's key over the sources (raw JSON text joined like a table seed's), or <see langword="null"/> when a value is missing.</summary>
    private static string? KeyOf(SeedRow? row, List<(string? Cell, JsonElement? Constant)> sources, int[] positions)
    {
        var parts = new string[sources.Count];
        for (var k = 0; k < sources.Count; k++)
        {
            var value = sources[k].Cell is null ? sources[k].Constant
                : row is not null && positions[k] >= 0 && positions[k] < row.Values.Count ? row.Values[positions[k]] : null;
            if (Raw(value) is not { } raw)
                return null;
            parts[k] = raw;
        }

        return string.Join("\u0000", parts);
    }

    /// <summary>Records what fills a column; reports a column filled twice or by the database (MQ7118) and returns false then.</summary>
    private static bool Fill(Column column, string what, Dictionary<string, string> filled, Table table, string pointer, ReferenceTypeBinding binding, Report report)
    {
        if (column.Identity is not null || column.Generated is not null || column.Computed is not null)
        {
            report.Add("MQ7118", $"Column '{column.Name}' of table '{table.Name}' is filled by the database; a binding cannot write it.", pointer, binding.Id);
            return false;
        }

        if (!filled.TryAdd(column.Id, what))
        {
            report.Add("MQ7118", $"Column '{column.Name}' of table '{table.Name}' is filled twice: by {filled[column.Id]} and by {what}.", pointer, binding.Id);
            return false;
        }

        return true;
    }

    /// <summary>Why a built-in field does not fit its column, or <see langword="null"/> (user fields are left to the database).</summary>
    private static string? Fit(ReferenceType type, string field, Column column)
    {
        var columnType = column.Type ?? "string";
        switch (field)
        {
            case "code":
                return CodeFit(type, column)?.Problem;
            case "label" or "description":
                if (!Texts.Contains(columnType))
                    return $"text in a {columnType} column";
                if (field == "label" && type.Label.Length is { } labelLength && column.Length is { } max && labelLength > max)
                    return $"labels of up to {labelLength} characters, more than its length {max}";
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Why a column cannot hold a reference type's codes, shared by a binding's code column (MQ7118) and a column typed by the type
    /// (MQ4083, next-phase-2-design.md section 3.2): a text code needs a text column long enough for the code's length, an integer code an
    /// integer or decimal column, a UUID code a uuid or text column. <see cref="CodeMisfit.Length"/> tells a length that is too short
    /// from a type that cannot hold the codes at all. A column without a type is read as <c>string</c>.
    /// </summary>
    /// <param name="type">The reference type.</param>
    /// <param name="column">The column.</param>
    /// <returns>The problem, or <see langword="null"/> when the column fits.</returns>
    internal static CodeMisfit? CodeFit(ReferenceType type, Column column)
    {
        var columnType = column.Type ?? "string";
        if (type.Code.Type is "string" && !Texts.Contains(columnType))
            return new($"a text code in a {columnType} column", false);
        if (Integers.Contains(type.Code.Type) && !Integers.Contains(columnType) && columnType is not "decimal")
            return new($"an integer code in a {columnType} column", false);
        if (type.Code.Type is "uuid" && columnType is not "uuid" && !Texts.Contains(columnType))
            return new($"a UUID code in a {columnType} column", false);
        if (type.Code.Type is "string" && type.Code.Length is { } codeLength && column.Length is { } length && codeLength > length)
            return new($"codes of up to {codeLength} characters, more than its length {length}", true);
        return null;
    }

    /// <summary>Why a column cannot hold a reference type's codes (<see cref="CodeFit"/>).</summary>
    /// <param name="Problem">The reason, in words.</param>
    /// <param name="Length">Whether the column's length is too short (else its type cannot hold the codes).</param>
    internal sealed record CodeMisfit(string Problem, bool Length);

    private static string? Raw(JsonElement? value) => value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } v ? v.GetRawText() : null;

    /// <summary>
    /// The key a binding gives the rows it puts in its table, as the resolver takes it when no table seed keys the table: the primary
    /// key when the binding fills every column of it (fields and constants), else the first unique constraint it fills whole that holds
    /// the code's column (any one when no field is the code), else <see langword="null"/> (MQ7119 asks for one of the two). A foreign-key
    /// entry's natural key names the table's rows by it (next-phase-2-design.md section 3.4, decision 2), with
    /// <see cref="KeysOf"/> giving the bound rows' keys in seed terms.
    /// </summary>
    /// <param name="table">The bound table, with the members it inherits.</param>
    /// <param name="binding">The binding.</param>
    /// <returns>The key's column ids.</returns>
    public static IReadOnlyList<string>? RowKeyOf(Table table, ReferenceTypeBinding binding)
    {
        var filled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in binding.Fields.Select(f => f.Column).Concat(binding.Constants.Select(c => c.Column)))
        {
            if (ColumnOf(table, column) is { } found)
                filled.Add(found.Id);
        }

        if (table.PrimaryKey is { Columns.Count: > 0 } primaryKey && primaryKey.Columns.All(filled.Contains))
            return primaryKey.Columns;
        var code = binding.Fields.FirstOrDefault(f => f.Attribute == "code") is { } field ? ColumnOf(table, field.Column)?.Id : null;
        return table.Uniques.FirstOrDefault(u => u.Columns.Count > 0 && u.Columns.All(filled.Contains) && (code is null || u.Columns.Contains(code, StringComparer.Ordinal)))?.Columns;
    }

    /// <summary>
    /// The row keys over some columns of a table that the seeds of the types bound to it put there, one entry per seed (keys as raw
    /// JSON text joined like a table seed's); a type whose binding fills not every one of the columns is left out.
    /// </summary>
    public static List<(Seed Seed, HashSet<string> Keys)> KeysOf(ValidationContext context, Table table, IReadOnlyList<string> columnIds)
    {
        table = MemberRules.WithMembers(context.Model, table);
        var result = new List<(Seed, HashSet<string>)>();
        foreach (var (type, binding) in context.BoundTypesOf(table.Id))
        {
            if (!string.Equals(binding.Database, table.Database, StringComparison.Ordinal) || Sources(table, binding, columnIds) is not { } sources)
                continue;
            foreach (var seed in context.SeedsOf(type.Id))
            {
                var positions = Positions(seed, sources);
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in context.RowsOf(seed))
                {
                    if (KeyOf(row, sources, positions) is { } key)
                        keys.Add(key);
                }

                result.Add((seed, keys));
            }
        }

        return result;
    }
}
