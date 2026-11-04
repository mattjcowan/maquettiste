using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Engine.Model;

/// <summary>
/// The members one table inherits from the stereotypes of its effective set (next-phase-2-design.md section 3.1), each keyed
/// <c>&lt;tableKey&gt;.&lt;memberId&gt;</c>: the member's own id is its keyed id, and every column it names that the stereotype declares
/// is a keyed id too (a contributed index on column <c>C</c> lists <c>&lt;tableKey&gt;.C</c>). Redefinitions are applied.
/// </summary>
/// <param name="TableKey">The table key: a designed or imported table's file id, else the synthesized key.</param>
/// <param name="Columns">The inherited columns; <c>Id</c> is the keyed id, the redefined facets applied.</param>
/// <param name="PrimaryKeys">The inherited primary keys (more than one is MQ4076).</param>
/// <param name="Uniques">The inherited unique constraints.</param>
/// <param name="ForeignKeys">The inherited foreign keys.</param>
/// <param name="Indexes">The inherited indexes.</param>
/// <param name="Checks">The inherited check constraints.</param>
internal sealed record TableMemberSet(string TableKey,
    IReadOnlyList<InheritedMember<Column>> Columns,
    IReadOnlyList<InheritedMember<PrimaryKey>> PrimaryKeys,
    IReadOnlyList<InheritedMember<UniqueConstraint>> Uniques,
    IReadOnlyList<InheritedMember<MemberForeignKey>> ForeignKeys,
    IReadOnlyList<InheritedMember<TableIndex>> Indexes,
    IReadOnlyList<InheritedMember<CheckConstraint>> Checks)
{
    /// <summary>
    /// Every member the table inherits before its <c>excludes</c> (after <c>skip</c>), by member id: the member as its stereotype declares
    /// it, with the columns it names keyed and no redefinition applied. Links (<see cref="TableMembers.CheckLinks"/>) judge entries
    /// against it.
    /// </summary>
    public IReadOnlyDictionary<string, object> Inheritable { get; init; } = FrozenDictionary<string, object>.Empty;

    /// <summary>Whether the table inherits no member.</summary>
    public bool IsEmpty => Columns.Count == 0 && PrimaryKeys.Count == 0 && Uniques.Count == 0 && ForeignKeys.Count == 0 && Indexes.Count == 0
        && Checks.Count == 0;

    /// <summary>Whether a value is the keyed id of an inherited column of the set.</summary>
    /// <param name="key">A column id or key.</param>
    /// <returns><see langword="true"/> for an inherited column's keyed id.</returns>
    public bool HasColumn(string key)
    {
        foreach (var column in Columns)
        {
            if (string.Equals(column.Key, key, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>A set without members.</summary>
    /// <param name="tableKey">The table key.</param>
    /// <returns>The empty set.</returns>
    public static TableMemberSet Empty(string tableKey) => new(tableKey, [], [], [], [], [], []);
}

/// <summary>
/// Tables as holders of contributed members (next-phase-2-design.md section 3.1): member sets, the facets a redefinition may state per
/// member kind, and the link checks. A member set is a pure function of the snapshot, the table and <c>skip</c>, with no resolve, and is
/// cached per snapshot and (table key, skip).
/// </summary>
internal static class TableMembers
{
    private static readonly FrozenSet<string> NoSkip = FrozenSet<string>.Empty;

    private static readonly ConditionalWeakTable<ModelSnapshot, Cache> Caches = new();

    /// <summary>The facets a redefinition may state, by member kind (MQ2027 refuses others), in ordinal order.</summary>
    private static readonly FrozenDictionary<Type, ImmutableArray<string>> FacetTable = new Dictionary<Type, ImmutableArray<string>>
    {
        [typeof(Column)] = ["collation", "comment", "default", "defaultName", "defaultSql", "nullable"],
        [typeof(PrimaryKey)] = ["clustered", "columns", "name"],
        [typeof(UniqueConstraint)] = ["columns", "name", "nullsNotDistinct"],
        [typeof(MemberForeignKey)] = ["deferrable", "name", "onDelete", "onDeleteColumns", "onUpdate"],
        [typeof(TableIndex)] = ["method", "name", "storage", "where"],
        [typeof(CheckConstraint)] = ["name"],
    }.ToFrozenDictionary();

    /// <summary>
    /// A table file's member set: its holder (<see cref="ModelSnapshot.HolderOf"/>) and its table key. For a designed or imported table
    /// this is the exact set. For an overlay it is the candidate set (skip empty): which stereotypes a synthesized table skips needs the
    /// columns it stores, which only resolution knows.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="file">The table file.</param>
    /// <returns>The member set.</returns>
    public static TableMemberSet Of(ModelSnapshot model, Table file)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(file);
        var tableKey = TableKeyOf(file) ?? file.Id;
        // Only the snapshot's own instance is cached: a candidate table with the same id (a save being judged) gets its own set.
        if (!ReferenceEquals(model.Get<Table>(file.Id), file))
            return Build(tableKey, model.EffectiveStereotypes(file), file.Redefines, file.Excludes, NoSkip);
        return Caches.GetValue(model, static _ => new Cache()).Files.GetOrAdd(file.Id,
            static (_, state) => Of(state.Model, state.Key, state.Model.EffectiveStereotypes(state.File), state.File.Redefines, state.File.Excludes, NoSkip),
            (Model: model, Key: tableKey, File: file));
    }

    /// <summary>The resolver's form, for a table with or without a file; <paramref name="skip"/> holds the stereotypes realized by attributes.</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="tableKey">The table key.</param>
    /// <param name="effective">The table's effective stereotype set.</param>
    /// <param name="redefines">The table's redefinitions (empty without a file).</param>
    /// <param name="excludes">The member ids the table excludes (empty without a file).</param>
    /// <param name="skip">The keys of the stereotypes whose members the table does not inherit.</param>
    /// <returns>The member set.</returns>
    public static TableMemberSet Of(ModelSnapshot model, string tableKey, IReadOnlyList<AppliedStereotype> effective,
        IReadOnlyList<TableRedefinition> redefines, IReadOnlyList<string> excludes, IReadOnlySet<string> skip)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tableKey);
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(redefines);
        ArgumentNullException.ThrowIfNull(excludes);
        ArgumentNullException.ThrowIfNull(skip);
        if (!HasMembers(effective, skip))
            return TableMemberSet.Empty(tableKey);

        var sets = Caches.GetValue(model, static _ => new Cache()).Sets;
        var cacheKey = (tableKey, SkipKey(skip));
        if (sets.TryGetValue(cacheKey, out var cached))
        {
            // The inputs are a function of the snapshot and the table; a caller passing other ones gets a set of its own, uncached.
            return cached.Matches(effective, redefines, excludes) ? cached.Set : Build(tableKey, effective, redefines, excludes, skip);
        }

        return Store(sets, cacheKey, new Entry(effective, redefines, excludes, Build(tableKey, effective, redefines, excludes, skip)));
    }

    /// <summary>
    /// Stores a built entry unless another caller stored one for the key first, and answers the built entry's inputs: the stored set
    /// only when it is this entry or was built from the same inputs, else the built set (uncached), as a cache hit does.
    /// </summary>
    /// <param name="sets">The cache.</param>
    /// <param name="key">The table key and skip key.</param>
    /// <param name="entry">The entry just built.</param>
    /// <returns>The member set for the entry's inputs.</returns>
    internal static TableMemberSet Store(ConcurrentDictionary<(string TableKey, string Skip), Entry> sets, (string TableKey, string Skip) key, Entry entry)
    {
        var stored = sets.GetOrAdd(key, entry);
        return ReferenceEquals(stored, entry) || stored.Matches(entry.Effective, entry.Redefines, entry.Excludes) ? stored.Set : entry.Set;
    }

    /// <summary>The table key of a table file: its id, or the synthesized key an overlay targets (engine-design.md section 7.3).</summary>
    /// <param name="file">The table file.</param>
    /// <returns>The key, or <see langword="null"/> for an overlay that names neither an entity nor a relation.</returns>
    public static string? TableKeyOf(Table file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Origin != TableOrigin.Synthesized)
            return file.Id;
        if (file.Entity is { } entity)
            return (file.Attribute is { } attribute ? entity + "." + attribute : entity) + "@" + file.Database;
        return file.Relation is { } relation ? relation + "@" + file.Database : null;
    }

    /// <summary>The facets a redefinition may state for a member, by its kind, in ordinal order; empty for an unknown record.</summary>
    /// <param name="member">A stereotype member: a column, primary key, unique, contributed foreign key, index or check.</param>
    /// <returns>The JSON names of the facets.</returns>
    public static IReadOnlyList<string> FacetsOf(object member) =>
        FacetTable.TryGetValue(member.GetType(), out var facets) ? facets : [];

    /// <summary>How messages name a member's kind.</summary>
    /// <param name="member">A stereotype member.</param>
    /// <returns><c>column</c>, <c>primary key</c>, <c>unique</c>, <c>foreign key</c>, <c>index</c> or <c>check</c>.</returns>
    public static string KindName(object member) => member switch
    {
        Column => "column",
        PrimaryKey => "primary key",
        UniqueConstraint => "unique",
        MemberForeignKey => "foreign key",
        TableIndex => "index",
        CheckConstraint => "check",
        _ => "member",
    };

    /// <summary>
    /// MQ2027, MQ2028 and MQ4078 for one table: the kind-free link problems (<see cref="ContributedMembers.CheckLinks"/>), a facet the
    /// member's kind does not take, an excluded column that a member the table keeps still uses, and a facet equal to the value the
    /// member inherits. The validator (table files) and the resolver (overlays) both call it and place the findings: each carries the
    /// table's id and its pointer in the table file, and no file path.
    /// </summary>
    /// <param name="set">The table's member set (for an overlay, the resolver's, which knows <c>skip</c>).</param>
    /// <param name="file">The table file whose <c>redefines</c> and <c>excludes</c> are judged.</param>
    /// <returns>The findings, in list order.</returns>
    public static IReadOnlyList<Diagnostic> CheckLinks(TableMemberSet set, Table file)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(file);
        if (file.Redefines.Count == 0 && file.Excludes.Count == 0)
            return [];

        var findings = new List<Diagnostic>();
        void Add(string rule, DiagnosticSeverity fallback, string message, string pointer) =>
            findings.Add(new Diagnostic(rule, RuleCatalog.TryGet(rule, out var info) ? info.DefaultSeverity : fallback, message, file.Id, null, pointer, null, null));

        var inheritable = set.Inheritable.Keys.ToFrozenSet(StringComparer.Ordinal);
        foreach (var finding in ContributedMembers.CheckLinks(inheritable, file.Redefines, file.Excludes))
        {
            var excludesEntry = finding.Pointer.StartsWith("/excludes/", StringComparison.Ordinal);
            switch (finding.Problem)
            {
                case MemberLinkProblem.NotInherited when excludesEntry:
                    Add("MQ2028", DiagnosticSeverity.Error,
                        $"Exclusion names '{finding.Member}', which is not a member the table inherits from its stereotypes; remove the entry.", finding.Pointer);
                    break;
                case MemberLinkProblem.NotInherited:
                    Add("MQ2027", DiagnosticSeverity.Error,
                        $"Redefinition names '{finding.Member}', which is not a member the table inherits from its stereotypes; fix or remove the entry.", finding.Pointer);
                    break;
                case MemberLinkProblem.RedefinedTwice:
                    Add("MQ2027", DiagnosticSeverity.Error,
                        $"Member '{finding.Member}' is redefined twice; state its facets in one entry.", finding.Pointer);
                    break;
                case MemberLinkProblem.NoFacet:
                    Add("MQ2027", DiagnosticSeverity.Error,
                        $"Redefinition of member '{finding.Member}' states no facet; state the facets it changes, or remove the entry.", finding.Pointer);
                    break;
                case MemberLinkProblem.ExcludedAndRedefined:
                    Add("MQ2028", DiagnosticSeverity.Error,
                        $"Member '{finding.Member}' is both excluded and redefined; keep one of the two entries.", finding.Pointer);
                    break;
            }
        }

        for (var i = 0; i < file.Redefines.Count; i++)
        {
            var redefinition = file.Redefines[i];
            if (!set.Inheritable.TryGetValue(redefinition.Member, out var member))
                continue;
            var allowed = FacetsOf(member);
            var entry = "/redefines/" + i.ToString(CultureInfo.InvariantCulture) + "/";
            foreach (var facet in redefinition.Facets)
            {
                if (!allowed.Contains(facet, StringComparer.Ordinal))
                {
                    var kind = KindName(member);
                    var hint = member is Column && facet is "name"
                        ? " A column's name and type facets come from the stereotype: exclude the member and declare an own column instead."
                        : "";
                    Add("MQ2027", DiagnosticSeverity.Error,
                        $"Redefinition of {kind} '{redefinition.Member}' states '{facet}', which a {kind} does not take (it takes {string.Join(", ", allowed)}).{hint}",
                        entry + facet);
                }
                else if (EqualsInherited(member, redefinition, facet))
                {
                    Add("MQ4078", DiagnosticSeverity.Warning,
                        $"Redefinition of {KindName(member)} '{redefinition.Member}' states '{facet}' equal to the value it inherits, so the table stops following the stereotype for that facet; remove the facet.",
                        entry + facet);
                }
            }
        }

        for (var i = 0; i < file.Excludes.Count; i++)
        {
            var id = file.Excludes[i];
            if (!set.Inheritable.TryGetValue(id, out var member) || member is not Column column)
                continue;
            var users = UsersOf(set, ContributedMembers.KeyOf(set.TableKey, id));
            if (users.Count > 0)
            {
                Add("MQ2028", DiagnosticSeverity.Error,
                    $"Excluding column '{column.Name}' ({id}) leaves inherited {string.Join(", ", users)} naming it; exclude {(users.Count == 1 ? "that member" : "those members")} too.",
                    "/excludes/" + i.ToString(CultureInfo.InvariantCulture));
            }
        }

        return findings;
    }

    // ---- building ----

    private static bool HasMembers(IReadOnlyList<AppliedStereotype> effective, IReadOnlySet<string> skip)
    {
        foreach (var applied in effective)
        {
            var s = applied.Stereotype;
            if (!skip.Contains(s.Key) && (s.Columns.Count > 0 || s.PrimaryKey is not null || s.Uniques.Count > 0 || s.ForeignKeys.Count > 0
                || s.Indexes.Count > 0 || s.Checks.Count > 0))
                return true;
        }

        return false;
    }

    private static string SkipKey(IReadOnlySet<string> skip) =>
        skip.Count == 0 ? "" : string.Join('\n', skip.Order(StringComparer.Ordinal));

    private static TableMemberSet Build(string tableKey, IReadOnlyList<AppliedStereotype> effective, IReadOnlyList<TableRedefinition> redefines,
        IReadOnlyList<string> excludes, IReadOnlySet<string> skip)
    {
        if (!HasMembers(effective, skip))
            return TableMemberSet.Empty(tableKey);

        // Each stereotype member with the columns it names keyed (only the stereotype's own columns: a member naming another
        // stereotype's or the table's column keeps the bare id, which then resolves to nothing, MQ2025 and MQ4008).
        var keyed = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
        var inheritable = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var applied in effective)
        {
            var s = applied.Stereotype;
            if (skip.Contains(s.Key))
                continue;
            var own = s.Columns.Select(c => c.Id).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
            string K(string id) => own.Contains(id) ? ContributedMembers.KeyOf(tableKey, id) : id;
            IReadOnlyList<string> Ks(IReadOnlyList<string> ids) => [.. ids.Select(K)];
            void Add(object member, string? id, object keyedMember)
            {
                keyed[member] = keyedMember;
                if (!string.IsNullOrEmpty(id))
                    inheritable.TryAdd(id, keyedMember);
            }

            foreach (var c in s.Columns)
                Add(c, c.Id, c);
            if (s.PrimaryKey is { } pk)
                Add(pk, pk.Id, pk with { Columns = Ks(pk.Columns) });
            foreach (var u in s.Uniques)
                Add(u, u.Id, u with { Columns = Ks(u.Columns) });
            foreach (var f in s.ForeignKeys)
                Add(f, f.Id, f with { Columns = Ks(f.Columns), OnDeleteColumns = Ks(f.OnDeleteColumns) });
            foreach (var x in s.Indexes)
                Add(x, x.Id, x with { Columns = [.. x.Columns.Select(c => c.Column is { } id ? c with { Column = K(id) } : c)], Include = Ks(x.Include) });
            foreach (var c in s.Checks)
                Add(c, c.Id, c.Column is { } column ? c with { Column = K(column) } : c);
        }

        T Keyed<T>(T member) where T : class => keyed.TryGetValue(member, out var k) ? (T)k : member;

        IReadOnlyList<InheritedMember<T>> Inherit<T>(Func<Stereotype, IReadOnlyList<T>> membersOf, Func<T, string?> idOf,
            Func<T, TableRedefinition, T> apply, Func<T, string, T> withId) where T : class =>
            [.. ContributedMembers.Inherit(tableKey, effective, membersOf, idOf, redefines, excludes, skip, (m, r) => apply(Keyed(m), r))
                .Select(m => m with { Member = withId(Keyed(m.Member), m.Key) })];

        return new TableMemberSet(tableKey,
            Inherit<Column>(s => s.Columns, c => c.Id, ApplyColumn, (c, key) => c with { Id = key }),
            Inherit<PrimaryKey>(s => s.PrimaryKey is { } pk ? [pk] : [], pk => pk.Id, ApplyPrimaryKey, (pk, key) => pk with { Id = key }),
            Inherit<UniqueConstraint>(s => s.Uniques, u => u.Id, ApplyUnique, (u, key) => u with { Id = key }),
            Inherit<MemberForeignKey>(s => s.ForeignKeys, f => f.Id, ApplyForeignKey, (f, key) => f with { Id = key }),
            Inherit<TableIndex>(s => s.Indexes, x => x.Id, ApplyIndex, (x, key) => x with { Id = key }),
            Inherit<CheckConstraint>(s => s.Checks, c => c.Id, ApplyCheck, (c, key) => c with { Id = key }))
        {
            Inheritable = inheritable.ToFrozenDictionary(StringComparer.Ordinal),
        };
    }

    // ---- applying a redefinition: only the facets the member's kind takes ----

    private static Column ApplyColumn(Column c, TableRedefinition r)
    {
        // Setting default or defaultSql replaces the inherited default of either form.
        var redefault = r.Default is not null || r.DefaultSql is not null;
        return c with
        {
            Nullable = r.Nullable ?? c.Nullable,
            Default = redefault ? r.Default : c.Default,
            DefaultSql = redefault ? r.DefaultSql ?? ImmutableDictionary<string, string>.Empty : c.DefaultSql,
            DefaultName = r.DefaultName ?? c.DefaultName,
            Comment = r.Comment ?? c.Comment,
            Collation = r.Collation ?? c.Collation,
        };
    }

    private static PrimaryKey ApplyPrimaryKey(PrimaryKey pk, TableRedefinition r) =>
        pk with { Columns = r.Columns ?? pk.Columns, Name = r.Name ?? pk.Name, Clustered = r.Clustered ?? pk.Clustered };

    private static UniqueConstraint ApplyUnique(UniqueConstraint u, TableRedefinition r) =>
        u with { Columns = r.Columns ?? u.Columns, Name = r.Name ?? u.Name, NullsNotDistinct = r.NullsNotDistinct ?? u.NullsNotDistinct };

    private static MemberForeignKey ApplyForeignKey(MemberForeignKey f, TableRedefinition r) => f with
    {
        Name = r.Name ?? f.Name,
        OnDelete = r.OnDelete ?? f.OnDelete,
        OnDeleteColumns = r.OnDeleteColumns ?? f.OnDeleteColumns,
        OnUpdate = r.OnUpdate ?? f.OnUpdate,
        Deferrable = r.Deferrable ?? f.Deferrable,
    };

    private static TableIndex ApplyIndex(TableIndex x, TableRedefinition r) =>
        x with { Name = r.Name ?? x.Name, Where = r.Where ?? x.Where, Method = r.Method ?? x.Method, Storage = r.Storage ?? x.Storage };

    private static CheckConstraint ApplyCheck(CheckConstraint c, TableRedefinition r) => c with { Name = r.Name ?? c.Name };

    // ---- checks ----

    /// <summary>Whether a stated facet equals the value the member inherits (MQ4078); the member's column references are keyed.</summary>
    private static bool EqualsInherited(object member, TableRedefinition r, string facet) => (member, facet) switch
    {
        (Column c, "nullable") => r.Nullable == (c.Nullable ?? true),
        (Column c, "default") => c.Default is { } d && r.Default is { } rd && JsonElement.DeepEquals(d, rd) && c.DefaultSql.Count == 0,
        (Column c, "defaultSql") => c.Default is null && SameMap(c.DefaultSql, r.DefaultSql),
        (Column c, "defaultName") => string.Equals(c.DefaultName, r.DefaultName, StringComparison.Ordinal),
        (Column c, "comment") => string.Equals(c.Comment, r.Comment, StringComparison.Ordinal),
        (Column c, "collation") => string.Equals(c.Collation, r.Collation, StringComparison.Ordinal),
        (PrimaryKey pk, "columns") => r.Columns is { } columns && columns.SequenceEqual(pk.Columns, StringComparer.Ordinal),
        (PrimaryKey pk, "name") => string.Equals(pk.Name, r.Name, StringComparison.Ordinal),
        (PrimaryKey pk, "clustered") => pk.Clustered == r.Clustered,
        (UniqueConstraint u, "columns") => r.Columns is { } columns && columns.SequenceEqual(u.Columns, StringComparer.Ordinal),
        (UniqueConstraint u, "name") => string.Equals(u.Name, r.Name, StringComparison.Ordinal),
        (UniqueConstraint u, "nullsNotDistinct") => u.NullsNotDistinct == r.NullsNotDistinct,
        (MemberForeignKey f, "name") => string.Equals(f.Name, r.Name, StringComparison.Ordinal),
        (MemberForeignKey f, "onDelete") => f.OnDelete == r.OnDelete,
        (MemberForeignKey f, "onDeleteColumns") => r.OnDeleteColumns is { } columns && columns.SequenceEqual(f.OnDeleteColumns, StringComparer.Ordinal),
        (MemberForeignKey f, "onUpdate") => f.OnUpdate == r.OnUpdate,
        (MemberForeignKey f, "deferrable") => f.Deferrable == r.Deferrable,
        (TableIndex x, "name") => string.Equals(x.Name, r.Name, StringComparison.Ordinal),
        (TableIndex x, "where") => string.Equals(x.Where, r.Where, StringComparison.Ordinal),
        (TableIndex x, "method") => x.Method == r.Method,
        (TableIndex x, "storage") => r.Storage is { } storage && SameStorage(x.Storage, storage),
        (CheckConstraint c, "name") => string.Equals(c.Name, r.Name, StringComparison.Ordinal),
        _ => false,
    };

    private static bool SameMap(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string>? b) =>
        b is not null && a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && string.Equals(kv.Value, v, StringComparison.Ordinal));

    private static bool SameStorage(IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> a,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && kv.Value.Count == other.Count
            && kv.Value.All(p => other.TryGetValue(p.Key, out var v) && JsonElement.DeepEquals(p.Value, v)));

    /// <summary>
    /// The members of a set that name a keyed column, as messages list them (<c>index '&lt;id&gt;'</c>), in set order: a check by its
    /// <c>column</c> or a path of its condition.
    /// </summary>
    private static List<string> UsersOf(TableMemberSet set, string columnKey)
    {
        var users = new List<string>();
        bool Names(IReadOnlyList<string> columns) => columns.Contains(columnKey, StringComparer.Ordinal);
        foreach (var pk in set.PrimaryKeys)
        {
            if (Names(pk.Member.Columns))
                users.Add("primary key '" + pk.MemberId + "'");
        }

        foreach (var u in set.Uniques)
        {
            if (Names(u.Member.Columns))
                users.Add("unique '" + u.MemberId + "'");
        }

        foreach (var f in set.ForeignKeys)
        {
            if (Names(f.Member.Columns) || Names(f.Member.OnDeleteColumns))
                users.Add("foreign key '" + f.MemberId + "'");
        }

        foreach (var x in set.Indexes)
        {
            if (x.Member.Columns.Any(c => string.Equals(c.Column, columnKey, StringComparison.Ordinal)) || Names(x.Member.Include))
                users.Add("index '" + x.MemberId + "'");
        }

        // A check in the expression language names the column in its condition too, by the stereotype's own column id.
        foreach (var c in set.Checks)
        {
            if (string.Equals(c.Member.Column, columnKey, StringComparison.Ordinal) || (c.Member.Condition is { } condition
                && Validation.ExprSites.PathIds(condition).Any(id => string.Equals(ContributedMembers.KeyOf(set.TableKey, id), columnKey, StringComparison.Ordinal))))
                users.Add("check '" + c.MemberId + "'");
        }

        return users;
    }

    /// <summary>One snapshot's member sets: by table file id, and by (table key, skip) with the inputs they were built from.</summary>
    private sealed class Cache
    {
        public ConcurrentDictionary<string, TableMemberSet> Files { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<(string TableKey, string Skip), Entry> Sets { get; } = new();
    }

    /// <summary>A cached member set with the inputs it was built from.</summary>
    internal sealed record Entry(IReadOnlyList<AppliedStereotype> Effective, IReadOnlyList<TableRedefinition> Redefines, IReadOnlyList<string> Excludes,
        TableMemberSet Set)
    {
        public bool Matches(IReadOnlyList<AppliedStereotype> effective, IReadOnlyList<TableRedefinition> redefines, IReadOnlyList<string> excludes) =>
            Effective.SequenceEqual(effective) && Redefines.SequenceEqual(redefines) && Excludes.SequenceEqual(excludes, StringComparer.Ordinal);
    }
}
