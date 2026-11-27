using System.Text.RegularExpressions;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.SchemaDiff;

/// <summary>One table a materialize turned from a projection into a designed table: its keys before and after.</summary>
/// <param name="OldKey">The synthesized table key (<c>&lt;entityId&gt;@&lt;databaseId&gt;</c>).</param>
/// <param name="NewKey">The designed table's id.</param>
/// <param name="Columns">Each column's key before (an attribute path) and after (the designed column's id).</param>
internal sealed record TableRekey(string OldKey, string NewKey, IReadOnlyDictionary<string, string> Columns);

/// <summary>
/// Rewrites the keys of a committed physical snapshot after <c>materialize-tables</c> (erratum E43): the designed table keeps every
/// physical property of the projected table, only its keys change (the table key, the column keys, and the keys derived from them
/// for constraints and indexes, here and in the foreign keys and reference columns of other tables), so the next schema diff sees
/// the same table instead of a drop and a create. The same step reads a baseline under today's keys before every diff: the
/// snapshot's aliases, then the open <c>rekey</c> facts it lacks (next-phase-1-design.md section 3.2). A key that is no longer in
/// the snapshot is not moved again, so aliases and facts that record the same move never move a key twice.
/// </summary>
internal static partial class SnapshotRekey
{
    [GeneratedRegex(@"#[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatSuffix();

    /// <summary>
    /// A projected column that held a reference type's codes (type <c>reference</c>) in a table a materialize stored, read as its stored
    /// successor when that is a plain column of the same native type, and the constraint the sql-ddl pack made for it (a CHECK, or a
    /// foreign key to the lookup table or to the table the type is bound to) read as the one the stored table now declares under the
    /// pack's name (<c>ck_&lt;table&gt;_&lt;column&gt;_ref</c>, <c>fk_&lt;table&gt;_&lt;column&gt;_ref</c>): storing the table changed no column
    /// and no constraint the database has, only who declares them (retire-projection-design.md section 7). A projected column whose
    /// stored table declares no such constraint, while its type was bound to a table, lost a foreign key: it stays as it was, and the
    /// change asks how its values convert (0.28.0).
    /// </summary>
    /// <param name="snapshot">The snapshot, already under the new keys.</param>
    /// <param name="tables">The tables moved.</param>
    /// <param name="successor">A table as the model has it now, by key, or <see langword="null"/>.</param>
    /// <param name="bothWays">Also read a stored plain column as the projected reference column that holds the codes again (an undo).</param>
    /// <returns>The snapshot, or the same instance when no such column is in it.</returns>
    public static PhysicalSnapshot ReadStoredReferenceColumns(PhysicalSnapshot snapshot, IReadOnlyList<TableRekey> tables, Func<string, SnapshotTable?> successor,
        bool bothWays = false)
    {
        var moved = tables.Where(t => !string.Equals(t.OldKey, t.NewKey, StringComparison.Ordinal)).Select(t => t.NewKey).ToHashSet(StringComparer.Ordinal);
        if (moved.Count == 0 || (!bothWays && !snapshot.Tables.Any(t => moved.Contains(t.Key) && t.Columns.Any(c => c.Type == "reference"))))
            return snapshot;
        var result = new List<SnapshotTable>(snapshot.Tables.Count);
        foreach (var table in snapshot.Tables)
        {
            if (!moved.Contains(table.Key) || (!bothWays && !table.Columns.Any(c => c.Type == "reference")) || successor(table.Key) is not { } now)
            {
                result.Add(table);
                continue;
            }

            var columns = new List<SnapshotColumn>(table.Columns.Count);
            var foreignKeys = table.ForeignKeys.ToList();
            var checks = table.Checks.ToList();
            foreach (var c in table.Columns)
            {
                // The other way (an undo): a stored plain column that holds codes again as a projected reference column.
                if (c.Type != "reference" && now.Columns.FirstOrDefault(x => x.Key == c.Key) is { Type: "reference" } back
                    && string.Equals(back.NativeType, c.NativeType, StringComparison.Ordinal))
                {
                    columns.Add(c with { Type = back.Type, ReferenceType = back.ReferenceType, Strategy = back.Strategy, ReferenceTable = back.ReferenceTable });
                    continue;
                }

                var next = c.Type == "reference" ? now.Columns.FirstOrDefault(x => x.Key == c.Key) : null;
                if (next is null || next.Type == "reference" || !string.Equals(next.NativeType, c.NativeType, StringComparison.Ordinal))
                {
                    columns.Add(c);
                    continue;
                }

                var fk = now.ForeignKeys.FirstOrDefault(f => f.Name == "fk_" + now.Name + "_" + next.Name + "_ref" && f.Columns.Count == 1 && f.Columns[0] == c.Key);
                var check = now.Checks.FirstOrDefault(k => k.Name == "ck_" + now.Name + "_" + next.Name + "_ref");
                if (c.ReferenceTable is not null && fk is null)
                {
                    columns.Add(c);
                    continue;
                }

                columns.Add(c with { Type = next.Type, ReferenceType = null, Strategy = null, ReferenceTable = null });
            }

            result.Add(table with { Columns = columns, ForeignKeys = foreignKeys, Checks = checks });
        }

        return snapshot with { Tables = result };
    }

    /// <summary>
    /// The snapshot with what the open <c>rekey</c> facts adopted (<see cref="Migration.Adopted"/>) read as already there, as the model
    /// now declares it: a table the snapshot lacks, a constraint of a table it holds, a column's default. Applied while the fact is open
    /// only: the snapshot the next run saves holds them, and a later change of them migrates as any change does.
    /// </summary>
    /// <param name="before">The snapshot under today's keys.</param>
    /// <param name="facts">The open rekey facts.</param>
    /// <param name="current">The model's capture now.</param>
    /// <returns>The snapshot, or the same instance when nothing is adopted.</returns>
    public static PhysicalSnapshot Adopt(PhysicalSnapshot before, IEnumerable<Migration> facts, PhysicalSnapshot current)
    {
        var adopted = facts.Where(f => f.Fact == MigrationFact.Rekey).SelectMany(f => f.Adopted).ToList();
        if (adopted.Count == 0)
            return before;
        var tables = before.Tables.ToList();
        var changed = false;
        var schemas = before.Schemas?.ToList();
        foreach (var key in adopted)
        {
            if (key.Kind == "schema")
            {
                if (schemas is not null && schemas.All(s => s.Name != key.To) && current.Schemas?.FirstOrDefault(s => s.Name == key.To) is { } schema)
                {
                    schemas.Add(schema);
                    changed = true;
                }

                continue;
            }

            if (key.Kind == "table")
            {
                if (tables.All(t => t.Key != key.To) && current.Tables.FirstOrDefault(t => t.Key == key.To) is { } table)
                {
                    tables.Add(table);
                    changed = true;
                }

                continue;
            }

            var at = tables.FindIndex(t => t.Key == key.Table);
            if (at < 0 || current.Tables.FirstOrDefault(t => t.Key == key.Table) is not { } now)
                continue;
            var held = tables[at];
            switch (key.Kind)
            {
                // A capture keys constraints by their content: an adopted one is found by its name.
                case "foreign-key" when held.ForeignKeys.All(f => f.Name != key.Name) && now.ForeignKeys.FirstOrDefault(f => f.Name == key.Name) is { } fk:
                    tables[at] = held with { ForeignKeys = [.. held.ForeignKeys, fk] };
                    changed = true;
                    break;
                case "check" when held.Checks.All(c => c.Name != key.Name) && now.Checks.FirstOrDefault(c => c.Name == key.Name) is { } check:
                    tables[at] = held with { Checks = [.. held.Checks, check] };
                    changed = true;
                    break;
                case "column" when now.Columns.FirstOrDefault(c => c.Key == key.To) is { } column:
                    tables[at] = held with
                    {
                        Columns = [.. held.Columns.Select(c => c.Key == key.To ? c with { Default = column.Default, DefaultSql = column.DefaultSql } : c)],
                    };
                    changed = true;
                    break;
            }
        }

        return changed ? before with { Schemas = schemas, Tables = [.. tables.OrderBy(t => t.Schema ?? "", StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Key, StringComparer.Ordinal)] } : before;
    }

    /// <summary>
    /// The snapshot without what an adoption took over when the model no longer declares it (an undo of the materialize before any run
    /// rendered its fact): the table, a constraint by its name, a column's default are the pack's again, outside the snapshot, so their
    /// going is no drop. A projected column that holds a reference type's codes again reads the same way its stored column did.
    /// </summary>
    /// <param name="before">The capture before the write, under the keys after it.</param>
    /// <param name="adoptions">The adoptions of the open, unrendered rekey facts.</param>
    /// <param name="tables">The tables the write moved (from a stored table's id to the projected key).</param>
    /// <param name="after">The capture after the write.</param>
    /// <returns>The capture, or the same instance when nothing is given back.</returns>
    public static PhysicalSnapshot Unadopt(PhysicalSnapshot before, IReadOnlyList<MigrationRekey> adoptions, IReadOnlyList<TableRekey> tables, PhysicalSnapshot after)
    {
        string Moved(string? key) => key is null ? "" : tables.FirstOrDefault(t => t.OldKey == key)?.NewKey ?? key;
        string MovedColumn(string? table, string column) =>
            table is not null && tables.FirstOrDefault(t => t.OldKey == table) is { } moved && moved.Columns.TryGetValue(column, out var to) ? to : column;
        var changed = false;
        var result = new List<SnapshotTable>(before.Tables.Count);
        foreach (var table in before.Tables)
        {
            if (adoptions.Any(a => a.Kind == "table" && Moved(a.To) == table.Key) && after.Tables.All(t => t.Key != table.Key))
            {
                changed = true;
                continue;
            }

            var mine = adoptions.Where(a => a.Kind != "table" && Moved(a.Table) == table.Key).ToList();
            if (mine.Count == 0)
            {
                result.Add(table);
                continue;
            }

            var now = after.Tables.FirstOrDefault(t => t.Key == table.Key);
            var foreignKeys = table.ForeignKeys.Where(f => !mine.Any(a => a.Kind == "foreign-key" && a.Name == f.Name) || now?.ForeignKeys.Any(x => x.Name == f.Name) == true).ToList();
            var checks = table.Checks.Where(c => !mine.Any(a => a.Kind == "check" && a.Name == c.Name) || now?.Checks.Any(x => x.Name == c.Name) == true).ToList();
            var columns = table.Columns.Select(c =>
            {
                if (!mine.Any(a => a.Kind == "column" && MovedColumn(a.Table, a.To) == c.Key) || now?.Columns.FirstOrDefault(x => x.Key == c.Key) is not { } next
                    || next.Default is not null || next.DefaultSql is not null)
                    return c;
                return c with { Default = null, DefaultSql = null };
            }).ToList();
            changed |= foreignKeys.Count != table.ForeignKeys.Count || checks.Count != table.Checks.Count || !columns.SequenceEqual(table.Columns);
            result.Add(table with { ForeignKeys = foreignKeys, Checks = checks, Columns = columns });
        }

        return changed ? before with { Tables = result } : before;
    }

    /// <summary>The snapshot with the keys rewritten, or the same instance when nothing in it names a rewritten key.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="tables">The tables rewritten.</param>
    /// <param name="sequences">Key sequences rewritten: the synthesized sequence key and the sequence file's id.</param>
    /// <returns>The snapshot.</returns>
    public static PhysicalSnapshot Apply(PhysicalSnapshot snapshot, IReadOnlyList<TableRekey> tables, IReadOnlyDictionary<string, string> sequences)
    {
        var byKey = tables.ToDictionary(t => t.OldKey, StringComparer.Ordinal);
        var changed = false;
        var result = new List<SnapshotTable>(snapshot.Tables.Count);
        foreach (var table in snapshot.Tables)
        {
            var own = byKey.GetValueOrDefault(table.Key);
            var touches = own is not null || table.ForeignKeys.Any(f => byKey.ContainsKey(f.ReferencedTable))
                || table.Columns.Any(c => (c.Sequence is { } s && sequences.ContainsKey(s)) || (c.ReferenceTable is { } bound && byKey.ContainsKey(bound)));
            if (!touches)
            {
                result.Add(table);
                continue;
            }

            changed = true;
            string Col(string key) => own is not null && own.Columns.TryGetValue(key, out var mapped) ? mapped : key;
            IReadOnlyList<string> Cols(IReadOnlyList<string> keys) => [.. keys.Select(Col)];
            result.Add(table with
            {
                Key = own?.NewKey ?? table.Key,
                Columns = [.. table.Columns.Select(c => c with
                {
                    Key = Col(c.Key),
                    Sequence = c.Sequence is { } s && sequences.TryGetValue(s, out var newSequence) ? newSequence : c.Sequence,
                    // A reference column's bound table follows its table's key (next-phase-1-design.md section 3.2, decision 26).
                    ReferenceTable = c.ReferenceTable is { } bound && byKey.TryGetValue(bound, out var boundTable) ? boundTable.NewKey : c.ReferenceTable,
                })],
                PrimaryKey = table.PrimaryKey is { } pk ? pk with { Columns = Cols(pk.Columns) } : null,
                Uniques = [.. table.Uniques.Select(u => u with { Key = "uq:" + string.Join(',', Cols(u.Columns)) + Suffix(u.Key), Columns = Cols(u.Columns) })],
                ForeignKeys = [.. table.ForeignKeys.Select(f =>
                {
                    var target = byKey.GetValueOrDefault(f.ReferencedTable);
                    var referenced = target is null ? f.ReferencedColumns : [.. f.ReferencedColumns.Select(c => target.Columns.TryGetValue(c, out var m) ? m : c)];
                    var columns = Cols(f.Columns);
                    var referencedTable = target?.NewKey ?? f.ReferencedTable;
                    return f with
                    {
                        Key = "fk:" + string.Join(',', columns) + "->" + referencedTable + "(" + string.Join(',', referenced) + ")" + Suffix(f.Key),
                        Columns = columns,
                        ReferencedTable = referencedTable,
                        ReferencedColumns = referenced,
                    };
                })],
                Indexes = [.. table.Indexes.Select(i =>
                {
                    var columns = i.Columns.Select(c => c.Column is null ? c : c with { Column = Col(c.Column) }).ToList();
                    var include = Cols(i.Include);
                    var suffix = Suffix(i.Key);
                    var parts = i.Key[..(i.Key.Length - suffix.Length)].Split(';');
                    parts[0] = "ix:" + string.Join(',', columns.Select(c => SnapshotCapture.IndexColumnKey(c.Column, c.Expression) + (c.Descending ? " desc" : "")));
                    for (var p = 1; p < parts.Length; p++)
                    {
                        if (parts[p].StartsWith("include=", StringComparison.Ordinal))
                            parts[p] = "include=" + string.Join(',', include);
                    }

                    return i with { Key = string.Join(';', parts) + suffix, Columns = columns, Include = include };
                })],
            });
        }

        var sequenceList = snapshot.Sequences.Select(s => sequences.TryGetValue(s.Key, out var newKey) ? s with { Key = newKey } : s).ToList();
        changed |= snapshot.Sequences.Any(s => sequences.ContainsKey(s.Key));
        return changed ? snapshot with { Tables = result, Sequences = sequenceList } : snapshot;
    }

    private static string Suffix(string key) => RepeatSuffix().Match(key) is { Success: true } match ? match.Value : "";
}
