using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;

namespace Maquettiste.Engine.Migrations;

/// <summary>
/// Stale migration facts (next-phase-1-design.md section 3.2, MQ4103): open facts whose effect the model no longer shows. Two
/// judgments share the reading of a database's capture under each fact's keys today (<see cref="FactKeys"/>):
/// <list type="bullet">
/// <item><description>a <b>pending</b> fact, alone, by the validator: its object shows its <c>from</c> again, or a state its
/// <c>from</c> widens to (a type changed back or widened back, a dropped object that is back, a column made nullable again). Pending
/// facts never reach a migration, so this needs no baseline (<see cref="PendingAsync"/>);</description></item>
/// <item><description><b>chains</b> of the facts a baseline lacks, by the store: per object (by its key today) and per aspect (its
/// name, its type, its nullability, its existence), the candidates compose in applied order into one net change from the first
/// candidate's <c>from</c> to the object's state in the working model; the chain is stale when that net change needs no fact (the
/// same state, or a widening). Candidate <c>rekey</c> facts are stale together when their moves, composed, bring every key back
/// (<see cref="Chains"/>).</description></item>
/// </list>
/// A chain whose object is gone (renamed, then dropped) is not stale: fold joins it. Facts in the baseline are never candidates. A
/// candidate <c>value-map</c> is judged against a baseline model only (next-phase-2-design.md section 3.5, decision 11): it is stale
/// when its type held none of its <c>from</c> codes there and its names did not change, so no database stores what it maps; with only a
/// committed physical snapshot, which holds no rows, it is never stale.
/// </summary>
internal static class StaleFacts
{
    /// <summary>A stale fact and why.</summary>
    /// <param name="Fact">The fact.</param>
    /// <param name="Message">The MQ4103 message.</param>
    internal sealed record StaleFact(Migration Fact, string Message);

    /// <summary>
    /// The validator's judgment of pending facts: the databases that hold a pending fact of <paramref name="pending"/> are resolved
    /// (one conceptual run) and captured, and each pending fact whose object shows its <c>from</c> again, or a state its <c>from</c>
    /// widens to, is stale. A database that does not resolve or capture judges none of its facts (they stay MQ4101).
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="pending">The pending facts to judge (not held, intent missing).</param>
    /// <param name="resolver">The resolver.</param>
    /// <param name="parallelism">The capture's parallelism.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The MQ4103 message of each stale fact, by id.</returns>
    public static async Task<IReadOnlyDictionary<string, string>> PendingAsync(ModelSnapshot model, IReadOnlyCollection<Migration> pending,
        IModelResolver resolver, int parallelism, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(resolver);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var databaseIds = pending.Select(f => f.Database).Where(id => model.Get<Database>(id) is not null).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToList();
        if (databaseIds.Count == 0)
            return result;

        IReadOnlyList<RDatabase?> resolved;
        try
        {
            if (resolver is ModelResolver own)
            {
                resolved = (await own.ResolveDatabasesAsync(model, databaseIds, freeze: false, ct).ConfigureAwait(false)).Databases;
            }
            else
            {
                var whole = await resolver.ResolveAsync(model, null, ct).ConfigureAwait(false);
                resolved = [.. databaseIds.Select(id => whole.Databases.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal)))];
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return result; // a shape the resolver cannot take: every pending fact stays MQ4101
        }

