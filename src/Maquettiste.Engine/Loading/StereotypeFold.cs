using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;

namespace Maquettiste.Engine.Loading;

/// <summary>
/// The batch operation <c>fold-into-stereotype</c> (next-phase-2-design.md section 3.1, "Fold into a stereotype"): adopts a stereotype's
/// members in designed and imported tables that hold the same structure as their own. Per target table it carries the stereotype,
/// matches own columns to the stereotype's by name and own keys, indexes and checks structurally (after mapping the folded columns to
/// their keyed ids), rewrites every reference to a moved id to the inherited member's keyed id, removes the own entries, and excludes
/// what the table lacks when asked to. The caller resolves the databases before and after and hands them to <see cref="Verify"/> and
/// <see cref="Confirm"/>, which hold the fold to its promise: each target's capture equals its capture before, moved through the fold's
/// key map, column positions aside. A difference in a redefinable facet becomes a <c>redefines</c> entry with the own value when
/// <c>redefine</c> is set; every other difference refuses the table. Everything is planned against one snapshot; no id is minted.
/// </summary>
internal sealed class StereotypeFold
{
    /// <summary>The annotations an own column may carry only when its member carries the same (the fold never drops documentation).</summary>
    private static readonly string[] Annotations = ["displayName", "pluralName", "description", "rationale", "decision", "status", "stereotypes", "tags", "category", "properties"];

    private readonly ModelSnapshot _snapshot;
    private readonly Stereotype _stereotype;
    private readonly bool _redefine;
    private readonly bool _excludeMissing;
    private readonly IReadOnlyList<Table> _targets;
    private readonly IReadOnlyDictionary<string, string?> _written;
    private readonly Dictionary<string, TableFold> _folds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _refusals = new(StringComparer.Ordinal);
    private readonly List<string> _refusalOrder = [];
    private readonly Dictionary<string, (string Hash, JsonObject Node, string Because)> _work = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly HashSet<string> _memberColumns;
    private readonly Dictionary<string, JsonObject> _parsed = new(StringComparer.Ordinal);

    private StereotypeFold(ModelSnapshot snapshot, Stereotype stereotype, IReadOnlyList<Table> targets, bool redefine, bool excludeMissing,
        IReadOnlyDictionary<string, string?> written)
    {
        _snapshot = snapshot;
        _written = written;
        _stereotype = stereotype;
        _targets = targets;
        _redefine = redefine;
        _excludeMissing = excludeMissing;
        _memberColumns = stereotype.Columns.Select(c => c.Id).OfType<string>().Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The databases of the target tables, by id (ordinal): the only ones the fold resolves.</summary>
    public IReadOnlyList<string> Databases => [.. _targets.Select(t => t.Database).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>Why the operation or each refused table is refused, one message per table, in target order; empty when it applies.</summary>
    public IReadOnlyList<string> Refusals => [.. _refusalOrder.Select(id => _refusals[id])];

    /// <summary>Whether <see cref="Verify"/> wrote a redefinition, so the candidate must be resolved and confirmed again.</summary>
    public bool Redefined { get; private set; }

    /// <summary>
    /// Reads the operation: the stereotype (it applies to tables and has members) and exactly one of the tables (designed or imported
    /// table files) and the schema (every designed or imported table file of its database whose stated schema is it, or none stated when it
    /// is the default). A target that another operation of the batch writes, or the stereotype itself written so, refuses the operation;
    /// so does, once planned, a document whose references the fold rewrites (<see cref="Plan"/>).
    /// </summary>
    /// <param name="snapshot">The model as loaded.</param>
    /// <param name="o">The operation, its id fields resolved to ids.</param>
    /// <param name="written">
    /// The ids the batch's other operations write, each with the pointer of the operation that names it (an update or a delete), or
    /// <see langword="null"/> when an expanded operation writes it.
    /// </param>
    /// <param name="refusals">Why the operation is refused, one message per refused table; empty when the fold can be planned.</param>
    /// <returns>The fold, or <see langword="null"/> when refused.</returns>
    public static StereotypeFold? Read(ModelSnapshot snapshot, BatchOperation o, IReadOnlyDictionary<string, string?> written, out IReadOnlyList<string> refusals)
    {
        var problems = new List<string>();
        refusals = problems;
        if (o.Id is null || snapshot.Get<Stereotype>(o.Id) is not { } stereotype)
        {
            problems.Add($"'{o.Id}' is not a stereotype: name the stereotype whose members the tables adopt.");
            return null;
        }

        if (!stereotype.AppliesTo.Contains("table", StringComparer.Ordinal))
            problems.Add($"Stereotype '{stereotype.Key}' does not apply to tables, so it has no members to fold into.");
        else if (stereotype.Columns.Count == 0 && stereotype.PrimaryKey is null && stereotype.Uniques.Count == 0 && stereotype.ForeignKeys.Count == 0
            && stereotype.Checks.Count == 0 && stereotype.Indexes.Count == 0)
            problems.Add($"Stereotype '{stereotype.Key}' has no members (columns, keys, indexes or checks) to fold into; add them to the stereotype first.");
        if (written.ContainsKey(stereotype.Id))
            problems.Add($"Stereotype '{stereotype.Key}' is also written by {WrittenBy(written, stereotype.Id)}; fold in a batch of its own.");
        if ((o.Tables is null) == (o.Schema is null))
            problems.Add("Name exactly one of tables and schema: the tables to fold, or the schema whose tables are folded.");
        if (problems.Count > 0)
            return null;

        var targets = new List<Table>();
        if (o.Tables is { } tables)
        {
            foreach (var id in tables.Distinct(StringComparer.Ordinal))
            {
                if (snapshot.Get<Table>(id) is not { } table)
                    problems.Add($"'{id}' is not a table.");
                else if (table.Origin == TableOrigin.Synthesized)
                    problems.Add($"Table '{ChangePlanner.ReadableName(snapshot, table)}' is an overlay of a synthesized table, whose columns come from attributes: materialize it first, then fold the designed table.");
                else
                    targets.Add(table);
            }
        }
        else if (snapshot.All<Database>().FirstOrDefault(d => d.Schemas.Any(s => s.Id == o.Schema)) is { } database)
        {
            targets.AddRange(snapshot.All<Table>().Where(t => t.Database == database.Id && t.Origin != TableOrigin.Synthesized
                && string.Equals(snapshot.StereotypeDefaults.SchemaOf(t.Database, t.Schema), o.Schema, StringComparison.Ordinal)));
        }
        else
        {
            problems.Add($"'{o.Schema}' is not a schema of a database.");
        }

        foreach (var table in targets.Where(t => written.ContainsKey(t.Id)))
            problems.Add($"Table '{table.Name}' is also written by {WrittenBy(written, table.Id)}; fold in a batch of its own.");
        if (problems.Count > 0)
            return null;
        targets.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name) is var n and not 0 ? n : string.CompareOrdinal(a.Id, b.Id));
        return new StereotypeFold(snapshot, stereotype, targets, o.Redefine == true, o.ExcludeMissing == true, written);
    }

