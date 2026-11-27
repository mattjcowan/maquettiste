using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Text;

namespace Maquettiste.Engine.Loading;

/// <summary>The hierarchies and lists a <c>materialize-tables</c> plan asked about, with what it used and the default.</summary>
/// <param name="Hierarchies">One per hierarchy in the request.</param>
/// <param name="Lists">One per list attribute of the requested entities.</param>
internal sealed record MaterializeChoiceSet(IReadOnlyList<MaterializeHierarchyChoice> Hierarchies, IReadOnlyList<MaterializeListChoice> Lists);

/// <summary><c>materialize-tables</c> (retire-projection-design.md section 7): designed tables proposed from entities, and the bindings to them.</summary>
internal sealed partial class Materializer
{
    /// <summary>The hierarchy shapes as the request and the API name them.</summary>
    internal static readonly IReadOnlyDictionary<string, DesignHierarchy> HierarchyNames = new Dictionary<string, DesignHierarchy>(StringComparer.Ordinal)
    {
        ["one-table"] = DesignHierarchy.OneTable,
        ["per-concrete"] = DesignHierarchy.PerConcrete,
        ["per-type"] = DesignHierarchy.PerType,
    };

    private static string NameOf(DesignHierarchy shape) => shape switch
    {
        DesignHierarchy.PerConcrete => "per-concrete",
        DesignHierarchy.PerType => "per-type",
        _ => "one-table",
    };

    private MaterializeChoiceSet? _choices;
    private ILookup<string, Entity>? _derived;

    /// <summary>
    /// Plans <c>materialize-tables</c>: for the requested entities, the tables a designer proposes with the shape the projection gives
    /// them today (a hierarchy as one table, a table per concrete entity or a table per type; a list or a single value object stored as a
    /// table in a child table; a many-to-many relation in a junction table), each a designed table, and a binding per entity to them
    /// (type column and codes, base foreign key, child tables), a relation mapping per relation naming its foreign key or junction
    /// table, the overlays folded in and the entity mappings deleted. Every table records its projected key in the snapshot's aliases,
    /// so the next migration sees no change.
    /// </summary>
    /// <param name="databaseId">The database.</param>
    /// <param name="entityIds">The entities.</param>
    /// <param name="schemaId">The schema of the database the tables go to, or <see langword="null"/> for each table's own.</param>
    /// <param name="hierarchy">The shape per hierarchy root id (<c>*</c> for every hierarchy), or <see langword="null"/> for the defaults.</param>
    /// <param name="listStorage">The storage per list attribute id (<c>*</c> for every list), or <see langword="null"/> for the defaults.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan.</returns>
    public async Task<MaterializeResult> TablesAsync(string databaseId, IReadOnlyList<string> entityIds, string? schemaId,
        IReadOnlyDictionary<string, string>? hierarchy, IReadOnlyDictionary<string, string>? listStorage, CancellationToken ct)
    {
        if (snapshot.Get<Database>(databaseId) is not { } db)
            return Refused($"'{databaseId}' is not a database.");
        if (schemaId is not null && !db.Schemas.Any(s => s.Id == schemaId))
            return Refused($"Database '{db.Name}' has no schema '{schemaId}'.");
        foreach (var (key, value) in hierarchy ?? new Dictionary<string, string>())
        {
            if (!HierarchyNames.ContainsKey(value))
                return Refused($"Hierarchy shape '{value}' (for '{key}') is not one-table, per-concrete or per-type.");
        }

        foreach (var (key, value) in listStorage ?? new Dictionary<string, string>())
        {
            if (value is not ("table" or "json"))
                return Refused($"List storage '{value}' (for '{key}') is not table or json.");
        }

        var mappings = snapshot.All<Mapping>().Where(m => m.Database == db.Id).ToList();
        var entities = new List<Entity>();
        var requested = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in entityIds.Distinct(StringComparer.Ordinal))
        {
            if (snapshot.Get<Entity>(id) is not { } entity)
                return Refused($"'{id}' is not an entity.");
            if (entity.Bindings.FirstOrDefault(b => b.Database == db.Id) is { } existing)
                return Refused($"Entity '{entity.Name}' is already bound to database '{db.Name}' (binding {existing.Id}): it reads from what its binding names; edit the binding instead of materializing the entity again.");
            if (mappings.FirstOrDefault(m => m.Entity == entity.Id && m.Table is not null) is { } mapped)
                return Refused($"Entity '{entity.Name}' is mapped to table '{mapped.Table}' of database '{db.Name}' by mapping '{mapped.Name}', so it has no table of its own: bind it to that table instead.");
            entities.Add(entity);
            requested.Add(entity.Id);
        }

