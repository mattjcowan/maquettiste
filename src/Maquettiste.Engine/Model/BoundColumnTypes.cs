using System.Runtime.CompilerServices;

namespace Maquettiste.Engine.Model;

/// <summary>
/// One binding field that maps a column of a table file: the entity, the binding and field indexes, the attribute (or value object
/// member) it maps, the element that declares that attribute, and the enum, reference type or scalar type the attribute is typed by.
/// </summary>
/// <param name="EntityId">The bound entity's id.</param>
/// <param name="Binding">The binding's index in the entity's bindings.</param>
/// <param name="Field">The field's index in the binding's fields.</param>
/// <param name="Attribute">The attribute or value object member the field maps.</param>
/// <param name="OwnerId">The element that declares <paramref name="Attribute"/>: the entity, a base, a stereotype or a value object.</param>
/// <param name="TypeId">The enum, reference type or scalar type the attribute is typed by; <see langword="null"/> for any other type and
/// for a collection.</param>
internal sealed record BoundColumnUse(string EntityId, int Binding, int Field, ModelAttribute Attribute, string OwnerId, string? TypeId);

/// <summary>
/// The type a column of a designed or imported table holds, worked out from the entity side (0.28.0): storage never names the model, so a
/// column states only its physical type, and the entities' bindings say which attribute each column stores. A column's bound type is the
/// enum, reference type or scalar type of the attribute the bindings map to it, set only when every binding field that maps the column
/// maps an attribute of that one type; a column no binding maps, or whose fields disagree, has none. A pure function of the snapshot,
/// cached per snapshot, so validation and resolution read the same answer.
/// </summary>
/// <remarks>
/// The fields are those of each entity's first binding to a database (a second one is MQ4050 and ignored). A binding reaches the table
/// files it reads (<c>source</c>) and writes (<c>write.table</c>) in its database; a field's column is found as the resolver finds it: by
/// id (a keyed id for an inherited column), by name, then by name ignoring case, then ignoring case and underscores, when only one column
/// matches; in a write table that is not the source, also by the name of the source column it reads.
/// </remarks>
internal sealed class BoundColumnTypes
{
    private static readonly ConditionalWeakTable<ModelSnapshot, BoundColumnTypes> Cache = new();

    private readonly Dictionary<(string Table, string Column), List<BoundColumnUse>> _uses = [];
    private readonly Dictionary<(string Table, string Column), string> _types = [];
    private readonly Dictionary<string, List<(string Table, string Column)>> _byType = new(StringComparer.Ordinal);

    private BoundColumnTypes(ModelSnapshot model)
    {
        foreach (var entity in model.All<Entity>().OrderBy(e => e.Id, StringComparer.Ordinal))
            Collect(model, entity);
        foreach (var (key, uses) in _uses)
        {
            if (uses[0].TypeId is { } typeId && uses.TrueForAll(u => string.Equals(u.TypeId, typeId, StringComparison.Ordinal)))
            {
                _types[key] = typeId;
                if (!_byType.TryGetValue(typeId, out var columns))
                    _byType[typeId] = columns = [];
                columns.Add(key);
            }
        }

        foreach (var columns in _byType.Values)
            columns.Sort((a, b) => string.CompareOrdinal(a.Table, b.Table) is var c and not 0 ? c : string.CompareOrdinal(a.Column, b.Column));
    }

    /// <summary>The bound column types of a snapshot.</summary>
    /// <param name="model">The snapshot.</param>
    /// <returns>The types, built once per snapshot.</returns>
    public static BoundColumnTypes Of(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Cache.GetValue(model, static m => new BoundColumnTypes(m));
    }

    /// <summary>The enum, reference type or scalar type a column holds, by its bindings; <see langword="null"/> when it has none.</summary>
    /// <param name="tableId">The table file's id.</param>
    /// <param name="columnId">The column's id (an inherited column's keyed id).</param>
    /// <returns>The type's id.</returns>
    public string? TypeOf(string tableId, string columnId) => _types.GetValueOrDefault((tableId, columnId));

    /// <summary>The binding fields that map a column, in entity id then binding then field order.</summary>
    /// <param name="tableId">The table file's id.</param>
    /// <param name="columnId">The column's id (an inherited column's keyed id).</param>
    /// <returns>The fields.</returns>
    public IReadOnlyList<BoundColumnUse> UsesOf(string tableId, string columnId) =>
        _uses.TryGetValue((tableId, columnId), out var uses) ? uses : [];

    /// <summary>The columns a type is the bound type of, ordinal by table id then column id.</summary>
    /// <param name="typeId">The enum, reference type or scalar type.</param>
    /// <returns>The (table id, column id) pairs.</returns>
    public IReadOnlyList<(string Table, string Column)> ColumnsOf(string typeId) =>
        _byType.TryGetValue(typeId, out var columns) ? columns : [];

