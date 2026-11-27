using System.Text.RegularExpressions;

namespace Maquettiste.Testing;

/// <summary>
/// The exact check of a <see cref="MaterializeProofResult"/>: every file of the sql-ddl output after the conversion is the file before,
/// line for line and in order, except the differences "Create tables…" makes on purpose, each matched to what the pack wrote before:
/// <list type="bullet">
/// <item>A constraint the pack added in seed.sql for a column holding a reference type's codes (<c>ALTER TABLE … ADD CONSTRAINT x_ref …</c>,
/// or on SQLite its triggers) is declared in the table's CREATE TABLE under the same name: the same text after <c>ADD</c> (on SQLite, the
/// same referenced table and code column, or the same codes); the line before it gains a comma.</item>
/// <item>A table the pack created in seed.sql (a lookup table, a list's table) is a designed table: its CREATE TABLE body is the pack's,
/// with the declared constraints above; a schema the pack created in seed.sql is declared.</item>
/// <item>seed.sql keeps every row of every table, as a multiset per table; it loses the pack's reference objects (the statements above,
/// the lookup tables' CREATE TABLE) and writes rows in the bound form (another comment and statement layout). Each table's rows are
/// written the same way: inserted when missing, or updated on their key with the same columns; and the same retire deletes remain (the
/// same table and codes, whatever the line breaks).</item>
/// </list>
/// Anything else (a dropped row, table, column or file, a moved line) is a problem.
/// </summary>
public static partial class MaterializeProofCheck
{
    [GeneratedRegex(@"^ALTER TABLE (?<table>\S+) ADD (?<constraint>CONSTRAINT .+);$")]
    private static partial Regex AddConstraint();

    [GeneratedRegex(@"^\s*CONSTRAINT (?<name>\S+_ref) (?<rest>.+?),?$")]
    private static partial Regex DeclaredReference();

