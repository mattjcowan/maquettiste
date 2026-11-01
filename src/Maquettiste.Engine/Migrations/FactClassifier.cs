using System.Collections.Frozen;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.SchemaDiff;

namespace Maquettiste.Engine.Migrations;

/// <summary>What one write changed in one database, as <see cref="FactClassifier.Classify"/> reads it.</summary>
internal sealed record ClassifierInput
{
    /// <summary>The database's id.</summary>
    public required string DatabaseId { get; init; }

    /// <summary>The database's dialect after the change (only PostgreSQL has widenings).</summary>
    public required Dialect Dialect { get; init; }

    /// <summary>The database's capture before the change.</summary>
    public required PhysicalSnapshot Before { get; init; }

    /// <summary>The database's capture after the change.</summary>
    public required PhysicalSnapshot After { get; init; }

    /// <summary>The database's open facts in the model after the change, in any order (the facts the write itself writes included).</summary>
    public IReadOnlyList<Migration> OpenFacts { get; init; } = [];

    /// <summary>The ids of the facts the write itself creates or updates: they explain their changes, and their <c>rekey</c> keys are key maps.</summary>
    public IReadOnlySet<string> WrittenFacts { get; init; } = FrozenSet<string>.Empty;

    /// <summary>The tables a materialize in the same write moved (<c>MaterializeResult.Rekeys</c>).</summary>
    public IReadOnlyList<TableRekey> Rekeys { get; init; } = [];

    /// <summary>The key sequences a materialize in the same write moved (<c>MaterializeResult.Sequences</c>).</summary>
    public IReadOnlyDictionary<string, string> SequenceRekeys { get; init; } = FrozenDictionary<string, string>.Empty;
}

/// <summary>The facts a write records in one database.</summary>
/// <param name="Created">New facts, in the order their ids were taken (the order they chain in).</param>
/// <param name="Updated">Open pending facts whose <c>to</c> followed their column, by id.</param>
internal sealed record ClassifierResult(IReadOnlyList<Migration> Created, IReadOnlyList<Migration> Updated)
{
    /// <summary>A result with no facts.</summary>
    public static ClassifierResult Empty { get; } = new([], []);

    /// <summary>Whether the write records nothing.</summary>
    public bool IsEmpty => Created.Count == 0 && Updated.Count == 0;
}

/// <summary>
/// The classifier (next-phase-1-design.md section 3.2): two captures of one database, its open facts after the change, the dialect
/// and the write's key maps in; facts out. It compares resolved physical captures, never documents, and never matches objects by
/// name: a moved key is known from the write's key maps (a materialize's, the keys of <c>rekey</c> facts the write itself creates,
/// and the reverse of open <c>rekey</c> facts whose new keys the write removes, an undo of a materialize), never guessed. The
/// before capture is moved through those maps (<see cref="SnapshotRekey.Apply"/>) and the two captures are compared by key, as
/// the schema diff matches them:
/// <list type="bullet">
/// <item><description>keys the write's materialize or undo moved: one complete <c>rekey</c> fact, every key moved;</description></item>
/// <item><description>a database schema, table, view, sequence or column with the same key and a new name (or a table, view or
/// sequence in a schema other than its schema's rename): a complete <c>rename</c>;</description></item>
/// <item><description>a column type (its native type, else its keyword and facets) narrowed: a <c>change-type</c> pending a
/// conversion and a data check; changed otherwise, or a reference column's strategy or bound table changed: a <c>change-type</c>
/// pending a conversion; widened or the same: nothing;</description></item>
/// <item><description>a column made not null: a <c>backfill</c> pending a value or a data check; made nullable: nothing;</description></item>
/// <item><description>a table, column or sequence whose key disappears: a <c>drop</c> pending its acknowledgement (a dropped
/// table's columns get none), even when an object with the same name appears;</description></item>
/// <item><description>added objects, indexes, constraints, views dropped, routines, types, SQL objects, defaults, comments,
/// identity, collation and storage: nothing.</description></item>
/// </list>
/// A change that a fact the write itself creates or updates already explains (same fact type, same object) is not recorded again.
/// A column whose last open fact is a pending <c>change-type</c> (not held, no conversion, no data check) keeps that one fact: a
/// type change rewrites its <c>to</c>, unless the new type is its <c>from</c> or a widening of it, which leaves it as it is
/// (stale, MQ4103). New facts chain in the order: the write's <c>rekey</c> first (the other facts name the keys it moved), then
/// schema renames by key, then by (object key, member key; rename, change-type, backfill, drop); ids are taken in that order, and
/// each fact's <c>after</c> is the heads of the database's open facts on the same table (the facts of the table and of its members
/// that no other such fact follows).
/// </summary>
internal static class FactClassifier
{
    private const int RankRename = 0;
    private const int RankChangeType = 2;
    private const int RankBackfill = 3;
    private const int RankDrop = 4;

