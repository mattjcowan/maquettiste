using System.Collections.Frozen;
using System.Globalization;
using Maquettiste.Engine.Migrations;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The rules of migration facts (next-phase-1-design.md section 3.2): MQ4101 (a fact lacks the intent its change needs), MQ4104 (the
/// <c>after</c> links of a database's facts form a cycle), MQ4105 (a link to a fact of another database), MQ4106 (fields that do not
/// fit the fact's type), MQ4107 (two facts change one object and neither comes after the other), MQ4108 (SQL text without an entry for
/// the database's dialect), MQ4111 (a link to a fact that does not exist) and MQ4056 (facts outside PostgreSQL), and MQ4103 for a
/// pending fact the validator judged stale on a resolved database (<see cref="Migrations.StaleFacts.PendingAsync"/>), and MQ4113 (a value
/// map that maps away a live code of its reference type; next-phase-2-design.md section 3.5). The rules that
/// need a baseline (stale chains of complete facts, unexplained changes) are the store's. Facts are judged on their recorded keys; a later
/// <c>rekey</c> fact does not join two keys for MQ4107 here.
/// </summary>
internal static class MigrationRules
{
    /// <summary>The object kinds a table holds as members; a member names its table.</summary>
    private static readonly FrozenSet<string> MemberKinds = new[] { "column", "index", "unique", "foreign-key", "check" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> RenameKinds = new[] { "table", "column", "view", "sequence", "schema" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> DropKinds = new[] { "table", "column", "sequence" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> ColumnKind = new[] { "column" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> RekeyKinds =
        new[] { "table", "column", "index", "unique", "foreign-key", "check", "sequence", "sql-object" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The model's facts with what only the set of them decides: per fact, the MQ4104, MQ4105, MQ4107 and MQ4111 findings. Computed
    /// once per validation (<see cref="Analyze"/>) and read by every fact's validation in parallel; never changed after it is built.
    /// </summary>
    internal sealed class FactGraph
    {
        private readonly Dictionary<string, List<(string Rule, string Message, string Pointer)>> _findings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _stale = new(StringComparer.Ordinal);

        /// <summary>
        /// Marks a pending fact stale (MQ4103 instead of MQ4101): its object shows its <c>from</c> again, or a state its <c>from</c>
        /// widens to. Called before the facts are validated, never during.
        /// </summary>
        /// <param name="factId">The fact's id.</param>
        /// <param name="message">The MQ4103 message.</param>
        internal void MarkStale(string factId, string message) => _stale[factId] = message;

        /// <summary>The MQ4103 message of a pending fact judged stale, or <see langword="null"/>.</summary>
        /// <param name="factId">The fact's id.</param>
        /// <returns>The message.</returns>
        public string? StaleOf(string factId) => _stale.GetValueOrDefault(factId);

        internal void Add(string factId, string rule, string message, string pointer)
        {
            if (!_findings.TryGetValue(factId, out var list))
                _findings[factId] = list = [];
            list.Add((rule, message, pointer));
        }

        /// <summary>The set-level findings of one fact.</summary>
        /// <param name="factId">The fact's id.</param>
        /// <returns>The findings, in the order found.</returns>
        public IReadOnlyList<(string Rule, string Message, string Pointer)> FindingsOf(string factId) =>
            _findings.TryGetValue(factId, out var list) ? list : [];
    }

    /// <summary>The fact's state as every record, answer and line names it (decision 14).</summary>
    /// <param name="fact">The fact.</param>
    /// <param name="dialect">Its database's dialect.</param>
    /// <returns><c>held</c> (held, not released), <c>pending</c> (intent missing), <c>released</c> or <c>complete</c>.</returns>
    internal static string State(Migration fact, Dialect dialect)
    {
        if (fact.Held && !fact.Released)
            return "held";
        if (Missing(fact, dialect).Count > 0)
            return "pending";
        return fact.Held ? "released" : "complete";
    }

    /// <summary>
    /// The intent fields MQ4101 names, whatever the fact's hold: <c>conversion</c> (and <c>dataCheck</c> when the step narrows) for a
    /// <c>change-type</c>, <c>acknowledge</c> for a <c>drop</c>, <c>value</c> for a <c>backfill</c> that has neither a value nor a data
    /// check, <c>skipWhen</c> for a <c>raw</c> fact; empty for the other types and for a fact that has its intent.
    /// </summary>
    /// <param name="fact">The fact.</param>
    /// <param name="dialect">Its database's dialect (the widening table decides a narrowing).</param>
    /// <returns>The missing fields, in that order.</returns>
    internal static IReadOnlyList<string> Missing(Migration fact, Dialect dialect)
    {
        switch (fact.Fact)
        {
            case MigrationFact.ChangeType:
                var missing = new List<string>(2);
                if (fact.Conversion is null)
                    missing.Add("conversion");
                if (fact.DataCheck is null && Narrows(fact, dialect))
                    missing.Add("dataCheck");
                return missing;
            case MigrationFact.Drop when fact.Acknowledge is null:
                return ["acknowledge"];
            case MigrationFact.Backfill when fact.Value.Count == 0 && fact.DataCheck is null:
                return ["value"];
            case MigrationFact.Raw when fact.SkipWhen.Count == 0:
                return ["skipWhen"];
            default:
                return [];
        }
    }

    /// <summary>Whether a <c>change-type</c> fact's step narrows its column (the widening table, PostgreSQL only).</summary>
    internal static bool Narrows(Migration fact, Dialect dialect) =>
        fact is { Fact: MigrationFact.ChangeType, From: { } from, To: { } to } && Widening.Classify(dialect, from, to) == TypeChange.Narrowing;

    /// <summary>The set-level findings of every fact of the model.</summary>
    /// <param name="model">The model.</param>
    /// <returns>The graph.</returns>
    public static FactGraph Analyze(ModelSnapshot model)
    {
        var graph = new FactGraph();
        var facts = model.All<Migration>().OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
        var byId = facts.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal); // a fact to the facts it follows, same database

        foreach (var fact in facts)
        {
            var follows = new List<string>();
            for (var i = 0; i < fact.After.Count; i++)
            {
                var id = fact.After[i];
                var pointer = Ptr.At("/after", i);
                if (!byId.TryGetValue(id, out var other))
                {
                    graph.Add(fact.Id, "MQ4111",
                        $"The fact follows '{id}', which is not a migration fact of the model (folded or removed in another copy); the link is ignored for ordering. Remove it, or name the fact that replaced it.",
                        pointer);
                }
                else if (!string.Equals(other.Database, fact.Database, StringComparison.Ordinal))
                {
                    graph.Add(fact.Id, "MQ4105",
                        $"The fact follows '{id}', a fact of another database; after links order the facts of one database. Remove the link.", pointer);
                }
                else if (!follows.Contains(id, StringComparer.Ordinal))
                {
                    follows.Add(id);
                }
            }

            edges[fact.Id] = follows;
        }

        foreach (var database in facts.GroupBy(f => f.Database, StringComparer.Ordinal))
        {
            var members = database.ToList();
            foreach (var cycle in Cycles(members, edges))
            {
                var names = string.Join(", ", cycle);
                foreach (var id in cycle)
                    graph.Add(id, "MQ4104", $"The after links of facts {names} form a cycle, so no order applies them; remove one of the links.", "/after");
            }

            Conflicts(graph, members, edges);
        }

        return graph;
    }

    /// <summary>Runs the rules on one fact file.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="report">The report of a fact's document.</param>
    /// <param name="graph">The validation's fact graph.</param>
    public static void Validate(ValidationContext context, Report report, FactGraph graph)
    {
        if (report.Document.Element is not Migration fact)
            return;
        var database = context.Model.Get<Database>(fact.Database);
        CheckFields(fact, report);
        CheckLiveCodes(context, fact, report);
        if (database is not null)
        {
            // A pending fact whose change the model no longer shows is stale, never pending (section 3.2).
            if (graph.StaleOf(fact.Id) is { } stale)
                report.Add("MQ4103", stale, "/fact");
            else
                CheckIntent(fact, database.Dialect, report);
            CheckDialectEntries(fact, database, report);
            if (database.Dialect != Dialect.PostgreSql)
            {
                // A rekey writes no DDL on any dialect, so only its ledger row is missing here (section 3.2).
                report.Add("MQ4056", fact.Fact == MigrationFact.Rekey
                    ? Invariant($"Database '{database.Name}' ({DialectInfo.Name(database.Dialect)}) has no migration ledger: a rekey writes no DDL on any dialect, and no ledger rows are written for this fact.")
                    : Invariant($"Database '{database.Name}' ({DialectInfo.Name(database.Dialect)}) has no migration fact steps: its migrations keep TODO comments for this {Name(fact.Fact)} fact, and no ledger rows are written."),
                    "/fact");
            }
        }

        foreach (var (rule, message, pointer) in graph.FindingsOf(fact.Id))
            report.Add(rule, message, pointer);
    }

    // ---- MQ4106: fields that do not fit the type ----

    private static void CheckFields(Migration fact, Report report)
    {
        var what = Name(fact.Fact);
        switch (fact.Fact)
        {
            case MigrationFact.Rename:
                CheckObjectKind(fact, RenameKinds, report);
                if (fact is { From: { } from, To: { } to } && string.Equals(from.Name, to.Name, StringComparison.Ordinal)
                    && string.Equals(from.Schema, to.Schema, StringComparison.Ordinal))
                    report.Add("MQ4106", $"The rename's from and to are the same ('{to.Name}'): a rename changes the name or the schema.", "/to");
                break;
            case MigrationFact.ChangeType:
                CheckObjectKind(fact, ColumnKind, report);
                if (fact is { From: { } fromType, To: { } toType } && SameType(fromType, toType))
                    report.Add("MQ4106", "The change-type's from and to are the same type: a change-type changes the type, the native type, a facet, or a reference column's strategy or bound table.", "/to");
                break;
            case MigrationFact.Drop:
                CheckObjectKind(fact, DropKinds, report);
                break;
            case MigrationFact.Backfill:
                CheckObjectKind(fact, ColumnKind, report);
                break;
            case MigrationFact.ValueMap or MigrationFact.StateMap:
                for (var i = 0; i < fact.Objects.Count; i++)
                {
                    if (!ColumnKind.Contains(fact.Objects[i].Kind))
                        report.Add("MQ4106", $"A {what} fact maps the values of columns; this object is a {fact.Objects[i].Kind}.", Ptr.At("/objects", i) + "/kind");
                }

                CheckValues(fact, report);
                if (fact.Fact == MigrationFact.ValueMap)
                    CheckValueMap(fact, report);
                break;
            case MigrationFact.Rekey:
                CheckKeys(fact, report);
                break;
        }

        if (fact.Object is { } single)
            CheckMembership(single, "/object", report);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < fact.Objects.Count; i++)
        {
            var pointer = Ptr.At("/objects", i);
            CheckMembership(fact.Objects[i], pointer, report);
            if (!seen.Add(Identity(fact.Objects[i])))
                report.Add("MQ4106", $"The {Describe(fact.Objects[i])} is listed twice in objects.", pointer);
        }

        if (fact.Conversion is { } conversion)
        {
            var expression = conversion.Expression.Count > 0;
            if (conversion.Cast == expression)
            {
                report.Add("MQ4106", expression
                    ? "The conversion has both forms: cast true and an expression; keep one."
                    : "The conversion has neither form: set cast to true, or give an expression per dialect.", "/conversion");
            }
        }

        if (fact.DataCheck is { } check)
        {
            var query = check.Query.Count > 0;
            if (query == check.Accept is not null)
            {
                report.Add("MQ4106", query
                    ? "The data check has both forms: a query and accept; keep one."
                    : "The data check has neither form: give a query per dialect whose rows the step must not find, or accept with the reason none is needed.", "/dataCheck");
            }
        }

        if (fact.Released && !fact.Held)
            report.Add("MQ4106", "The fact is released but not held: released lets a held fact run; set held too, or remove released.", "/released");
    }

    private static void CheckObjectKind(Migration fact, FrozenSet<string> kinds, Report report)
    {
        if (fact.Object is { } o && !kinds.Contains(o.Kind))
        {
            report.Add("MQ4106", $"A {Name(fact.Fact)} fact takes an object of kind {string.Join(", ", kinds.Order(StringComparer.Ordinal))}; this one is a {o.Kind}.",
                "/object/kind");
        }
    }

    private static void CheckMembership(MigrationObject o, string pointer, Report report)
    {
        var member = MemberKinds.Contains(o.Kind);
        if (member && o.Table is null)
            report.Add("MQ4106", $"The {o.Kind} '{o.Name ?? o.Key}' names no table: a {o.Kind} is known by its table's key and its own.", pointer);
        else if (!member && o.Table is not null)
            report.Add("MQ4106", $"The {o.Kind} '{o.Name ?? o.Key}' names a table, which only a column, index or constraint has.", pointer + "/table");
    }

    private static void CheckKeys(Migration fact, Report report)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < fact.Keys.Count; i++)
        {
            var key = fact.Keys[i];
            var pointer = Ptr.At("/keys", i);
            if (!RekeyKinds.Contains(key.Kind))
            {
                report.Add("MQ4106", $"A rekey moves the keys of tables, columns, sequences and SQL objects, and the ids of indexes, uniques, foreign keys and checks; this entry is a {key.Kind}.",
                    pointer + "/kind");
            }
            else if (MemberKinds.Contains(key.Kind) && key.Table is null)
            {
                report.Add("MQ4106", $"The {key.Kind} key '{key.From}' names no table: a {key.Kind} entry gives its table's key before the fact.", pointer);
            }
            else if (!MemberKinds.Contains(key.Kind) && key.Table is not null)
            {
                report.Add("MQ4106", $"The {key.Kind} key '{key.From}' names a table, which only a column, index, unique, foreign-key or check entry has.", pointer + "/table");
            }

            if (string.Equals(key.From, key.To, StringComparison.Ordinal))
                report.Add("MQ4106", $"The {key.Kind} key '{key.From}' moves to itself.", pointer + "/to");
            if (!seen.Add(key.Kind + "\u0000" + key.Table + "\u0000" + key.From))
                report.Add("MQ4106", $"The {key.Kind} key '{key.From}' is listed twice.", pointer);
        }
    }

    /// <summary>A value or state map's codes: each stored code once, and never mapped to itself.</summary>
    private static void CheckValues(Migration fact, Report report)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < fact.Values.Count; i++)
        {
            var value = fact.Values[i];
            var pointer = Ptr.At("/values", i);
            if (!codes.Add(value.From))
                report.Add("MQ4106", $"The code '{value.From}' is listed twice in values: each stored code has one replacement.", pointer);
            if (string.Equals(value.From, value.To, StringComparison.Ordinal))
                report.Add("MQ4106", $"The code '{value.From}' maps to itself: a values entry replaces a stored code with another.", pointer + "/to");
        }
    }

    /// <summary>
    /// A value map's shape (next-phase-2-design.md section 3.5): it changes a name or maps codes in a column or collection, no <c>to</c>
    /// is also a <c>from</c> (a chain would not be idempotent), and each collection is listed once.
    /// </summary>
    private static void CheckValueMap(Migration fact, Report report)
    {
        var renames = !string.Equals(fact.From?.Name, fact.To?.Name, StringComparison.Ordinal)
            || !string.Equals(fact.From?.PluralName, fact.To?.PluralName, StringComparison.Ordinal);
        if (!renames && fact.Values.Count == 0)
            report.Add("MQ4106", "The value map maps no code and changes no name: list the codes in values, or give from and to the type's names before and after.", "/values");
        else if (!renames && fact.Objects.Count == 0 && fact.Collections.Count == 0)
            report.Add("MQ4106", "The value map maps codes in no column or collection and changes no name: list the columns in objects or the collections that hold the codes.", "/objects");

        var froms = fact.Values.Select(v => v.From).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < fact.Values.Count; i++)
        {
            var value = fact.Values[i];
            if (!string.Equals(value.From, value.To, StringComparison.Ordinal) && froms.Contains(value.To))
                report.Add("MQ4106", $"The code '{value.From}' maps to '{value.To}', which this value map also maps away: a chain is not idempotent; map each code to its final code.", Ptr.At("/values", i) + "/to");
        }

        var collections = new HashSet<(string, string)>();
        for (var i = 0; i < fact.Collections.Count; i++)
        {
            var collection = fact.Collections[i];
            if (!collections.Add((collection.Table, collection.Attribute)))
                report.Add("MQ4106", $"The collection '{collection.Name ?? collection.Attribute}' of table '{collection.TableName ?? collection.Table}' is listed twice in collections.", Ptr.At("/collections", i));
        }
    }

    // ---- MQ4113: a value map that maps away a live code ----

    /// <summary>
    /// A value map whose <c>from</c> is a live code of its reference type (next-phase-2-design.md section 3.5, decision 3): in an environment
    /// that applies the fact late, the map would move the data of the row that holds the code now. A type the model no longer has is not
    /// judged (the id is historical).
    /// </summary>
    private static void CheckLiveCodes(ValidationContext context, Migration fact, Report report)
    {
        if (fact.Fact != MigrationFact.ValueMap || fact.ReferenceType is not { } typeId || context.Model.Get<ReferenceType>(typeId) is not { } type)
            return;
        for (var i = 0; i < fact.Values.Count; i++)
        {
            var from = fact.Values[i].From;
            if (context.HasLiveCode(type.Id, System.Text.Json.JsonSerializer.SerializeToElement(from)))
            {
                report.Add("MQ4113", $"The value map maps away '{from}', which is a live code of reference type '{type.Name}': applied late, it would move the data of the row that holds the code now. Give that row another code, or fold the fact if it is stale.",
                    Ptr.At("/values", i) + "/from");
            }
        }
    }

    // A reference column's strategy and bound table are part of its type: changing them alone is a change-type (section 3.2).
    private static bool SameType(MigrationState a, MigrationState b) =>
        string.Equals(a.Type, b.Type, StringComparison.Ordinal) && a.Length == b.Length && a.Precision == b.Precision && a.Scale == b.Scale
        && string.Equals(a.NativeType, b.NativeType, StringComparison.Ordinal) && Widening.SameReference(a, b);

    // ---- MQ4101: the intent a change needs ----

    private static void CheckIntent(Migration fact, Dialect dialect, Report report)
    {
        foreach (var (message, pointer) in IntentFindings(fact, dialect))
            report.Add("MQ4101", message, pointer);
    }

    /// <summary>
    /// The MQ4101 findings of one fact, one per missing intent field, at that field's pointer; none for a held fact that is not
    /// released (decision 7). The store reports them on the facts a write records, whose files are not indexed yet.
    /// </summary>
    /// <param name="fact">The fact.</param>
    /// <param name="dialect">Its database's dialect.</param>
    /// <returns>The messages and pointers, in the order of <see cref="Missing"/>.</returns>
    internal static IEnumerable<(string Message, string Pointer)> IntentFindings(Migration fact, Dialect dialect)
    {
        if (fact.Held && !fact.Released)
            yield break; // a held fact is complete for validate until it is released (decision 7)
        var subject = fact.Object is { } o ? $"The {Name(fact.Fact)} of {Describe(o)}" : $"The {Name(fact.Fact)} fact";
        foreach (var field in Missing(fact, dialect))
        {
            var message = field switch
            {
                "conversion" => $"{subject} needs a conversion: set conversion to {{ \"cast\": true }} or give an expression per dialect, or hold the fact.",
                "dataCheck" => Invariant($"{subject} narrows the column ({Type(fact.From!)} to {Type(fact.To!)}), so it also needs a data check: a query per dialect that finds the rows that would not fit, or accept with the reason no check is needed; or hold the fact."),
                "acknowledge" => $"{subject} needs acknowledge: say why the data may go, or hold the fact.",
                "value" => $"{subject} needs a value for the rows that are NULL, or a data check that finds them; or hold the fact.",
                _ => $"{subject} needs a skipWhen guard: a query per dialect whose row means the end state already holds; or hold the fact.",
            };
            yield return (message, "/" + field);
        }
    }

    private static string Type(MigrationState state)
    {
        if (state.NativeType is { } native)
            return native;
        var facets = new List<string>(3);
        if (state.Length is { } length)
            facets.Add(length.ToString(CultureInfo.InvariantCulture));
        if (state.Precision is { } precision)
            facets.Add(precision.ToString(CultureInfo.InvariantCulture));
        if (state.Scale is { } scale)
            facets.Add(scale.ToString(CultureInfo.InvariantCulture));
        return (state.Type ?? "?") + (facets.Count > 0 ? "(" + string.Join(", ", facets) + ")" : "");
    }

    // ---- MQ4108: SQL text for the database's dialect ----

    private static void CheckDialectEntries(Migration fact, Database database, Report report)
    {
        var dialect = DialectInfo.Name(database.Dialect);
        void Check(IReadOnlyDictionary<string, string> map, string pointer, string what)
        {
            if (map.Count > 0 && !map.ContainsKey(dialect) && !map.ContainsKey("*"))
            {
                report.Add("MQ4108", $"The {what} has no entry for {dialect} (the dialect of database '{database.Name}') and no \"*\" entry, so a {dialect} migration has nothing to run.",
                    pointer);
            }
        }

        Check(fact.Value, "/value", "value");
        Check(fact.Where, "/where", "where");
        Check(fact.Sql, "/sql", "sql");
        Check(fact.SkipWhen, "/skipWhen", "skipWhen guard");
        if (fact.Conversion is { } conversion)
            Check(conversion.Expression, "/conversion/expression", "conversion");
        if (fact.DataCheck is { } check)
            Check(check.Query, "/dataCheck/query", "data check");
    }

    // ---- MQ4104: cycles ----

    /// <summary>The cycles of one database's after links (strongly connected components with a link inside), ids ordinal.</summary>
    private static List<List<string>> Cycles(List<Migration> facts, Dictionary<string, List<string>> edges)
    {
        // Tarjan's algorithm, iterative; facts and links are visited in ordinal order, so the result is deterministic.
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var cycles = new List<List<string>>();
        var counter = 0;
        foreach (var start in facts.Select(f => f.Id))
        {
            if (index.ContainsKey(start))
                continue;
            var work = new Stack<(string Node, int Next)>();
            Enter(start);
            work.Push((start, 0));
            while (work.Count > 0)
            {
                var (node, next) = work.Pop();
                var targets = edges[node];
                if (next < targets.Count)
                {
                    work.Push((node, next + 1));
                    var target = targets[next];
                    if (!index.ContainsKey(target))
                    {
                        Enter(target);
                        work.Push((target, 0));
                    }
                    else if (onStack.Contains(target))
                    {
                        low[node] = Math.Min(low[node], index[target]);
                    }

                    continue;
                }

                if (work.Count > 0)
                {
                    var parent = work.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[node]);
                }

                if (low[node] != index[node])
                    continue;
                var component = new List<string>();
                string member;
                do
                {
                    member = stack.Pop();
                    onStack.Remove(member);
                    component.Add(member);
                }
                while (!string.Equals(member, node, StringComparison.Ordinal));

                if (component.Count > 1 || edges[node].Contains(node, StringComparer.Ordinal))
                    cycles.Add([.. component.Order(StringComparer.Ordinal)]);
            }
        }

        return [.. cycles.OrderBy(c => c[0], StringComparer.Ordinal)];

        void Enter(string node)
        {
            index[node] = low[node] = counter++;
            stack.Push(node);
            onStack.Add(node);
        }
    }

    // ---- MQ4107: unordered facts on one object ----

    private static void Conflicts(FactGraph graph, List<Migration> facts, Dictionary<string, List<string>> edges)
    {
        // What each fact changes, by object identity; a table drop also changes every member of its table.
        var byObject = new Dictionary<string, List<(Migration Fact, string Pointer)>>(StringComparer.Ordinal);
        var byTable = new Dictionary<string, List<(Migration Fact, string Pointer)>>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            foreach (var (o, pointer) in Changed(fact))
            {
                Add(byObject, Identity(o), (fact, pointer));
                if (o.Table is { } table && MemberKinds.Contains(o.Kind))
                    Add(byTable, table, (fact, pointer));
            }
        }

        var pairs = new HashSet<(string Later, string Earlier, string Pointer, string Object)>();
        foreach (var group in byObject.Values)
            Pairs(group, group);
        foreach (var fact in facts)
        {
            if (fact is { Fact: MigrationFact.Drop, Object: { Kind: "table" } dropped } && byTable.TryGetValue(dropped.Key, out var members))
                Pairs([(fact, "/object")], members);
        }

        foreach (var (later, earlier, pointer, what) in pairs.OrderBy(p => p.Later, StringComparer.Ordinal).ThenBy(p => p.Earlier, StringComparer.Ordinal)
            .ThenBy(p => p.Pointer, StringComparer.Ordinal))
        {
            graph.Add(later, "MQ4107",
                $"This fact and fact {earlier} both change {what} and neither comes after the other (merged copies changed one object); order them with after, or fold them.",
                pointer);
        }

        void Pairs(List<(Migration Fact, string Pointer)> left, List<(Migration Fact, string Pointer)> right)
        {
            foreach (var (a, aPointer) in left)
            {
                foreach (var (b, bPointer) in right)
                {
                    if (string.Equals(a.Id, b.Id, StringComparison.Ordinal) || Follows(a.Id, b.Id) || Follows(b.Id, a.Id))
                        continue;
                    var aLater = string.CompareOrdinal(a.Id, b.Id) > 0;
                    var (later, earlier, pointer) = aLater ? (a, b, aPointer) : (b, a, bPointer);
                    var o = Changed(later).First(c => c.Pointer == pointer).Object;
                    pairs.Add((later.Id, earlier.Id, pointer, Describe(o)));
                }
            }
        }

        bool Follows(string from, string target)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { from };
            var queue = new Queue<string>([from]);
            while (queue.Count > 0)
            {
                foreach (var next in edges[queue.Dequeue()])
                {
                    if (string.Equals(next, target, StringComparison.Ordinal))
                        return true;
                    if (seen.Add(next))
                        queue.Enqueue(next);
                }
            }

            return false;
        }

        static void Add(Dictionary<string, List<(Migration, string)>> index, string key, (Migration, string) entry)
        {
            if (!index.TryGetValue(key, out var list))
                index[key] = list = [];
            list.Add(entry);
        }
    }

    /// <summary>The objects a fact changes, with their pointers: a rename's, type change's, drop's or backfill's object, a map's columns.</summary>
    private static IEnumerable<(MigrationObject Object, string Pointer)> Changed(Migration fact)
    {
        switch (fact.Fact)
        {
            case MigrationFact.Rename or MigrationFact.ChangeType or MigrationFact.Drop or MigrationFact.Backfill when fact.Object is { } o:
                yield return (o, "/object");
                break;
            case MigrationFact.ValueMap or MigrationFact.StateMap:
                for (var i = 0; i < fact.Objects.Count; i++)
                    yield return (fact.Objects[i], Ptr.At("/objects", i));
                break;
        }
    }

    /// <summary>An object's identity: a member by its table's key and its own, any other object by its key.</summary>
    private static string Identity(MigrationObject o) =>
        MemberKinds.Contains(o.Kind) ? "m\u0000" + o.Kind + "\u0000" + o.Table + "\u0000" + o.Key : "o\u0000" + o.Key;

    private static string Describe(MigrationObject o) =>
        o.Kind + " '" + (o.TableName is { } table && MemberKinds.Contains(o.Kind) ? table + "." : "") + (o.Name ?? o.Key) + "'";

    private static string Name(MigrationFact fact) => fact switch
    {
        MigrationFact.Rename => "rename",
        MigrationFact.Rekey => "rekey",
        MigrationFact.ChangeType => "change-type",
        MigrationFact.Drop => "drop",
        MigrationFact.Adopt => "adopt",
        MigrationFact.Baseline => "baseline",
        MigrationFact.Backfill => "backfill",
        MigrationFact.Raw => "raw",
        MigrationFact.ValueMap => "value-map",
        _ => "state-map",
    };

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