    /// <summary>Names the other operation that writes an id: its pointer when it names the element, else the batch's other operations.</summary>
    private static string WrittenBy(IReadOnlyDictionary<string, string?> written, string id) =>
        written.GetValueOrDefault(id) is { } pointer ? $"another operation of this batch ({pointer})" : "another operation of this batch";

    // ---- steps 1 to 4 -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Plans the fold against the databases as resolved before it: carry, match columns by name and keys, indexes and checks
    /// structurally, rewrite every reference to a moved id, remove the own entries. A table where nothing matches and the stereotype
    /// was not already effective is skipped.
    /// </summary>
    /// <param name="before">The target databases as resolved before the fold, by id.</param>
    public void Plan(IReadOnlyDictionary<string, RDatabase> before)
    {
        // Columns first, for every table: a contributed foreign key may reference a column another target folds.
        foreach (var table in _targets)
        {
            if (!before.TryGetValue(table.Database, out var rdb) || rdb.Tables.FirstOrDefault(t => t.Key == table.Id) is not { } rtable)
            {
                Refuse(table, "it does not resolve, so the fold cannot hold it to its shape.");
                continue;
            }

            var fold = new TableFold(table, rdb, rtable);
            _folds[table.Id] = fold;
            if (table.ExcludeStereotypes?.Contains(_stereotype.Key, StringComparer.Ordinal) == true)
            {
                Refuse(table, $"it excludes stereotype '{_stereotype.Key}' (excludeStereotypes); remove the exclusion first.");
                continue;
            }

            fold.WasEffective = _snapshot.EffectiveStereotypes(table).Any(a => a.Stereotype.Id == _stereotype.Id);
            fold.Excludes.UnionWith(table.Excludes);
            MatchColumns(fold);
        }

        var moved = MovedColumns();
        foreach (var fold in _targets.Where(t => !_refusals.ContainsKey(t.Id)).Select(t => _folds[t.Id]))
        {
            MatchConstraints(fold, moved);
            if (fold.Matched == 0 && !fold.WasEffective)
            {
                fold.Skipped = true;
                continue;
            }

            if (fold.Missing.Count > 0 && !_excludeMissing)
            {
                Refuse(fold.File, $"the fold would add {string.Join(", ", fold.Missing)}, which it lacks; set excludeMissing to exclude them.");
                continue;
            }

            foreach (var id in fold.MissingIds)
                fold.Excludes.Add(id);
        }

        if (_refusals.Count > 0)
            return;
        var changed = _targets.Select(t => _folds[t.Id]).Where(f => !f.Skipped && f.Changes).ToList();
        foreach (var fold in changed)
            Node(fold.File.Id, $"adopts the members of stereotype {_stereotype.Key} in place of its own");
        RewriteReferences(changed);
        foreach (var fold in changed)
            Edit(fold);
    }

    /// <summary>Matches each member column to the own column with its name (ordinal); a member the table excludes is matched too.</summary>
    private void MatchColumns(TableFold fold)
    {
        var table = fold.File;
        foreach (var member in _stereotype.Columns)
        {
            if (string.IsNullOrEmpty(member.Id))
                continue;
            if (table.Columns.FirstOrDefault(c => string.Equals(c.Name, member.Name, StringComparison.Ordinal) && !fold.Columns.ContainsKey(c.Id)) is { } own)
            {
                if (AnnotationsLacking(table, own, member) is { } lacking)
                {
                    Refuse(table, $"its column '{own.Name}' carries {lacking}, which the stereotype's column lacks; move it to the stereotype first, so the fold drops no documentation.");
                    return;
                }

                fold.Columns[own.Id] = ContributedMembers.KeyOf(table.Id, member.Id);
                fold.ColumnPairs.Add((own.Id, member.Id, own.Name));
                fold.Excludes.Remove(member.Id);
                fold.Matched++;
                continue;
            }

            Unmatched(fold, member.Id, $"column '{member.Name}'");
        }
    }