    /// <summary>Classifies one write in one database.</summary>
    /// <param name="input">The captures, facts and key maps.</param>
    /// <param name="ids">The id generator (<c>EngineOptions.EffectiveIdGenerator</c>).</param>
    /// <returns>The facts to create and update.</returns>
    public static ClassifierResult Classify(ClassifierInput input, IIdGenerator ids)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ids);
        var run = new Run(input);
        return run.Classify(ids);
    }

    /// <summary>One classification.</summary>
    private sealed class Run(ClassifierInput input)
    {
        private readonly List<Candidate> _candidates = [];
        private readonly Dictionary<string, Migration> _updated = new(StringComparer.Ordinal);
        private IReadOnlyList<Migration> _ordered = [];
        private FactKeys.KeyMoves _moves = FactKeys.Moves([]);
        private FactKeys.RekeyMap? _newMoves;

        /// <summary>Each open fact's object identities today (its table groups and objects), by fact id.</summary>
        private readonly Dictionary<string, List<(string Kind, string? Table, string Key)>> _objects = new(StringComparer.Ordinal);

        /// <summary>The position of the last open fact in applied order whose <c>object</c> has an identity today.</summary>
        private readonly Dictionary<(string Kind, string? Table, string Key), int> _lastByObject = [];

        /// <summary>The changes the facts the write itself creates or updates record: fact type and object identity today.</summary>
        private readonly HashSet<(MigrationFact Fact, (string Kind, string? Table, string Key) Object)> _explained = [];

        /// <summary>The write's table key maps, old key to new (a bound reference table moves with them).</summary>
        private readonly Dictionary<string, string> _tableMoves = new(StringComparer.Ordinal);

        public ClassifierResult Classify(IIdGenerator ids)
        {
            _ordered = FactKeys.Order(input.OpenFacts.Where(f => string.Equals(f.Database, input.DatabaseId, StringComparison.Ordinal)));
            _moves = FactKeys.Moves(_ordered);

            var (tables, sequences, recorded) = KeyMaps();
            Migration? rekey = null;
            if (recorded.Count > 0)
            {
                rekey = new Migration { Id = "", Database = input.DatabaseId, Fact = MigrationFact.Rekey, Keys = recorded };
                _newMoves = FactKeys.RekeyMap.Of(rekey);
            }

            foreach (var t in tables)
                _tableMoves[t.OldKey] = t.NewKey;
            for (var i = 0; i < _ordered.Count; i++)
            {
                var fact = _ordered[i];
                var objects = CurrentObjects(fact, i);
                _objects[fact.Id] = objects;
                if (fact.Object is null)
                    continue;
                _lastByObject[objects[0]] = i; // the object's identity comes first; a later fact replaces an earlier one
                if (input.WrittenFacts.Contains(fact.Id))
                {
                    foreach (var identity in objects)
                        _explained.Add((fact.Fact, identity));
                }
            }

            var before = tables.Count > 0 || sequences.Count > 0 ? SnapshotRekey.Apply(input.Before, tables, sequences) : input.Before;
            Compare(before, input.After);
            if (_candidates.Count == 0 && rekey is null)
                return _updated.Count == 0 ? ClassifierResult.Empty : new ClassifierResult([], [.. _updated.Values.OrderBy(f => f.Id, StringComparer.Ordinal)]);

            var ordered = _candidates
                .OrderBy(c => c.Group, StringComparer.Ordinal).ThenBy(c => c.Member, StringComparer.Ordinal).ThenBy(c => c.Rank).ToList();
            var created = new List<Migration>(ordered.Count + 1);
            var heads = new Heads(_ordered, _objects);
            if (rekey is not null)
                created.Add(heads.Chain(rekey with { Id = ids.NewId() }, RekeyObjects(rekey)));
            foreach (var candidate in ordered)
            {
                var fact = candidate.Fact with { Id = ids.NewId(), Database = input.DatabaseId };
                created.Add(heads.Chain(fact, [Identity(candidate.Fact.Object!)]));
            }

            return new ClassifierResult(created, [.. _updated.Values.OrderBy(f => f.Id, StringComparer.Ordinal)]);
        }

        // ---- key maps ----

        /// <summary>
        /// The write's key maps: the materialize's, the keys of <c>rekey</c> facts the write creates, and the reverse of open
        /// <c>rekey</c> facts whose new keys the write removes (the old key is back in the after capture); the first map of a key wins.
        /// Also the keys the new <c>rekey</c> fact records: those the materialize or the undo moved that exist before the change and
        /// that no written fact records already.
        /// </summary>
        private (List<TableRekey> Tables, Dictionary<string, string> Sequences, List<MigrationRekey> Recorded) KeyMaps()
        {
            var tables = new Dictionary<string, TableRekey>(StringComparer.Ordinal);
            var sequences = new Dictionary<string, string>(StringComparer.Ordinal);
            var recordable = new List<TableRekey>();
            var recordableSequences = new List<(string From, string To)>();

            foreach (var t in input.Rekeys.OrderBy(t => t.OldKey, StringComparer.Ordinal))
            {
                if (tables.TryAdd(t.OldKey, t))
                    recordable.Add(t);
            }

            foreach (var (from, to) in input.SequenceRekeys.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (sequences.TryAdd(from, to))
                    recordableSequences.Add((from, to));
            }

            var writtenKeys = new HashSet<(string Kind, string? Table, string From)>();
            foreach (var fact in _ordered.Where(f => f.Fact == MigrationFact.Rekey && input.WrittenFacts.Contains(f.Id)))
            {
                foreach (var key in fact.Keys)
                    writtenKeys.Add((key.Kind, key.Table, key.From));
                var (factTables, factSequences) = FactKeys.KeyMaps(fact);
                foreach (var t in factTables)
                    tables.TryAdd(t.OldKey, t);
                foreach (var (from, to) in factSequences)
                    sequences.TryAdd(from, to);
            }

            var beforeTables = input.Before.Tables.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
            var afterTables = input.After.Tables.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
            var beforeSequences = input.Before.Sequences.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
            var afterSequences = input.After.Sequences.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var fact in _ordered.Where(f => f.Fact == MigrationFact.Rekey && !input.WrittenFacts.Contains(f.Id)))
            {
                var (back, backSequences) = FactKeys.KeyMaps(fact, reverse: true);
                foreach (var t in back)
                {
                    // An undo: the fact's new key leaves and its old key comes back.
                    if (!string.Equals(t.OldKey, t.NewKey, StringComparison.Ordinal) && beforeTables.Contains(t.OldKey) && !afterTables.Contains(t.OldKey)
                        && afterTables.Contains(t.NewKey) && tables.TryAdd(t.OldKey, t))
                        recordable.Add(t);
                }

                foreach (var (from, to) in backSequences.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (beforeSequences.Contains(from) && !afterSequences.Contains(from) && afterSequences.Contains(to) && sequences.TryAdd(from, to))
                        recordableSequences.Add((from, to));
                }
            }

            var recorded = new List<MigrationRekey>();
            var beforeByKey = input.Before.Tables.ToDictionary(t => t.Key, StringComparer.Ordinal);
            foreach (var t in recordable.OrderBy(t => t.OldKey, StringComparer.Ordinal))
            {
                if (!beforeByKey.TryGetValue(t.OldKey, out var table))
                    continue;
                if (!string.Equals(t.OldKey, t.NewKey, StringComparison.Ordinal) && !writtenKeys.Contains(("table", null, t.OldKey)))
                    recorded.Add(new MigrationRekey { Kind = "table", From = t.OldKey, To = t.NewKey, Name = table.Name });
                foreach (var column in table.Columns.OrderBy(c => c.Key, StringComparer.Ordinal))
                {
                    if (t.Columns.TryGetValue(column.Key, out var to) && !string.Equals(column.Key, to, StringComparison.Ordinal)
                        && !writtenKeys.Contains(("column", t.OldKey, column.Key)))
                        recorded.Add(new MigrationRekey { Kind = "column", Table = t.OldKey, From = column.Key, To = to, Name = column.Name });
                }
            }

            var sequenceNames = input.Before.Sequences.ToDictionary(s => s.Key, s => s.Name, StringComparer.Ordinal);
            foreach (var (from, to) in recordableSequences.OrderBy(p => p.From, StringComparer.Ordinal))
            {
                if (sequenceNames.TryGetValue(from, out var name) && !string.Equals(from, to, StringComparison.Ordinal)
                    && !writtenKeys.Contains(("sequence", null, from)))
                    recorded.Add(new MigrationRekey { Kind = "sequence", From = from, To = to, Name = name });
            }

            return ([.. tables.Values.OrderBy(t => t.OldKey, StringComparer.Ordinal)], sequences, recorded);
        }

        // ---- comparison ----

        private void Compare(PhysicalSnapshot before, PhysicalSnapshot after)
        {
            // Database schemas: a rename is one fact; the tables, views and sequences that follow it record nothing.
            var schemaRenames = new Dictionary<string, string>(StringComparer.Ordinal);
            var afterSchemas = (after.Schemas ?? []).ToDictionary(s => s.Key, StringComparer.Ordinal);
            foreach (var schema in (before.Schemas ?? []).OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                if (afterSchemas.TryGetValue(schema.Key, out var now) && !string.Equals(schema.Name, now.Name, StringComparison.Ordinal))
                {
                    schemaRenames.TryAdd(schema.Name, now.Name);
                    var o = new MigrationObject { Kind = "schema", Key = schema.Key, Name = schema.Name };
                    Add("\u0000" + schema.Key, "", RankRename, new Migration
                    {
                        Id = "", Database = input.DatabaseId, Fact = MigrationFact.Rename, Object = o,
                        From = new MigrationState { Name = schema.Name }, To = new MigrationState { Name = now.Name },
                    });
                }
            }

            string? AtTime(string? schema) => schema is not null && schemaRenames.TryGetValue(schema, out var renamed) ? renamed : schema;

            var afterTables = after.Tables.ToDictionary(t => t.Key, StringComparer.Ordinal);
            foreach (var table in before.Tables.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                var schema = AtTime(table.Schema);
                if (!afterTables.TryGetValue(table.Key, out var now))
                {
                    Add(table.Key, "", RankDrop, new Migration
                    {
                        Id = "", Database = input.DatabaseId, Fact = MigrationFact.Drop,
                        Object = new MigrationObject { Kind = "table", Key = table.Key, Schema = schema, Name = table.Name },
                    });
                    continue;
                }

                if (!string.Equals(table.Name, now.Name, StringComparison.Ordinal) || !string.Equals(schema, now.Schema, StringComparison.Ordinal))
                {
                    Add(table.Key, "", RankRename, new Migration
                    {
                        Id = "", Database = input.DatabaseId, Fact = MigrationFact.Rename,
                        Object = new MigrationObject { Kind = "table", Key = table.Key, Schema = schema, Name = table.Name },
                        From = new MigrationState { Name = table.Name, Schema = schema }, To = new MigrationState { Name = now.Name, Schema = now.Schema },
                    });
                }

                CompareColumns(table, now);
            }

            var afterViews = after.Views.ToDictionary(v => v.Key, StringComparer.Ordinal);
            foreach (var view in before.Views.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                var schema = AtTime(view.Schema);
                if (afterViews.TryGetValue(view.Key, out var now)
                    && (!string.Equals(view.Name, now.Name, StringComparison.Ordinal) || !string.Equals(schema, now.Schema, StringComparison.Ordinal)))
                {
                    Add(view.Key, "", RankRename, new Migration
                    {
                        Id = "", Database = input.DatabaseId, Fact = MigrationFact.Rename,
                        Object = new MigrationObject { Kind = "view", Key = view.Key, Schema = schema, Name = view.Name },
                        From = new MigrationState { Name = view.Name, Schema = schema }, To = new MigrationState { Name = now.Name, Schema = now.Schema },
                    });
                }
            }

            var afterSequences = after.Sequences.ToDictionary(s => s.Key, StringComparer.Ordinal);
            foreach (var sequence in before.Sequences.OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                var schema = AtTime(sequence.Schema);
                var o = new MigrationObject { Kind = "sequence", Key = sequence.Key, Schema = schema, Name = sequence.Name };
                if (!afterSequences.TryGetValue(sequence.Key, out var now))
                {
                    Add(sequence.Key, "", RankDrop, new Migration { Id = "", Database = input.DatabaseId, Fact = MigrationFact.Drop, Object = o });
                }
                else if (!string.Equals(sequence.Name, now.Name, StringComparison.Ordinal) || !string.Equals(schema, now.Schema, StringComparison.Ordinal))
                {
                    Add(sequence.Key, "", RankRename, new Migration
                    {
                        Id = "", Database = input.DatabaseId, Fact = MigrationFact.Rename, Object = o,
                        From = new MigrationState { Name = sequence.Name, Schema = schema }, To = new MigrationState { Name = now.Name, Schema = now.Schema },
                    });
                }
            }
        }

        private void CompareColumns(SnapshotTable table, SnapshotTable now)
        {
            var afterColumns = now.Columns.ToDictionary(c => c.Key, StringComparer.Ordinal);
            foreach (var column in table.Columns.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                // Members carry the names at the time: after the table's own rename (it comes first), and after the column's rename.
                MigrationObject Member(string name) => new()
                {
                    Kind = "column", Table = table.Key, Key = column.Key, Schema = now.Schema, TableName = now.Name, Name = name,
                };

                if (!afterColumns.TryGetValue(column.Key, out var current))
                {
                    Add(table.Key, column.Key, RankDrop, new Migration { Id = "", Database = input.DatabaseId, Fact = MigrationFact.Drop, Object = Member(column.Name) });
                    continue;
                }

                if (!string.Equals(column.Name, current.Name, StringComparison.Ordinal))
                {
                    Add(table.Key, column.Key, RankRename, new Migration
                    {
                        Id = "", Database = input.DatabaseId, Fact = MigrationFact.Rename, Object = Member(column.Name),
                        From = new MigrationState { Name = column.Name }, To = new MigrationState { Name = current.Name },
                    });
                }

                // A reference column's strategy and bound table are part of its state, so changing them alone is a conversion; the
                // bound table's key is read after the write's key maps, as the column's own key is.
                var from = TypeState(column) with { ReferenceTable = column.ReferenceTable is { } bound ? _tableMoves.GetValueOrDefault(bound) ?? bound : null };
                var to = TypeState(current);
                var change = Widening.Classify(input.Dialect, from, to);
                if (change != TypeChange.Same)
                    ChangeType(table.Key, Member(current.Name), from, to, change);

                if (column.Nullable && !current.Nullable)
                    Add(table.Key, column.Key, RankBackfill, new Migration { Id = "", Database = input.DatabaseId, Fact = MigrationFact.Backfill, Object = Member(current.Name) });
            }
        }

        /// <summary>A column's type change: a pending fact follows it, else narrowing and conversion record a <c>change-type</c>.</summary>
        private void ChangeType(string tableKey, MigrationObject o, MigrationState from, MigrationState to, TypeChange change)
        {
            if (Explained(MigrationFact.ChangeType, o))
                return;
            if (LastFactOf(o) is (var position, { Fact: MigrationFact.ChangeType, Held: false, Conversion: null, DataCheck: null, From: { } pendingFrom } pending))
            {
                // The pending fact's sides read its bound table's key today, as its object's key is read.
                var net = Widening.Classify(input.Dialect, CurrentState(pendingFrom, position), to);
                if (net is TypeChange.Same or TypeChange.Widening)
                    return; // back at its from, or a widening of it: stale (MQ4103), and fold removes it
                if (pending.To is null || !SameState(CurrentState(pending.To, position), to))
                    _updated[pending.Id] = (_updated.GetValueOrDefault(pending.Id) ?? pending) with { To = to };
                return;
            }

            if (change is TypeChange.Same or TypeChange.Widening)
                return;
            Add(tableKey, o.Key, RankChangeType, new Migration
            {
                Id = "", Database = input.DatabaseId, Fact = MigrationFact.ChangeType, Object = o, From = from, To = to,
            }, explainedChecked: true);
        }

        private static MigrationState TypeState(SnapshotColumn c) => new()
        {
            Type = c.Type, Length = c.Length, Precision = c.Precision, Scale = c.Scale, NativeType = c.NativeType,
            Strategy = c.Strategy, ReferenceTable = c.ReferenceTable,
        };

        private static bool SameState(MigrationState a, MigrationState b) => string.Equals(a.Type, b.Type, StringComparison.Ordinal)
            && a.Length == b.Length && a.Precision == b.Precision && a.Scale == b.Scale && string.Equals(a.NativeType, b.NativeType, StringComparison.Ordinal)
            && Widening.SameReference(a, b);

        /// <summary>A recorded side with its bound table's key moved to today's, as the key of the fact at <paramref name="position"/> moves.</summary>
        private MigrationState CurrentState(MigrationState state, int position) => state.ReferenceTable is { } bound
            ? state with { ReferenceTable = Current(new MigrationObject { Kind = "table", Key = bound }, position).Key }
            : state;

        private void Add(string group, string member, int rank, Migration fact, bool explainedChecked = false)
        {
            if (!explainedChecked && Explained(fact.Fact, fact.Object!))
                return;
            _candidates.Add(new Candidate(group, member, rank, fact));
        }

        /// <summary>Whether a fact the write itself creates or updates records the same change: the same fact type on the same object.</summary>
        private bool Explained(MigrationFact type, MigrationObject o) => _explained.Count > 0 && _explained.Contains((type, Identity(o)));

        /// <summary>The last open fact in applied order whose object is <paramref name="o"/>, by its key today, and its position.</summary>
        private (int Position, Migration Fact)? LastFactOf(MigrationObject o) =>
            _lastByObject.TryGetValue(Identity(o), out var position) ? (position, _ordered[position]) : null;

        // ---- keys today and groups ----

        /// <summary>An object's keys today: through the open <c>rekey</c> facts after the fact, then through the write's new one.</summary>
        private MigrationObject Current(MigrationObject o, int position)
        {
            var (table, key) = _moves.Current(position, o.Kind, o.Table, o.Key);
            if (_newMoves is not null)
                (table, key) = _newMoves.Move(o.Kind, table, key);
            return o with { Table = table, Key = key };
        }

        private List<(string Kind, string? Table, string Key)> CurrentObjects(Migration fact, int position)
        {
            var result = new List<(string, string?, string)>();
            if (fact.Object is { } single)
                result.Add(Identity(Current(single, position)));
            foreach (var o in fact.Objects)
                result.Add(Identity(Current(o, position)));
            if (fact.Fact == MigrationFact.Rekey)
            {
                var own = FactKeys.RekeyMap.Of(fact);
                foreach (var key in fact.Keys)
                {
                    // A rekey belongs to every table (and sequence and SQL object) whose keys it moved, as they read today.
                    var (kind, objectKey) = RekeyedObject(own, key);
                    var moved = Current(new MigrationObject { Kind = kind, Key = objectKey }, position);
                    result.Add(Identity(moved));
                }
            }

            return result;
        }

        private List<(string Kind, string? Table, string Key)> RekeyObjects(Migration rekey)
        {
            var own = FactKeys.RekeyMap.Of(rekey);
            var result = new List<(string, string?, string)>();
            foreach (var key in rekey.Keys)
            {
                var (kind, objectKey) = RekeyedObject(own, key);
                result.Add((kind, null, objectKey));
            }

            return result;
        }

        /// <summary>
        /// The object one key of a <c>rekey</c> fact belongs to, after the fact: a table, sequence or SQL object itself, a column its
        /// table (moved by <paramref name="own"/>, the fact's own moves).
        /// </summary>
        private static (string Kind, string Key) RekeyedObject(FactKeys.RekeyMap own, MigrationRekey key) => key.Kind switch
        {
            "table" or "sequence" or "sql-object" => (key.Kind, key.To),
            _ => ("table", own.Move("table", null, key.Table ?? key.From).Key),
        };

        /// <summary>An object's identity: a member by its table's key and its own, any other object by its kind and key.</summary>
        private static (string Kind, string? Table, string Key) Identity(MigrationObject o) =>
            FactKeys.MemberKinds.Contains(o.Kind) ? (o.Kind, o.Table, o.Key) : (o.Kind, null, o.Key);
    }

    /// <summary>A fact to record, with its place in the write's order.</summary>
    private sealed record Candidate(string Group, string Member, int Rank, Migration Fact);

    /// <summary>
    /// The heads of the database's open facts per table: the facts of a table and of its members that no other such fact follows
    /// (directly or through other facts). Each new fact follows the heads of its table and becomes its only head. Facts are indexed by
    /// group once; a group's heads are found once per write, by one walk of the <c>after</c> links from all its facts at once
    /// (<see cref="Unreached"/>), and kept: a fact chained into one group becomes that group's only head, and a fact chained into
    /// several (the write's <c>rekey</c>) has its groups' heads found again when a later fact needs them. A write that touches a
    /// few tables so walks the links a few times, never once per fact of a table.
    /// </summary>
    private sealed class Heads
    {
        private readonly Dictionary<string, List<string>> _byGroup = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<string>> _after = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _heads = new(StringComparer.Ordinal);

        public Heads(IReadOnlyList<Migration> ordered, Dictionary<string, List<(string Kind, string? Table, string Key)>> objects)
        {
            foreach (var fact in ordered)
            {
                _after[fact.Id] = fact.After;
                Index(fact.Id, Groups(objects[fact.Id]));
            }
        }

        /// <summary>Sets <paramref name="fact"/>'s <c>after</c> to the heads of its groups and adds it.</summary>
        public Migration Chain(Migration fact, IReadOnlyList<(string Kind, string? Table, string Key)> objects)
        {
            var groups = Groups(objects);
            List<string> heads;
            if (groups.Count == 1)
            {
                heads = [.. HeadsOf(groups.First())];
            }
            else
            {
                var members = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var group in groups)
                {
                    if (!_byGroup.TryGetValue(group, out var ids))
                        continue;
                    foreach (var id in ids)
                    {
                        if (seen.Add(id))
                            members.Add(id);
                    }
                }

                heads = Unreached(members);
            }

            heads.Sort(StringComparer.Ordinal);
            _after[fact.Id] = heads;
            Index(fact.Id, groups);
            foreach (var group in groups)
            {
                // In one group the new fact follows every head, so it is the only one; across groups a fact that another group's
                // fact reaches may have been left out of its after, so those groups are walked again when needed.
                if (groups.Count == 1)
                    _heads[group] = [fact.Id];
                else
                    _heads.Remove(group);
            }

            return fact with { After = heads };
        }

        private List<string> HeadsOf(string group)
        {
            if (!_heads.TryGetValue(group, out var heads))
                _heads[group] = heads = _byGroup.TryGetValue(group, out var ids) ? Unreached(ids) : [];
            return heads;
        }

        private void Index(string id, HashSet<string> groups)
        {
            foreach (var group in groups)
            {
                if (!_byGroup.TryGetValue(group, out var ids))
                    _byGroup[group] = ids = [];
                ids.Add(id);
            }
        }

        /// <summary>
        /// The members that no other member reaches through <c>after</c> links among the known facts, in their order. One walk from
        /// every member at once, each step carrying the member it started from; a fact keeps the first member that reached it and
        /// whether a second one did, so it is visited at most twice. A member reached only from itself (a cycle, MQ4104) stays a
        /// head.
        /// </summary>
        private List<string> Unreached(List<string> members)
        {
            if (members.Count <= 1)
                return [.. members];
            var first = new Dictionary<string, string>(StringComparer.Ordinal);
            var twice = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<(string Fact, string From)>();
            foreach (var member in members)
                Step(member, member);
            while (queue.Count > 0)
            {
                var (fact, from) = queue.Dequeue();
                Step(fact, from);
            }

            return [.. members.Where(m => !twice.Contains(m) && (!first.TryGetValue(m, out var by) || string.Equals(by, m, StringComparison.Ordinal)))];

            void Step(string fact, string from)
            {
                foreach (var link in _after[fact])
                {
                    if (!_after.ContainsKey(link))
                        continue;
                    if (!first.TryGetValue(link, out var by))
                    {
                        first[link] = from;
                        queue.Enqueue((link, from));
                    }
                    else if (!string.Equals(by, from, StringComparison.Ordinal) && twice.Add(link))
                    {
                        queue.Enqueue((link, from));
                    }
                }
            }
        }

        /// <summary>The tables (or other objects) a fact's objects belong to.</summary>
        private static HashSet<string> Groups(IEnumerable<(string Kind, string? Table, string Key)> objects)
        {
            var groups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (kind, table, key) in objects)
            {
                groups.Add(kind switch
                {
                    "table" => "t:" + key,
                    "schema" => "s:" + key,
                    _ when table is not null => "t:" + table,
                    _ => "o:" + key,
                });
            }

            return groups;
        }
    }
}
