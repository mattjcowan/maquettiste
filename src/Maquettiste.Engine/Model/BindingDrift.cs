using System.Collections.Frozen;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Text;

namespace Maquettiste.Engine.Model;

/// <summary>
/// One column of a table-file source as provenance sees it: an own entry of the file, or a member the table inherits from a stereotype
/// (its keyed id, the table's redefinition applied).
/// </summary>
/// <param name="Id">The column id, or an inherited column's keyed id <c>&lt;tableId&gt;.&lt;memberId&gt;</c>.</param>
/// <param name="Name">The column's name.</param>
/// <param name="Entry">The column entry the shape hash reads.</param>
/// <param name="Inherited">Whether the column is inherited from a stereotype.</param>
internal sealed record ShapeColumn(string Id, string Name, Column Entry, bool Inherited)
{
    /// <summary>The value provenance records for the column (<see cref="ColumnShapeHash.Of"/>).</summary>
    public string Shape { get; } = ColumnShapeHash.Of(Entry);
}

/// <summary>What a binding's source is, as drift sees it.</summary>
internal enum DriftSource
{
    /// <summary>A designed or imported table file of the binding's database: the only source whose columns have ids.</summary>
    TableFile,

    /// <summary>A view, a query, a synthesized table key or an overlay file: provenance does not describe it.</summary>
    NotTableFile,

    /// <summary>Nothing of the binding's database (MQ4044 reports it): no drift is computed.</summary>
    Unresolved,
}

/// <summary>A column of the source that nothing in the binding names.</summary>
/// <param name="Column">The column id (an inherited column's keyed id).</param>
/// <param name="Name">The column's name.</param>
/// <param name="Status">
/// What fills it, as the resolver's accounting says: <c>unaccounted</c>, <c>identity</c>, <c>default</c> or <c>computed</c>.
/// </param>
internal sealed record DriftAddedColumn(string Column, string Name, string Status);

/// <summary>A column a field of the binding maps, renamed or retyped since provenance recorded it.</summary>
/// <param name="Column">The column id (an inherited column's keyed id).</param>
/// <param name="Name">The column's name now.</param>
/// <param name="Attribute">The field's attribute reference: an attribute id, <c>attributeId.memberId</c> or a relation end id.</param>
/// <param name="AttributeName">The name the field goes by, as the resolver names it (the reference itself when it names nothing).</param>
/// <param name="Field">The field's index in the binding's <c>fields</c>.</param>
/// <param name="Recorded">The recorded value, <c>"&lt;name part&gt;:&lt;type part&gt;"</c>.</param>
/// <param name="Current">The column's value now.</param>
internal sealed record DriftMappedColumn(string Column, string Name, string Attribute, string AttributeName, int Field, string Recorded,
    string Current);

/// <summary>One binding's drift (next-phase-2-design.md section 3.3, decision 5).</summary>
/// <param name="Source">What the source is.</param>
/// <param name="Table">The source table file, with <see cref="DriftSource.TableFile"/>.</param>
/// <param name="HasProvenance">Whether the binding records provenance.</param>
/// <param name="Stale">
/// Whether the provenance does not describe the source (MQ4088): the source is not a table file, or none of the recorded ids is a
/// column of it. A stale provenance is read as none.
/// </param>
/// <param name="Columns">The source's columns, own entries in file order then inherited members; empty unless a table file.</param>
/// <param name="Added">
/// Columns not recorded that nothing in the binding names, in column order; without provenance (or with a stale one), only those
/// nothing accounts for (<c>unaccounted</c>, what MQ4047 reports).
/// </param>
/// <param name="Renamed">Mapped columns whose name part changed, in column order then field order.</param>
/// <param name="Retyped">Mapped columns whose type part changed, in column order then field order.</param>
/// <param name="Removed">Recorded ids the source no longer has, in ordinal order.</param>
internal sealed record BindingDriftReport(DriftSource Source, Table? Table, bool HasProvenance, bool Stale, IReadOnlyList<ShapeColumn> Columns,
    IReadOnlyList<DriftAddedColumn> Added, IReadOnlyList<DriftMappedColumn> Renamed, IReadOnlyList<DriftMappedColumn> Retyped,
    IReadOnlyList<string> Removed)
{
    /// <summary>Whether the provenance is read: the binding has one, and it describes its table-file source.</summary>
    public bool Reads => HasProvenance && !Stale && Source == DriftSource.TableFile;

    /// <summary>Whether there is something to report: a stale provenance, or a column added, renamed, retyped or removed.</summary>
    public bool HasDrift => Stale || Added.Count > 0 || Renamed.Count > 0 || Retyped.Count > 0 || Removed.Count > 0;
}

