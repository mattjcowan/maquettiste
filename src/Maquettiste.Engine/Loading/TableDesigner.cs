using System.Globalization;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Text;

namespace Maquettiste.Engine.Loading;

/// <summary>
/// The tables "Create tables…" proposes for entities (retire-projection-design.md section 7): the shape-proposing code that used to
/// run inside resolution (a table per entity, one per hierarchy or per concrete entity or per type, child tables, junction tables,
/// foreign key columns, key sequences, constraint names and comments from the naming conventions), run only when someone asks for
/// tables. It reads the resolved entities, relations and types of the model as it is, the effective conventions and type map of the
/// database, what a relation mapping already says about a relation (read, never written), and the tables the database already has for
/// entities outside the request. It writes nothing: the materializer turns the proposal into table files and bindings. For the same
/// settings, every table has the shape the projection gave it before 0.29.0 (column names, types, nullability, keys, foreign keys with
/// their actions, uniques, indexes, sequences, comments).
/// </summary>
internal sealed class TableDesigner
{
    private readonly ModelSnapshot _model;
    private readonly ResolvedModel _resolved;
    private readonly Database _db;
    private readonly EffectiveConventions _conv;
    private readonly IReadOnlyDictionary<string, string> _typeMap;
    private readonly string _dialect;
    private readonly Inflector _inflector;
    private readonly Func<string, DesignRelationHints?> _relationHints;
    private readonly Func<string, DesignHierarchy> _shapeOf;
    private readonly Func<string, StorageKind?> _listStorage;
    private readonly Func<string, DesignTarget?> _outside;
    private readonly Dictionary<string, DesignedEntity> _entities = new(StringComparer.Ordinal);
    private readonly List<DesignedEntity> _entityOrder = [];
    private readonly List<DesignedTable> _tables = [];
    private readonly Dictionary<string, DesignedTable> _tablesByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DesignedSequence> _sequences = new(StringComparer.Ordinal);
    private readonly List<DesignedRelation> _relations = [];
    private readonly List<string> _notes = [];
    private readonly Dictionary<string, DesignTarget?> _outsideCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DesignedTable> _lookups = new(StringComparer.Ordinal);
    private string? _refusal;

    /// <summary>Creates a designer for one database.</summary>
    /// <param name="model">The model as it is.</param>
    /// <param name="resolved">Its resolution (the entities, relations and types; the database's existing tables).</param>
    /// <param name="db">The database the tables go to.</param>
    /// <param name="relationHints">What a relation's mapping to the database says, by relation id.</param>
    /// <param name="shapeOf">The shape of a hierarchy, by its root entity's id.</param>
    /// <param name="listStorage">The storage the request asks for a list attribute (table or json), by attribute id, or <see langword="null"/>.</param>
    /// <param name="outside">The table the database already has for an entity outside the request, or <see langword="null"/>.</param>
    public TableDesigner(ModelSnapshot model, ResolvedModel resolved, Database db, Func<string, DesignRelationHints?> relationHints,
        Func<string, DesignHierarchy> shapeOf, Func<string, StorageKind?> listStorage, Func<string, DesignTarget?> outside)
    {
        _model = model;
        _resolved = resolved;
        _db = db;
        _conv = EffectiveConventions.For(model.Settings, db.Name);
        _typeMap = DialectTypeMaps.Effective(db.Dialect, model.Settings);
        _dialect = DialectTypeMaps.Name(db.Dialect);
        _inflector = new Inflector(model.Settings.Inflection);
        _relationHints = relationHints;
        _shapeOf = shapeOf;
        _listStorage = listStorage;
        _outside = outside;
    }

    /// <summary>The effective conventions of the database.</summary>
    public EffectiveConventions Conventions => _conv;

    /// <summary>
    /// Whether only the columns' shapes are wanted (materialize-columns): reference columns then need no storage strategy and get no
    /// constraint, as a column added to an existing table never did.
    /// </summary>
    public bool ColumnsOnly { get; init; }

    /// <summary>Proposes the tables of the requested entities. Hierarchies must be requested whole (the caller checks).</summary>
    /// <param name="requested">The requested entities' ids.</param>
    /// <returns>The proposal.</returns>
    public TableProposal Design(IReadOnlySet<string> requested)
    {
        foreach (var entity in _resolved.Entities)
        {
            if (!requested.Contains(entity.Id))
                continue;
            var e = new DesignedEntity { Entity = entity };
            _entities[entity.Id] = e;
            _entityOrder.Add(e);
        }

        foreach (var e in _entityOrder)
        {
            for (var b = e.Entity.Base; b is not null; b = b.Base)
            {
                if (_entities.TryGetValue(b.Id, out var basePlacement))
                {
                    e.Base = basePlacement;
                    basePlacement.Derived.Add(e);
                    break;
                }
            }
        }

        foreach (var e in _entityOrder)
        {
            var root = e;
            var seen = new HashSet<DesignedEntity>(ReferenceEqualityComparer.Instance);
            while (root.Base is not null && seen.Add(root))
                root = root.Base;
            e.Root = root;
            foreach (var key in root.Entity.Key?.Attributes ?? e.Entity.Key?.Attributes ?? RList<RAttribute>.Empty)
                e.KeyIds.Add(key.Id);
        }

        foreach (var e in _entityOrder)
        {
            if (e.Root.Derived.Count > 0 || e.Base is not null)
                e.Shape = _shapeOf(e.Root.Entity.Id);
        }

        CreateEntityTables();
        foreach (var relation in _resolved.Relations)
            DesignRelation(relation);

        if (!ColumnsOnly)
            AddReferenceConstraints();
        Finish();
        return new TableProposal(_entityOrder, _tables, _relations, _notes) { Refusal = _refusal };
    }

    // ---- entity tables ----------------------------------------------------------------------------------------------------------

    private void CreateEntityTables()
    {
        foreach (var p in _entityOrder)
        {
            var owns = p.Shape switch
            {
                DesignHierarchy.OneTable => ReferenceEquals(p.Root, p),
                DesignHierarchy.PerConcrete => !p.Entity.IsAbstract,
                _ => true,
            };
            if (!owns)
                continue;
            var t = new DesignedTable
            {
                Key = p.Entity.Id + "@" + _db.Id,
                Name = TableNameFor(p.Entity.Name, ExplicitPlural(p.Entity.Id)),
                Kind = DesignedTableKind.Entity,
                Entity = p.Entity,
                PackageId = _model.Get<Entity>(p.Entity.Id)?.Package,
            };
            AddTable(t);
            p.Table = t;
        }

        foreach (var p in _entityOrder)
        {
            if (p.Shape == DesignHierarchy.OneTable && !ReferenceEquals(p.Root, p))
                p.Table = p.Root.Table;
        }

        foreach (var p in _entityOrder)
        {
            if (p.Table is null || !ReferenceEquals(p.Table.Entity, p.Entity))
                continue;
            AddEntityColumns(p, p.Table);
        }
    }

    private void AddTable(DesignedTable t)
    {
        _tables.Add(t);
        _tablesByKey.TryAdd(t.Key, t);
    }