    [GeneratedRegex(@"^CREATE TABLE (IF NOT EXISTS )?(?<name>\S+) \($")]
    private static partial Regex CreateTable();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    /// <summary>The problems; empty when the proof holds.</summary>
    public static List<string> Problems(MaterializeProofResult proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var problems = new List<string>();
        foreach (var path in proof.Before.Keys.Where(p => !proof.After.ContainsKey(p)))
            problems.Add("dropped file " + path);
        foreach (var path in proof.After.Keys.Where(p => p.StartsWith('(')))
            problems.Add(path + ": " + proof.After[path]);
        foreach (var database in proof.Before.Keys.Concat(proof.After.Keys).Where(p => p.EndsWith("/seed.sql", StringComparison.Ordinal))
            .Select(p => p[..^"seed.sql".Length]).Distinct(StringComparer.Ordinal))
        {
            var seed = Lines(proof.Before.GetValueOrDefault(database + "seed.sql"));
            var pack = PackObjects(seed);
            foreach (var path in proof.Before.Keys.Union(proof.After.Keys).Where(p => p.StartsWith(database, StringComparison.Ordinal) && p.EndsWith(".sql", StringComparison.Ordinal)
                && !p.EndsWith("/seed.sql", StringComparison.Ordinal) && !p.Contains("/migrations/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                var after = Lines(proof.After.GetValueOrDefault(path));
                if (!proof.Before.TryGetValue(path, out var was))
                {
                    CheckNewScript(path, after, pack, problems);
                    continue;
                }

                CheckScript(path, Lines(was), after, pack, problems);
            }

            CheckSeed(database + "seed.sql", seed, Lines(proof.After.GetValueOrDefault(database + "seed.sql")), problems);
        }

        return problems;
    }

    private static List<string> Lines(string? text) => text is null ? [] : [.. text.Split('\n').Select(l => l.TrimEnd('\r'))];

    /// <summary>What the pack created in a database's seed.sql: constraints by name, tables by qualified name (their body lines), schemas.</summary>
    private sealed record Pack(Dictionary<string, (string Table, string Text)> Constraints, Dictionary<string, List<string>> Triggers,
        Dictionary<string, List<string>> Tables, HashSet<string> Schemas);

    private static Pack PackObjects(List<string> seed)
    {
        var constraints = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        var triggers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var tables = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var schemas = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < seed.Count; i++)
        {
            var line = seed[i];
            if (AddConstraint().Match(line) is { Success: true } add)
            {
                var text = add.Groups["constraint"].Value;
                constraints[text.Split(' ')[1]] = (add.Groups["table"].Value, text);
            }
            else if (line.StartsWith("CREATE TRIGGER ", StringComparison.Ordinal))
            {
                var name = line.Split(' ')[2];
                var owner = name[..name.LastIndexOf('_')];
                if (!triggers.TryGetValue(owner, out var list))
                    triggers[owner] = list = [];
                list.Add(line);
            }
            else if (CreateTable().Match(line) is { Success: true } create)
            {
                var body = new List<string>();
                for (i++; i < seed.Count && seed[i] != ");"; i++)
                {
                    body.Add(seed[i].TrimEnd(','));
                    if (DeclaredReference().Match(seed[i]) is { Success: true } inline)
                        constraints[inline.Groups["name"].Value] = (create.Groups["name"].Value, seed[i].Trim().TrimEnd(','));
                }

                tables[create.Groups["name"].Value] = body;
            }
            else if (line.StartsWith("CREATE SCHEMA", StringComparison.Ordinal) || line.StartsWith("IF SCHEMA_ID(", StringComparison.Ordinal))
            {
                schemas.Add(line);
            }
        }

        // The schema of a table the pack created (a lookup table's storage option) is one the database holds: declared now.
        foreach (var name in tables.Keys.Where(n => n.Contains('.', StringComparison.Ordinal)))
        {
            var schema = name[..name.IndexOf('.', StringComparison.Ordinal)];
            schemas.Add("CREATE SCHEMA IF NOT EXISTS " + schema + ";");
            schemas.Add("IF SCHEMA_ID(N'" + schema + "') IS NULL EXEC(N'CREATE SCHEMA " + schema + "');");
        }

        return new Pack(constraints, triggers, tables, schemas);
    }

    /// <summary>Whether a declared line is the constraint the pack wrote under that name (on SQLite, its triggers' target or codes).</summary>
    private static bool IsPackConstraint(string line, Pack pack)
    {
        if (DeclaredReference().Match(line) is not { Success: true } declared)
            return false;
        var name = declared.Groups["name"].Value;
        var text = "CONSTRAINT " + name + " " + declared.Groups["rest"].Value;
        if (pack.Constraints.TryGetValue(name, out var added))
            return added.Text == text;
        if (!pack.Triggers.TryGetValue(name, out var triggers))
            return false;
        var rest = declared.Groups["rest"].Value;
        if (rest.StartsWith("FOREIGN KEY", StringComparison.Ordinal) && Regex.Match(rest, @"REFERENCES (?<t>\S+) \((?<c>\S+)\)$") is { Success: true } fk)
            return triggers.Any(t => t.Contains($"FROM {fk.Groups["t"].Value} WHERE {fk.Groups["c"].Value} = NEW.", StringComparison.Ordinal));
        if (rest.StartsWith("CHECK", StringComparison.Ordinal) && Regex.Match(rest, @"IN \((?<codes>.+)\)\)$") is { Success: true } check)
            return triggers.Any(t => t.Contains("NOT IN (" + check.Groups["codes"].Value + ")", StringComparison.Ordinal));
        return false;
    }

    /// <summary>A table script or schema script: the before lines in order, once the declared moves are taken out.</summary>
    private static void CheckScript(string path, List<string> before, List<string> after, Pack pack, List<string> problems)
    {
        var kept = new List<string>();
        for (var i = 0; i < after.Count; i++)
        {
            var line = after[i];
            if (DeclaredReference().IsMatch(line))
            {
                if (!IsPackConstraint(line, pack))
                    problems.Add($"{path}: declared constraint the pack did not write: {line}");
                continue;
            }

            if (CreateTable().Match(line) is { Success: true } create && TableOfPack(create.Groups["name"].Value, pack) is { } body)
            {
                // A table the pack created: its body, declared constraints aside, is the pack's; the block and its heading go.
                var lines = new List<string>();
                for (i++; i < after.Count && after[i] != ");"; i++)
                {
                    // A constraint the pack added with ALTER TABLE (a list's code column) is declared in the table now.
                    if (DeclaredReference().IsMatch(after[i]) && !body.Contains(after[i].TrimEnd(',')))
                    {
                        if (!IsPackConstraint(after[i], pack))
                            problems.Add($"{path}: declared constraint the pack did not write: {after[i]}");
                        continue;
                    }

                    lines.Add(after[i].TrimEnd(','));
                }

                if (!lines.SequenceEqual(body))
                    problems.Add($"{path}: table {create.Groups["name"].Value} differs from the one the pack created");
                if (kept.Count > 0 && kept[^1].StartsWith("-- ", StringComparison.Ordinal))
                    kept.RemoveAt(kept.Count - 1);
                if (i + 1 < after.Count && after[i + 1] == "GO")
                    i++;
                if (i + 1 < after.Count && after[i + 1].Length == 0)
                    i++;
                continue;
            }

            if ((pack.Schemas.Contains(line) || line.StartsWith("IF SCHEMA_ID(", StringComparison.Ordinal) && pack.Schemas.Contains(line)) && !before.Contains(line))
            {
                if (i + 1 < after.Count && after[i + 1] == "GO" && !(i > 0 && after[i - 1] == "GO"))
                    i++;
                if (i + 1 < after.Count && after[i + 1].Length == 0 && (kept.Count == 0 || kept[^1].Length == 0))
                    i++;
                continue;
            }
            kept.Add(line);
        }

        static string Normal(string l) => l.StartsWith("-- Schema of database", StringComparison.Ordinal) ? Digits().Replace(l, "#") : l.TrimEnd(',');
        var a = before.Select(Normal).ToList();
        var b = kept.Select(Normal).ToList();
        if (!a.SequenceEqual(b))
        {
            var at = Enumerable.Range(0, Math.Min(a.Count, b.Count)).FirstOrDefault(k => a[k] != b[k], Math.Min(a.Count, b.Count));
            problems.Add($"{path}: line {at + 1} differs: '{(at < a.Count ? a[at] : "<end>")}' became '{(at < b.Count ? b[at] : "<end>")}'");
        }
    }

    private static List<string>? TableOfPack(string name, Pack pack) => pack.Tables.GetValueOrDefault(name);

    /// <summary>A new table script: a table the pack created, nothing else.</summary>
    private static void CheckNewScript(string path, List<string> after, Pack pack, List<string> problems)
    {
        var create = after.Select(l => CreateTable().Match(l)).FirstOrDefault(m => m.Success);
        if (create is null || TableOfPack(create.Groups["name"].Value, pack) is not { } body)
        {
            problems.Add($"{path}: a new script for a table the pack did not create");
            return;
        }

        var start = after.FindIndex(l => l == create.Value);
        var lines = after.Skip(start + 1).TakeWhile(l => l != ");")
            .Where(l => !DeclaredReference().IsMatch(l) || body.Contains(l.TrimEnd(',')) || !IsPackConstraint(l, pack))
            .Select(l => l.TrimEnd(',')).ToList();
        if (!lines.SequenceEqual(body))
            problems.Add($"{path}: table {create.Groups["name"].Value} differs from the one the pack created");
    }

    /// <summary>
    /// seed.sql: the rows of each table as a multiset, the same; the other lines in order, the same, once the pack's reference objects
    /// (before) and the bound rows' form (after) are set aside.
    /// </summary>
    private static void CheckSeed(string path, List<string> before, List<string> after, List<string> problems)
    {
        var (rowsBefore, restBefore) = Rows(before, packObjects: true);
        var (rowsAfter, restAfter) = Rows(after, packObjects: false);
        foreach (var table in rowsBefore.Keys.Union(rowsAfter.Keys).Order(StringComparer.Ordinal))
        {
            var a = rowsBefore.GetValueOrDefault(table) ?? [];
            var b = rowsAfter.GetValueOrDefault(table) ?? [];
            if (!a.Order(StringComparer.Ordinal).SequenceEqual(b.Order(StringComparer.Ordinal)))
                problems.Add($"{path}: the rows of {table} differ ({a.Count} before, {b.Count} after)");
        }

        var (formsBefore, deletesBefore) = Writes(before);
        var (formsAfter, deletesAfter) = Writes(after);
        foreach (var table in formsBefore.Keys.Union(formsAfter.Keys).Order(StringComparer.Ordinal))
        {
            var a = formsBefore.GetValueOrDefault(table, "none");
            var b = formsAfter.GetValueOrDefault(table, "none");
            if (a != b)
                problems.Add($"{path}: the rows of {table} were written as {a}, now as {b}");
        }

        foreach (var delete in deletesBefore.Except(deletesAfter).Order(StringComparer.Ordinal))
            problems.Add($"{path}: the delete '{delete}' is gone");
        foreach (var delete in deletesAfter.Except(deletesBefore).Order(StringComparer.Ordinal))
            problems.Add($"{path}: the delete '{delete}' is new");

        if (!restBefore.SequenceEqual(restAfter))
        {
            var at = Enumerable.Range(0, Math.Min(restBefore.Count, restAfter.Count)).FirstOrDefault(k => restBefore[k] != restAfter[k], Math.Min(restBefore.Count, restAfter.Count));
            problems.Add($"{path}: line {at + 1} of the rest differs: '{(at < restBefore.Count ? restBefore[at] : "<end>")}' became '{(at < restAfter.Count ? restAfter[at] : "<end>")}'");
        }
    }

    [GeneratedRegex(@"(DO UPDATE SET|WHEN MATCHED THEN UPDATE SET|ON DUPLICATE KEY UPDATE) (?<sets>.+?);?$")]
    private static partial Regex UpdateSets();

    /// <summary>
    /// How each table's rows are written (<c>insert</c> when missing, or <c>update(columns)</c> on their key, the columns set when a row
    /// is there, lower case and sorted), and the DELETE statements, each on one line.
    /// </summary>
    private static (Dictionary<string, string> Forms, HashSet<string> Deletes) Writes(List<string> lines)
    {
        var updated = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var deletes = new HashSet<string>(StringComparer.Ordinal);
        string? table = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.StartsWith("DELETE FROM", StringComparison.Ordinal))
            {
                var text = line;
                while (!text.EndsWith(';') && i + 1 < lines.Count)
                    text += " " + lines[++i].Trim();
                deletes.Add(text);
                table = null;
                continue;
            }

            if (RowsStatement().Match(line) is { Success: true } statement)
            {
                table = statement.Groups["table"].Value;
                updated.TryAdd(table, []);
                continue;
            }

            if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal))
            {
                table = null;
                continue;
            }

            if (table is not null && UpdateSets().Match(line) is { Success: true } sets)
            {
                foreach (var set in sets.Groups["sets"].Value.Split(", "))
                {
                    var column = set.Split(" = ")[0].Trim();
                    updated[table].Add((column.Contains('.', StringComparison.Ordinal) ? column[(column.IndexOf('.', StringComparison.Ordinal) + 1)..] : column).ToLowerInvariant());
                }
            }
        }

        return (updated.ToDictionary(p => p.Key, p => p.Value.Count == 0 ? "insert" : "update(" + string.Join(",", p.Value) + ")", StringComparer.Ordinal), deletes);
    }

    [GeneratedRegex(@"\)\s+AS \w+ \([^()]*\)$")]
    private static partial Regex RowTail();

    [GeneratedRegex(@"^(INSERT INTO|MERGE INTO|MERGE) (?<table>\S+)")]
    private static partial Regex RowsStatement();

    /// <summary>
    /// The row tuples of each table's insert statements, and the other lines: comments, statement scaffolding (the form of an insert or
    /// merge, which changes on purpose) and, before, the pack's reference objects are set aside.
    /// </summary>
    private static (Dictionary<string, List<string>> Rows, List<string> Others) Rows(List<string> lines, bool packObjects)
    {
        var rows = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var rest = new List<string>();
        string? table = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (RowsStatement().Match(line) is { Success: true } statement)
            {
                table = statement.Groups["table"].Value;
                continue;
            }

            if (table is not null && line.StartsWith("    (", StringComparison.Ordinal))
            {
                if (!rows.TryGetValue(table, out var list))
                    rows[table] = list = [];
                list.Add(RowTail().Replace(line, ")").TrimEnd(',').TrimEnd(')').TrimEnd(')'));
                continue;
            }

            if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal) || line == "GO")
            {
                table = null;
                continue;
            }

            if (table is not null && (line.StartsWith("ON CONFLICT", StringComparison.Ordinal) || line.StartsWith("WHEN ", StringComparison.Ordinal)
                || line.StartsWith("USING", StringComparison.Ordinal) || line.StartsWith("ON ", StringComparison.Ordinal) || line.StartsWith("SELECT", StringComparison.Ordinal)
                || line.StartsWith("WHERE", StringComparison.Ordinal) || line.StartsWith(")", StringComparison.Ordinal) || line.StartsWith("  ", StringComparison.Ordinal)))
                continue;
            // DELETE statements are compared on their own (Writes).
            if (line.StartsWith("DELETE FROM", StringComparison.Ordinal))
            {
                for (; i < lines.Count && !lines[i].EndsWith(';'); i++)
                {
                }

                table = null;
                continue;
            }

            if (packObjects && (line.Contains("_ref", StringComparison.Ordinal) || line.StartsWith("CREATE SCHEMA", StringComparison.Ordinal)
                || line.StartsWith("IF SCHEMA_ID(", StringComparison.Ordinal)))
                continue;
            if (packObjects && CreateTable().IsMatch(line))
            {
                for (; i < lines.Count && lines[i] != ");"; i++)
                {
                }

                continue;
            }

            if (packObjects && line.StartsWith("IF OBJECT_ID(", StringComparison.Ordinal))
                continue;
            table = null;
            rest.Add(line);
        }

        return (rows, rest);
    }
}