    /// <summary>
    /// Matches the stereotype's primary key, uniques, foreign keys, indexes and checks to the table's own by the properties that form a
    /// capture key, with the folded columns mapped to their keyed ids; a contributed foreign key's target is the carrier's parameter value.
    /// An own primary key matches the stereotype's whatever its columns: a difference in them is a redefinition (or a refusal).
    /// </summary>
    private void MatchConstraints(TableFold fold, IReadOnlyDictionary<string, string> moved)
    {
        var table = fold.File;
        var rtable = fold.Before;
        var dialect = DialectTypeMaps.Name(_snapshot.Get<Database>(table.Database)!.Dialect);
        string K(string id) => _memberColumns.Contains(id) ? ContributedMembers.KeyOf(table.Id, id) : id;
        string M(string key) => moved.TryGetValue(key, out var keyed) ? keyed : key;
        string Join(IEnumerable<string> keys) => string.Join(',', keys);
        string? ForDialect(IReadOnlyDictionary<string, string>? values) =>
            values is null ? null : values.TryGetValue(dialect, out var value) ? value : values.TryGetValue("*", out var any) ? any : null;

        if (_stereotype.PrimaryKey is { Id: { Length: > 0 } pkId })
        {
            if (table.PrimaryKey is { } own && rtable.PrimaryKey is not null)
            {
                fold.PrimaryKey = own.Id ?? "";
                fold.Excludes.Remove(pkId);
                fold.Matched++;
            }
            else
            {
                Unmatched(fold, pkId, "a primary key");
            }
        }

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        void Match(string kind, string memberId, string? signature, IEnumerable<(string Id, string Name, string Signature)> owns, string what)
        {
            if (signature is not null && owns.FirstOrDefault(o => !claimed.Contains(o.Id) && o.Signature == signature) is { Id: not null } own)
            {
                claimed.Add(own.Id);
                fold.Constraints.Add(new MovedConstraint(kind, own.Id, memberId, own.Name));
                fold.Excludes.Remove(memberId);
                fold.Matched++;
                return;
            }

            Unmatched(fold, memberId, what);
        }

        var uniques = rtable.Uniques.Where(u => u.Id is not null && u.MemberOrigin.MemberId is null)
            .Select(u => (u.Id!, u.Name, Join(u.Columns.Select(c => M(c.Key))))).ToList();
        foreach (var u in _stereotype.Uniques.Where(u => u.Id.Length > 0))
            Match("unique", u.Id, Join(u.Columns.Select(K)), uniques, $"unique on {Names(u.Columns)}");

        // A foreign key a relation mapping names stays own: a relation mapping cannot name an inherited foreign key (decision 30).
        // A key an entry's foreignKeys names is mapped too (field "foreignKeys"); a types item's is "foreignKey" (several-types-design.md 19.6).
        var mapped = rtable.ForeignKeys.Where(f => f.Id is { } id && _snapshot.ReferencesTo(id).Any(r => r.Field is "foreignKey" or "foreignKeys"
            && _snapshot.Get<Element>(r.FromElementId) is Mapping or Relation)).Select(f => f.Id!).ToHashSet(StringComparer.Ordinal);
        var foreignKeys = rtable.ForeignKeys.Where(f => f.Id is not null && f.MemberOrigin.MemberId is null && !mapped.Contains(f.Id))
            .Select(f => (f.Id!, f.Name, Join(f.Columns.Select(c => M(c.Key))) + "->" + f.ReferencedTable.Key + "(" + Join(f.ReferencedColumns.Select(c => M(c.Key))) + ")"))
            .ToList();
        foreach (var f in _stereotype.ForeignKeys.Where(f => f.Id.Length > 0))
        {
            string? signature = null;
            if ((f.ReferencesTable ?? ParameterValue(table, f.ReferencesParameter)) is { } target)
            {
                var referenced = f.ReferencesColumns.Count > 0 ? f.ReferencesColumns.Select(M)
                    : fold.Database.Tables.FirstOrDefault(t => t.Key == target)?.PrimaryKey?.Columns.Select(c => M(c.Key)) ?? [];
                signature = Join(f.Columns.Select(K)) + "->" + target + "(" + Join(referenced) + ")";
            }

            Match("foreign-key", f.Id, signature, foreignKeys, $"foreign key on {Names(f.Columns)}");
        }

        var dialectKind = _snapshot.Get<Database>(table.Database)!.Dialect;
        var indexes = rtable.Indexes.Where(x => x.Id is not null && x.MemberOrigin.MemberId is null)
            .Select(x => (x.Id!, x.Name, IndexSignature(x.Columns.Select(c => (c.Column is { } col ? M(col.Key) : null, c.Column is null ? c.Expression : null, c.OperatorClass, c.Descending, c.Nulls)),
                x.Unique, x.Method, x.Include.Select(c => M(c.Key)), x.Where))).ToList();
        foreach (var x in _stereotype.Indexes.Where(x => x.Id.Length > 0))
        {
            var columns = x.Columns.Select(c => (c.Column is { } col ? K(col) : null, c.Column is null ? ForDialect(c.Expression) : null, c.OperatorClass, c.Descending,
                NullsOrder(c, dialectKind))).ToList();
            var signature = columns.Any(c => c.Item1 is null && c.Item2 is null) ? null
                : IndexSignature(columns, x.Unique, ResolutionValues.Kebab(x.Method), x.Include.Select(K), x.Where);
            Match("index", x.Id, signature, indexes, $"index on {Names(x.Columns.Select(c => c.Column ?? "(expression)"))}");
        }

        var checks = rtable.Checks.Where(c => c.Id is not null && c.MemberOrigin.MemberId is null).Select(c => (c.Id!, c.Name, c.Expression)).ToList();
        foreach (var c in _stereotype.Checks.Where(c => c.Id.Length > 0))
            Match("check", c.Id, ForDialect(c.Expression), checks, $"check '{ForDialect(c.Expression) ?? c.Id}'");
    }

    private static string IndexSignature(IEnumerable<(string? Column, string? Expression, string? OperatorClass, bool Descending, string? Nulls)> columns, bool unique,
        string method, IEnumerable<string> include, string? where) =>
        string.Join(',', columns.Select(c => (c.Column ?? "expr:" + c.Expression) + "|" + c.OperatorClass + "|" + (c.Descending ? "desc" : "") + "|" + c.Nulls))
        + ";" + (unique ? "unique" : "") + ";" + method + ";" + string.Join(',', include) + ";" + where;

    /// <summary>The nulls order an index column resolves to, as the resolver writes it (PostgreSQL, and only when not the default).</summary>
    private static string? NullsOrder(IndexColumn column, Dialect dialect) =>
        dialect != Dialect.PostgreSql || column.Nulls is not { } nulls ? null
        : nulls == IndexNulls.First && !column.Descending ? "first"
        : nulls == IndexNulls.Last && column.Descending ? "last"
        : null;

    /// <summary>A member the table does not match: already inherited or excluded, it stays so; otherwise the fold would add it.</summary>
    private static void Unmatched(TableFold fold, string memberId, string what)
    {
        if (fold.Excludes.Contains(memberId) || (fold.WasEffective && !fold.File.Excludes.Contains(memberId, StringComparer.Ordinal)))
            return;
        fold.Missing.Add(what);
        fold.MissingIds.Add(memberId);
    }

    /// <summary>The carrier's value of a stereotype parameter that names a table, or <see langword="null"/>.</summary>
    private static string? ParameterValue(Table table, string? parameter) =>
        parameter is not null && table.Properties.TryGetValue(parameter, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } id ? id : null;