    private void AddEntityColumns(DesignedEntity p, DesignedTable t)
    {
        var entity = p.Entity;
        switch (p.Shape)
        {
            case DesignHierarchy.OneTable:
                foreach (var a in entity.Attributes)
                    AddAttributeColumns(t, a, p.KeyIds.Contains(a.Id), false, entity.Name);
                foreach (var d in Descendants(p))
                {
                    foreach (var a in d.Entity.Attributes)
                    {
                        if (!t.ByKey.ContainsKey(a.Id) && !HasPathUnder(t, a.Id))
                            AddAttributeColumns(t, a, p.KeyIds.Contains(a.Id), true, entity.Name);
                    }
                }

                var discriminator = AddColumn(t, "discriminator", Render(_conv.DiscriminatorColumn), "string", 64, null, null, false, null, null, null);
                foreach (var member in Descendants(p).Prepend(p))
                    member.Discriminator = discriminator;
                break;
            case DesignHierarchy.PerType when p.Base is not null:
                foreach (var a in entity.Attributes)
                {
                    if (p.KeyIds.Contains(a.Id) || ReferenceEquals(a.DeclaringEntity, entity))
                        AddAttributeColumns(t, a, p.KeyIds.Contains(a.Id), false, entity.Name);
                }

                break;
            default:
                foreach (var a in entity.Attributes)
                    AddAttributeColumns(t, a, p.KeyIds.Contains(a.Id), false, entity.Name);
                break;
        }

        if (t.PrimaryKey.Count == 0)
            t.PrimaryKey.AddRange(KeyColumnKeys(t, p));

        // Table per type: the derived table's key is also a foreign key to the base table.
        if (p.Shape == DesignHierarchy.PerType && p.Base?.Table is { } baseTable && !ReferenceEquals(baseTable, t))
        {
            var fk = new DesignedForeignKey
            {
                Columns = [.. t.PrimaryKey], Target = new DesignTarget { Designed = baseTable }, OnDelete = "cascade", OnUpdate = "no-action", IsBase = true,
            };
            t.ForeignKeys.Add(fk);
            p.BaseForeignKey = fk;
            // Its key columns follow the base table's.
            foreach (var key in t.PrimaryKey)
            {
                if (t.Resolve(key) is not { } column || baseTable.Resolve(key) is not { } referenced)
                    continue;
                (column.Type, column.Length, column.Precision, column.Scale, column.NativeType) =
                    (referenced.Type, referenced.Length, referenced.Precision, referenced.Scale, referenced.NativeType);
            }
        }

        ApplyKeyGeneration(p, t);
        AddAttributeConstraints(t, entity, p.Shape == DesignHierarchy.OneTable ? Descendants(p).Select(d => d.Entity) : []);
        RunDeferred(t);
    }

    private static void RunDeferred(DesignedTable t)
    {
        foreach (var action in t.Deferred.ToList())
            action();
        t.Deferred.Clear();
    }

    private static bool HasPathUnder(DesignedTable t, string attributeId) =>
        t.Columns.Any(c => c.AttributePath is { } path && path.StartsWith(attributeId + ".", StringComparison.Ordinal));

    private static IEnumerable<DesignedEntity> Descendants(DesignedEntity p)
    {
        foreach (var d in p.Derived)
        {
            yield return d;
            foreach (var dd in Descendants(d))
                yield return dd;
        }
    }

    private static IEnumerable<string> KeyColumnKeys(DesignedTable t, DesignedEntity p) =>
        (p.Root.Entity.Key?.Attributes ?? p.Entity.Key?.Attributes ?? RList<RAttribute>.Empty)
            .SelectMany(k => t.Columns.Where(c => c.AttributePath is { } path
                && (string.Equals(path, k.Id, StringComparison.Ordinal) || path.StartsWith(k.Id + ".", StringComparison.Ordinal))))
            .Select(c => c.Key);

    /// <summary>Identity or a sequence on the key column (engine-design.md section 7.5, D37).</summary>
    private void ApplyKeyGeneration(DesignedEntity p, DesignedTable t)
    {
        if (p.Shape == DesignHierarchy.PerType && p.Base is not null)
            return; // the key comes from the base row
        var strategy = _model.Get<Entity>(p.Root.Entity.Id)?.Key?.Strategy;
        if (t.PrimaryKey.Count != 1 || t.Resolve(t.PrimaryKey[0]) is not { } column)
            return;
        if (strategy == IdentityStrategy.DatabaseIdentity && column.Sequence is null)
        {
            column.Identity = true;
            return;
        }

        if (strategy != IdentityStrategy.Sequence || column.Identity)
            return;

        var key = p.Root.Entity.Id + ".sequence@" + _db.Id;
        if (!_sequences.TryGetValue(key, out var sequence))
        {
            var tableName = p.Root.Table?.Name ?? TableNameFor(p.Root.Entity.Name, ExplicitPlural(p.Root.Entity.Id));
            var type = column.Type is "int16" or "int32" or "int64" ? column.Type : "int64";
            sequence = new DesignedSequence
            {
                Key = key,
                Name = Render(_conv.SequenceName, ("table", tableName), ("entity", p.Root.Entity.Name)),
                Type = type,
                SchemaId = t.SchemaId,
            };
            _sequences[key] = sequence;
        }

        column.Sequence = sequence;
    }

    /// <summary>Unique and indexed attributes, and alternate keys, as constraints of the table holding their columns.</summary>
    private static void AddAttributeConstraints(DesignedTable t, REntity entity, IEnumerable<REntity> more)
    {
        foreach (var e in more.Prepend(entity))
        {
            foreach (var a in e.Attributes)
            {
                if (!a.Unique && !a.Indexed)
                    continue;
                if (!ReferenceEquals(e, entity) && !ReferenceEquals(a.DeclaringEntity, e))
                    continue;
                var keys = ColumnsOf(t, a.Id);
                if (keys.Count == 0)
                    continue;
                if (a.Unique)
                    t.Uniques.Add(new DesignedUnique(keys, null, null));
                else
                    t.Indexes.Add(new DesignedIndex([.. keys.Select(k => new DesignedIndexColumn(k, false))], [], null, false, "default", null));
            }

            foreach (var ak in e.AlternateKeys)
            {
                var keys = ak.Attributes.SelectMany(a => ColumnsOf(t, a.Id)).ToList();
                if (keys.Count > 0 && ak.Attributes.All(a => ColumnsOf(t, a.Id).Count > 0) && !t.Uniques.Any(u => u.Columns.SequenceEqual(keys)))
                    t.Uniques.Add(new DesignedUnique(keys, null, ak.Name));
            }
        }
    }

    private static List<string> ColumnsOf(DesignedTable t, string attributeId) =>
        [.. t.Columns.Where(c => c.AttributePath is { } path
                && (string.Equals(path, attributeId, StringComparison.Ordinal) || path.StartsWith(attributeId + ".", StringComparison.Ordinal)))
            .Select(c => c.Key)];

    // ---- attribute columns ------------------------------------------------------------------------------------------------------

    /// <summary>Adds the columns of one attribute (engine-design.md section 7.5) to a table.</summary>
    private void AddAttributeColumns(DesignedTable t, RAttribute a, bool isKey, bool forceNullable, string entityName)
    {
        if (a.Derived is { Stored: false })
            return; // derived: not stored unless marked stored
        var nullable = !isKey && (forceNullable || !a.Required);
        var name = isKey
            ? Render(_conv.KeyColumn, ("attribute", a.Name), ("entity", entityName), ("table", t.Name))
            : ColumnName(a.Name);
        var storage = a.Collection ? _listStorage(a.Id) : null;
        AddValueColumns(t, a, a.Id, name, nullable, storage, a.Name, 0);
    }