        if (entities.Count == 0)
            return Refused("Name at least one entity to materialize.");
        // A hierarchy is stored whole: every entity of it that has no binding to the database is in the request.
        foreach (var entity in entities)
        {
            foreach (var member in HierarchyOf(entity))
            {
                if (requested.Contains(member.Id))
                    continue;
                if (member.Bindings.Any(b => b.Database == db.Id))
                    return Refused($"Entity '{entity.Name}' is in a hierarchy with entity '{member.Name}', which is already bound to database '{db.Name}': create the tables of a hierarchy together, or bind its entities by hand.");
                return Refused($"Entity '{entity.Name}' is in a hierarchy with entity '{member.Name}': name every entity of the hierarchy, so its tables are made together.");
            }
        }

        _databaseName = db.Name;
        var resolved = await resolver.ResolveAsync(snapshot, null, ct).ConfigureAwait(false);
        var rdb = resolved.Databases.First(d => d.Id == db.Id);

        // Overlays (read as hints, folded into the designed tables), by what they adjust; an entity's checks held as text become trees.
        var overlays = new Dictionary<string, Table>(StringComparer.Ordinal);
        foreach (var table in snapshot.All<Table>().Where(t => t.Database == db.Id && t.Origin == TableOrigin.Synthesized).OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            if (OverlayTarget(table) is { } target)
                overlays.TryAdd(target, table);
        }

        foreach (var entity in entities)
        {
            if (!overlays.TryGetValue("entity:" + entity.Id, out var overlay))
                continue;
            var (withTrees, unparsed) = WithTrees(entity, overlay);
            if (unparsed is not null)
                return unparsed;
            overlays["entity:" + entity.Id] = withTrees;
        }

        var conventions = EffectiveConventions.For(snapshot.Settings, db.Name);
        var entityHints = new Dictionary<string, DesignEntityHints>(StringComparer.Ordinal);
        var relationHints = new Dictionary<string, DesignRelationHints>(StringComparer.Ordinal);
        foreach (var m in mappings)
        {
            var attributes = m.Attributes.GroupBy(a => a.Attribute, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => new DesignAttributeHint(g.First().Storage, g.First().Prefix, g.First().Ignore), StringComparer.Ordinal);
            if (m.Entity is { } entityId)
                entityHints.TryAdd(entityId, new DesignEntityHints(m.Schema, Shape(m.Inheritance), ResolutionValues.Plain(m.DiscriminatorValue), attributes));
            else if (m.Relation is { } relationId)
                relationHints.TryAdd(relationId, new DesignRelationHints(m.Shape, m.ForeignKeyEnd, m.Ignore, m.JunctionTable is not null || m.ForeignKey is not null, attributes));
        }

        DesignHierarchy ShapeOf(string rootId) =>
            hierarchy?.GetValueOrDefault(rootId) is { } asked ? HierarchyNames[asked]
            : hierarchy?.GetValueOrDefault("*") is { } all ? HierarchyNames[all]
            : DefaultShape(rootId);
        DesignHierarchy DefaultShape(string rootId) => entityHints.GetValueOrDefault(rootId)?.Hierarchy ?? Shape(conventions.Inheritance) ?? DesignHierarchy.OneTable;
        StorageKind? ListStorageOf(string attributeId) =>
            (listStorage?.GetValueOrDefault(attributeId) ?? listStorage?.GetValueOrDefault("*")) switch
            {
                "json" => StorageKind.Json,
                "table" => StorageKind.Table,
                _ => null,
            };

        var designer = new TableDesigner(snapshot, resolved, db, id => entityHints.GetValueOrDefault(id), id => relationHints.GetValueOrDefault(id),
            target => overlays.GetValueOrDefault(target), ShapeOf, ListStorageOf, id => Outside(db, rdb, mappings, id));
        var proposal = designer.Design(requested);
        // The choices come back with a refusal too, so a tool can offer another shape or storage.
        if (Choices(proposal, entityHints, designer, ShapeOf, DefaultShape, hierarchy, listStorage, resolved.Relations) is { } refusal)
            return refusal;
        if (proposal.Refusal is { } shapeRefusal)
            return Refused(shapeRefusal) with { Choices = _choices };