    /// <summary>The member columns' names, for messages.</summary>
    private string Names(IEnumerable<string> columns) =>
        "(" + string.Join(", ", columns.Select(id => _stereotype.Columns.FirstOrDefault(c => c.Id == id)?.Name ?? id)) + ")";

    /// <summary>The annotations an own column carries that its member lacks, in words, or <see langword="null"/>.</summary>
    private string? AnnotationsLacking(Table table, Column own, Column member)
    {
        var ownNode = Entry(Peek(table.Id), "columns", own.Id);
        var memberNode = Entry(Peek(_stereotype.Id), "columns", member.Id!);
        var lacking = Annotations.Where(name => ownNode?[name] is { } value && !IsEmpty(value) && !JsonNode.DeepEquals(value, memberNode?[name])).ToList();
        return lacking.Count == 0 ? null : string.Join(", ", lacking);

        static bool IsEmpty(JsonNode value) => value is JsonArray { Count: 0 } or JsonObject { Count: 0 };
    }

    /// <summary>Points every site that names a moved id at the inherited member's keyed id, and every query column text that names one.</summary>
    private void RewriteReferences(IReadOnlyList<TableFold> folds)
    {
        var targets = folds.Select(f => f.File.Id).ToHashSet(StringComparer.Ordinal);
        var moved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fold in folds)
        {
            foreach (var (own, keyed) in fold.Columns)
                moved[own] = keyed;
            foreach (var c in fold.Constraints)
                moved[c.OwnId] = ContributedMembers.KeyOf(fold.File.Id, c.MemberId);
            if (fold.PrimaryKey is { Length: > 0 } pk && _stereotype.PrimaryKey?.Id is { } memberPk)
                moved[pk] = ContributedMembers.KeyOf(fold.File.Id, memberPk);
        }

        foreach (var (oldId, newId) in moved.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            foreach (var reference in _snapshot.ReferencesTo(oldId))
            {
                // A query's column texts are rewritten from the resolved query below; a migration fact's keys are history.
                if (_snapshot.GetDocument(reference.FromElementId)?.Element is not { } holder || holder is Query or Migration
                    || Peek(reference.FromElementId) is not { } peeked || !JsonPointer.TryGetParent(peeked, reference.JsonPointer, out var parent, out var last))
                    continue;
                switch (parent)
                {
                    case JsonObject obj when obj[last] is JsonValue v && v.TryGetValue<string>(out var text) && text == oldId:
                        Node(holder.Id, Because(holder));
                        obj[last] = newId;
                        break;
                    case JsonArray array when JsonPointer.TryIndex(last, out var index) && index < array.Count && array[index] is JsonValue v
                        && v.TryGetValue<string>(out var text) && text == oldId:
                        Node(holder.Id, Because(holder));
                        array[index] = newId;
                        break;
                }
            }
        }

        MoveProvenanceKeys(folds);

        foreach (var rdb in folds.Select(f => f.Database).Distinct())
        {
            foreach (var query in rdb.Queries)
            {
                var replace = QueryColumnRewrite.Replacements(query, _ => null,
                    (t, column) => targets.Contains(t.Key) && moved.TryGetValue(column.Key, out var keyed) ? keyed : null, byKeyOnly: true);
                if (replace.Count > 0)
                    QueryColumnRewrite.Apply(Node(query.Id, $"query {query.Name} names the inherited columns by their keyed ids"), replace);
            }
        }