    private void AddValueColumns(DesignedTable t, RAttribute a, string path, string name, bool nullable, StorageKind? storage, string prefix, int depth)
    {
        if (a.Type.ReferenceType is { } reference)
        {
            // A single value is one column holding the row's code; a list is a child table with one code column (what the sql-ddl
            // pack made of a template-defined collection), or a JSON column when the request asks for one.
            if (a.Collection)
            {
                // Only the request turns such a list into JSON.
                if (depth == 0 && _listStorage(a.Id) == StorageKind.Json)
                    AddColumn(t, path, name, "json", null, null, null, nullable, null, a, path);
                else if (depth == 0 && !KnownStrategy(reference))
                    return;
                else if (a.Owner is RRelation relation && KeepsListsInTables(reference))
                    _refusal ??= $"Attribute '{a.Name}' of relation '{relation.Name}' is a list of reference type '{reference.Name}' kept in a table of its own, which no binding can hold yet: store it as JSON, or leave the relation out.";
                else if (depth == 0 && t.Kind == DesignedTableKind.Entity && KeepsListsInTables(reference))
                    t.Deferred.Add(() => CreateReferenceListTable(t, a));
                else if (!reference.Bindings.ContainsKey(_db.Name) && reference.Storage.TryGetValue(_db.Name, out var listChoice)
                    && listChoice.Strategy == "native" && _dialect == "postgresql")
                    _refusal ??= $"Attribute '{a.Name}' is a list of reference type '{reference.Name}', which the sql-ddl pack keeps in an array of the PostgreSQL enum type '{Casing.Snake(reference.Name)}_t', a shape no binding holds: store the list as JSON (listStorage json), or store the type as a lookup table, then create the tables again.";
                else if (depth == 0 && t.Kind == DesignedTableKind.Entity)
                    _notes.Add($"Attribute '{a.Name}' is a list of reference type '{reference.Name}', whose storage in database '{_db.Name}' keeps no list in a table of its own: it gets no table; store it as JSON, or bind it by hand.");
                else
                    _notes.Add($"Attribute '{a.Name}' is a list of reference type '{reference.Name}' inside table '{t.Name}': it gets no table of its own; store it as JSON, or bind it by hand.");
                return;
            }

            if (!KnownStrategy(reference))
                return;
            AddColumn(t, path, name, "reference", a.Length, null, null, nullable, a.Default, a, path, reference);
            return;
        }

        if (a.Type.ValueObject is { } vo)
        {
            if (a.Collection)
            {
                if (CollectionStorage(storage) == StorageKind.Json)
                    AddColumn(t, path, name, "json", null, null, null, nullable, null, a, path);
                else
                    t.Deferred.Add(() => CreateChildTable(t, a, path, true));
                return;
            }

            var kind = storage is StorageKind.Embedded or StorageKind.Table or StorageKind.Json ? storage.Value
                : _conv.ValueObjectStorage is StorageKind.Embedded or StorageKind.Table or StorageKind.Json ? _conv.ValueObjectStorage : StorageKind.Embedded;
            switch (kind)
            {
                case StorageKind.Json:
                    AddColumn(t, path, name, "json", null, null, null, nullable, null, a, path);
                    break;
                case StorageKind.Table:
                    t.Deferred.Add(() => CreateChildTable(t, a, path, false));
                    break;
                default:
                    Embed(t, vo, path, prefix, nullable, depth, []);
                    break;
            }

            return;
        }

        if (a.Collection)
        {
            if (CollectionStorage(storage) == StorageKind.Json)
                AddColumn(t, path, name, "json", null, null, null, nullable, null, a, path);
            else
                t.Deferred.Add(() => CreateChildTable(t, a, path, true));
            return;
        }

        if (a.Type.Enum is { } e)
        {
            AddEnumColumn(t, a, e, path, name, nullable, storage);
            return;
        }

        AddColumn(t, path, name, a.Type.Builtin ?? "string", a.Length, a.Precision, a.Scale, nullable, a.Default, a, path, scalar: a.Type.Scalar);
    }

    private StorageKind CollectionStorage(StorageKind? storage) =>
        storage is StorageKind.Table or StorageKind.Json ? storage.Value
        : _conv.ValueObjectCollectionStorage == StorageKind.Json ? StorageKind.Json : StorageKind.Table;

    /// <summary>The storage a list attribute gets when the request says nothing: the conventions'.</summary>
    /// <param name="referenceList">Whether it is a list of a reference type (a table of its own unless asked otherwise).</param>
    public StorageKind DefaultListStorage(bool referenceList) =>
        referenceList ? StorageKind.Table : CollectionStorage(null);

    /// <summary>Embedded value object: prefixed columns, recursively (keys <c>&lt;attr&gt;.&lt;member&gt;</c>); a containment cycle stops.</summary>
    private void Embed(DesignedTable t, RValueObject vo, string path, string prefix, bool nullable, int depth, List<RValueObject> outer)
    {
        var containment = new List<RValueObject>(outer) { vo };
        foreach (var m in vo.Attributes)
        {
            var memberPath = path + "." + m.Id;
            var memberName = Render(_conv.ValueObjectColumn, ("attribute", prefix), ("member", m.Name));
            var memberNullable = nullable || !m.Required;
            if (m.Type.ValueObject is { } nested && !m.Collection && depth < 16 && !containment.Any(c => ReferenceEquals(c, nested)))
                Embed(t, nested, memberPath, memberName, memberNullable, depth + 1, containment);
            else if (m.Collection || m.Type.ValueObject is not null)
                AddColumn(t, memberPath, memberName, "json", null, null, null, memberNullable, null, m, memberPath);
            else if (m.Type.Enum is { } e)
                AddEnumColumn(t, m, e, memberPath, memberName, memberNullable, null);
            else
                AddColumn(t, memberPath, memberName, m.Type.Builtin ?? "string", m.Length, m.Precision, m.Scale, memberNullable, m.Default, m, memberPath,
                    scalar: m.Type.Scalar);
        }
    }

    /// <summary>Enum storage: <c>int</c> (value, else ordinal) or <c>string</c> (code, else name).</summary>
    private void AddEnumColumn(DesignedTable t, RAttribute a, REnum e, string path, string name, bool nullable, StorageKind? storage)
    {
        var kind = storage is StorageKind.Int or StorageKind.String ? storage.Value
            : _conv.EnumStorage is StorageKind.Int or StorageKind.String ? _conv.EnumStorage : StorageKind.Int;
        var member = a.Default is string text
            ? e.Members.FirstOrDefault(m => string.Equals(m.Name, text, StringComparison.Ordinal) || string.Equals(m.Code, text, StringComparison.Ordinal))
            : null;
        switch (kind)
        {
            case StorageKind.String:
                AddColumn(t, path, name, "string", StringLength(e), null, null, nullable, member is null ? a.Default : member.Code ?? member.Name, a, path);
                break;
            default:
                AddColumn(t, path, name, IntegerType(e), null, null, null, nullable, member is null ? a.Default : ValueOf(e, member), a, path);
                break;
        }
    }

    private static long ValueOf(REnum e, REnumMember member)
    {
        for (var i = 0; i < e.Members.Count; i++)
        {
            if (ReferenceEquals(e.Members[i], member))
                return member.Value ?? i;
        }

        return member.Value ?? 0;
    }

    private static string IntegerType(REnum e) =>
        e.Members.Any(m => m.Value is < int.MinValue or > int.MaxValue) ? "int64" : "int32";

    private static int StringLength(REnum e) => Math.Max(1, e.Members.Select(m => (m.Code ?? m.Name).Length).DefaultIfEmpty(1).Max());