        // A lookup table's schema option names a schema the database may not declare: the change declares it, as the pack created it.
        foreach (var t in proposal.Tables.Where(t => t.SchemaId is null && t.SchemaName is not null))
            t.SchemaId = SchemaFor(db, t.SchemaName!);

        // Ids: an overlay's table keeps its id, a table stored before (and undone) takes back the ids its alias recorded.
        var tableIds = new Dictionary<DesignedTable, string>(ReferenceEqualityComparer.Instance);
        var columnIds = new Dictionary<DesignedTable, Dictionary<string, string>>(ReferenceEqualityComparer.Instance);
        foreach (var t in proposal.Tables)
        {
            var tableId = t.Overlay?.Id ?? Reused(t.Key, "table") ?? ids.NewId();
            var known = AliasOf(t.Key, "table") is { } alias && alias.Alias == tableId ? alias.Columns : null;
            var columns = new Dictionary<string, string>(StringComparer.Ordinal);
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var column in t.Columns)
            {
                var id = t.Overlay?.Columns.FirstOrDefault(c => c.Attribute == column.Key || (c.Attribute is null && c.Id == column.Key))?.Id;
                if (id is null && known?.GetValueOrDefault(column.Key) is { } was && !taken.Contains(was) && t.Overlay?.Columns.Any(c => c.Id == was) != true)
                    id = was;
                id ??= ids.NewId();
                taken.Add(id);
                columns[column.Key] = id;
            }