    private void Collect(ModelSnapshot model, Entity entity)
    {
        var databases = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < entity.Bindings.Count; i++)
        {
            var binding = entity.Bindings[i];
            if (!databases.Add(binding.Database))
                continue;
            var source = TableOf(model, binding.Source, binding.Database);
            var write = binding.Write is { None: true } || binding.Write?.Table is not { } writeKey ? null : TableOf(model, writeKey, binding.Database);
            if (source is null && write is null)
                continue;
            for (var j = 0; j < binding.Fields.Count; j++)
            {
                var field = binding.Fields[j];
                if (AttributeOf(model, entity, field.Attribute) is not { } found)
                    continue; // an end, or nothing of the entity (MQ4045)
                var use = new BoundColumnUse(entity.Id, i, j, found.Attribute, found.OwnerId, found.Attribute.Collection ? null : TypeIdOf(model, found.Attribute.Type));
                var sourceColumn = source is null ? null : Find(source.Value.Columns, field.Column);
                if (sourceColumn is not null)
                    Add(source!.Value.Table.Id, sourceColumn.Id, use);
                if (write is { } w && !ReferenceEquals(w.Table, source?.Table)
                    && (Find(w.Columns, field.Column) ?? (sourceColumn is null ? null : Find(w.Columns, sourceColumn.Name))) is { } writeColumn)
                    Add(w.Table.Id, writeColumn.Id, use);
            }
        }
    }

    private void Add(string tableId, string columnId, BoundColumnUse use)
    {
        if (!_uses.TryGetValue((tableId, columnId), out var uses))
            _uses[(tableId, columnId)] = uses = [];
        uses.Add(use);
    }

    /// <summary>A designed or imported table file of the database, with its own and inherited columns.</summary>
    private static (Table Table, IReadOnlyList<Column> Columns)? TableOf(ModelSnapshot model, string key, string database)
    {
        if (model.Get<Table>(key) is not { Origin: not TableOrigin.Synthesized } table || !string.Equals(table.Database, database, StringComparison.Ordinal))
            return null;
        var members = TableMembers.Of(model, table);
        return (table, members.Columns.Count == 0 ? table.Columns : [.. table.Columns, .. members.Columns.Select(m => m.Member)]);
    }

    /// <summary>A column by id, by name, then loosely by name (ignoring case, then also underscores) when only one matches.</summary>
    private static Column? Find(IReadOnlyList<Column> columns, string reference)
    {
        foreach (var column in columns)
        {
            if (string.Equals(column.Id, reference, StringComparison.Ordinal))
                return column;
        }

        foreach (var column in columns)
        {
            if (string.Equals(column.Name, reference, StringComparison.Ordinal))
                return column;
        }

        var ignoringCase = columns.Where(c => string.Equals(c.Name, reference, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        if (ignoringCase.Count > 0)
            return ignoringCase.Count == 1 ? ignoringCase[0] : null;
        var folded = Fold(reference);
        var matches = columns.Where(c => Fold(c.Name) == folded).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;

        static string Fold(string text) => text.Replace("_", "", StringComparison.Ordinal).ToUpperInvariant();
    }

    /// <summary>
    /// The attribute a binding field names: an own or inherited attribute, one a stereotype of the entity adds, or
    /// <c>&lt;attributeId&gt;.&lt;memberId&gt;</c>, a member of a single value object attribute; with the element that declares it.
    /// </summary>
    private static (ModelAttribute Attribute, string OwnerId)? AttributeOf(ModelSnapshot model, Entity entity, string reference)
    {
        var dot = reference.IndexOf('.', StringComparison.Ordinal);
        var head = dot > 0 ? reference[..dot] : reference;
        (ModelAttribute Attribute, string OwnerId)? owner = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var e = entity; e is not null && seen.Add(e.Id); e = e.Base is { } b ? model.Get<Entity>(b) : null)
        {
            if (e.Attributes.FirstOrDefault(a => string.Equals(a.Id, head, StringComparison.Ordinal)) is { } attribute)
            {
                owner = (attribute, e.Id);
                break;
            }
        }

        if (owner is null)
        {
            foreach (var applied in model.EffectiveStereotypes(entity))
            {
                if (applied.Stereotype.Attributes.FirstOrDefault(a => string.Equals(a.Id, head, StringComparison.Ordinal)) is { } attribute)
                {
                    owner = (attribute, applied.Stereotype.Id);
                    break;
                }
            }
        }

        if (owner is not { } found || dot <= 0)
            return owner;
        if (found.Attribute.Collection || found.Attribute.Type.Ref is not { } voId || model.Get<ValueObject>(voId) is not { } valueObject)
            return null;
        var memberId = reference[(dot + 1)..];
        return valueObject.Attributes.FirstOrDefault(m => string.Equals(m.Id, memberId, StringComparison.Ordinal)) is { } member ? (member, valueObject.Id) : null;
    }

    /// <summary>The enum, reference type or scalar type a type reference names, or <see langword="null"/>.</summary>
    private static string? TypeIdOf(ModelSnapshot model, TypeRef type) =>
        type.Ref is { } id && model.GetDocument(id)?.Element is EnumType or ReferenceType or ScalarType ? id : null;
}