    /// <summary>A child table (a value object stored as a table, or a list): the owner's key as a foreign key, the order column for a
    /// list, then the value columns.</summary>
    private void CreateChildTable(DesignedTable owner, RAttribute a, string path, bool collection)
    {
        // Shapes a binding cannot hold yet (retire-projection-design.md section 3): refused, never dropped.
        if (a.Owner is RRelation relation)
        {
            _refusal ??= $"Attribute '{a.Name}' of relation '{relation.Name}' is stored in a table of its own, which no binding can hold yet: store it as JSON (listStorage json), or leave the relation out.";
            return;
        }

        if (owner.Kind == DesignedTableKind.Child)
        {
            _refusal ??= $"Attribute '{a.Name}' is stored in a table of its own inside the table of attribute '{owner.Attribute?.Name}', which no binding can hold yet: store it as JSON or embedded.";
            return;
        }

        var ownerId = owner.Entity?.Id ?? owner.Relation?.Id ?? owner.Key;
        var ownerName = owner.Entity?.Name ?? owner.Relation?.Name ?? owner.Name;
        var t = new DesignedTable
        {
            Key = ownerId + "." + a.Id + "@" + _db.Id,
            Name = NamePattern.Render(_conv.ChildTable, new Dictionary<string, string>(StringComparer.Ordinal) { ["entity"] = ownerName, ["attribute"] = a.Name },
                    _conv.TableCase, _conv.PluralTables ? _inflector : null, owner.Entity is { } ownerEntity ? ExplicitPlural(ownerEntity.Id) : null),
            SchemaId = owner.SchemaId,
            Kind = DesignedTableKind.Child,
            Entity = owner.Entity,
            Relation = owner.Relation,
            Attribute = a,
            Owner = owner,
            IsList = collection,
            PackageId = owner.PackageId,
        };
        AddTable(t);
        owner.Children.Add(t);
        var ownerKeys = new List<string>();
        foreach (var pkKey in owner.PrimaryKey)
        {
            if (owner.Resolve(pkKey) is not { } ownerColumn)
                continue;
            var columnName = Render(_conv.ForeignKeyColumn, ("role", ownerName), ("key", ownerColumn.Attribute?.Name ?? ownerColumn.Name),
                ("entity", ownerName), ("table", t.Name));
            AddColumn(t, ownerColumn.Key, columnName, ownerColumn.Type, ownerColumn.Length, ownerColumn.Precision, ownerColumn.Scale, false, null, null, null,
                scalar: ownerColumn.Scalar, follows: Shape(ownerColumn));
            ownerKeys.Add(ownerColumn.Key);
        }

        if (collection)
            AddColumn(t, "position", Render(_conv.OrderColumn), "int32", null, null, null, false, null, null, null);
        if (a.Type.ValueObject is { } vo)
        {
            foreach (var m in vo.Attributes)
            {
                var memberPath = path + "." + m.Id;
                if (m.Type.ValueObject is { } nested && !m.Collection)
                    Embed(t, nested, memberPath, m.Name, !m.Required, 1, [vo]);
                else
                    AddValueColumns(t, m, memberPath, ColumnName(m.Name), !m.Required, m.Collection ? StorageKind.Json : null, m.Name, 1);
            }
        }
        else
        {
            AddScalarValue(t, a, path, ColumnName(_inflector.Singularize(a.Name)));
        }

        if (t.PrimaryKey.Count == 0)
        {
            t.PrimaryKey.AddRange(ownerKeys);
            if (collection)
                t.PrimaryKey.Add("position");
        }

        if (ownerKeys.Count > 0)
            t.ForeignKeys.Add(new DesignedForeignKey
            {
                Columns = ownerKeys, Target = new DesignTarget { Designed = owner }, OnDelete = "cascade", OnUpdate = "no-action", IsOwner = true,
            });
        RunDeferred(t);
    }

    /// <summary>
    /// The table of a list of a reference type, with the shape the sql-ddl pack gave the template-defined collection it replaces:
    /// <c>&lt;table&gt;_&lt;attribute&gt;</c>, the owner's key columns prefixed with the owner's name, and <c>&lt;attribute singular&gt;_code</c>,
    /// keyed by all of them, its owner key cascading on delete.
    /// </summary>
    private void CreateReferenceListTable(DesignedTable owner, RAttribute a)
    {
        var reference = a.Type.ReferenceType!;
        var ownerName = ReferenceListOwnerName(owner, a);
        var name = owner.Name + "_" + Casing.Snake(a.Name);
        var t = new DesignedTable
        {
            Key = owner.Entity!.Id + "." + a.Id + "@" + _db.Id,
            Name = name,
            SchemaId = owner.SchemaId,
            Kind = DesignedTableKind.Child,
            Entity = owner.Entity,
            Attribute = a,
            Owner = owner,
            IsList = true,
            IsReferenceList = true,
            PackageId = owner.PackageId,
            PrimaryKeyName = "pk_" + name,
        };
        AddTable(t);
        owner.Children.Add(t);
        var ownerKeys = new List<string>();
        foreach (var pkKey in owner.PrimaryKey)
        {
            if (owner.Resolve(pkKey) is not { } ownerColumn)
                continue;
            AddColumn(t, ownerColumn.Key, Casing.Snake(ownerName) + "_" + ownerColumn.Name, ownerColumn.Type, ownerColumn.Length, ownerColumn.Precision,
                ownerColumn.Scale, false, null, null, null, scalar: ownerColumn.Scalar, follows: Shape(ownerColumn));
            ownerKeys.Add(ownerColumn.Key);
        }

        var code = AddColumn(t, a.Id, _inflector.Singularize(Casing.Snake(a.Name)) + "_code", reference.Code.Type, reference.Code.Length, null, null, false, null, a, a.Id);
        if (reference.Code.Type == "string" && reference.Code.Length is null)
        {
            code.Type = "text";
            code.Length = null;
        }

        code.NativeType = CodeSqlType(reference.Code.Type, reference.Code.Length);

        t.PrimaryKey.AddRange(ownerKeys);
        t.PrimaryKey.Add(a.Id);
        if (ownerKeys.Count > 0)
            t.ForeignKeys.Add(new DesignedForeignKey
            {
                Name = "fk_" + name + "_owner", Columns = ownerKeys, Target = new DesignTarget { Designed = owner }, OnDelete = "cascade", OnUpdate = "no-action",
                IsOwner = true,
            });
    }

    /// <summary>
    /// Whether the sql-ddl pack kept a list of the reference type in a table of its own in this database: the type is bound to a table
    /// there, or its storage strategy keeps lists and is not a PostgreSQL enum type (which keeps them in an array column).
    /// </summary>
    private bool KeepsListsInTables(RReferenceType reference) =>
        reference.Bindings.ContainsKey(_db.Name)
        || (reference.Storage.TryGetValue(_db.Name, out var choice) && choice.Collections
            && !(string.Equals(choice.Strategy, "native", StringComparison.Ordinal) && _dialect == "postgresql"));

    /// <summary>The owner name the sql-ddl pack gave a reference list's key columns: the first entity in model order storing the list in the table.</summary>
    private string ReferenceListOwnerName(DesignedTable owner, RAttribute a)
    {
        foreach (var e in _entityOrder)
        {
            if (ReferenceEquals(e.Table, owner) && e.Entity.Attributes.Any(x => string.Equals(x.Id, a.Id, StringComparison.Ordinal)))
                return e.Entity.Name;
        }

        return owner.Entity!.Name;
    }

    /// <summary>The value column of a list of scalars or enums in its child table.</summary>
    private void AddScalarValue(DesignedTable t, RAttribute a, string path, string name)
    {
        if (a.Type.Enum is { } e)
            AddEnumColumn(t, a, e, path, name, false, null);
        else
            AddColumn(t, path, name, a.Type.Builtin ?? "string", a.Length, a.Precision, a.Scale, false, null, a, path, scalar: a.Type.Scalar);
    }

    private static KeyColumnShape Shape(DesignedColumn c) => new(c.Key, c.Type, c.Length, c.Precision, c.Scale, c.NativeType, c.Scalar);