/// <summary>
/// The drift of one binding, from files alone (next-phase-2-design.md section 3.3, decision 5): the resolver (MQ4087, MQ4088), the drift
/// report and re-sync share it. It lives in <c>Model/</c>, beside <see cref="TableMembers"/>, because the resolver uses no
/// <c>Loading/</c> type. A field's column is found as the resolver finds it (exact name, id, then loose name).
/// </summary>
internal static class BindingDrift
{
    private static readonly IReadOnlyList<ShapeColumn> NoColumns = [];

    /// <summary>One binding's drift.</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="entity">The entity that holds the binding (its attributes name the fields).</param>
    /// <param name="binding">The binding.</param>
    /// <returns>The report.</returns>
    public static BindingDriftReport Of(ModelSnapshot model, Entity entity, EntityBinding binding)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(binding);
        var hasProvenance = binding.Provenance is not null;
        var source = SourceOf(model, binding, out var table);
        if (source != DriftSource.TableFile)
            return new BindingDriftReport(source, null, hasProvenance, hasProvenance && source == DriftSource.NotTableFile, NoColumns, [], [], [], []);

        var columns = ColumnsOf(model, table!);
        var recorded = binding.Provenance?.Columns ?? FrozenDictionary<string, string>.Empty;
        var ids = columns.Select(c => c.Id).ToFrozenSet(StringComparer.Ordinal);
        var stale = recorded.Count > 0 && !recorded.Keys.Any(ids.Contains);
        var reads = hasProvenance && !stale;

        // What the binding names: its fields (by column), constants, listed columns and soft delete.
        var mappedBy = new Dictionary<int, List<int>>();
        for (var i = 0; i < binding.Fields.Count; i++)
        {
            var at = Find(columns, binding.Fields[i].Column);
            if (at < 0)
                continue;
            if (!mappedBy.TryGetValue(at, out var fields))
                mappedBy[at] = fields = [];
            fields.Add(i);
        }

        var named = new HashSet<int>(mappedBy.Keys);
        foreach (var constant in binding.Constants)
            Name(named, columns, constant.Column);
        foreach (var listed in binding.Columns)
            Name(named, columns, listed.Column);
        if (binding.Delete?.Soft is { } soft && WritesSource(binding, table!))
            Name(named, columns, soft.Column);

        var added = new List<DriftAddedColumn>();
        var renamed = new List<DriftMappedColumn>();
        var retyped = new List<DriftMappedColumn>();
        var dialect = DialectOf(model, table!);
        IReadOnlySet<string>? primaryKey = null;
        string Status(ShapeColumn column) => StatusOf(model, column, primaryKey ??= PrimaryKeyOf(model, table!, columns), dialect);
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            if (!reads || !recorded.TryGetValue(column.Id, out var was))
            {
                // Not recorded: added when nothing names it; without provenance only what nothing accounts for (MQ4047's columns).
                if (!named.Contains(i) && Status(column) is var status && (reads || status == "unaccounted"))
                    added.Add(new DriftAddedColumn(column.Id, column.Name, status));
                continue;
            }