        var judged = pending.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < databaseIds.Count; i++)
        {
            if (resolved[i] is not { } database)
                continue;
            PhysicalSnapshot capture;
            try
            {
                capture = SnapshotCapture.Capture(database, 0, parallelism, ct);
            }
            catch (ArgumentException)
            {
                continue;
            }

            var dialect = model.Get<Database>(databaseIds[i])!.Dialect;
            var ordered = FactKeys.Order(model.All<Migration>().Where(f => string.Equals(f.Database, databaseIds[i], StringComparison.Ordinal)));
            var today = new Today(ordered, capture);
            for (var position = 0; position < ordered.Count; position++)
            {
                var fact = ordered[position];
                if (judged.Contains(fact.Id) && PendingStale(fact, position, today, dialect) is { } why)
                    result[fact.Id] = why;
            }
        }

        return result;
    }

    /// <summary>
    /// The stale chains of a database's candidate facts (the open facts a baseline lacks), judged against its capture in the working
    /// model, in applied order.
    /// </summary>
    /// <param name="ordered">The database's open facts in applied order (<see cref="FactKeys.Order"/>).</param>
    /// <param name="isCandidate">Whether a fact is a candidate (the baseline lacks it).</param>
    /// <param name="capture">The database's capture in the working model.</param>
    /// <param name="dialect">The database's dialect.</param>
    /// <param name="baseline">The baseline model's reference codes, which judge value maps; <see langword="null"/> (no baseline model):
    /// no value map is stale.</param>
    /// <returns>The stale facts, in applied order.</returns>
    public static IReadOnlyList<StaleFact> Chains(IReadOnlyList<Migration> ordered, Func<Migration, bool> isCandidate, PhysicalSnapshot capture, Dialect dialect,
        BaselineCodes? baseline = null)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(isCandidate);
        ArgumentNullException.ThrowIfNull(capture);
        var today = new Today(ordered, capture);
        var chains = new Dictionary<(string Aspect, string Kind, string? Table, string Key), List<int>>();
        var order = new List<(string, string, string?, string)>();
        var rekeys = new List<int>();
        var valueMaps = new SortedDictionary<int, StaleFact>();
        for (var position = 0; position < ordered.Count; position++)
        {
            var fact = ordered[position];
            if (!isCandidate(fact))
                continue;
            if (fact.Fact == MigrationFact.Rekey)
            {
                rekeys.Add(position);
                continue;
            }

            if (fact.Fact == MigrationFact.ValueMap)
            {
                if (baseline is not null && ValueMapStale(fact, baseline) is { } unstored)
                    valueMaps[position] = unstored;
                continue;
            }

            var aspect = fact.Fact switch
            {
                MigrationFact.Rename => "name",
                MigrationFact.ChangeType => "type",
                MigrationFact.Backfill => "null",
                MigrationFact.Drop => "drop",
                _ => null,
            };
            if (aspect is null || fact.Object is not { } o)
                continue;
            var (table, key) = today.Moves.Current(position, o.Kind, o.Table, o.Key);
            var group = (aspect, o.Kind, FactKeys.MemberKinds.Contains(o.Kind) ? table : null, key);
            if (!chains.TryGetValue(group, out var members))
            {
                chains[group] = members = [];
                order.Add(group);
            }

            members.Add(position);
        }

        var stale = new SortedDictionary<int, StaleFact>();
        foreach (var group in order)
        {
            var members = chains[group];
            var first = members[0];
            if (ChainStale(ordered[first], first, group.Item1, today, dialect) is not { } why)
                continue;
            var ids = string.Join(", ", members.Select(p => ordered[p].Id));
            foreach (var position in members)
            {
                var fact = ordered[position];
                stale[position] = new StaleFact(fact, members.Count == 1
                    ? $"The {Name(fact)} of {Describe(fact.Object!)} is stale: {why}, so no migration needs it; migration fold removes it."
                    : $"The {Name(fact)} of {Describe(fact.Object!)} is stale: with facts {ids} it nets to nothing ({why}), so no migration needs them; migration fold removes them.");
            }
        }

        foreach (var (position, fact) in valueMaps)
            stale[position] = fact;
        if (rekeys.Count > 0 && RekeysCancel(ordered, rekeys))
        {
            var ids = string.Join(", ", rekeys.Select(p => ordered[p].Id));
            foreach (var position in rekeys)
            {
                stale[position] = new StaleFact(ordered[position],
                    $"The rekey is stale: with facts {ids} every key it moved is back where it was (a materialize made and undone), so no migration needs them; migration fold removes them.");
            }
        }

        return [.. stale.Values];
    }

    /// <summary>
    /// A value map that maps nothing a database can store (section 3.5, decision 11): its names did not change, and the type whose codes it
    /// maps (a merge's merged-away type, else its type) held none of its <c>from</c> codes in the baseline model; or <see langword="null"/>.
    /// </summary>
    private static StaleFact? ValueMapStale(Migration fact, BaselineCodes baseline)
    {
        if (!string.Equals(fact.From?.Name, fact.To?.Name, StringComparison.Ordinal)
            || !string.Equals(fact.From?.PluralName, fact.To?.PluralName, StringComparison.Ordinal))
            return null;
        if ((fact.FromReferenceType ?? fact.ReferenceType) is not { } type || fact.Values.Any(v => baseline.MayHold(type, v.From)))
            return null;
        var name = fact.From?.Name ?? fact.To?.Name ?? type;
        var codes = string.Join(", ", fact.Values.Select(v => "'" + v.From + "'"));
        return new StaleFact(fact,
            $"The value map of reference type '{name}' is stale: in the baseline model the type held none of the codes it maps ({codes}) and its names did not change, so no database stores them; migration fold removes it.");
    }

    /// <summary>Why a pending fact is stale, or <see langword="null"/> when it is not.</summary>
    private static string? PendingStale(Migration fact, int position, Today today, Dialect dialect)
    {
        if (fact.Object is not { } o)
            return null;
        var (table, key) = today.Moves.Current(position, o.Kind, o.Table, o.Key);
        string Subject() => $"The pending {Name(fact)} of {Describe(o)} is stale";
        switch (fact.Fact)
        {
            case MigrationFact.ChangeType when fact.From is { } from && today.Column(table, key) is { } column:
                var change = Widening.Classify(dialect, today.State(from, position), TypeState(column));
                return change switch
                {
                    TypeChange.Same => $"{Subject()}: the column shows its from type again, so no migration needs it; migration fold removes it.",
                    TypeChange.Widening => $"{Subject()}: the column's type widens its from type, so no migration needs it; migration fold removes it.",
                    _ => null,
                };
            case MigrationFact.Drop when today.Exists(o.Kind, table, key):
                return $"{Subject()}: the {o.Kind} is back, so no migration drops it; migration fold removes the fact.";
            case MigrationFact.Backfill when today.Column(table, key) is { Nullable: true }:
                return $"{Subject()}: the column is nullable again, so no migration needs a value; migration fold removes the fact.";
            default:
                return null;
        }
    }

    /// <summary>Why a chain whose first candidate is <paramref name="first"/> nets to nothing, or <see langword="null"/>.</summary>
    private static string? ChainStale(Migration first, int position, string aspect, Today today, Dialect dialect)
    {
        var o = first.Object!;
        var (table, key) = today.Moves.Current(position, o.Kind, o.Table, o.Key);
        switch (aspect)
        {
            case "name" when first.From is { Name: { } name } from:
                var now = today.Name(o.Kind, table, key);
                if (now is null || !string.Equals(now.Value.Name, name, StringComparison.Ordinal))
                    return null;
                // A schema the fact names is read under the schema renames after it, as the object's schema is named today.
                if (from.Schema is { } schema && o.Kind is "table" or "view" or "sequence"
                    && !string.Equals(today.SchemaToday(schema, position), now.Value.Schema, StringComparison.Ordinal))
                    return null;
                return $"the {o.Kind} is named '{name}' again";
            case "type" when first.From is { } typeFrom && today.Column(table, key) is { } column:
                return Widening.Classify(dialect, today.State(typeFrom, position), TypeState(column)) switch
                {
                    TypeChange.Same => "the column shows its first type again",
                    TypeChange.Widening => "the column's type widens its first type",
                    _ => null,
                };
            case "null" when today.Column(table, key) is { Nullable: true }:
                return "the column is nullable again";
            case "drop" when today.Exists(o.Kind, table, key):
                return $"the {o.Kind} is back";
            default:
                return null;
        }
    }

    /// <summary>
    /// Whether the candidate <c>rekey</c> facts, composed in applied order from the first, bring every key they move back where it was.
    /// Each key is followed from its value before the chain (the <c>from</c> of the first candidate that lists it, unless an earlier
    /// candidate moved a key there, which is then the same object) through every candidate from there on, and must end where it began.
    /// </summary>
    private static bool RekeysCancel(IReadOnlyList<Migration> ordered, List<int> positions)
    {
        var tracked = new List<(string Kind, string? StartTable, string StartKey, string? Table, string Key)>();
        foreach (var position in positions)
        {
            var fact = ordered[position];
            var current = tracked.Select(t => (t.Kind, t.Table, t.Key)).ToHashSet();
            foreach (var key in fact.Keys)
            {
                if (current.Add((key.Kind, key.Table, key.From)))
                    tracked.Add((key.Kind, key.Table, key.From, key.Table, key.From));
            }

            // The map moves tables, columns, sequences and SQL objects; a constraint id moves by its own entry, under its table's key
            // before the fact (next-phase-2-design.md section 3.1), so a rekey that moves only constraint ids never reads as cancelled.
            var map = FactKeys.RekeyMap.Of(fact);
            var constraints = fact.Keys.Where(k => k.Table is not null && FactKeys.ConstraintKinds.Contains(k.Kind))
                .GroupBy(k => (k.Kind, Table: k.Table!, k.From)).ToDictionary(g => g.Key, g => g.First().To);
            for (var i = 0; i < tracked.Count; i++)
            {
                var key = tracked[i].Table is { } at && constraints.TryGetValue((tracked[i].Kind, at, tracked[i].Key), out var to) ? to : tracked[i].Key;
                var (table, moved) = map.Move(tracked[i].Kind, tracked[i].Table, key);
                tracked[i] = tracked[i] with { Table = table, Key = moved };
            }
        }

        return tracked.TrueForAll(t => string.Equals(t.Key, t.StartKey, StringComparison.Ordinal)
            && string.Equals(t.Table, t.StartTable, StringComparison.Ordinal));
    }

    /// <summary>A schema name recorded by the fact at <paramref name="position"/>, renamed by the schema renames after it in applied order.</summary>
    /// <param name="ordered">A database's open facts in applied order.</param>
    /// <param name="schema">The schema name as the fact recorded it.</param>
    /// <param name="position">The fact's place in the order.</param>
    /// <returns>The schema's name today, as the facts tell it.</returns>
    internal static string SchemaToday(IReadOnlyList<Migration> ordered, string schema, int position)
    {
        for (var i = position + 1; i < ordered.Count; i++)
        {
            if (ordered[i] is { Fact: MigrationFact.Rename, Object.Kind: "schema", From.Name: { } from, To.Name: { } to }
                && string.Equals(from, schema, StringComparison.Ordinal))
                schema = to;
        }

        return schema;
    }

    /// <summary>A column's type state as a <c>change-type</c> side records it.</summary>
    internal static MigrationState TypeState(SnapshotColumn c) => new()
    {
        Type = c.Type, Length = c.Length, Precision = c.Precision, Scale = c.Scale, NativeType = c.NativeType,
        Strategy = c.Strategy, ReferenceTable = c.ReferenceTable,
    };

    private static string Name(Migration fact) => ResolutionValues.Kebab(fact.Fact);

    private static string Describe(MigrationObject o) =>
        o.Kind + " '" + (o.TableName is { Length: > 0 } table && FactKeys.MemberKinds.Contains(o.Kind) ? table + "." : "") + (o.Name ?? o.Key) + "'";

    /// <summary>A database's capture indexed by key, read under the keys facts have today.</summary>
    private sealed class Today
    {
        private readonly IReadOnlyList<Migration> _ordered;
        private readonly Dictionary<string, SnapshotTable> _tables;
        private readonly Dictionary<string, SnapshotView> _views;
        private readonly Dictionary<string, SnapshotSequence> _sequences;
        private readonly Dictionary<string, SnapshotSchema> _schemas;
        private readonly Dictionary<string, Dictionary<string, SnapshotColumn>> _columns = new(StringComparer.Ordinal);

        public Today(IReadOnlyList<Migration> ordered, PhysicalSnapshot capture)
        {
            _ordered = ordered;
            Moves = FactKeys.Moves(ordered);
            _tables = capture.Tables.GroupBy(t => t.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            _views = capture.Views.GroupBy(v => v.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            _sequences = capture.Sequences.GroupBy(s => s.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            _schemas = (capture.Schemas ?? []).GroupBy(s => s.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }

        public FactKeys.KeyMoves Moves { get; }

        public SnapshotColumn? Column(string? table, string key)
        {
            if (table is null || !_tables.TryGetValue(table, out var t))
                return null;
            if (!_columns.TryGetValue(table, out var columns))
                _columns[table] = columns = t.Columns.GroupBy(c => c.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            return columns.GetValueOrDefault(key);
        }

        public bool Exists(string kind, string? table, string key) => kind switch
        {
            "table" => _tables.ContainsKey(key),
            "column" => Column(table, key) is not null,
            "sequence" => _sequences.ContainsKey(key),
            "view" => _views.ContainsKey(key),
            "schema" => _schemas.ContainsKey(key),
            _ => false,
        };

        /// <summary>An object's name and schema today, or <see langword="null"/> when it is gone.</summary>
        public (string Name, string? Schema)? Name(string kind, string? table, string key) => kind switch
        {
            "table" when _tables.TryGetValue(key, out var t) => (t.Name, t.Schema),
            "view" when _views.TryGetValue(key, out var v) => (v.Name, v.Schema),
            "sequence" when _sequences.TryGetValue(key, out var s) => (s.Name, s.Schema),
            "schema" when _schemas.TryGetValue(key, out var schema) => (schema.Name, null),
            "column" when Column(table, key) is { } c => (c.Name, null),
            _ => null,
        };

        /// <summary>A recorded side with its bound table's key moved to today's, as the key of the fact at <paramref name="position"/> moves.</summary>
        public MigrationState State(MigrationState state, int position) => state.ReferenceTable is { } bound
            ? state with { ReferenceTable = Moves.Current(position, "table", null, bound).Key }
            : state;

        /// <summary>A schema name recorded at <paramref name="position"/>, renamed by the schema renames after it in applied order.</summary>
        public string SchemaToday(string schema, int position) => StaleFacts.SchemaToday(_ordered, schema, position);
    }
}

/// <summary>
/// The reference codes of a baseline model (next-phase-2-design.md section 3.5): per reference type, the codes of its written rows there.
/// A type that a seed fills with a rows script may have held any code. Value maps are judged
/// stale against them (<see cref="StaleFacts.Chains"/>), and <c>validate --against</c> reads what a former code was (MQ4112).
/// </summary>
internal sealed class BaselineCodes
{
    private readonly ReferenceDataIndex _data;
    private readonly HashSet<string> _scripted;

    private BaselineCodes(ReferenceDataIndex data, HashSet<string> scripted)
    {
        _data = data;
        _scripted = scripted;
    }

    /// <summary>The codes of a baseline model.</summary>
    /// <param name="baseline">The baseline model.</param>
    /// <returns>Its codes.</returns>
    public static BaselineCodes Of(ModelSnapshot baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var data = baseline.Index.ReferenceData ?? ReferenceDataIndex.Build(baseline.Documents.Select(d => d.Element));
        var scripted = baseline.All<Seed>().Where(s => s.RowsFrom?.Script is not null).Select(s => s.Target).ToHashSet(StringComparer.Ordinal);
        return new BaselineCodes(data, scripted);
    }

    /// <summary>Whether a written row of the type held the code in the baseline (its canonical text).</summary>
    /// <param name="typeId">The reference type's id.</param>
    /// <param name="code">The code.</param>
    /// <returns>Whether it was a live code there.</returns>
    public bool Held(string typeId, string code) => _data.Codes.TryGetValue(typeId, out var codes) && codes.ContainsKey(code);

    /// <summary>Whether the type may have held the code in the baseline: a written row held it, or a rows script fills the type.</summary>
    /// <param name="typeId">The reference type's id.</param>
    /// <param name="code">The code.</param>
    /// <returns>Whether a database built from the baseline may store it.</returns>
    public bool MayHold(string typeId, string code) => _scripted.Contains(typeId) || Held(typeId, code);
}