    /// <summary>
    /// Adds a column (or returns the existing one with that key). A column that stores a custom scalar type takes its native type for the
    /// dialect; a foreign key column passes the referenced column as <paramref name="follows"/> and takes its native type.
    /// </summary>
    private DesignedColumn AddColumn(DesignedTable t, string key, string name, string type, int? length, int? precision, int? scale, bool nullable,
        object? defaultValue, RAttribute? attribute, string? path, RReferenceType? reference = null, RScalarType? scalar = null, KeyColumnShape? follows = null)
    {
        if (t.ByKey.TryGetValue(key, out var existing))
            return existing;
        var c = new DesignedColumn
        {
            Key = key,
            Name = name,
            Type = reference is not null ? reference.Code.Type : type,
            Length = length,
            Precision = precision,
            Scale = scale,
            Nullable = nullable,
            Default = defaultValue,
            Attribute = attribute,
            AttributePath = path,
            ReferenceType = reference,
        };
        ApplyFacetDefaults(c);
        if (scalar is not null && !string.Equals(c.Type, scalar.Base, StringComparison.Ordinal))
            scalar = null;
        c.Scalar = scalar;
        c.NativeType = follows is not null ? follows.NativeType : NativeType(c.Type, c.Length, c.Precision, c.Scale, scalar, c.FixedLength, c.Unicode);
        t.Columns.Add(c);
        t.ByKey[key] = c;
        return c;
    }

    /// <summary>
    /// The SQL the sql-ddl pack wrote for an attribute's named default expression (now, today, new-uuid) in the database's dialect, so
    /// the default moves into the table (retire-projection-design.md section 7); "" when the dialect has none.
    /// </summary>
    private string DefaultExpression(string expression, DesignedColumn c) => expression switch
    {
        "now" => _dialect switch
        {
            "sqlserver" => c.Type == "datetimeoffset" ? "SYSDATETIMEOFFSET()" : "SYSUTCDATETIME()",
            "mysql" when c.Precision is > 0 => "CURRENT_TIMESTAMP(" + c.Precision.Value.ToString(CultureInfo.InvariantCulture) + ")",
            _ => "CURRENT_TIMESTAMP",
        },
        "today" => _dialect switch
        {
            "sqlserver" => "CAST(SYSUTCDATETIME() AS date)",
            "mysql" => "(CURRENT_DATE)",
            _ => "CURRENT_DATE",
        },
        "new-uuid" => _dialect switch
        {
            "postgresql" => "gen_random_uuid()",
            "sqlserver" => "NEWID()",
            "mysql" => "(UUID())",
            "oracle" => "SYS_GUID()",
            _ => "",
        },
        _ => "",
    };

    /// <summary>Fills a column's missing facets with the conventions' defaults; a range column takes none.</summary>
    private void ApplyFacetDefaults(DesignedColumn c)
    {
        switch (c.Type)
        {
            case "string":
                c.Length ??= _conv.DefaultStringLength;
                break;
            case "decimal":
                c.Precision ??= _conv.DecimalPrecision;
                c.Scale ??= _conv.DecimalScale;
                break;
            case "time":
            case "datetime":
            case "datetimeoffset":
                c.Precision ??= _conv.DatetimePrecision;
                break;
        }
    }

    /// <summary>The native type of a logical type with its facets: a custom type's own for the dialect, else the type map's entry.</summary>
    private string NativeType(string type, int? length, int? precision, int? scale, RScalarType? scalar = null, bool fixedLength = false, bool? unicode = null)
    {
        if (BuiltinTypes.IsRange(type))
            return NativeType("text", null, null, null);
        var pattern = scalar is not null && scalar.NativeTypes.TryGetValue(_dialect, out var own) ? own : null;
        return pattern is null
            ? DialectTypeMaps.Render(_typeMap, DialectTypeMaps.VariantKey(_typeMap, type, fixedLength, unicode), length, precision, scale, _conv)
            : DialectTypeMaps.RenderPattern(pattern, type, length, precision, scale, _conv);
    }

    // ---- relations --------------------------------------------------------------------------------------------------------------

    /// <summary>Where the rows of an end's entity live: a requested entity's table, or the table the database already has.</summary>
    private DesignTarget? TargetOf(REntity entity)
    {
        if (_entities.TryGetValue(entity.Id, out var p))
            return p.Table is { } table ? new DesignTarget { Designed = table } : null;
        if (!_outsideCache.TryGetValue(entity.Id, out var target))
        {
            target = _outside(entity.Id);
            _outsideCache[entity.Id] = target;
        }

        return target;
    }

    private bool Placed(REntity entity) => _entities.ContainsKey(entity.Id) || TargetOf(entity) is not null;

    private void DesignRelation(RRelation rel)
    {
        // A relation with an end of several types is stored only as its mapping describes.
        if (_model.Get<Relation>(rel.Id) is not { } src || EndCoverage.HasSeveralTypes(src))
            return;
        if (!rel.Ends.All(e => e.Entity is { } entity && Placed(entity)) || !rel.Ends.Any(e => _entities.ContainsKey(e.Entity!.Id)))
            return;
        var hints = _relationHints(rel.Id);
        if (hints is { Stored: true })
            return;
        // An abstract base stored as a table per concrete entity has no table: a relation that points at it has no binding form yet.
        if (rel.Ends.FirstOrDefault(e => _entities.TryGetValue(e.Entity!.Id, out var p) && p.Shape == DesignHierarchy.PerConcrete && p.Table is null) is { } end)
        {
            _refusal ??= $"Relation '{rel.Name}' has end '{end.Role}' at entity '{end.Entity!.Name}', an abstract base its hierarchy stores as a table per concrete entity, so it has no table to point at, a shape no binding holds yet: store the hierarchy as one table or a table per type, then create the tables again.";
            return;
        }

        switch (ShapeOf(rel, src, hints))
        {
            case RelationShape.ForeignKey:
                DesignForeignKeyShape(rel, src, hints);
                break;
            default:
                // A junction table joins entities whose tables are designed: each end in the request, or already bound.
                if (rel.Ends.All(e => _entities.ContainsKey(e.Entity!.Id) || TargetOf(e.Entity!) is not null))
                    DesignJunction(rel, src);
                break;
        }
    }

    /// <summary>
    /// The relation's shape: a junction table for an n-ary or many-to-many relation; else the mapping's <c>shape</c>, else a junction table
    /// for a relation that carries attributes, else a foreign key.
    /// </summary>
    private static RelationShape ShapeOf(RRelation rel, Relation src, DesignRelationHints? m)
    {
        var nary = rel.Ends.Count != 2 || src.RelationKind == RelationKind.NAry;
        var manyToMany = string.Equals(rel.Cardinality, "many-to-many", StringComparison.Ordinal);
        if (nary)
            return RelationShape.Junction;
        var shape = m?.Shape ?? (rel.Attributes.Count > 0 || manyToMany ? RelationShape.Junction : RelationShape.ForeignKey);
        return shape == RelationShape.ForeignKey && manyToMany ? RelationShape.Junction : shape;
    }

    private static (REnd Principal, REnd Dependent) PrincipalAndDependent(RRelation rel, DesignRelationHints? m)
    {
        var e0 = rel.Ends[0];
        var e1 = rel.Ends[1];
        if (e0.IsMany != e1.IsMany)
            return e0.IsMany ? (e1, e0) : (e0, e1);
        if ((e0.Min == 1) != (e1.Min == 1))
            return e0.Min == 1 ? (e0, e1) : (e1, e0);
        return string.Equals(m?.ForeignKeyEnd, e0.Id, StringComparison.Ordinal) ? (e1, e0) : (e0, e1);
    }

    private static string PrincipalAction(REnd principal, Relation src) => principal.OnDelete switch
    {
        "cascade" => "cascade",
        "restrict" => "restrict",
        "set-null" => "set-null",
        _ => src.RelationKind == RelationKind.Composition ? "cascade" : "no-action",
    };