            tableIds[t] = tableId;
            columnIds[t] = columns;
            // A table the pack created in seed.sql: every lookup table of the database, and a list's table when its owner was projected.
            if (t.Kind == DesignedTableKind.Lookup || (t.IsReferenceList && t.Owner is { } owner && rdb.Tables.Any(x => x.Key == owner.Key)))
                _adopted.Add(new MigrationRekey { Kind = "table", From = tableId, To = tableId, Name = t.Name });
            if (t.Overlay is { } overlay && t.Kind == DesignedTableKind.Entity && UnstoredRead(snapshot.Get<Entity>(t.Entity!.Id)!, t, overlay, columns) is { } unstored)
                return Refused(unstored);
        }

        // The columns the projected table inherited from its stereotypes stay inherited; their keyed ids move with the table key.
        var inherited = new Dictionary<DesignedTable, List<(string From, string To, Column Member)>>(ReferenceEqualityComparer.Instance);
        var remaps = new Dictionary<DesignedTable, Dictionary<string, string>>(ReferenceEqualityComparer.Instance);
        foreach (var t in proposal.Tables)
        {
            inherited[t] = [.. InheritedColumns(db, t, tableIds[t])];
            var remap = new Dictionary<string, string>(columnIds[t], StringComparer.Ordinal);
            foreach (var (from, to, _) in inherited[t])
                remap[from] = to;
            remaps[t] = remap;
            _rekeys.Add(new SchemaDiff.TableRekey(t.Key, tableIds[t], remap));
        }

        var fkIds = new Dictionary<DesignedForeignKey, string>(ReferenceEqualityComparer.Instance);
        var nodes = new Dictionary<DesignedTable, JsonObject>(ReferenceEqualityComparer.Instance);
        var typeMap = DialectTypeMaps.Effective(db.Dialect, snapshot.Settings);
        foreach (var t in proposal.Tables)
            DropIndexesInheritedOnes(db, t);
        var checkIds = new Dictionary<DesignedCheck, string>(ReferenceEqualityComparer.Instance);
        foreach (var t in proposal.Tables)
            nodes[t] = TableNode(db, schemaId, t, tableIds, columnIds, remaps[t], fkIds, conventions, typeMap, checkIds);

        // What the pack wrote for a projected table outside the snapshot, now declared by the table: adopted by the write's rekey fact, so
        // the next schema diff reads it as there already (once), and an undo, which removes the fact, removes the adoption.
        foreach (var t in proposal.Tables.Where(t => t.Kind != DesignedTableKind.Lookup && rdb.Tables.Any(x => x.Key == t.Key)))
        {
            var tableId = tableIds[t];
            foreach (var fk in t.ForeignKeys.Where(f => f.IsReference && fkIds.ContainsKey(f)))
                _adopted.Add(new MigrationRekey { Kind = "foreign-key", Table = tableId, From = fkIds[fk], To = fkIds[fk], Name = fk.Name });
            foreach (var check in t.Checks.Where(checkIds.ContainsKey))
                _adopted.Add(new MigrationRekey { Kind = "check", Table = tableId, From = checkIds[check], To = checkIds[check], Name = check.Name });
            foreach (var column in t.Columns.Where(c => c.DefaultFromExpression))
                _adopted.Add(new MigrationRekey { Kind = "column", Table = tableId, From = columnIds[t][column.Key], To = columnIds[t][column.Key], Name = column.Name });
        }

        // Writes: per entity (by name), its table and child tables, then the entity with its binding; then the junction tables.
        var written = new HashSet<DesignedTable>(ReferenceEqualityComparer.Instance);
        void WriteTable(DesignedTable t)
        {
            if (!written.Add(t))
                return;
            var what = t.Kind switch
            {
                DesignedTableKind.Child => $"the table of attribute {t.Attribute!.Name} of {t.Entity?.Name ?? t.Relation?.Name}",
                DesignedTableKind.Junction => $"the junction table of relation {t.Relation!.Name}",
                DesignedTableKind.Lookup => $"the lookup table of reference type {t.ReferenceType!.Name}",
                _ => $"the table of entity {t.Entity!.Name}",
            };
            if (t.Overlay is { } overlay)
                Put(overlay.Id, snapshot.GetDocument(overlay.Id)!.Hash, nodes[t], BatchOp.Update, what + ", with its overlay folded in, becomes a designed table");
            else
                Put(tableIds[t], null, nodes[t], BatchOp.Create, what + " as a designed table");
            foreach (var child in t.Children)
                WriteTable(child);
        }

        foreach (var e in proposal.Entities.OrderBy(e => e.Entity.Name, StringComparer.Ordinal).ThenBy(e => e.Entity.Id, StringComparer.Ordinal))
        {
            var entity = snapshot.Get<Entity>(e.Entity.Id)!;
            if (e.Table is { } table)
            {
                WriteTable(table);
                var entityNode = Node(entity.Id, BatchOp.Update, $"binds entity {entity.Name} to table {table.Name}");
                var bindings = entityNode["bindings"] as JsonArray ?? [];
                entityNode["bindings"] = bindings;
                var binding = BindingFor(db, e, table, tableIds, columnIds, fkIds, resolved, entityHints.GetValueOrDefault(entity.Id), inherited[table]);
                // The binding records every column of the table it creates, hashed from the entries this operation writes (decision 4).
                if (ShapesOf(nodes[table]) is { } shapes)
                    RecordProvenance(binding, shapes, written: null);
                bindings.Add(binding);
            }
            else
            {
                _notes.Add($"Entity '{entity.Name}' is abstract and its hierarchy has a table per concrete entity: it gets no table and no binding in database '{db.Name}'.");
            }

            foreach (var mapping in mappings.Where(m => m.Entity == entity.Id))
                Put(mapping.Id, snapshot.GetDocument(mapping.Id)!.Hash, null, BatchOp.Delete, $"entity {entity.Name} is bound now, so its mapping to database {db.Name} is no longer used");
        }

        foreach (var t in proposal.Tables)
            WriteTable(t);
        foreach (var lookup in proposal.Tables.Where(t => t.Kind == DesignedTableKind.Lookup))
            BindReferenceType(db, lookup, tableIds[lookup], columnIds[lookup]);
        foreach (var relation in proposal.Relations)
            WriteRelationMapping(db, relation, mappings, tableIds, columnIds, fkIds);
        foreach (var note in proposal.Notes)
            _notes.Add(note);

        var byKey = new Dictionary<string, (string Id, Dictionary<string, string> Columns)>(StringComparer.Ordinal);
        var byOverlay = new Dictionary<string, (string Id, Dictionary<string, string> Columns)>(StringComparer.Ordinal);
        foreach (var t in proposal.Tables)
        {
            byKey[t.Key] = (tableIds[t], columnIds[t]);
            if (t.Overlay is { } overlay)
                byOverlay[overlay.Id] = (tableIds[t], columnIds[t]);
        }

        RewriteReferences(db, rdb, byKey, byOverlay);
        return Result();
    }

    private readonly Dictionary<string, string> _addedSchemas = new(StringComparer.Ordinal);

    /// <summary>The id of a schema of the database by name, declared by the change (and adopted: the pack created it) when it is missing.</summary>
    private string SchemaFor(Database db, string name)
    {
        if (db.Schemas.FirstOrDefault(s => s.Name == name) is { } schema)
            return schema.Id;
        if (_addedSchemas.TryGetValue(name, out var added))
            return added;
        var id = ids.NewId();
        _addedSchemas[name] = id;
        var node = Node(db.Id, BatchOp.Update, $"declares schema {name}, which a lookup table's storage names");
        var schemas = node["schemas"] as JsonArray ?? [];
        node["schemas"] = schemas;
        schemas.Add(new JsonObject { ["id"] = id, ["name"] = name });
        _adopted.Add(new MigrationRekey { Kind = "schema", From = name, To = name, Name = name });
        return id;
    }

    /// <summary>Every other entity of an entity's inheritance hierarchy (its bases, and every entity derived from any of them).</summary>
    private IEnumerable<Entity> HierarchyOf(Entity entity)
    {
        var root = entity;
        var seen = new HashSet<string>(StringComparer.Ordinal) { entity.Id };
        while (root.Base is { } baseId && snapshot.Get<Entity>(baseId) is { } b && seen.Add(b.Id))
            root = b;
        var derived = _derived ??= snapshot.All<Entity>().Where(e => e.Base is not null).ToLookup(e => e.Base!, StringComparer.Ordinal);
        var stack = new Stack<Entity>([root]);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (!visited.Add(e.Id))
                continue;
            if (e.Id != entity.Id)
                yield return e;
            foreach (var d in derived[e.Id].OrderBy(x => x.Id, StringComparer.Ordinal))
                stack.Push(d);
        }
    }

    private static DesignHierarchy? Shape(InheritanceStrategy? strategy) => strategy switch
    {
        InheritanceStrategy.Tph => DesignHierarchy.OneTable,
        InheritanceStrategy.Tpc => DesignHierarchy.PerConcrete,
        InheritanceStrategy.Tpt => DesignHierarchy.PerType,
        _ => null,
    };

    private static string? OverlayTarget(Table table) =>
        table.Entity is { } entity ? (table.Attribute is { } attribute ? $"child:{entity}.{attribute}" : $"entity:{entity}")
        : table.Relation is { } relation ? $"relation:{relation}"
        : null;

    /// <summary>
    /// The table the database already has for an entity outside the request: the table its binding writes, the designed table an older
    /// mapping names, or the table it (or the base whose table holds its rows) is still projected into.
    /// </summary>
    private DesignTarget? Outside(Database db, RDatabase rdb, List<Mapping> mappings, string entityId)
    {
        if (snapshot.Get<Entity>(entityId) is not { } entity)
            return null;
        if (entity.Bindings.FirstOrDefault(b => b.Database == db.Id) is { } binding)
            return binding.Write is { None: true } ? null : Existing(rdb, binding.Write?.Table ?? binding.Source, false, entity.Name);
        if (mappings.FirstOrDefault(m => m.Entity == entityId && m.Table is not null) is { Table: { } mappedTable })
            return Existing(rdb, mappedTable, false, entity.Name);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var e = entity; e is not null && seen.Add(e.Id); e = e.Base is { } b ? snapshot.Get<Entity>(b) : null)
        {
            if (Existing(rdb, e.Id + "@" + db.Id, true, e.Name) is { } projected)
                return projected;
        }

        return null;
    }

    private static DesignTarget? Existing(RDatabase rdb, string key, bool projected, string entityName) =>
        rdb.Tables.FirstOrDefault(t => t.Key == key) is { } table
            ? new DesignTarget
            {
                ExistingKey = table.Key, ExistingName = table.Name, ExistingProjected = projected, ExistingEntityName = entityName,
                ExistingKeyColumns = [.. (table.PrimaryKey?.Columns ?? []).Select(c => new KeyColumnShape(c.Key, c.Type == "reference" ? c.CodeType ?? "string" : c.Type,
                    c.Length, c.Precision, c.Scale, c.NativeType, null))],
            }
            : null;

    /// <summary>
    /// The hierarchies and lists the request asks about, with the shape and storage the plan uses and the defaults; a key of the
    /// request's <c>hierarchy</c> that names no hierarchy root of the request, or of its <c>listStorage</c> that names no list
    /// attribute of a requested entity, refuses the operation.
    /// </summary>
    private MaterializeResult? Choices(TableProposal proposal, Dictionary<string, DesignEntityHints> hints, TableDesigner designer,
        Func<string, DesignHierarchy> shapeOf, Func<string, DesignHierarchy> defaultShape, IReadOnlyDictionary<string, string>? hierarchy,
        IReadOnlyDictionary<string, string>? listStorage, IReadOnlyList<RRelation> resolvedRelations)
    {
        var hierarchies = new List<MaterializeHierarchyChoice>();
        foreach (var root in proposal.Entities.Where(e => e.Shape is not null && ReferenceEquals(e.Root, e)))
        {
            var members = proposal.Entities.Where(e => ReferenceEquals(e.Root, root)).OrderBy(e => ReferenceEquals(e, root) ? 0 : 1).Select(e => e.Entity.Name).ToList();
            hierarchies.Add(new MaterializeHierarchyChoice(root.Entity.Id, root.Entity.Name, members, NameOf(shapeOf(root.Entity.Id)), NameOf(defaultShape(root.Entity.Id))));
        }

        var lists = new List<MaterializeListChoice>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in proposal.Entities)
        {
            foreach (var a in e.Entity.Attributes)
            {
                if (!a.Collection || (a.Type.ValueObject is null && a.Type.Builtin is null && a.Type.Enum is null && a.Type.ReferenceType is null && a.Type.Scalar is null))
                    continue;
                var hint = hints.GetValueOrDefault(e.Entity.Id)?.Attributes.GetValueOrDefault(a.Id)
                    ?? (a.DeclaringEntity is { } d ? hints.GetValueOrDefault(d.Id)?.Attributes.GetValueOrDefault(a.Id) : null);
                if (hint is { Ignore: true } || (a.Derived is { Stored: false } && hint is null) || !seen.Add(a.Id))
                    continue;
                var fallback = designer.DefaultListStorage(hint, a.Type.ReferenceType is not null) == StorageKind.Json ? "json" : "table";
                var asked = listStorage?.GetValueOrDefault(a.Id) ?? listStorage?.GetValueOrDefault("*");
                var owner = a.DeclaringEntity is { } declaring && proposal.Entities.Any(x => x.Entity.Id == declaring.Id) ? declaring : e.Entity;
                lists.Add(new MaterializeListChoice(owner.Id, owner.Name, a.Id, a.Name, asked ?? fallback, fallback));
            }
        }

        // A relation's list attributes, for a relation with an end in the request, can be stored as JSON too.
        foreach (var relation in resolvedRelations.Where(r => r.Ends.Any(e => e.Entity is { } x && proposal.Entities.Any(p => p.Entity.Id == x.Id))))
        {
            foreach (var a in relation.Attributes.Where(a => a.Collection && seen.Add(a.Id)))
            {
                var asked = listStorage?.GetValueOrDefault(a.Id) ?? listStorage?.GetValueOrDefault("*");
                var fallback = designer.DefaultListStorage(null, a.Type.ReferenceType is not null) == StorageKind.Json ? "json" : "table";
                lists.Add(new MaterializeListChoice(relation.Id, relation.Name, a.Id, a.Name, asked ?? fallback, fallback));
            }
        }

        foreach (var key in hierarchy?.Keys ?? [])
        {
            if (key != "*" && !hierarchies.Any(h => h.Root == key))
                return Refused($"'{key}' in hierarchy is not the root entity of a hierarchy in the request.");
        }

        foreach (var key in listStorage?.Keys ?? [])
        {
            if (key != "*" && !lists.Any(l => l.Attribute == key))
                return Refused($"'{key}' in listStorage is not a list attribute of an entity in the request.");
        }

        _choices = new MaterializeChoiceSet(hierarchies, lists);
        return null;
    }
}