            if (!mappedBy.TryGetValue(i, out var mapping) || string.Equals(was, column.Shape, StringComparison.Ordinal))
                continue;
            var nameChanged = !string.Equals(ColumnShapeHash.NamePartOf(was), ColumnShapeHash.NamePartOf(column.Shape), StringComparison.Ordinal);
            var typeChanged = !string.Equals(ColumnShapeHash.TypePartOf(was), ColumnShapeHash.TypePartOf(column.Shape), StringComparison.Ordinal);
            foreach (var f in mapping)
            {
                var field = binding.Fields[f];
                var change = new DriftMappedColumn(column.Id, column.Name, field.Attribute, AttributeName(model, entity, field.Attribute), f, was, column.Shape);
                if (nameChanged)
                    renamed.Add(change);
                if (typeChanged)
                    retyped.Add(change);
            }
        }

        IReadOnlyList<string> removed = reads ? [.. recorded.Keys.Where(id => !ids.Contains(id)).Order(StringComparer.Ordinal)] : [];
        return new BindingDriftReport(DriftSource.TableFile, table, hasProvenance, stale, columns, added, renamed, retyped, removed);
    }

    /// <summary>What a binding's source is: a designed or imported table file of its database, something else, or nothing.</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="binding">The binding.</param>
    /// <param name="table">The table file, with <see cref="DriftSource.TableFile"/>.</param>
    /// <returns>The source's kind.</returns>
    public static DriftSource SourceOf(ModelSnapshot model, EntityBinding binding, out Table? table)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(binding);
        table = null;
        var key = binding.Source;
        switch (model.Get<Element>(key))
        {
            case Table { Origin: not TableOrigin.Synthesized } file when file.Database == binding.Database:
                table = file;
                return DriftSource.TableFile;
            case Table t when t.Database == binding.Database:
            case View v when v.Database == binding.Database:
            case Query q when q.Database == binding.Database:
            case Entity or Relation: // a synthesized table key without its database
                return DriftSource.NotTableFile;
            case not null:
                return DriftSource.Unresolved;
        }

        // A synthesized table key: <entity>[.<attribute>]@<database> or <relation>@<database>, or the same without the database.
        var at = key.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0 && !string.Equals(key[(at + 1)..], binding.Database, StringComparison.Ordinal))
            return DriftSource.Unresolved;
        var owner = (at >= 0 ? key[..at] : key).Split('.')[0];
        return model.Get<Element>(owner) is Entity or Relation ? DriftSource.NotTableFile : DriftSource.Unresolved;
    }

    /// <summary>A table file's columns as provenance sees them: own entries in file order, then inherited members in 3.1's order.</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="table">A designed or imported table file.</param>
    /// <returns>The columns.</returns>
    public static IReadOnlyList<ShapeColumn> ColumnsOf(ModelSnapshot model, Table table)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(table);
        var columns = new List<ShapeColumn>(table.Columns.Count);
        foreach (var column in table.Columns)
        {
            if (!string.IsNullOrEmpty(column.Id))
                columns.Add(new ShapeColumn(column.Id, column.Name, column, Inherited: false));
        }

        foreach (var member in TableMembers.Of(model, table).Columns)
            columns.Add(new ShapeColumn(member.Key, member.Member.Name, member.Member, Inherited: true));
        return columns;
    }

    /// <summary>
    /// A column of the source by a binding's reference, as the resolver finds a table column: the exact name, the id (or keyed id), then
    /// the name ignoring case, then the name with underscores dropped, each loose step only when one column matches.
    /// </summary>
    /// <param name="columns">The source's columns.</param>
    /// <param name="reference">A column id, keyed id or physical name.</param>
    /// <returns>The column's index, or -1.</returns>
    public static int Find(IReadOnlyList<ShapeColumn> columns, string reference)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(reference);
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Name, reference, StringComparison.Ordinal))
                return i;
        }

        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Id, reference, StringComparison.Ordinal))
                return i;
        }

        var found = Single(columns, c => string.Equals(c.Name, reference, StringComparison.OrdinalIgnoreCase), out var any);
        if (any)
            return found;
        var folded = Fold(reference);
        return Single(columns, c => string.Equals(Fold(c.Name), folded, StringComparison.Ordinal), out _);

        static string Fold(string text) => text.Replace("_", "", StringComparison.Ordinal).ToUpperInvariant();
    }

    /// <summary>
    /// The name a field goes by, as the resolver names it: an attribute's name (own, a base's or a stereotype's), the owner's name plus
    /// the member's for <c>attributeId.memberId</c>, a to-one navigation's name plus <c>Id</c> for a relation end, plus <c>Type</c> for
    /// <c>endId.type</c> and plus the entity's name and <c>Id</c> for <c>endId.entityId</c>; else the reference.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="reference">The field's attribute reference.</param>
    /// <returns>The name.</returns>
    public static string AttributeName(ModelSnapshot model, Entity entity, string reference)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(reference);
        if (AttributeOf(model, entity, reference) is { } attribute)
            return attribute.Name;
        var dot = reference.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0 && AttributeOf(model, entity, reference[..dot]) is { Type.Ref: { } typeId } owner
            && model.Get<ValueObject>(typeId)?.Attributes.FirstOrDefault(m => string.Equals(m.Id, reference[(dot + 1)..], StringComparison.Ordinal)) is { } member)
            return owner.Name + Casing.Pascal(member.Name);
        if (EndOf(model, reference) is { } end)
            return end.Navigation.Length > 0 ? end.Navigation + "Id" : Casing.Camel(end.Role + "Id");
        // The field forms of a relation end (several-types-design.md decision 46): <endId>.type names the type column (attachedToType),
        // <endId>.<entityId> a key column per type (attachedToPartId).
        if (dot > 0 && EndOf(model, reference[..dot]) is { } typed)
        {
            var rest = reference[(dot + 1)..];
            if (string.Equals(rest, "type", StringComparison.Ordinal))
                return typed.Navigation.Length > 0 ? typed.Navigation + "Type" : Casing.Camel(typed.Role + "Type");
            if (model.Get<Entity>(rest) is { } keyOf)
                return typed.Navigation.Length > 0 ? typed.Navigation + Casing.Pascal(keyOf.Name) + "Id" : Casing.Camel(typed.Role + Casing.Pascal(keyOf.Name) + "Id");
        }

        return reference;
    }

    /// <summary>The relation end with an id, or <see langword="null"/>.</summary>
    private static RelationEnd? EndOf(ModelSnapshot model, string id) =>
        model.TryGetEntry(id, out var entry) && model.Get<Relation>(entry.OwnerId) is { } relation
            ? relation.Ends.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal))
            : null;

    /// <summary>An attribute of the entity by id: its own, a base's, or one a stereotype of the entity or a base gives.</summary>
    private static ModelAttribute? AttributeOf(ModelSnapshot model, Entity entity, string id)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var e = entity; e is not null && seen.Add(e.Id); e = e.Base is { } b ? model.Get<Entity>(b) : null)
        {
            if (e.Attributes.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal)) is { } own)
                return own;
            foreach (var applied in model.EffectiveStereotypes(e))
            {
                if (applied.Stereotype.Attributes.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal)) is { } given)
                    return given;
            }
        }

        return null;
    }

    /// <summary>
    /// What fills a column nothing in the binding names, as the resolver's accounting says: <c>identity</c> (an identity, or a sequence on
    /// a primary key column), <c>computed</c>, <c>default</c> (a literal, or SQL for the database's dialect) or <c>unaccounted</c>.
    /// </summary>
    private static string StatusOf(ModelSnapshot model, ShapeColumn column, IReadOnlySet<string> primaryKey, string dialect)
    {
        var entry = column.Entry;
        if (entry.Generated == ColumnGeneration.Identity
            || (entry.Generated == ColumnGeneration.Sequence && entry.Sequence is { } sequence && model.Get<Sequence>(sequence) is not null
                && primaryKey.Contains(column.Id)))
            return "identity";
        if (entry.Computed is not null)
            return "computed";
        if (entry.Default is not null || entry.DefaultSql.ContainsKey(dialect) || entry.DefaultSql.ContainsKey("*"))
            return "default";
        return "unaccounted";
    }

    /// <summary>The ids of the table's primary key columns, own and inherited.</summary>
    private static HashSet<string> PrimaryKeyOf(ModelSnapshot model, Table table, IReadOnlyList<ShapeColumn> columns)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void Add(IEnumerable<string> references)
        {
            foreach (var reference in references)
            {
                if (Find(columns, reference) is var at and >= 0)
                    keys.Add(columns[at].Id);
            }
        }

        if (table.PrimaryKey is { } pk)
            Add(pk.Columns);
        foreach (var inherited in TableMembers.Of(model, table).PrimaryKeys)
            Add(inherited.Member.Columns);
        return keys;
    }

    private static string DialectOf(ModelSnapshot model, Table table) =>
        model.Get<Database>(table.Database) is { } db ? DialectTypeMaps.Name(db.Dialect) : "";

    /// <summary>Whether the binding writes its source table (absent write: the source, since it is a table).</summary>
    private static bool WritesSource(EntityBinding binding, Table table) =>
        binding.Write is null || (!binding.Write.None && string.Equals(binding.Write.Table, table.Id, StringComparison.Ordinal));

    private static void Name(HashSet<int> named, IReadOnlyList<ShapeColumn> columns, string reference)
    {
        if (Find(columns, reference) is var at and >= 0)
            named.Add(at);
    }

    private static int Single(IReadOnlyList<ShapeColumn> columns, Func<ShapeColumn, bool> match, out bool any)
    {
        var found = -1;
        var count = 0;
        for (var i = 0; i < columns.Count && count < 2; i++)
        {
            if (match(columns[i]))
            {
                found = i;
                count++;
            }
        }

        any = count > 0;
        return count == 1 ? found : -1;
    }
}