    /// <summary>The tables of a requested entity's rows that a dependent's foreign key columns go into.</summary>
    private static List<DesignedTable> HostsOf(DesignedEntity p)
    {
        if (p.Shape == DesignHierarchy.PerConcrete)
        {
            var hosts = new List<DesignedTable>();
            Collect(p);
            return hosts;

            void Collect(DesignedEntity x)
            {
                if (x.Table is not null && !hosts.Contains(x.Table))
                    hosts.Add(x.Table);
                foreach (var d in x.Derived)
                    Collect(d);
            }
        }

        return p.Table is null ? [] : [p.Table];
    }

    /// <summary>The root of an entity's hierarchy among the requested entities, else the entity's own topmost base.</summary>
    private REntity RootOf(REntity entity)
    {
        if (_entities.TryGetValue(entity.Id, out var p))
            return p.Root.Entity;
        var root = entity;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (root.Base is { } b && seen.Add(root.Id))
            root = b;
        return root;
    }

    private void DesignForeignKeyShape(RRelation rel, Relation src, DesignRelationHints? m)
    {
        var (principal, dependent) = PrincipalAndDependent(rel, m);
        if (!_entities.TryGetValue(dependent.Entity!.Id, out var dp))
            return; // the foreign key belongs to a table the request does not design
        if (RootOf(principal.Entity!).Key is not { Attributes.Count: > 0 } key)
            return;
        var target = TargetOf(principal.Entity!);
        var targetKeys = KeyShapes(target);
        var onDelete = PrincipalAction(principal, src);
        var oneToOne = string.Equals(rel.Cardinality, "one-to-one", StringComparison.Ordinal);
        var foreign = new List<DesignedForeignKey>();
        var attributeColumns = new List<(string, DesignedTable, DesignedColumn)>();
        foreach (var host in HostsOf(dp))
        {
            var forceNull = principal.Min == 0 || (dp.Shape == DesignHierarchy.OneTable && !ReferenceEquals(dp.Root, dp));
            var keys = AddReferenceColumns(host, principal, rel, key, targetKeys, forceNull);
            if (target is not null && keys.Count > 0)
            {
                var fk = new DesignedForeignKey { Columns = keys, Target = target, OnDelete = onDelete, OnUpdate = "no-action", Relation = rel, End = dependent, Host = host };
                host.ForeignKeys.Add(fk);
                foreign.Add(fk);
            }

            if (oneToOne && keys.Count > 0)
                host.Uniques.Add(new DesignedUnique(keys, null, null));
            foreach (var a in rel.Attributes)
            {
                var before = host.Columns.Count;
                AddAttributeColumns(host, a, false, true, rel.Name);
                foreach (var column in host.Columns.Skip(before))
                    attributeColumns.Add((column.AttributePath ?? column.Key, host, column));
            }

            RunDeferred(host);
        }

        _relations.Add(new DesignedRelation(rel, null, foreign, new Dictionary<string, DesignedForeignKey>(StringComparer.Ordinal), attributeColumns));
    }

    private static IReadOnlyList<KeyColumnShape> KeyShapes(DesignTarget? target) =>
        target?.Designed is { } designed ? [.. designed.PrimaryKey.Select(designed.Resolve).OfType<DesignedColumn>().Select(Shape)]
        : target?.ExistingKeyColumns ?? [];

    /// <summary>The foreign key columns that reference an end's entity key (keys <c>&lt;endId&gt;.&lt;keyAttrId&gt;</c>).</summary>
    private List<string> AddReferenceColumns(DesignedTable host, REnd end, RRelation rel, RKey key, IReadOnlyList<KeyColumnShape> targetKeys, bool nullable)
    {
        var keys = new List<string>();
        for (var i = 0; i < key.Attributes.Count; i++)
        {
            var ka = key.Attributes[i];
            var referenced = i < targetKeys.Count ? targetKeys[i] : null;
            var columnKey = end.Id + "." + ka.Id;
            var name = Render(_conv.ForeignKeyColumn, ("role", end.Role), ("key", ka.Name), ("entity", end.Entity!.Name), ("relation", rel.Name),
                ("table", host.Name));
            AddColumn(host, columnKey, name, referenced?.Type ?? ka.Type.Builtin ?? "string", referenced?.Length ?? ka.Length,
                referenced?.Precision ?? ka.Precision, referenced?.Scale ?? ka.Scale, nullable, null, null, null,
                scalar: referenced is not null ? referenced.Scalar : ka.Type.Scalar, follows: referenced);
            keys.Add(columnKey);
        }

        return keys;
    }

    private void DesignJunction(RRelation rel, Relation src)
    {
        var name = JunctionName(rel);
        var t = new DesignedTable
        {
            Key = rel.Id + "@" + _db.Id,
            Name = name,
            Kind = DesignedTableKind.Junction,
            Relation = rel,
            PackageId = src.Package,
        };
        AddTable(t);
        var surrogate = src.AllowDuplicates || src.Ends.Any(e => e.Ordered);
        if (surrogate)
        {
            var id = AddColumn(t, "id", Render(_conv.KeyColumn, ("attribute", "id"), ("entity", rel.Name), ("table", name)), "int64", null, null, null, false,
                null, null, null);
            if (id.Sequence is null)
                id.Identity = true;
        }

        var allKeys = new List<string>();
        var endKeys = new Dictionary<string, DesignedForeignKey>(StringComparer.Ordinal);
        foreach (var end in rel.Ends)
        {
            if (RootOf(end.Entity!).Key is not { Attributes.Count: > 0 } endKey)
                continue;
            var target = TargetOf(end.Entity!);
            var keys = AddReferenceColumns(t, end, rel, endKey, KeyShapes(target), false);
            allKeys.AddRange(keys);
            if (target is null || keys.Count == 0)
                continue;
            var fk = new DesignedForeignKey
            {
                Columns = keys, Target = target, OnDelete = string.Equals(end.OnDelete, "restrict", StringComparison.Ordinal) ? "restrict" : "cascade",
                OnUpdate = "no-action", Relation = rel, End = end,
            };
            t.ForeignKeys.Add(fk);
            endKeys[end.Id] = fk;
        }

        if (src.Ends.Any(e => e.Ordered))
            AddColumn(t, "position", Render(_conv.OrderColumn), "int32", null, null, null, false, null, null, null);
        var attributeColumns = new List<(string, DesignedTable, DesignedColumn)>();
        foreach (var a in rel.Attributes)
        {
            var before = t.Columns.Count;
            AddAttributeColumns(t, a, false, false, rel.Name);
            foreach (var column in t.Columns.Skip(before))
                attributeColumns.Add((column.AttributePath ?? column.Key, t, column));
        }

        if (t.PrimaryKey.Count == 0)
            t.PrimaryKey.AddRange(surrogate ? ["id"] : allKeys);
        if (surrogate && !src.AllowDuplicates && allKeys.Count > 0)
            t.Uniques.Add(new DesignedUnique(allKeys, null, null));
        RunDeferred(t);
        _relations.Add(new DesignedRelation(rel, t, [], endKeys, attributeColumns));
    }

