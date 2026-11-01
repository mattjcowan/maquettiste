using System.Collections.Frozen;
using System.Globalization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.SchemaDiff;

namespace Maquettiste.Engine.Migrations;

/// <summary>
/// <c>validate --against</c> (next-phase-1-design.md section 3.2): which changes since a baseline model need a migration fact that
/// none of the facts the baseline lacks explains (MQ4102). Each database of both models (by id) is captured on both sides; the
/// baseline's capture is first read under today's keys (<see cref="ToTodayKeys"/>: the working model's open <c>rekey</c> facts that
/// the baseline lacks, applied in applied order), then classified from baseline to working as a write is
/// (<see cref="FactClassifier"/>). Every change that needs a fact must be explained by the facts absent from the baseline: renames
/// composed from the baseline name to the working name; <c>change-type</c> facts composed from the baseline type to the working type,
/// widenings allowed around them; a <c>drop</c> for a drop, a <c>backfill</c> for a column made not null. Pending and held facts
/// explain their change (MQ4101 reports what they lack). Nothing reads a ledger table, a physical snapshot or a database.
/// </summary>
/// <remarks>
/// The capture helper (<see cref="CapturesAsync"/>, the rekey step included) also serves <c>generate --against</c>, which diffs
/// each working database from the baseline captured the same way.
/// </remarks>
internal static class BaselineCheck
{
    /// <summary>A baseline's captures, read under the working model's keys.</summary>
    /// <param name="Captures">Per database id the baseline holds, its capture under today's keys, without the objects of owners
    /// that have errors in the baseline.</param>
    /// <param name="LeftOut">Per database id, the baseline owners with errors whose objects were left out (ordinal).</param>
    /// <param name="Unresolved">The ids of the databases the baseline holds that did not resolve or capture.</param>
    /// <param name="FailedOwners">The top-level ids of the baseline elements with errors (and what their errors leave out).</param>
    /// <param name="Facts">The ids of the baseline's migration facts.</param>
    internal sealed record BaselineCaptures(IReadOnlyDictionary<string, PhysicalSnapshot> Captures,
        IReadOnlyDictionary<string, IReadOnlyList<string>> LeftOut, IReadOnlySet<string> Unresolved, IReadOnlySet<string> FailedOwners,
        IReadOnlySet<string> Facts)
    {
        /// <summary>
        /// Per database id in <see cref="Captures"/>, the capture under the baseline's own keys (before the <c>rekey</c> step), whose
        /// state a schema diff names as where it starts (<see cref="SchemaDiffResult.FromState"/>).
        /// </summary>
        public IReadOnlyDictionary<string, PhysicalSnapshot> Stored { get; init; } = new Dictionary<string, PhysicalSnapshot>(StringComparer.Ordinal);
    }

    /// <summary>A change since the baseline that no fact explains.</summary>
    /// <param name="Need">The fact the change needs, as the classifier would record it (keys today, names at the baseline).</param>
    /// <param name="RekeyHint">For a drop: an object of the same kind with the dropped one's name was added under another key.</param>
    internal sealed record Unexplained(Migration Need, bool RekeyHint);

    /// <summary>
    /// The baseline's captures of the given databases (those it holds, matched by id), under the working model's keys: the open
    /// <c>rekey</c> facts of <paramref name="working"/> that the baseline lacks are applied first, in applied order, so a table that a
    /// materialize stored as a file is one table, not a drop and a create. Objects whose owners have errors in the baseline are left
    /// out (MQ4109 names them).
    /// </summary>
    /// <param name="baseline">The baseline.</param>
    /// <param name="working">The working model.</param>
    /// <param name="databaseIds">The working databases to capture.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The captures.</returns>
    public static async Task<BaselineCaptures> CapturesAsync(ModelBaseline baseline, ModelSnapshot working, IReadOnlyList<string> databaseIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(working);
        ArgumentNullException.ThrowIfNull(databaseIds);
        var model = await baseline.Store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var ids = databaseIds.Where(id => model.Get<Database>(id) is not null).Distinct(StringComparer.Ordinal).ToList();
        var side = await baseline.Store.CaptureDatabasesAsync(model, ids, ct).ConfigureAwait(false);
        var facts = model.All<Migration>().Select(f => f.Id).ToFrozenSet(StringComparer.Ordinal);
        var captures = new Dictionary<string, PhysicalSnapshot>(StringComparer.Ordinal);
        var stored = new Dictionary<string, PhysicalSnapshot>(StringComparer.Ordinal);
        var leftOut = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!side.Captures.TryGetValue(id, out var capture))
            {
                unresolved.Add(id);
                continue;
            }

