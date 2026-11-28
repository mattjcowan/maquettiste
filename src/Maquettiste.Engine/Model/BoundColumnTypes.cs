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
/// <param name="ChildTable">The index of the binding's child table entry whose field it is, or -1 for a field of the binding itself.</param>
internal sealed record BoundColumnUse(string EntityId, int Binding, int Field, ModelAttribute Attribute, string OwnerId, string? TypeId, int ChildTable = -1);

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
    private static readonly ConditionalWeakTable<ModelSnapshot, Lazy<BoundColumnTypes>> Cache = new();

    private readonly Dictionary<(string Table, string Column), List<BoundColumnUse>> _uses = [];
    private readonly Dictionary<(string Table, string Column), string> _types = [];
    private readonly Dictionary<string, List<(string Table, string Column)>> _byType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedSet<string>> _tablesByElement = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ColumnLookup?> _tables = new(StringComparer.Ordinal);

    private BoundColumnTypes(ModelSnapshot model)
    {
        foreach (var entity in model.All<Entity>().OrderBy(e => e.Id, StringComparer.Ordinal))
            Collect(model, entity);
        foreach (var (key, uses) in _uses)
        {
            foreach (var use in uses)
            {
                foreach (var element in new[] { use.EntityId, use.OwnerId })
                {
                    if (!_tablesByElement.TryGetValue(element, out var tables))
                        _tablesByElement[element] = tables = new SortedSet<string>(StringComparer.Ordinal);
                    tables.Add(key.Table);
                }
            }
        }

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
        _tables.Clear(); // the column indexes serve only the collection, and the instance lives as long as its snapshot
    }

    /// <summary>The bound column types of a snapshot.</summary>
    /// <param name="model">The snapshot.</param>
    /// <returns>The types, built once per snapshot.</returns>
    public static BoundColumnTypes Of(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        // A Lazy, so the parallel validation workers that ask first wait for one build instead of each building their own (the table's
        // factory runs outside its lock).
        return Cache.GetValue(model, static m => new Lazy<BoundColumnTypes>(() => new BoundColumnTypes(m), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
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

    /// <summary>
    /// The table files with a column some binding field maps through an element: the bound entity, or the entity, base, stereotype or
    /// value object that declares the mapped attribute; ordinal.
    /// </summary>
    /// <param name="elementId">The element's id.</param>
    /// <returns>The table ids.</returns>
    public IReadOnlyCollection<string> TablesThrough(string elementId) =>
        _tablesByElement.TryGetValue(elementId, out var tables) ? tables : [];

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
            var source = Lookup(model, binding.Source, binding.Database);
            var write = binding.Write is { None: true } || binding.Write?.Table is not { } writeKey ? null : Lookup(model, writeKey, binding.Database);
            if (source is null && write is null)
                continue;
            for (var j = 0; j < binding.Fields.Count; j++)
            {
                var field = binding.Fields[j];
                if (AttributeOf(model, entity, field.Attribute) is not { } found)
                    continue; // an end, or nothing of the entity (MQ4045)
                var use = new BoundColumnUse(entity.Id, i, j, found.Attribute, found.OwnerId, found.Attribute.Collection ? null : TypeIdOf(model, found.Attribute.Type));
                var sourceColumn = source?.Find(field.Column);
                if (sourceColumn is not null)
                    Add(source!.Table.Id, sourceColumn.Id, use);
                if (write is { } w && !ReferenceEquals(w, source)
                    && (w.Find(field.Column) ?? (sourceColumn is null ? null : w.Find(sourceColumn.Name))) is { } writeColumn)
                    Add(w.Table.Id, writeColumn.Id, use);
            }

            // A child table's columns (retire-projection-design.md section 5): a list of single values holds one item per row, so its value
            // column holds the item's type.
            for (var k = 0; k < binding.ChildTables.Count; k++)
            {
                var child = binding.ChildTables[k];
                if (Lookup(model, child.Table, binding.Database) is not { } table)
                    continue;
                for (var j = 0; j < child.Fields.Count; j++)
                {
                    var field = child.Fields[j];
                    if (AttributeOf(model, entity, field.Attribute, listHead: true) is not { } found)
                        continue;
                    var whole = string.Equals(field.Attribute, child.Attribute, StringComparison.Ordinal);
                    var use = new BoundColumnUse(entity.Id, i, j, found.Attribute, found.OwnerId,
                        found.Attribute.Collection && !whole ? null : TypeIdOf(model, found.Attribute.Type), k);
                    if (table.Find(field.Column) is { } column)
                        Add(table.Table.Id, column.Id, use);
                }
            }
        }
    }

    private void Add(string tableId, string columnId, BoundColumnUse use)
    {
        if (!_uses.TryGetValue((tableId, columnId), out var uses))
            _uses[(tableId, columnId)] = uses = [];
        uses.Add(use);
    }

    /// <summary>A designed or imported table file of the database, with its own and inherited columns indexed; built once per table.</summary>
    private ColumnLookup? Lookup(ModelSnapshot model, string key, string database)
    {
        if (!_tables.TryGetValue(key, out var lookup))
        {
            lookup = model.Get<Table>(key) is { } table ? new ColumnLookup(model, table) : null;
            _tables[key] = lookup;
        }

        return lookup is not null && string.Equals(lookup.Table.Database, database, StringComparison.Ordinal) ? lookup : null;
    }

    /// <summary>
    /// A table file's own and inherited columns, found as the resolver finds a binding's column: by id, by name, then by name ignoring
    /// case, then ignoring case and underscores, when only one column matches.
    /// </summary>
    private sealed class ColumnLookup
    {
        private readonly IReadOnlyList<Column> _columns;
        private readonly Dictionary<string, Column> _byId;
        private Dictionary<string, Column>? _byName;
        private Dictionary<string, Column?>? _byLowerName;
        private Dictionary<string, Column?>? _byFoldedName;

        public ColumnLookup(ModelSnapshot model, Table table)
        {
            Table = table;
            var members = TableMembers.Of(model, table);
            _columns = members.Columns.Count == 0 ? table.Columns : [.. table.Columns, .. members.Columns.Select(m => m.Member)];
            _byId = new Dictionary<string, Column>(_columns.Count, StringComparer.Ordinal);
            foreach (var column in _columns)
                _byId.TryAdd(column.Id, column);
        }

        public Table Table { get; }

        // Fields name columns by id, so the name indexes are built only when a reference is not one (lookups run only while the types are
        // worked out, on one thread).
        public Column? Find(string reference)
        {
            if (_byId.TryGetValue(reference, out var column))
                return column;
            if (_byName is null)
                BuildNames();
            return _byName!.TryGetValue(reference, out column) ? column
                : _byLowerName!.TryGetValue(reference, out var loose) ? loose
                : _byFoldedName!.GetValueOrDefault(Fold(reference));
        }

        private void BuildNames()
        {
            _byName = new Dictionary<string, Column>(_columns.Count, StringComparer.Ordinal);
            _byLowerName = new Dictionary<string, Column?>(_columns.Count, StringComparer.OrdinalIgnoreCase);
            _byFoldedName = new Dictionary<string, Column?>(_columns.Count, StringComparer.Ordinal);
            foreach (var column in _columns)
            {
                _byName.TryAdd(column.Name, column);
                // A second column of the same loose name makes the loose match ambiguous: null.
                _byLowerName[column.Name] = _byLowerName.ContainsKey(column.Name) ? null : column;
                var folded = Fold(column.Name);
                _byFoldedName[folded] = _byFoldedName.ContainsKey(folded) ? null : column;
            }
        }

        private static string Fold(string text) => text.Replace("_", "", StringComparison.Ordinal).ToUpperInvariant();
    }

    /// <summary>
    /// The attribute a binding field names: an own or inherited attribute, one a stereotype of the entity adds, or
    /// <c>&lt;attributeId&gt;.&lt;memberId&gt;</c>, a member of a single value object attribute (deeper for a nested value object, and with
    /// <paramref name="listHead"/> a member of a list's item); with the element that declares it.
    /// </summary>
    private static (ModelAttribute Attribute, string OwnerId)? AttributeOf(ModelSnapshot model, Entity entity, string reference, bool listHead = false)
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
        // A member of a value object, to any depth (retire-projection-design.md section 6); a child table's row is one item of a list.
        (ModelAttribute Attribute, string OwnerId) current = found;
        foreach (var memberId in reference[(dot + 1)..].Split('.'))
        {
            if ((current.Attribute.Collection && !(listHead && ReferenceEquals(current.Attribute, found.Attribute)))
                || current.Attribute.Type.Ref is not { } voId || model.Get<ValueObject>(voId) is not { } valueObject
                || valueObject.Attributes.FirstOrDefault(m => string.Equals(m.Id, memberId, StringComparison.Ordinal)) is not { } member)
                return null;
            current = (member, valueObject.Id);
        }

        return current;
    }

    /// <summary>The enum, reference type or scalar type a type reference names, or <see langword="null"/>.</summary>
    private static string? TypeIdOf(ModelSnapshot model, TypeRef type) =>
        type.Ref is { } id && model.GetDocument(id)?.Element is EnumType or ReferenceType or ScalarType ? id : null;
}