    /// <summary>The <c>junctionTable</c> pattern; an n-ary relation's <c>{entity2}</c> joins the names of every end after the first.</summary>
    private string JunctionName(RRelation rel) =>
        NamePattern.Render(_conv.JunctionTable, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["entity1"] = rel.Ends[0].Entity!.Name,
            ["entity2"] = string.Join('_', rel.Ends.Skip(1).Select(e => e.Entity!.Name)),
            ["relation"] = rel.Name,
        }, _conv.TableCase);

    // ---- reference columns ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The constraint the database has today for each column holding a reference type's codes, stated by the designed table itself
    /// (a table states its own checks; binding never changes DDL, reference-data-and-seeds.md section 11): a foreign key to the code
    /// column of the table that holds the type's rows (the table a type binding names, or the lookup table the sql-ddl pack made, now
    /// designed and bound), or a CHECK listing today's codes; a PostgreSQL enum type is kept as the column's native type when a
    /// database type of that name exists. Named as the pack names them (<c>fk_&lt;table&gt;_&lt;column&gt;_ref</c>,
    /// <c>ck_&lt;table&gt;_&lt;column&gt;_ref</c>).
    /// </summary>
    private void AddReferenceConstraints()
    {
        foreach (var t in _tables.ToList())
        {
            if (t.Kind == DesignedTableKind.Lookup)
                continue;
            foreach (var c in t.Columns.ToList())
            {
                var reference = c.ReferenceType ?? (t.IsReferenceList && c.Key == t.Attribute?.Id ? t.Attribute.Type.ReferenceType : null);
                if (reference is not null)
                    AddReferenceConstraint(t, c, reference);
            }
        }
    }

    private void AddReferenceConstraint(DesignedTable t, DesignedColumn c, RReferenceType reference)
    {
        var fkName = "fk_" + t.Name + "_" + c.Name + "_ref";
        if (reference.Bindings.TryGetValue(_db.Name, out var binding))
        {
            // The type's rows are in a table of the database: a key into its code column, when that column alone is a key there.
            if (binding.CodeColumn is not { } code || !IsSoleKey(binding.Table, code))
                return;
            t.ForeignKeys.Add(new DesignedForeignKey
            {
                Name = fkName, Columns = [c.Key], OnDelete = "no-action", OnUpdate = "no-action", TargetColumns = [code.Key], IsReference = true,
                Target = new DesignTarget { ExistingKey = binding.Table.Key, ExistingName = binding.Table.Name },
            });
            return;
        }

        var strategy = reference.Storage.TryGetValue(_db.Name, out var choice) ? choice.Strategy : null;
        if (strategy == "native" && _dialect != "postgresql")
            strategy = "check";
        switch (strategy)
        {
            case "lookup-table":
                t.ForeignKeys.Add(new DesignedForeignKey
                {
                    Name = fkName, Columns = [c.Key], OnDelete = "no-action", OnUpdate = "no-action", TargetColumns = ["code"], IsReference = true,
                    Target = new DesignTarget { Designed = LookupOf(reference) },
                });
                break;
            case "check":
                var codes = string.Join(", ", reference.Rows.Select(r => Rendering.SqlDialects.Literal(r.Code, _db.Dialect)));
                t.Checks.Add(new DesignedCheck("ck_" + t.Name + "_" + c.Name + "_ref", c.Key, Rendering.SqlDialects.Quote(c.Name, _db.Dialect, _db.Quoting) + " IN (" + codes + ")"));
                break;
            case "native":
                // The pack's enum type: a database type of that name keeps it as the column's type, else storing would change the column.
                var name = Casing.Snake(reference.Name) + "_t";
                if (_model.All<DatabaseType>().FirstOrDefault(d => d.Database == _db.Id && string.Equals(d.Name, name, StringComparison.Ordinal)) is { } type)
                    c.NativeType = type.Name;
                else
                    _refusal ??= $"Column '{c.Name}' of table '{t.Name}' holds the codes of reference type '{reference.Name}' as the PostgreSQL enum type '{name}' the sql-ddl pack creates, which no database type of database '{_db.Name}' declares: add a database type '{name}' (an enum of the codes), or store the type as a lookup table or a check, then create the tables again.";
                break;
            default:
                _notes.Add($"Column '{c.Name}' of table '{t.Name}' holds the codes of reference type '{reference.Name}', whose storage strategy '{strategy}' a table cannot state: it gets no constraint.");
                break;
        }
    }

    /// <summary>
    /// Whether the designer can state how the sql-ddl pack stores a reference type in this database: a binding to a table, or a storage
    /// strategy of one of the pack's own keys (<c>lookup-table</c>, <c>check</c>, <c>native</c>). Otherwise (no strategy, which the pack fills
    /// from its parameters, or a key of the project's own that the pack maps) materialize never guesses: it refuses, naming the type.
    /// </summary>
    private bool KnownStrategy(RReferenceType reference)
    {
        if (ColumnsOnly || reference.Bindings.ContainsKey(_db.Name))
            return true;
        var strategy = reference.Storage.TryGetValue(_db.Name, out var choice) ? choice.Strategy : null;
        if (strategy is "lookup-table" or "check" or "native")
            return true;
        _refusal ??= $"Reference type '{reference.Name}' has {(strategy is null ? "no storage strategy" : $"the storage strategy '{strategy}'")} in database '{_db.Name}', which materialize cannot read: the sql-ddl pack decides it from its own parameters. Set conventions.referenceStorage (or the type's own storage) to lookup-table, check or native, then create the tables again.";
        return false;
    }

    private static bool IsSoleKey(RTable table, RColumn column) =>
        (table.PrimaryKey is { Columns.Count: 1 } pk && ReferenceEquals(pk.Columns[0], column))
        || table.Uniques.Any(u => u.Columns.Count == 1 && ReferenceEquals(u.Columns[0], column));

    /// <summary>
    /// The lookup table of a reference type, with the shape the sql-ddl pack gives it: <c>code</c>, <c>label</c>, a <c>description</c>
    /// when a row has one, a column per single-valued field, keyed by the code, with a foreign key per reference-typed field. The
    /// materializer binds the type to it, so the type's rows go there.
    /// </summary>
    private DesignedTable LookupOf(RReferenceType reference)
    {
        if (_lookups.TryGetValue(reference.Id, out var known))
            return known;
        var options = reference.Storage.TryGetValue(_db.Name, out var choice) ? choice.Options : null;
        var name = options?.GetValueOrDefault("tableName") as string ?? Casing.Snake(reference.PluralName ?? _inflector.Pluralize(reference.Name));
        var schemaName = options?.GetValueOrDefault("schema") as string;
        var t = new DesignedTable
        {
            Key = "lookup:" + reference.Id + "@" + _db.Id,
            Name = name,
            SchemaId = schemaName is null ? null : _db.Schemas.FirstOrDefault(s => s.Name == schemaName)?.Id,
            SchemaName = schemaName,
            Kind = DesignedTableKind.Lookup,
            ReferenceType = reference,
            PrimaryKeyName = "pk_" + name,
        };
        _lookups[reference.Id] = t;
        AddTable(t);
        CodeColumn(t, "code", "code", reference.Code.Type, reference.Code.Length, false);
        CodeColumn(t, "label", "label", "string", reference.Label.Length, false);
        if (reference.Rows.Any(r => r.Description is not null))
            CodeColumn(t, "description", "description", "string", null, true);
        t.PrimaryKey.Add("code");
        foreach (var f in reference.Attributes.Where(f => !f.Collection))
        {
            var column = Casing.Snake(f.Name);
            if (f.Type.ReferenceType is { } target)
            {
                CodeColumn(t, f.Id, column, target.Code.Type, target.Code.Length, !f.Required);
                AddReferenceConstraint(t, t.Resolve(f.Id)!, target);
            }
            else
            {
                AddColumn(t, f.Id, column, f.Type.Builtin ?? "string", f.Length, f.Precision, f.Scale, !f.Required, null, null, null, scalar: f.Type.Scalar);
            }

            t.LookupFields.Add((f.Id, f.Id));
        }

        return t;
    }

    /// <summary>A column of a lookup table holding a code or a label: a string without a length is <c>text</c>, as the pack writes it.</summary>
    private void CodeColumn(DesignedTable t, string key, string name, string type, int? length, bool nullable)
    {
        var c = AddColumn(t, key, name, type == "string" && length is null ? "text" : type, length, null, null, nullable, null, null, null);
        c.NativeType = CodeSqlType(type, length);
        if (key is "code" or "label" or "description")
            t.LookupFields.Add((key, key));
    }

    /// <summary>
    /// The SQL type the sql-ddl pack gives a code or a label (its <c>ref_sql_type</c>): on Oracle the type map's, as the columns holding
    /// the codes have it (<c>varchar2(n char)</c>, <c>clob</c> without a length, <c>number(5)</c>, <c>number(10)</c>), so the foreign keys
    /// from them join columns of one type (MQ4005).
    /// </summary>
    private string CodeSqlType(string type, int? length) => type switch
    {
        "int16" when _dialect == "oracle" => "number(5)",
        "int32" when _dialect == "oracle" => "number(10)",
        not ("uuid" or "int64") when _dialect == "oracle" => length is { } o ? "varchar2(" + o.ToString(CultureInfo.InvariantCulture) + " char)" : "clob",
        "uuid" => _dialect switch { "postgresql" => "uuid", "sqlserver" => "uniqueidentifier", "sqlite" => "TEXT", _ => "char(36)" },
        "int16" => _dialect == "sqlite" ? "INTEGER" : "smallint",
        "int32" => _dialect == "sqlite" ? "INTEGER" : _dialect == "sqlserver" ? "int" : "integer",
        "int64" => _dialect == "sqlite" ? "INTEGER" : _dialect == "oracle" ? "NUMBER(19)" : "bigint",
        _ => _dialect == "sqlite" ? "TEXT"
            : _dialect == "sqlserver" ? (length is { } n ? "nvarchar(" + n.ToString(CultureInfo.InvariantCulture) + ")" : "nvarchar(max)")
            : length is { } m ? "varchar(" + m.ToString(CultureInfo.InvariantCulture) + ")" : "text",
    };

    // ---- finishing --------------------------------------------------------------------------------------------------------------

    private void Finish()
    {
        foreach (var t in _tables)
        {
            FillComments(t);
            // What the sql-ddl pack read from the attribute moves into the column: a named default expression becomes its SQL default.
            foreach (var c in t.Columns)
            {
                if (c.Default is null && c.DefaultSql is null && c.Sequence is null && c.Attribute?.DefaultExpression is { } expression
                    && DefaultExpression(expression, c) is { Length: > 0 } sql)
                {
                    c.DefaultSql = sql;
                    c.DefaultFromExpression = true;
                }
            }

            var pk = t.PrimaryKey.Select(t.Resolve).OfType<DesignedColumn>().Distinct().ToList();
            if (pk.Count == 0)
                continue;
            foreach (var column in pk)
            {
                column.IsPrimaryKey = true;
                column.Nullable = false;
            }

            t.ResolvedPrimaryKey = (t.PrimaryKeyName ?? Render(_conv.PrimaryKeyName, ("table", t.Name)), pk);
        }

        foreach (var t in _tables)
        {
            foreach (var fk in t.ForeignKeys)
            {
                var columns = fk.Columns.Select(t.Resolve).ToList();
                if (fk.Target is null || columns.Count == 0 || columns.Any(c => c is null))
                    continue;
                List<string> referenced;
                if (fk.Target.Designed is { } target)
                {
                    var keys = fk.TargetColumns.Count > 0 ? fk.TargetColumns.Select(target.Resolve).ToList()
                        : target.ResolvedPrimaryKey is { } targetPk ? [.. targetPk.Columns] : [];
                    if (keys.Any(c => c is null))
                        continue;
                    referenced = [.. keys.Select(c => c!.Key)];
                }
                else
                {
                    referenced = fk.TargetColumns.Count > 0 ? [.. fk.TargetColumns] : [.. fk.Target.ExistingKeyColumns.Select(k => k.Key)];
                }

                if (referenced.Count != columns.Count)
                    continue;
                fk.ResolvedColumns.AddRange(columns!);
                fk.ReferencedKeys.AddRange(referenced);
                fk.Name ??= Render(_conv.ForeignKeyName, ("table", t.Name), ("columns", Joined(fk.ResolvedColumns)));
                fk.Resolved = true;
            }

            foreach (var u in t.Uniques)
            {
                var columns = u.Columns.Select(t.Resolve).ToList();
                if (columns.Count == 0 || columns.Any(c => c is null))
                    continue;
                var name = u.Name ?? (u.NameToken is null
                    ? Render(_conv.UniqueName, ("table", t.Name), ("columns", Joined(columns!)))
                    : Render(_conv.UniqueName, ("table", t.Name), ("columns", Joined(columns!)), ("name", u.NameToken)));
                t.ResolvedUniques.Add((u.Id, name, [.. columns.OfType<DesignedColumn>()], u.NullsNotDistinct));
            }

            var indexes = t.Indexes.Select(i => (Spec: i, Columns: i.Columns.Select(c => (Column: c.Column is null ? null : t.Resolve(c.Column), Spec: c)).ToList(),
                    Include: i.Include.Select(t.Resolve).ToList()))
                .Where(x => x.Columns.Count > 0 && x.Columns.All(c => c.Column is not null || c.Spec.Expression is not null) && x.Include.All(c => c is not null))
                .ToList();
            var fileColumnSets = indexes.Where(x => x.Spec.FromFile && x.Columns.All(c => c.Column is not null))
                .Select(x => Joined(x.Columns.Select(c => c.Column!))).ToHashSet(StringComparer.Ordinal);
            indexes.RemoveAll(x => !x.Spec.FromFile && fileColumnSets.Contains(Joined(x.Columns.Select(c => c.Column!))));
            foreach (var x in indexes)
            {
                var name = x.Spec.Name ?? Render(_conv.IndexName, ("table", t.Name), ("columns", string.Join('_', x.Columns.Select(c => c.Column?.Name ?? "expr"))));
                t.ResolvedIndexes.Add((x.Spec, name, x.Columns, [.. x.Include.OfType<DesignedColumn>()]));
            }
        }
    }

    private static string Joined(IEnumerable<DesignedColumn> columns) => string.Join('_', columns.Select(c => c.Name));

    /// <summary>
    /// The <c>comments</c> convention: with <c>descriptions</c>, a table takes the description of what it stores, a column its attribute's.
    /// </summary>
    private void FillComments(DesignedTable t)
    {
        if (_conv.Comments != CommentSource.Descriptions)
            return;
        t.Comment ??= t.Attribute is { } attribute && !t.IsReferenceList ? Text(OwnDescription(attribute))
            : t.IsReferenceList ? null : Text(t.Entity?.Description) ?? Text(t.Relation?.Description);
        foreach (var column in t.Columns)
            column.Comment ??= t.IsReferenceList ? null : Text(OwnDescription(column.Attribute));

        static string? Text(string? description) => string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    /// <summary>An attribute's own description: not the comment of a column a binding maps it to, which it shows when it has none.</summary>
    private static string? OwnDescription(RAttribute? attribute) => attribute is { DescriptionColumn: null } ? attribute.Description : null;

    // ---- names ------------------------------------------------------------------------------------------------------------------

    /// <summary>The table name of an entity by the <c>tableName</c> pattern (an explicit plural name wins).</summary>
    private string TableNameFor(string entityName, string? pluralName = null) =>
        NamePattern.Render(_conv.TableName, new Dictionary<string, string>(StringComparer.Ordinal) { ["entity"] = entityName, ["name"] = entityName },
            _conv.TableCase, _conv.PluralTables ? _inflector : null, pluralName);

    private string? ExplicitPlural(string entityId) => _model.Get<Entity>(entityId)?.PluralName;

    private string ColumnName(string attributeName) => Casing.Apply(attributeName, _conv.ColumnCase);

    private string Render(string pattern, params (string Token, string Value)[] tokens)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (token, value) in tokens)
            map[token] = value;
        return NamePattern.Render(pattern, map, _conv.ColumnCase);
    }
}