            var excluded = new Dictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
            capture = ModelStore.Without(capture, side.FailedOwners, excluded);
            if (excluded.Count > 0)
                leftOut[id] = [.. excluded.Keys.Order(StringComparer.Ordinal)];
            var ordered = FactKeys.Order(working.All<Migration>().Where(f => string.Equals(f.Database, id, StringComparison.Ordinal)));
            stored[id] = capture;
            captures[id] = ToTodayKeys(capture, ordered, f => !facts.Contains(f.Id));
        }

        return new BaselineCaptures(captures, leftOut, unresolved, side.FailedOwners, facts) { Stored = stored };
    }

    /// <summary>
    /// The baseline's files that did not load: a load error (MQ1xxx) on a file that gave no element, so whatever it holds is missing
    /// from the baseline's captures and its changes cannot be checked (MQ4109). Ordinal.
    /// </summary>
    /// <param name="baseline">The baseline model.</param>
    /// <returns>The files' paths, as the baseline's load diagnostics name them.</returns>
    public static IReadOnlyList<string> NotLoaded(ModelSnapshot baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var loaded = baseline.Documents.Select(d => d.Path).ToHashSet(StringComparer.Ordinal);
        return [.. baseline.LoadDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Rule.StartsWith("MQ1", StringComparison.Ordinal))
            .Select(d => d.FilePath).OfType<string>().Where(p => !loaded.Contains(p))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Of the baseline's files that did not load, those a database's comparison misses: the files in its folder (the folder of
    /// <paramref name="databasePath"/>) and, with <paramref name="withShared"/>, the files outside every database folder (settings,
    /// entities, relations, packages), which any database may derive from. A file in another database's folder is that one's.
    /// </summary>
    /// <param name="notLoaded">The files that did not load (<see cref="NotLoaded"/>).</param>
    /// <param name="databasePath">The path of the database's file (<c>.../databases/&lt;folder&gt;/database.json</c>).</param>
    /// <param name="withShared">Whether to count the files outside every database folder.</param>
    /// <returns>The files, ordinal.</returns>
    public static IReadOnlyList<string> NotLoadedFor(IReadOnlyList<string> notLoaded, string? databasePath, bool withShared)
    {
        ArgumentNullException.ThrowIfNull(notLoaded);
        if (notLoaded.Count == 0 || databasePath is null)
            return [];
        var folder = FolderOf(databasePath) + "/";
        var databases = FolderOf(FolderOf(databasePath)) + "/";
        return [.. notLoaded.Where(p => p.StartsWith(folder, StringComparison.Ordinal)
            || (withShared && !p.StartsWith(databases, StringComparison.Ordinal)))];

        static string FolderOf(string path) => path[..Math.Max(0, path.LastIndexOf('/'))];
    }

    /// <summary>
    /// A baseline capture read under today's keys: the <c>rekey</c> facts of <paramref name="ordered"/> that the baseline lacks move its
    /// keys, in applied order (<see cref="SnapshotRekey.Apply"/>), and a reference column's bound table follows its table's key. A SQL
    /// object's key moves too, unless the capture holds its new key already, so the diff sees the same object.
    /// </summary>
    /// <param name="capture">The baseline's capture.</param>
    /// <param name="ordered">The working database's open facts in applied order.</param>
    /// <param name="lacking">Whether the baseline lacks a fact.</param>
    /// <returns>The capture under today's keys (the same instance when no fact moves a key).</returns>
    public static PhysicalSnapshot ToTodayKeys(PhysicalSnapshot capture, IReadOnlyList<Migration> ordered, Func<Migration, bool> lacking)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(lacking);
        foreach (var fact in ordered)
        {
            if (fact.Fact != MigrationFact.Rekey || !lacking(fact))
                continue;
            var objects = FactKeys.SqlObjectMoves(fact);
            if (objects.Count > 0 && capture.Objects.Any(o => objects.ContainsKey(o.Key)))
            {
                var present = capture.Objects.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
                capture = capture with
                {
                    Objects = [.. capture.Objects
                        .Select(o => objects.TryGetValue(o.Key, out var to) && !present.Contains(to) ? o with { Key = to } : o)
                        .OrderBy(o => o.Key, StringComparer.Ordinal)],
                };
            }

            var (tables, sequences) = FactKeys.KeyMaps(fact);
            if (tables.Count == 0 && sequences.Count == 0)
                continue;
            capture = SnapshotRekey.Apply(capture, tables, sequences);
            var moved = tables.Where(t => !string.Equals(t.OldKey, t.NewKey, StringComparison.Ordinal))
                .ToDictionary(t => t.OldKey, t => t.NewKey, StringComparer.Ordinal);
            if (moved.Count > 0 && capture.Tables.Any(t => t.Columns.Any(c => c.ReferenceTable is { } bound && moved.ContainsKey(bound))))
            {
                capture = capture with
                {
                    Tables = [.. capture.Tables.Select(t => t.Columns.Any(c => c.ReferenceTable is { } bound && moved.ContainsKey(bound))
                        ? t with
                        {
                            Columns = [.. t.Columns.Select(c => c.ReferenceTable is { } bound && moved.TryGetValue(bound, out var now) ? c with { ReferenceTable = now } : c)],
                        }
                        : t)],
                };
            }
        }

        return capture;
    }

    /// <summary>
    /// The changes from a baseline capture (under today's keys) to the working capture that need a fact and that no fact the baseline
    /// lacks explains, in the classifier's order.
    /// </summary>
    /// <param name="baseline">The baseline capture under today's keys.</param>
    /// <param name="working">The working capture.</param>
    /// <param name="ordered">The working database's open facts in applied order.</param>
    /// <param name="lacking">Whether the baseline lacks a fact.</param>
    /// <param name="dialect">The working database's dialect.</param>
    /// <param name="databaseId">The database id.</param>
    /// <returns>The unexplained changes.</returns>
    public static IReadOnlyList<Unexplained> Check(PhysicalSnapshot baseline, PhysicalSnapshot working, IReadOnlyList<Migration> ordered,
        Func<Migration, bool> lacking, Dialect dialect, string databaseId)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(working);
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(lacking);
        var needed = FactClassifier.Classify(new ClassifierInput { DatabaseId = databaseId, Dialect = dialect, Before = baseline, After = working },
            new PlaceholderIds()).Created;
        if (needed.Count == 0)
            return [];

        var moves = FactKeys.Moves(ordered);
        var candidates = new Dictionary<(MigrationFact Fact, string Kind, string? Table, string Key), List<int>>();
        for (var position = 0; position < ordered.Count; position++)
        {
            var fact = ordered[position];
            if (fact.Object is not { } o || !lacking(fact))
                continue;
            var (table, key) = moves.Current(position, o.Kind, o.Table, o.Key);
            var identity = (fact.Fact, o.Kind, FactKeys.MemberKinds.Contains(o.Kind) ? table : null, key);
            if (!candidates.TryGetValue(identity, out var list))
                candidates[identity] = list = [];
            list.Add(position);
        }

        var result = new List<Unexplained>();
        foreach (var need in needed)
        {
            var o = need.Object!;
            var positions = candidates.GetValueOrDefault((need.Fact, o.Kind, FactKeys.MemberKinds.Contains(o.Kind) ? o.Table : null, o.Key)) ?? [];
            var explained = need.Fact switch
            {
                MigrationFact.Rename => RenameExplained(need, positions, ordered),
                MigrationFact.ChangeType => TypeExplained(need, positions, ordered, moves, dialect),
                _ => positions.Count > 0,
            };
            if (!explained)
                result.Add(new Unexplained(need, need.Fact == MigrationFact.Drop && AddedWithItsName(o, baseline, working)));
        }

        return result;
    }

    /// <summary>
    /// Where an unexplained change is reported in the working model: the column entry of a designed table or an overlay, the attribute
    /// of a derived column, the table, view or sequence file (or a derived table's overlay when it names the table, else the entity or
    /// relation it derives from), the database's schema entry, or the database file for a dropped object.
    /// </summary>
    /// <param name="model">The working model.</param>
    /// <param name="database">The database.</param>
    /// <param name="need">The fact the change needs.</param>
    /// <returns>The element id and the JSON pointer in its file (<see langword="null"/> for the whole file).</returns>
    public static (string ElementId, string? Pointer) Locate(ModelSnapshot model, Database database, Migration need)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(need);
        var o = need.Object!;
        if (need.Fact == MigrationFact.Drop)
            return (database.Id, null);
        switch (o.Kind)
        {
            case "schema":
                for (var i = 0; i < database.Schemas.Count; i++)
                {
                    if (string.Equals(database.Schemas[i].Id, o.Key, StringComparison.Ordinal))
                        return (database.Id, "/schemas/" + i.ToString(CultureInfo.InvariantCulture));
                }

                return (database.Id, null);
            case "view" or "sequence":
                return model.GetDocument(o.Key) is { } document ? (document.Element.Id, "/name") : (database.Id, null);
            case "column":
                return LocateColumn(model, database, o.Table!, o.Key) ?? LocateTable(model, database, o.Table!) ?? (database.Id, null);
            default:
                return LocateTable(model, database, o.Key) ?? (database.Id, null);
        }
    }

    /// <summary>The MQ4102 message of an unexplained change.</summary>
    /// <param name="unexplained">The change.</param>
    /// <param name="label">The baseline's label (<see cref="ModelBaseline.Label"/>).</param>
    /// <returns>The message.</returns>
    public static string Message(Unexplained unexplained, string label)
    {
        ArgumentNullException.ThrowIfNull(unexplained);
        var need = unexplained.Need;
        var text = $"Since the baseline ({label}) the model changed in a way stored rows must follow, and no migration fact the baseline lacks explains it: {FactDocuments.Summary(need)}. Record a {ResolutionKebab(need.Fact)} fact (a write through Maquettiste records one while migrations.record is automatic), or undo the change.";
        if (unexplained.RekeyHint)
            text += $" A {need.Object!.Kind} with its name was added under another key: if only its key moved (a materialize), record a rekey fact instead.";
        return text;
    }

    private static string ResolutionKebab(MigrationFact fact) => Resolution.ResolutionValues.Kebab(fact);

    /// <summary>Whether the facts at <paramref name="positions"/> compose the needed rename's name (and schema) from the baseline's to the model's.</summary>
    private static bool RenameExplained(Migration need, List<int> positions, IReadOnlyList<Migration> ordered)
    {
        if (need is not { From: { } from, To: { } to })
            return positions.Count > 0;
        var name = from.Name;
        foreach (var position in positions)
        {
            if (ordered[position] is { From.Name: { } factFrom, To.Name: { } factTo } && string.Equals(factFrom, name, StringComparison.Ordinal))
                name = factTo;
        }

        if (!string.Equals(name, to.Name, StringComparison.Ordinal))
            return false;
        if (string.Equals(from.Schema, to.Schema, StringComparison.Ordinal))
            return true;

        // A move to another schema (not its schema's rename): the facts' schemas are read under the schema renames after them.
        var schema = from.Schema;
        foreach (var position in positions)
        {
            if (ordered[position] is not { From: { } factFrom, To: { Schema: { } factTo } } || string.Equals(factFrom.Schema, factTo, StringComparison.Ordinal))
                continue;
            var was = factFrom.Schema is { } s ? StaleFacts.SchemaToday(ordered, s, position) : null;
            if (string.Equals(was, schema, StringComparison.Ordinal))
                schema = StaleFacts.SchemaToday(ordered, factTo, position);
        }

        return string.Equals(schema, to.Schema, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the <c>change-type</c> facts at <paramref name="positions"/> compose from the baseline type to the model's: each step
    /// starts from a type the state so far equals or widens to, and the model's type equals or widens the last step's.
    /// </summary>
    private static bool TypeExplained(Migration need, List<int> positions, IReadOnlyList<Migration> ordered, FactKeys.KeyMoves moves, Dialect dialect)
    {
        if (need is not { From: { } state, To: { } target })
            return positions.Count > 0;
        foreach (var position in positions)
        {
            if (ordered[position] is not { From: { } from, To: { } to })
                continue;
            if (Widening.Classify(dialect, state, Today(from, position, moves)) is TypeChange.Same or TypeChange.Widening)
                state = Today(to, position, moves);
        }

        return Widening.Classify(dialect, state, target) is TypeChange.Same or TypeChange.Widening;
    }

    private static MigrationState Today(MigrationState state, int position, FactKeys.KeyMoves moves) => state.ReferenceTable is { } bound
        ? state with { ReferenceTable = moves.Current(position, "table", null, bound).Key }
        : state;

    /// <summary>Whether the model gained an object of the dropped one's kind and name under a key the baseline does not have.</summary>
    private static bool AddedWithItsName(MigrationObject dropped, PhysicalSnapshot baseline, PhysicalSnapshot working)
    {
        switch (dropped.Kind)
        {
            case "table":
                var tables = baseline.Tables.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
                return working.Tables.Any(t => !tables.Contains(t.Key) && string.Equals(t.Name, dropped.Name, StringComparison.Ordinal)
                    && string.Equals(t.Schema, dropped.Schema, StringComparison.Ordinal));
            case "sequence":
                var sequences = baseline.Sequences.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
                return working.Sequences.Any(s => !sequences.Contains(s.Key) && string.Equals(s.Name, dropped.Name, StringComparison.Ordinal));
            case "column":
                var before = baseline.Tables.FirstOrDefault(t => string.Equals(t.Key, dropped.Table, StringComparison.Ordinal));
                var after = working.Tables.FirstOrDefault(t => string.Equals(t.Key, dropped.Table, StringComparison.Ordinal));
                if (before is null || after is null)
                    return false;
                var columns = before.Columns.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
                return after.Columns.Any(c => !columns.Contains(c.Key) && string.Equals(c.Name, dropped.Name, StringComparison.Ordinal));
            default:
                return false;
        }
    }

    /// <summary>
    /// Where a table is described: its file, a derived table's overlay when the overlay names it, else the entity or relation it
    /// derives from (whose name the table's follows).
    /// </summary>
    private static (string ElementId, string? Pointer)? LocateTable(ModelSnapshot model, Database database, string key)
    {
        if (model.GetDocument(key)?.Element is Table table)
            return (table.Id, "/name");
        var at = key.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0)
            return null;
        var target = key[..at];
        var overlay = Overlay(model, database, target);
        if (overlay is { Name.Length: > 0 })
            return (overlay.Id, "/name");
        var source = target.Split('.')[0];
        if (model.GetDocument(source) is { } document)
            return (document.Element.Id, "/name");
        return overlay is null ? null : (overlay.Id, null);
    }

    /// <summary>Where a column is described: its entry in a designed table or an overlay, else the attribute it stores.</summary>
    private static (string ElementId, string? Pointer)? LocateColumn(ModelSnapshot model, Database database, string tableKey, string columnKey)
    {
        var file = model.GetDocument(tableKey)?.Element as Table;
        var at = tableKey.IndexOf('@', StringComparison.Ordinal);
        file ??= at > 0 ? Overlay(model, database, tableKey[..at]) : null;
        if (file is not null)
        {
            for (var i = 0; i < file.Columns.Count; i++)
            {
                var column = file.Columns[i];
                if (string.Equals(column.Id, columnKey, StringComparison.Ordinal) || string.Equals(column.Attribute, columnKey, StringComparison.Ordinal))
                    return (file.Id, "/columns/" + i.ToString(CultureInfo.InvariantCulture));
            }
        }

        // A derived column: its key is an attribute path, whose first step names the attribute.
        if (model.TryGetEntry(columnKey.Split('.')[0], out var entry) && !string.Equals(entry.Id, entry.OwnerId, StringComparison.Ordinal))
            return (entry.OwnerId, entry.JsonPointer);
        return null;
    }

    /// <summary>The overlay of a derived table in a database: <c>entity</c>, <c>entity.attribute</c> (a child table) or <c>relation</c>.</summary>
    private static Table? Overlay(ModelSnapshot model, Database database, string target) =>
        model.All<Table>().Where(t => string.Equals(t.Database, database.Id, StringComparison.Ordinal))
            .FirstOrDefault(t => string.Equals(t.Entity is { } entity ? (t.Attribute is { } attribute ? entity + "." + attribute : entity) : t.Relation, target,
                StringComparison.Ordinal));

    /// <summary>Ids for the facts a check classifies and never writes.</summary>
    private sealed class PlaceholderIds : IIdGenerator
    {
        private long _next;

        public string NewId() => IdFormat.Format(0, 0, (ulong)Interlocked.Increment(ref _next));
    }
}