        string Because(Element holder) => targets.Contains(holder.Id) ? $"adopts the members of stereotype {_stereotype.Key} in place of its own"
            : $"{holder.KindName} {ChangePlanner.ReadableName(_snapshot, holder)} names the inherited members by their keyed ids";
    }

    /// <summary>
    /// Moves the provenance keys of the bindings that read a folded table with the column ids the fold rewrites (next-phase-2-design.md
    /// section 3.3, decision 2): provenance is a record, not a link, so the reference walk above never reaches it. The recorded values
    /// stay: the inherited member has the own column's name and shape, so the fold reads as no drift.
    /// </summary>
    private void MoveProvenanceKeys(IReadOnlyList<TableFold> folds)
    {
        var moved = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var fold in folds.Where(f => f.Columns.Count > 0))
            moved[fold.File.Id] = new Dictionary<string, string>(fold.Columns, StringComparer.Ordinal);
        if (moved.Count == 0)
            return;
        foreach (var entity in _snapshot.All<Entity>().OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            for (var i = 0; i < entity.Bindings.Count; i++)
            {
                if (entity.Bindings[i] is not { Provenance: { } provenance } binding || !moved.TryGetValue(binding.Source, out var columns)
                    || !provenance.Columns.Keys.Any(columns.ContainsKey))
                    continue;
                var node = Node(entity.Id, $"entity {entity.Name} records the inherited columns' keyed ids in its binding's provenance");
                if (node["bindings"] is not JsonArray bindings || bindings[i]?["provenance"]?["columns"] is not JsonObject recorded)
                    continue;
                var rekeyed = new JsonObject();
                foreach (var (key, value) in recorded.OrderBy(p => p.Key, StringComparer.Ordinal).ToList())
                    rekeyed[columns.GetValueOrDefault(key, key)] = value?.DeepClone();
                bindings[i]!["provenance"]!["columns"] = rekeyed;
            }
        }
    }

    /// <summary>The table's own edits, after its references were rewritten: carry, the own entries removed, excludes.</summary>
    private void Edit(TableFold fold)
    {
        var node = _work[fold.File.Id].Node;
        if (!fold.WasEffective)
        {
            var list = node["stereotypes"] as JsonArray ?? [];
            node["stereotypes"] = list;
            list.Add(_stereotype.Key);
        }

        Remove(node, "columns", fold.Columns.Keys);
        if (fold.PrimaryKey is not null)
            node.Remove("primaryKey");
        Remove(node, "uniques", fold.Constraints.Where(c => c.Kind == "unique").Select(c => c.OwnId));
        Remove(node, "foreignKeys", fold.Constraints.Where(c => c.Kind == "foreign-key").Select(c => c.OwnId));
        Remove(node, "indexes", fold.Constraints.Where(c => c.Kind == "index").Select(c => c.OwnId));
        Remove(node, "checks", fold.Constraints.Where(c => c.Kind == "check").Select(c => c.OwnId));
        if (fold.Excludes.Count == 0)
            node.Remove("excludes");
        else
            node["excludes"] = new JsonArray([.. fold.Excludes.Select(id => (JsonNode)id)]);

        static void Remove(JsonObject node, string list, IEnumerable<string> ids)
        {
            var gone = ids.ToHashSet(StringComparer.Ordinal);
            if (gone.Count == 0 || node[list] is not JsonArray array)
                return;
            foreach (var entry in array.OfType<JsonObject>().Where(e => e["id"]?.GetValue<string>() is { } id && gone.Contains(id)).ToList())
                array.Remove(entry);
        }
    }

    // ---- step 5 -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Compares each folded member with the own one it replaces, on the databases resolved before and after: a difference in a type
    /// facet refuses the table; a difference in a redefinable facet writes a <c>redefines</c> entry with the own value when
    /// <c>redefine</c> is set, else refuses the table. A redefinition never removes a default, a comment or a collation.
    /// </summary>
    /// <param name="before">The target databases as resolved before the fold.</param>
    /// <param name="after">The target databases as resolved on the candidate.</param>
    public void Verify(IReadOnlyDictionary<string, RDatabase> before, IReadOnlyDictionary<string, RDatabase> after)
    {
        var captures = new Dictionary<string, (PhysicalSnapshot Before, PhysicalSnapshot After)>(StringComparer.Ordinal);
        foreach (var fold in Changed())
        {
            if (!after.TryGetValue(fold.File.Database, out var adb) || adb.Tables.FirstOrDefault(t => t.Key == fold.File.Id) is not { } atable)
            {
                Refuse(fold.File, "it does not resolve after the fold.");
                continue;
            }

            if (!captures.TryGetValue(fold.File.Database, out var pair))
                captures[fold.File.Database] = pair = (Capture(before[fold.File.Database]), Capture(adb));
            var problems = new List<string>();
            var redefinitions = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
            JsonObject Redefinition(string memberId) => redefinitions.TryGetValue(memberId, out var entry) ? entry : redefinitions[memberId] = [];
            var capB = pair.Before.Tables.First(t => t.Key == fold.File.Id);
            var capA = pair.After.Tables.First(t => t.Key == fold.File.Id);
            foreach (var (ownId, memberId, name) in fold.ColumnPairs)
            {
                var keyed = ContributedMembers.KeyOf(fold.File.Id, memberId);
                CompareColumn(fold, capB.Columns.FirstOrDefault(c => c.Key == ownId), capA.Columns.FirstOrDefault(c => c.Key == keyed),
                    (fold.Before.Columns.FirstOrDefault(c => c.Key == ownId)?.StableId, atable.Columns.FirstOrDefault(c => c.Key == keyed)?.StableId),
                    name, () => Redefinition(memberId), problems);
            }

            CompareConstraints(fold, atable, Redefinition, problems);
            if (problems.Count > 0)
            {
                Refuse(fold.File, string.Join("; ", problems) + ".");
                continue;
            }

            if (redefinitions.Count == 0)
                continue;
            if (!_redefine)
            {
                Refuse(fold.File, $"its own members differ from the stereotype's in {string.Join(", ", redefinitions.Select(r => Describe(fold, r.Key) + " (" + string.Join(", ", r.Value.Select(p => p.Key)) + ")"))}; set redefine to write redefinitions with the table's values.");
                continue;
            }

            WriteRedefinitions(fold, redefinitions);
            Redefined = true;
        }
    }

    /// <summary>
    /// Confirms on the databases resolved after the fold (and its redefinitions) that every table of the target databases captures as it
    /// did before, moved through the fold's key map, column positions aside; a table that differs refuses its table.
    /// </summary>
    /// <param name="before">The target databases as resolved before the fold.</param>
    /// <param name="after">The target databases as resolved on the final candidate.</param>
    public void Confirm(IReadOnlyDictionary<string, RDatabase> before, IReadOnlyDictionary<string, RDatabase> after)
    {
        foreach (var databaseId in Databases)
        {
            if (!before.TryGetValue(databaseId, out var bdb) || !after.TryGetValue(databaseId, out var adb))
                continue;
            var rekeys = Changed().Where(f => f.File.Database == databaseId && f.Columns.Count > 0)
                .Select(f => new TableRekey(f.File.Id, f.File.Id, f.Columns)).ToList();
            var moved = SnapshotRekey.Apply(Capture(bdb), rekeys, new Dictionary<string, string>(StringComparer.Ordinal));
            var captured = Capture(adb);
            var afterTables = captured.Tables.ToDictionary(t => t.Key, StringComparer.Ordinal);
            foreach (var table in moved.Tables)
            {
                var other = afterTables.GetValueOrDefault(table.Key);
                if (other is not null && Normal(table) == Normal(other))
                    continue;
                var file = _folds.GetValueOrDefault(table.Key)?.File ?? _snapshot.Get<Table>(table.Key);
                var difference = other is null ? "it is gone" : Difference(table, other);
                if (file is not null)
                    Refuse(file, $"the fold would change it in the database ({difference}).");
                else
                    RefuseKey(table.Key, $"Table '{table.Name}': the fold would change it in the database ({difference}).");
            }
        }
    }

    /// <summary>
    /// Compares a folded column with the own one it replaces. <paramref name="stableIds"/> holds the resolved columns' <c>stableId</c>
    /// (own, inherited): a capture does not hold it, and a difference changes the id values of the table's rows, so it refuses the table
    /// as a type facet does (next-phase-2-design.md section 3.4, decision 22).
    /// </summary>
    private void CompareColumn(TableFold fold, SnapshotColumn? own, SnapshotColumn? inherited, (bool? Own, bool? Inherited) stableIds, string name,
        Func<JsonObject> entry, List<string> problems)
    {
        if (own is null || inherited is null)
        {
            problems.Add($"column '{name}' does not resolve on both sides");
            return;
        }

        var type = new List<string>();
        void Type<T>(string facet, T a, T b)
        {
            if (!EqualityComparer<T>.Default.Equals(a, b))
                type.Add($"{facet} ({Show(a)} here, {Show(b)} on the stereotype)");
        }

        Type("type", own.Type, inherited.Type);
        Type("subtype", own.Subtype, inherited.Subtype);
        Type("length", own.Length, inherited.Length);
        Type("precision", own.Precision, inherited.Precision);
        Type("scale", own.Scale, inherited.Scale);
        Type("native type", own.NativeType, inherited.NativeType);
        Type("identity", (own.Identity, own.IdentitySeed, own.IdentityIncrement, own.IdentityAlways), (inherited.Identity, inherited.IdentitySeed, inherited.IdentityIncrement, inherited.IdentityAlways));
        Type("sequence", own.Sequence, inherited.Sequence);
        Type("computed", (own.Computed, own.ComputedStored), (inherited.Computed, inherited.ComputedStored));
        Type("reference storage", (own.ReferenceType, own.Strategy, own.ReferenceTable), (inherited.ReferenceType, inherited.Strategy, inherited.ReferenceTable));
        Type("stable id", stableIds.Own ?? false, stableIds.Inherited ?? false);
        if (type.Count > 0)
        {
            problems.Add($"column '{name}' differs from the stereotype's in {string.Join(", ", type)}, which no redefinition changes: exclude the member and keep the own column");
            return;
        }

        if (own.Nullable != inherited.Nullable)
            entry()["nullable"] = own.Nullable;
        if (!JsonEquals(own.Default, inherited.Default) || own.DefaultSql != inherited.DefaultSql)
        {
            var column = Entry(Original(fold.File.Id), "columns", own.Key);
            if (column?["default"] is { } literal)
                entry()["default"] = literal.DeepClone();
            else if (column?["defaultSql"] is { } sql)
                entry()["defaultSql"] = sql.DeepClone();
            else if (own.Default is { } value)
                entry()["default"] = JsonSerializer.SerializeToNode(value);
            else if (own.DefaultSql is { } text)
                entry()["defaultSql"] = new JsonObject { [DialectTypeMaps.Name(_snapshot.Get<Database>(fold.File.Database)!.Dialect)] = text };
            else
                problems.Add($"column '{name}' has no default and the stereotype's has one, which a redefinition cannot remove");
        }

        Text("defaultName", own.DefaultName, inherited.DefaultName, "default name");
        Text("comment", own.Comment, inherited.Comment, "comment");
        Text("collation", own.Collation, inherited.Collation, "collation");

        void Text(string facet, string? a, string? b, string words)
        {
            if (a == b)
                return;
            if (a is { Length: > 0 })
                entry()[facet] = a;
            else
                problems.Add($"column '{name}' has no {words} and the stereotype's has one, which a redefinition cannot remove");
        }
    }

    private void CompareConstraints(TableFold fold, RTable after, Func<string, JsonObject> entry, List<string> problems)
    {
        var before = fold.Before;
        var moved = MovedColumns();
        IReadOnlyList<string> Keys(IEnumerable<RColumn> columns) => [.. columns.Select(c => moved.TryGetValue(c.Key, out var keyed) ? keyed : c.Key)];
        static JsonArray Array(IEnumerable<string> keys) => new([.. keys.Select(k => (JsonNode)k)]);

        if (fold.PrimaryKey is not null && _stereotype.PrimaryKey?.Id is { } pkId)
        {
            if (before.PrimaryKey is not { } own || after.PrimaryKey is not { } inherited)
            {
                problems.Add("the primary key does not resolve on both sides");
            }
            else
            {
                if (own.Name != inherited.Name)
                    entry(pkId)["name"] = own.Name;
                if (!Keys(own.Columns).SequenceEqual(Keys(inherited.Columns)))
                    entry(pkId)["columns"] = Array(Keys(own.Columns));
                if (own.Clustered != inherited.Clustered)
                {
                    if (own.Clustered is { } clustered)
                        entry(pkId)["clustered"] = clustered;
                    else
                        problems.Add("the primary key states no clustering and the stereotype's does, which a redefinition cannot remove");
                }

                if (own.WithoutOverlaps != inherited.WithoutOverlaps)
                    problems.Add("the primary key differs from the stereotype's in withoutOverlaps, which no redefinition changes");
            }
        }

        foreach (var c in fold.Constraints)
        {
            var keyed = ContributedMembers.KeyOf(fold.File.Id, c.MemberId);
            var what = $"{c.Kind.Replace('-', ' ')} '{c.Name}'";
            switch (c.Kind)
            {
                case "unique" when before.Uniques.FirstOrDefault(u => u.Id == c.OwnId) is { } own && after.Uniques.FirstOrDefault(u => u.Id == keyed) is { } inherited:
                    Name(own.Name, inherited.Name);
                    if (own.NullsNotDistinct != inherited.NullsNotDistinct)
                        entry(c.MemberId)["nullsNotDistinct"] = own.NullsNotDistinct;
                    if (own.WithoutOverlaps != inherited.WithoutOverlaps)
                        problems.Add($"{what} differs from the stereotype's in withoutOverlaps, which no redefinition changes");
                    break;
                case "foreign-key" when before.ForeignKeys.FirstOrDefault(f => f.Id == c.OwnId) is { } own && after.ForeignKeys.FirstOrDefault(f => f.Id == keyed) is { } inherited:
                    Name(own.Name, inherited.Name);
                    if (own.OnDelete != inherited.OnDelete)
                        entry(c.MemberId)["onDelete"] = own.OnDelete;
                    if (!Keys(own.OnDeleteColumns).SequenceEqual(Keys(inherited.OnDeleteColumns)))
                        entry(c.MemberId)["onDeleteColumns"] = Array(Keys(own.OnDeleteColumns));
                    if (own.OnUpdate != inherited.OnUpdate)
                        entry(c.MemberId)["onUpdate"] = own.OnUpdate;
                    if (own.Deferrable != inherited.Deferrable)
                        entry(c.MemberId)["deferrable"] = own.Deferrable;
                    if (own.Period != inherited.Period)
                        problems.Add($"{what} differs from the stereotype's in period, which no redefinition changes");
                    break;
                case "index" when before.Indexes.FirstOrDefault(x => x.Id == c.OwnId) is { } own && after.Indexes.FirstOrDefault(x => x.Id == keyed) is { } inherited:
                    Name(own.Name, inherited.Name);
                    if (!own.Storage.Select(p => (p.Name, p.Value)).SequenceEqual(inherited.Storage.Select(p => (p.Name, p.Value))))
                        entry(c.MemberId)["storage"] = Entry(Original(fold.File.Id), "indexes", c.OwnId)?["storage"]?.DeepClone() ?? new JsonObject();
                    break;
                case "check" when before.Checks.FirstOrDefault(x => x.Id == c.OwnId) is { } own && after.Checks.FirstOrDefault(x => x.Id == keyed) is { } inherited:
                    Name(own.Name, inherited.Name);
                    if (Entry(Original(fold.File.Id), "checks", c.OwnId)?["properties"] is JsonObject { Count: > 0 })
                        problems.Add($"{what} has custom properties, which no redefinition holds");
                    break;
                default:
                    problems.Add($"{what} does not resolve on both sides");
                    break;
            }

            void Name(string own, string inherited)
            {
                if (own != inherited)
                    entry(c.MemberId)["name"] = own;
            }
        }
    }

    /// <summary>Writes the redefinitions into the table's <c>redefines</c>, joining an entry the table already has for a member.</summary>
    private void WriteRedefinitions(TableFold fold, SortedDictionary<string, JsonObject> redefinitions)
    {
        var node = _work[fold.File.Id].Node;
        var list = node["redefines"] as JsonArray ?? [];
        foreach (var (member, facets) in redefinitions)
        {
            var target = list.OfType<JsonObject>().FirstOrDefault(e => e["member"]?.GetValue<string>() == member);
            if (target is null)
            {
                target = new JsonObject { ["member"] = member };
                list.Add(target);
            }

            foreach (var (facet, value) in facets.ToList())
                target[facet] = value?.DeepClone();
        }

        var sorted = list.OfType<JsonObject>().OrderBy(e => e["member"]?.GetValue<string>(), StringComparer.Ordinal).Select(e => (JsonNode)e.DeepClone()).ToList();
        node["redefines"] = new JsonArray([.. sorted]);
    }

    /// <summary>How a refusal names a member.</summary>
    private string Describe(TableFold fold, string memberId) =>
        fold.ColumnPairs.FirstOrDefault(p => p.MemberId == memberId) is { Name: not null } column ? $"column '{column.Name}'"
        : fold.Constraints.FirstOrDefault(c => c.MemberId == memberId) is { } constraint ? $"{constraint.Kind.Replace('-', ' ')} '{constraint.Name}'"
        : _stereotype.PrimaryKey?.Id == memberId ? "the primary key"
        : memberId;

    private static PhysicalSnapshot Capture(RDatabase database) => SnapshotCapture.Capture(database, 0);

    /// <summary>A captured table in a form that compares equal when only column positions and derived constraint keys differ.</summary>
    private static string Normal(SnapshotTable table) => JsonSerializer.Serialize(table with
    {
        Columns = [.. table.Columns.OrderBy(c => c.Key, StringComparer.Ordinal)],
        Uniques = Sorted(table.Uniques, u => u with { Key = "" }),
        ForeignKeys = Sorted(table.ForeignKeys, f => f with { Key = "" }),
        Checks = Sorted(table.Checks, c => c with { Key = "" }),
        Indexes = Sorted(table.Indexes, x => x with { Key = "" }),
        Exclusions = Sorted(table.Exclusions, x => x with { Key = "" }),
    });

    private static List<T> Sorted<T>(IEnumerable<T> items, Func<T, T> unkeyed) =>
        [.. items.Select(unkeyed).OrderBy(x => JsonSerializer.Serialize(x), StringComparer.Ordinal)];

    /// <summary>The first part of a table that differs between two captures, in words.</summary>
    private static string Difference(SnapshotTable before, SnapshotTable after)
    {
        foreach (var column in before.Columns.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var other = after.Columns.FirstOrDefault(c => c.Key == column.Key);
            if (other is null || JsonSerializer.Serialize(column) != JsonSerializer.Serialize(other))
                return $"column '{column.Name}'";
        }

        if (after.Columns.FirstOrDefault(c => before.Columns.All(b => b.Key != c.Key)) is { } added)
            return $"column '{added.Name}'";
        if (JsonSerializer.Serialize(before.PrimaryKey) != JsonSerializer.Serialize(after.PrimaryKey))
            return "the primary key";
        return JsonSerializer.Serialize(Sorted(before.Uniques, u => u with { Key = "" })) != JsonSerializer.Serialize(Sorted(after.Uniques, u => u with { Key = "" })) ? "a unique"
            : JsonSerializer.Serialize(Sorted(before.ForeignKeys, f => f with { Key = "" })) != JsonSerializer.Serialize(Sorted(after.ForeignKeys, f => f with { Key = "" })) ? "a foreign key"
            : JsonSerializer.Serialize(Sorted(before.Checks, c => c with { Key = "" })) != JsonSerializer.Serialize(Sorted(after.Checks, c => c with { Key = "" })) ? "a check"
            : JsonSerializer.Serialize(Sorted(before.Indexes, x => x with { Key = "" })) != JsonSerializer.Serialize(Sorted(after.Indexes, x => x with { Key = "" })) ? "an index"
            : "the table";
    }

    private static bool JsonEquals(JsonElement? a, JsonElement? b) =>
        a is null ? b is null : b is not null && JsonElement.DeepEquals(a.Value, b.Value);

    private static string Show<T>(T value) => value switch
    {
        null => "none",
        string s => "'" + s + "'",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "none",
    };

    // ---- result -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The changes, in the order they are applied, with what the classifier needs: per target database, the folded columns as table
    /// rekeys (the table key unchanged) and the constraint moves (<c>unique</c>, <c>foreign-key</c>, <c>index</c> and <c>check</c>
    /// entries, each with its table, by kind then from).
    /// </summary>
    /// <returns>The changes (in <see cref="MaterializeResult.Ops"/>) and one key-map result per target database with moves.</returns>
    public (MaterializeResult Changes, IReadOnlyList<MaterializeResult> KeyMaps) Result()
    {
        var ops = new List<MaterializeOp>();
        foreach (var id in _order)
        {
            var (hash, node, because) = _work[id];
            var element = _snapshot.Get<Element>(id)!;
            ops.Add(new MaterializeOp(BatchOp.Update, id, hash, node, element.KindName, ChangePlanner.ReadableName(_snapshot, element), because));
        }

        var notes = _folds.Values.Where(f => f.Skipped).OrderBy(f => f.File.Name, StringComparer.Ordinal)
            .Select(f => $"Table '{f.File.Name}' holds nothing stereotype '{_stereotype.Key}' declares, so it is left as it is.").ToList();
        var keyMaps = new List<MaterializeResult>();
        foreach (var group in Changed().GroupBy(f => f.File.Database, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var rekeys = group.Where(f => f.Columns.Count > 0).Select(f => new TableRekey(f.File.Id, f.File.Id, f.Columns)).ToList();
            var members = group.SelectMany(f => f.Constraints.Select(c => new MigrationRekey
            {
                Kind = c.Kind, Table = f.File.Id, From = c.OwnId, To = ContributedMembers.KeyOf(f.File.Id, c.MemberId), Name = c.Name,
            })).OrderBy(m => m.Kind, StringComparer.Ordinal).ThenBy(m => m.From, StringComparer.Ordinal).ToList();
            if (rekeys.Count > 0 || members.Count > 0)
                keyMaps.Add(new MaterializeResult(null, [], [], _snapshot.Get<Database>(group.Key)!.Name, rekeys, null, members));
        }

        return (new MaterializeResult(null, ops, notes), keyMaps);
    }

    // ---- working set ------------------------------------------------------------------------------------------------------------

    private IEnumerable<TableFold> Changed() =>
        _folds.Values.Where(f => !f.Skipped && f.Changes && !_refusals.ContainsKey(f.File.Id) && _work.ContainsKey(f.File.Id))
            .OrderBy(f => f.File.Name, StringComparer.Ordinal).ThenBy(f => f.File.Id, StringComparer.Ordinal);

    private void Refuse(Table table, string why) => RefuseKey(table.Id, $"Table '{ChangePlanner.ReadableName(_snapshot, table)}': {why}");

    private void RefuseKey(string key, string message)
    {
        if (_refusals.TryAdd(key, message))
            _refusalOrder.Add(key);
    }

    /// <summary>The folded columns of every target: own id to keyed id.</summary>
    private Dictionary<string, string> MovedColumns()
    {
        var moved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in _targets)
        {
            if (_folds.TryGetValue(table.Id, out var fold))
            {
                foreach (var (own, keyed) in fold.Columns)
                    moved[own] = keyed;
            }
        }

        return moved;
    }

    /// <summary>An element's document as the fold sees it: its working copy, else the file as read (parsed once, not yet a change).</summary>
    private JsonObject? Peek(string id)
    {
        if (_work.TryGetValue(id, out var working))
            return working.Node;
        if (_parsed.TryGetValue(id, out var parsed))
            return parsed;
        if (_snapshot.GetDocument(id) is not { } document || JsonNode.Parse(document.Json.GetRawText()) is not JsonObject node)
            return null;
        _parsed[id] = node;
        return node;
    }

    /// <summary>An element's document as the snapshot holds it, untouched by the fold.</summary>
    private JsonObject? Original(string id) =>
        _snapshot.GetDocument(id) is { } document ? JsonNode.Parse(document.Json.GetRawText()) as JsonObject : null;

    /// <summary>The entry with an id in one of a document's lists.</summary>
    private static JsonObject? Entry(JsonObject? node, string list, string id) =>
        (node?[list] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(e => e["id"]?.GetValue<string>() == id);

    /// <summary>
    /// The working copy of an existing element, read once. A document another operation of the batch writes refuses the fold, which
    /// would rewrite it too (the targets and the stereotype are refused when the operation is read).
    /// </summary>
    private JsonObject Node(string id, string because)
    {
        if (_work.TryGetValue(id, out var working))
            return working.Node;
        var document = _snapshot.GetDocument(id)!;
        if (_written.ContainsKey(id))
        {
            RefuseKey(id, $"The fold rewrites {document.Element.KindName} '{ChangePlanner.ReadableName(_snapshot, document.Element)}' to name the "
                + $"inherited members by their keyed ids, and it is also written by {WrittenBy(_written, id)}; fold in a batch of its own.");
        }

        var node = Peek(id)!;
        _parsed.Remove(id);
        _work[id] = (document.Hash, node, because);
        _order.Add(id);
        return node;
    }

    /// <summary>A constraint the fold moves: its kind, the own id, the member id and the own name (the database's).</summary>
    private sealed record MovedConstraint(string Kind, string OwnId, string MemberId, string Name);

    /// <summary>What the fold does to one target table.</summary>
    private sealed class TableFold(Table file, RDatabase database, RTable before)
    {
        public Table File { get; } = file;

        public RDatabase Database { get; } = database;

        public RTable Before { get; } = before;

        public bool WasEffective { get; set; }

        public bool Skipped { get; set; }

        public int Matched { get; set; }

        /// <summary>The folded columns: own id to keyed id.</summary>
        public Dictionary<string, string> Columns { get; } = new(StringComparer.Ordinal);

        public List<(string OwnId, string MemberId, string Name)> ColumnPairs { get; } = [];

        /// <summary>The own primary key's id ("" without one) when the stereotype's replaces it; otherwise <see langword="null"/>.</summary>
        public string? PrimaryKey { get; set; }

        public List<MovedConstraint> Constraints { get; } = [];

        public SortedSet<string> Excludes { get; } = new(StringComparer.Ordinal);

        public List<string> Missing { get; } = [];

        public List<string> MissingIds { get; } = [];

        /// <summary>Whether the table file changes: it carries the stereotype anew, folds a member or changes its excludes.</summary>
        public bool Changes => !WasEffective || Matched > 0 || !Excludes.SetEquals(File.Excludes);
    }
}
