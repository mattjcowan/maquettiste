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
/// for constraints and indexes, here and in the foreign keys of other tables), so the next schema diff sees the same table instead
/// of a drop and a create.
/// </summary>
internal static partial class SnapshotRekey
{
    [GeneratedRegex(@"#[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatSuffix();

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
                || table.Columns.Any(c => c.Sequence is { } s && sequences.ContainsKey(s));
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
