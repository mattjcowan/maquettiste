using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Text;

namespace Maquettiste.Engine.Loading;

/// <summary>One change a materialize operation plans.</summary>
/// <param name="Op">Create, update or delete.</param>
/// <param name="Id">The element id.</param>
/// <param name="Hash">The file hash the change expects (update and delete).</param>
/// <param name="Node">The element as it will be written (create and update).</param>
/// <param name="Kind">The kind name.</param>
/// <param name="Name">A readable name.</param>
/// <param name="Because">Why, in words.</param>
internal sealed record MaterializeOp(BatchOp Op, string Id, string? Hash, JsonObject? Node, string Kind, string Name, string Because);

/// <summary>What a materialize operation plans, or why it is refused.</summary>
/// <param name="Refusal">Why the operation is refused (nothing is planned), or <see langword="null"/>.</param>
/// <param name="Ops">The changes, in the order they are applied.</param>
/// <param name="Notes">What the operation leaves as it is, in words.</param>
/// <param name="Database">The database's name, for the snapshot's aliases.</param>
/// <param name="Rekeys">The projected tables that became designed tables, with their keys before and after (the snapshot's aliases).</param>
/// <param name="Sequences">The key sequences that became sequence files, by their synthesized key.</param>
internal sealed record MaterializeResult(string? Refusal, IReadOnlyList<MaterializeOp> Ops, IReadOnlyList<string> Notes, string? Database = null,
    IReadOnlyList<SchemaDiff.TableRekey>? Rekeys = null, IReadOnlyDictionary<string, string>? Sequences = null);

/// <summary>
/// The two materialize operations (erratum E43, engine-design.md section 7, "Bindings and materialize"): <c>materialize-tables</c>
/// writes, for each entity, a designed table with exactly the shape its projection has today and binds the entity to it (folding the
/// entity's table overlay into the table and deleting its mapping element); <c>materialize-entities</c> writes, for each designed or
/// imported table or view, an entity with one attribute per column, its key from the primary key, a binding, and many-to-one
/// relations for the foreign keys between the tables (or towards tables an entity is already bound to). Everything is planned against
/// one snapshot; the caller applies the changes all or nothing. A table (or key sequence) stored again after an undo takes back the
/// ids the committed snapshot's alias recorded for its projected key, whenever no file holds them now (<paramref name="aliases"/>):
/// storing again then yields the same table, column and sequence ids, the alias stays as it is, and the next migration sees one table.
/// </summary>
internal sealed class Materializer(ModelSnapshot snapshot, IModelResolver resolver, IIdGenerator ids, int parallelism, IReadOnlyList<SnapshotAlias>? aliases = null)
{
    private readonly Dictionary<string, (string? Hash, JsonObject Node, BatchOp Op, string Because)> _work = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly List<string> _notes = [];
    private readonly List<SchemaDiff.TableRekey> _rekeys = [];
    private readonly Dictionary<string, string> _sequenceKeys = new(StringComparer.Ordinal);
    private string? _databaseName;

    /// <summary>Plans <c>materialize-tables</c>.</summary>
    /// <param name="databaseId">The database.</param>
    /// <param name="entityIds">The entities.</param>
    /// <param name="schemaId">The schema of the database the tables go to, or <see langword="null"/> for the projected tables' own.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan.</returns>
    public async Task<MaterializeResult> TablesAsync(string databaseId, IReadOnlyList<string> entityIds, string? schemaId, CancellationToken ct)
    {
        if (snapshot.Get<Database>(databaseId) is not { } db)
            return Refused($"'{databaseId}' is not a database.");
        if (schemaId is not null && !db.Schemas.Any(s => s.Id == schemaId))
            return Refused($"Database '{db.Name}' has no schema '{schemaId}'.");
        var entities = new List<Entity>();
        foreach (var id in entityIds.Distinct(StringComparer.Ordinal))
        {
            if (snapshot.Get<Entity>(id) is not { } entity)
                return Refused($"'{id}' is not an entity.");
            if (entity.Bindings.FirstOrDefault(b => b.Database == db.Id) is { } existing)
                return Refused($"Entity '{entity.Name}' is already bound to database '{db.Name}' (binding {existing.Id}): it reads from what its binding names; edit the binding instead of materializing the entity again.");
            if (entity.Abstract)
                return Refused($"Entity '{entity.Name}' is abstract: it has no rows of its own to store.");
            if (entity.Base is not null || snapshot.All<Entity>().Any(e => e.Base == entity.Id))
                return Refused($"Entity '{entity.Name}' is part of an inheritance hierarchy, whose tables materialize does not write yet: bind its entities by hand.");
            entities.Add(entity);
        }

        if (entities.Count == 0)
            return Refused("Name at least one entity to materialize.");
        _databaseName = db.Name;

        // The shapes come from a resolution where every selected entity is projected into the database, mapped or not.
        var resolved = await resolver.ResolveAsync(Projected(db, entities), null, ct).ConfigureAwait(false);
        var rdb = resolved.Databases.First(d => d.Id == db.Id);
        var tables = new Dictionary<string, (Entity Entity, RTable Table, Table? Overlay, string Id, Dictionary<string, string> Columns)>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            var table = rdb.Tables.FirstOrDefault(t => t.Origin == "synthesized" && t.Entity?.Id == entity.Id && t.Attribute is null && !t.IsJunction);
            if (table is null)
                return Refused($"Entity '{entity.Name}' has no table of its own in database '{db.Name}'.");
            var overlay = snapshot.All<Table>().FirstOrDefault(t => t.Database == db.Id && t.Origin == TableOrigin.Synthesized && t.Entity == entity.Id && t.Attribute is null);
            var tableId = overlay?.Id ?? Reused(table.Key, "table") ?? ids.NewId();
            var known = AliasOf(table.Key, "table") is { } alias && alias.Alias == tableId ? alias.Columns : null;
            var columns = new Dictionary<string, string>(StringComparer.Ordinal);
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var column in table.Columns)
            {
                var id = overlay?.Columns.FirstOrDefault(c => c.Attribute == column.Key || (c.Attribute is null && c.Id == column.Key))?.Id;
                if (id is null && known?.GetValueOrDefault(column.Key) is { } was && !taken.Contains(was)
                    && overlay?.Columns.Any(c => c.Id == was) != true)
                    id = was;
                id ??= ids.NewId();
                taken.Add(id);
                columns[column.Key] = id;
            }

            tables[table.Key] = (entity, table, overlay, tableId, columns);
            if (overlay is not null)
                tables[overlay.Id] = tables[table.Key];
            foreach (var child in rdb.Tables.Where(t => t.Entity?.Id == entity.Id && t.Attribute is not null))
                _notes.Add($"Attribute '{child.Attribute!.Name}' of entity '{entity.Name}' is stored in table '{child.Name}' of its own, which a bound entity does not have: it is left out of the binding.");
        }

        var conventions = EffectiveConventions.For(snapshot.Settings, db.Name);
        var typeMap = DialectTypeMaps.Effective(db.Dialect, snapshot.Settings);
        var relationKeys = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (entity, table, overlay, tableId, columns) in tables.Values.DistinctBy(t => t.Id).OrderBy(t => t.Entity.Name, StringComparer.Ordinal).ThenBy(t => t.Entity.Id, StringComparer.Ordinal))
        {
            var node = TableNode(db, schemaId, entity, table, overlay, tableId, columns, tables, conventions, typeMap, relationKeys);
            _rekeys.Add(new SchemaDiff.TableRekey(table.Key, tableId, columns));
            if (overlay is not null)
                Put(overlay.Id, snapshot.GetDocument(overlay.Id)!.Hash, node, BatchOp.Update, $"the projected table of entity {entity.Name}, with its overlay folded in, becomes a designed table");
            else
                Put(tableId, null, node, BatchOp.Create, $"the projected table of entity {entity.Name} as a designed table");

            var entityNode = Node(entity.Id, BatchOp.Update, $"binds entity {entity.Name} to table {table.Name}");
            var bindings = entityNode["bindings"] as JsonArray ?? [];
            entityNode["bindings"] = bindings;
            bindings.Add(BindingFor(db, entity, table, tableId, columns, resolved));

            foreach (var mapping in snapshot.All<Mapping>().Where(m => m.Database == db.Id && m.Entity == entity.Id))
                Put(mapping.Id, snapshot.GetDocument(mapping.Id)!.Hash, null, BatchOp.Delete, $"entity {entity.Name} is bound now, so its mapping to database {db.Name} is no longer used");
        }

        foreach (var (relationId, fkId) in relationKeys)
            SetRelationForeignKey(db, relationId, fkId);
        RewriteReferences(db, rdb, tables);
        return Result();
    }

    /// <summary>The snapshot with every selected entity placed in the database by a mapping element (an ignoring one stops ignoring).</summary>
    private ModelSnapshot Projected(Database db, List<Entity> entities)
    {
        var documents = new List<ElementDocument>(snapshot.Documents.Count + entities.Count);
        var selected = entities.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var mapped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in snapshot.Documents)
        {
            if (document.Element is Mapping { Entity: { } entityId } mapping && mapping.Database == db.Id && selected.Contains(entityId))
            {
                mapped.Add(entityId);
                documents.Add(document with { Element = mapping with { Ignore = false } });
                continue;
            }

            documents.Add(document);
        }

        using var empty = JsonDocument.Parse("{}");
        foreach (var entity in entities.Where(e => !mapped.Contains(e.Id)))
        {
            var id = ids.NewId();
            documents.Add(new ElementDocument(new Mapping { Id = id, Database = db.Id, Entity = entity.Id }, "model/mappings/.materialize-" + id.ToLowerInvariant() + ".json",
                "", "", empty.RootElement.Clone(), null));
        }

        documents.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return ModelSnapshot.CreateAfter(null, documents, snapshot.Settings, snapshot.SettingsHash, snapshot.Extensions, snapshot.RuleScripts, snapshot.Version,
            null, parallelism, CancellationToken.None);
    }

    private JsonObject TableNode(Database db, string? schemaId, Entity entity, RTable table, Table? overlay, string tableId, Dictionary<string, string> columns,
        Dictionary<string, (Entity Entity, RTable Table, Table? Overlay, string Id, Dictionary<string, string> Columns)> tables,
        EffectiveConventions conventions, IReadOnlyDictionary<string, string> typeMap, SortedDictionary<string, string> relationKeys)
    {
        var node = new JsonObject { ["kind"] = "table", ["id"] = tableId, ["name"] = table.Name, ["database"] = db.Id };
        var schema = schemaId ?? overlay?.Schema ?? db.Schemas.FirstOrDefault(s => s.Name == table.Schema)?.Id;
        if (schema is not null)
            node["schema"] = schema;
        if (overlay is not null)
            CopyAnnotations(Raw(overlay.Id), node);
        if (table.Comment is { Length: > 0 } comment)
            node["comment"] = comment;

        var dialect = DialectTypeMaps.Name(db.Dialect);
        var columnArray = new JsonArray();
        foreach (var column in table.Columns)
        {
            var entry = overlay?.Columns.FirstOrDefault(c => c.Attribute == column.Key || (c.Attribute is null && c.Id == column.Key));
            var c = new JsonObject { ["id"] = columns[column.Key], ["name"] = column.Name };
            var type = column.Type == "reference" ? column.CodeType ?? "string" : column.Type;
            c["type"] = type;
            if (column.Length is { } length)
                c["length"] = length;
            if (column.Precision is { } precision)
                c["precision"] = precision;
            if (column.Scale is { } scale)
                c["scale"] = scale;
            var rendered = column.NativeType is null ? null
                : DialectTypeMaps.Render(typeMap, DialectTypeMaps.VariantKey(typeMap, type, column.FixedLength, column.Unicode), column.Length, column.Precision, column.Scale, conventions);
            if (column.Unicode is { } unicode)
                c["unicode"] = unicode;
            if (column.FixedLength)
                c["fixedLength"] = true;
            if (entry?.NativeType is { } native)
                c["nativeType"] = native;
            else if (column.NativeType is { } computedNative && !string.Equals(computedNative, rendered, StringComparison.Ordinal))
                c["nativeType"] = computedNative;
            if (!column.Nullable)
                c["nullable"] = false;
            if (column.Default is { } value)
                c["default"] = JsonSerializer.SerializeToNode(value);
            if (entry is { DefaultSql.Count: > 0 })
                c["defaultSql"] = JsonSerializer.SerializeToNode(entry.DefaultSql);
            else if (column.DefaultSql is { } sql)
                c["defaultSql"] = new JsonObject { [dialect] = sql };
            if (column.DefaultName is { } defaultName)
                c["defaultName"] = defaultName;
            if (column.Identity)
            {
                c["generated"] = "identity";
                if (column.IdentitySeed is not null || column.IdentityIncrement is not null || column.IdentityAlways)
                {
                    var identity = new JsonObject();
                    if (column.IdentitySeed is { } seed)
                        identity["seed"] = seed;
                    if (column.IdentityIncrement is { } increment)
                        identity["increment"] = increment;
                    if (column.IdentityAlways)
                        identity["always"] = true;
                    c["identity"] = identity;
                }
            }
            else if (column.Sequence is { } sequence)
            {
                c["generated"] = "sequence";
                c["sequence"] = snapshot.Get<Sequence>(sequence.Id) is not null ? sequence.Id : CreateSequence(db, sequence, schema);
            }

            if (column.Computed is { } computed)
            {
                c["computed"] = computed;
                if (column.ComputedStored)
                    c["computedStored"] = true;
            }

            if (column.Collation is { } collation)
                c["collation"] = collation;
            if (column.Comment is { Length: > 0 } columnComment)
                c["comment"] = columnComment;
            if (entry is not null && overlay is not null && Raw(overlay.Id)?["columns"] is JsonArray raw
                && raw.OfType<JsonObject>().FirstOrDefault(x => x["id"]?.GetValue<string>() == entry.Id) is { } rawEntry)
                CopyAnnotations(rawEntry, c);
            columnArray.Add(c);
        }

        node["columns"] = columnArray;
        string Col(RColumn column) => columns[column.Key];
        if (table.PrimaryKey is { } pk)
        {
            var key = new JsonObject { ["name"] = pk.Name, ["columns"] = new JsonArray([.. pk.Columns.Select(c => (JsonNode)Col(c))]) };
            if (pk.Clustered is { } clustered)
                key["clustered"] = clustered;
            node["primaryKey"] = key;
        }

        node["uniques"] = new JsonArray([.. table.Uniques.Select(u =>
        {
            var x = new JsonObject { ["id"] = u.Id ?? ids.NewId(), ["name"] = u.Name, ["columns"] = new JsonArray([.. u.Columns.Select(c => (JsonNode)Col(c))]) };
            if (u.NullsNotDistinct)
                x["nullsNotDistinct"] = true;
            return (JsonNode)x;
        })]);

        var foreignKeys = new JsonArray();
        foreach (var fk in table.ForeignKeys)
        {
            var target = fk.ReferencedTable;
            var materialized = tables.TryGetValue(target.Key, out var other) ? other : default;
            var fkId = fk.Id ?? ids.NewId();
            var fkNode = new JsonObject
            {
                ["id"] = fkId,
                ["name"] = fk.Name,
                ["columns"] = new JsonArray([.. fk.Columns.Select(c => (JsonNode)Col(c))]),
                ["referencesTable"] = materialized.Id ?? target.Key,
                ["referencesColumns"] = new JsonArray([.. fk.ReferencedColumns.Select(c => (JsonNode)(materialized.Columns is { } map ? map[c.Key] : c.Key))]),
                ["onDelete"] = fk.OnDelete,
                ["onUpdate"] = fk.OnUpdate,
            };
            if (fk.Deferrable != "not-deferrable")
                fkNode["deferrable"] = fk.Deferrable;
            foreignKeys.Add(fkNode);
            if (fk.Relation is { } relation && snapshot.Get<Relation>(relation.Id) is not null)
                relationKeys[relation.Id] = fkId;
            if (target.Origin == "synthesized" && materialized.Id is null)
                _notes.Add($"Foreign key '{fk.Name}' of table '{table.Name}' references table '{target.Name}', which entity '{target.Entity?.Name}' still projects: it names that table by its key until the entity is materialized too.");
        }

        node["foreignKeys"] = foreignKeys;
        var checks = new JsonArray();
        foreach (var check in table.Checks)
        {
            var file = overlay?.Checks.FirstOrDefault(x => x.Id == check.Id);
            var checkNode = new JsonObject
            {
                ["id"] = check.Id ?? ids.NewId(),
                ["name"] = check.Name,
                ["expression"] = file is not null ? JsonSerializer.SerializeToNode(file.Expression) : new JsonObject { ["*"] = check.Expression },
            };
            if (check.Column is { } checkColumn)
                checkNode["column"] = Col(checkColumn);
            checks.Add(checkNode);
        }

        node["checks"] = checks;
        JsonNode IndexColumnNode(RIndexColumn c)
        {
            var x = c.Column is { } column
                ? new JsonObject { ["column"] = Col(column) }
                : new JsonObject { ["expression"] = new JsonObject { [table.Database.Dialect] = c.Expression } };
            x["descending"] = c.Descending;
            if (c.Length is { } length)
                x["length"] = length;
            return x;
        }

        node["indexes"] = new JsonArray([.. table.Indexes.Select(index =>
        {
            var x = new JsonObject
            {
                ["id"] = index.Id ?? ids.NewId(),
                ["name"] = index.Name,
                ["columns"] = new JsonArray([.. index.Columns.Select(IndexColumnNode)]),
                ["include"] = new JsonArray([.. index.Include.Select(c => (JsonNode)Col(c))]),
                ["unique"] = index.Unique,
                ["method"] = index.Method,
            };
            if (index.Where is { } where)
                x["where"] = where;
            return (JsonNode)x;
        })]);
        return node;
    }

    /// <summary>A sequence file for a key sequence the projection synthesized, which a bound entity no longer gets.</summary>
    private string CreateSequence(Database db, RSequence sequence, string? schema)
    {
        var id = Reused(sequence.Id, "sequence") ?? ids.NewId();
        _sequenceKeys[sequence.Id] = id;
        var node = new JsonObject { ["kind"] = "sequence", ["id"] = id, ["name"] = sequence.Name, ["database"] = db.Id, ["type"] = sequence.Type };
        if (schema is not null)
            node["schema"] = schema;
        Put(id, null, node, BatchOp.Create, $"the key sequence {sequence.Name} the projection synthesized, as a sequence of its own");
        return id;
    }

    private static readonly string[] AnnotationNames = ["displayName", "pluralName", "description", "stereotypes", "tags", "category", "properties", "generation", "source"];

    private static void CopyAnnotations(JsonObject? from, JsonObject to)
    {
        if (from is null)
            return;
        foreach (var name in AnnotationNames)
        {
            if (from[name] is { } value)
                to[name] = value.DeepClone();
        }
    }

    private JsonObject? Raw(string id) =>
        snapshot.GetDocument(id) is { } document ? JsonNode.Parse(document.Json.GetRawText()) as JsonObject : null;

    /// <summary>The binding of a materialized entity: every column of its table accounted for, write and delete by default.</summary>
    private JsonObject BindingFor(Database db, Entity entity, RTable table, string tableId, Dictionary<string, string> columns, ResolvedModel resolved)
    {
        var fields = new JsonArray();
        var listed = new JsonArray();
        var rentity = resolved.Entities.First(e => e.Id == entity.Id);
        foreach (var column in table.Columns)
        {
            var target = FieldTarget(rentity, column.AttributePath ?? column.Key, resolved);
            if (target is not null)
            {
                fields.Add(new JsonObject { ["attribute"] = target, ["column"] = columns[column.Key] });
                continue;
            }

            if (column.Identity || column.Computed is not null || column.Default is not null || column.DefaultSql is not null)
                continue;
            listed.Add(new JsonObject { ["column"] = columns[column.Key], ["status"] = "ignored" });
            _notes.Add($"Column '{column.Name}' of table '{table.Name}' maps to no attribute of entity '{entity.Name}' a binding can hold: it is listed as ignored.");
        }

        var binding = new JsonObject { ["id"] = ids.NewId(), ["database"] = db.Id, ["source"] = tableId, ["fields"] = fields };
        if (listed.Count > 0)
            binding["columns"] = listed;
        return binding;
    }

    /// <summary>What a binding field names for a projected column: an attribute, a member, or a to-one end; null when none fits.</summary>
    private static string? FieldTarget(REntity entity, string path, ResolvedModel resolved)
    {
        var segments = path.Split('.');
        if (entity.Attributes.FirstOrDefault(a => a.Id == segments[0]) is { } attribute)
        {
            if (segments.Length == 1)
                return attribute.Collection ? null : path;
            if (segments.Length == 2 && attribute.Type.ValueObject is { } vo && vo.Attributes.FirstOrDefault(m => m.Id == segments[1]) is { Collection: false } member
                && member.Type.Kind != "value-object")
                return path;
            return null;
        }

        if (segments.Length == 2)
        {
            var end = resolved.Relations.SelectMany(r => r.Ends).FirstOrDefault(e => e.Id == segments[0]);
            if (end is { IsMany: false, Opposite: { } opposite } && opposite.Entity.Id == entity.Id && end.Entity.Key is { Attributes.Count: 1 } key && key.Attributes[0].Id == segments[1])
                return end.Id;
        }

        return null;
    }

    private void SetRelationForeignKey(Database db, string relationId, string fkId)
    {
        var relation = snapshot.Get<Relation>(relationId)!;
        if (snapshot.All<Mapping>().FirstOrDefault(m => m.Database == db.Id && m.Relation == relationId) is { } mapping)
        {
            var node = Node(mapping.Id, BatchOp.Update, $"relation {relation.Name} keeps its foreign key in the designed table");
            node["foreignKey"] = fkId;
            node.Remove("shape");
            return;
        }

        var id = ids.NewId();
        Put(id, null, new JsonObject
        {
            ["kind"] = "mapping", ["id"] = id, ["name"] = relation.Name + " in " + db.Name, ["database"] = db.Id, ["relation"] = relationId, ["foreignKey"] = fkId,
        }, BatchOp.Create, $"relation {relation.Name} names its foreign key in the designed table");
    }

    /// <summary>
    /// Points what named the projected tables by their keys at the designed tables: the sources and column references of the
    /// database's queries, the foreign keys of other tables, and other entities' bindings. A table that keeps its overlay's id keeps
    /// every reference by that id.
    /// </summary>
    private void RewriteReferences(Database db, RDatabase rdb,
        Dictionary<string, (Entity Entity, RTable Table, Table? Overlay, string Id, Dictionary<string, string> Columns)> tables)
    {
        var byKey = tables.Values.DistinctBy(t => t.Id).ToDictionary(t => t.Table.Key, StringComparer.Ordinal);
        foreach (var query in rdb.Queries)
        {
            var replace = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var source in Sources(query))
            {
                if (source.Table is { } t && byKey.TryGetValue(t.Key, out var hit) && source.Source != hit.Id)
                    replace[source.Source] = hit.Id;
            }

            foreach (var e in Expressions(query))
            {
                if (e is { Node: "column", Column: { } text, TableColumn: { } column, Source.Table: { } t } && byKey.TryGetValue(t.Key, out var hit)
                    && hit.Columns.TryGetValue(column.Key, out var newId))
                {
                    var dot = text.IndexOf('.', StringComparison.Ordinal);
                    var rewritten = dot > 0 && e.Alias is { } alias && text[..dot] == alias ? alias + "." + newId : text;
                    if (rewritten != text)
                        replace[text] = rewritten;
                }
            }

            if (replace.Count > 0 && Raw(query.Id) is { } raw && ReplaceStrings(raw, replace, ["source", "column"]))
                PutRewrite(query.Id, raw, $"query {query.Name} reads the designed table by its id");
        }

        foreach (var table in snapshot.All<Table>().Where(t => t.Database == db.Id && !tables.ContainsKey(t.Id)))
        {
            if (Raw(table.Id) is not { } raw || raw["foreignKeys"] is not JsonArray fks)
                continue;
            var changed = false;
            foreach (var fk in fks.OfType<JsonObject>())
            {
                if (fk["referencesTable"]?.GetValue<string>() is not { } referenced || !byKey.TryGetValue(referenced, out var hit) && !tables.TryGetValue(referenced, out hit))
                    continue;
                if (referenced != hit.Id)
                {
                    fk["referencesTable"] = hit.Id;
                    changed = true;
                }

                if (fk["referencesColumns"] is JsonArray columns)
                {
                    for (var i = 0; i < columns.Count; i++)
                    {
                        if (columns[i]?.GetValue<string>() is { } key && hit.Columns.TryGetValue(key, out var newId) && key != newId)
                        {
                            columns[i] = newId;
                            changed = true;
                        }
                    }
                }
            }

            if (changed)
                PutRewrite(table.Id, raw, $"table {ChangePlanner.ReadableName(snapshot, table)} references the designed table by its id");
        }

        foreach (var entity in snapshot.All<Entity>())
        {
            if (entity.Bindings.Count == 0)
                continue;
            var raw = _work.TryGetValue(entity.Id, out var working) ? working.Node : Raw(entity.Id);
            if (raw?["bindings"] is not JsonArray bindings)
                continue;
            var changed = false;
            foreach (var binding in bindings.OfType<JsonObject>())
            {
                var key = binding["source"]?.GetValue<string>();
                if (key is null || !byKey.TryGetValue(key, out var hit))
                    continue;
                var map = new Dictionary<string, string>(hit.Columns, StringComparer.Ordinal) { [key] = hit.Id };
                changed |= ReplaceStrings(binding, map, ["source", "table", "column"]);
            }

            if (changed && !_work.ContainsKey(entity.Id))
                PutRewrite(entity.Id, raw, $"entity {entity.Name} binds to the designed table by its id");
        }
    }

    private static IEnumerable<RQuerySource> Sources(RQuery query)
    {
        yield return query.From;
        foreach (var join in query.Joins)
            yield return join;
        foreach (var collection in query.Collections)
        {
            foreach (var source in Sources(collection.Query))
                yield return source;
        }

        foreach (var nested in Predicates(query).Where(p => p.Exists is not null).Select(p => p.Exists!))
        {
            foreach (var source in Sources(nested))
                yield return source;
        }
    }

    private static IEnumerable<RQueryPredicate> Predicates(RQuery query)
    {
        var roots = new List<RQueryPredicate?> { query.Where, query.Having };
        roots.AddRange(query.Joins.Select(j => j.On));
        foreach (var root in roots.OfType<RQueryPredicate>())
        {
            foreach (var p in Walk(root))
                yield return p;
        }

        static IEnumerable<RQueryPredicate> Walk(RQueryPredicate p)
        {
            yield return p;
            foreach (var child in p.And.Concat(p.Or).Concat(p.Not is { } not ? [not] : []))
            {
                foreach (var x in Walk(child))
                    yield return x;
            }
        }
    }

    private static IEnumerable<RQueryExpression> Expressions(RQuery query)
    {
        var roots = new List<RQueryExpression>();
        roots.AddRange(query.Select.Select(f => f.Expression));
        roots.AddRange(query.GroupBy);
        roots.AddRange(query.OrderBy.Select(o => o.Expression));
        foreach (var p in Predicates(query))
        {
            if (p.Left is { } left)
                roots.Add(left);
            if (p.Right is { } right)
                roots.Add(right);
            roots.AddRange(p.Values);
        }

        foreach (var root in roots)
        {
            foreach (var e in Walk(root))
                yield return e;
        }

        foreach (var nested in query.Collections.Select(c => c.Query).Concat(Predicates(query).Where(p => p.Exists is not null).Select(p => p.Exists!)))
        {
            foreach (var e in Expressions(nested))
                yield return e;
        }

        static IEnumerable<RQueryExpression> Walk(RQueryExpression e)
        {
            yield return e;
            var children = e.Args.Concat(e.Case.SelectMany(w => new[] { w.Then })).Concat(e.Else is { } x ? [x] : []).Concat(e.Cast is { } c ? [c] : []);
            foreach (var child in children)
            {
                foreach (var y in Walk(child))
                    yield return y;
            }

            foreach (var when in e.Case)
            {
                foreach (var p in WalkPredicate(when.When))
                {
                    foreach (var operand in (p.Left is { } l ? new[] { l } : []).Concat(p.Right is { } r ? [r] : []).Concat(p.Values))
                    {
                        foreach (var y in Walk(operand))
                            yield return y;
                    }
                }
            }
        }

        static IEnumerable<RQueryPredicate> WalkPredicate(RQueryPredicate p)
        {
            yield return p;
            foreach (var child in p.And.Concat(p.Or).Concat(p.Not is { } not ? [not] : []))
            {
                foreach (var x in WalkPredicate(child))
                    yield return x;
            }
        }
    }

    /// <summary>Replaces string values of the named properties anywhere under a node; true when one changed.</summary>
    private static bool ReplaceStrings(JsonNode node, IReadOnlyDictionary<string, string> map, string[] properties)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    var value = obj[name];
                    if (value is JsonValue v && properties.Contains(name) && v.TryGetValue<string>(out var text) && map.TryGetValue(text, out var replacement))
                    {
                        obj[name] = replacement;
                        changed = true;
                    }
                    else if (value is not null)
                    {
                        changed |= ReplaceStrings(value, map, properties);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array.ToList())
                {
                    if (item is not null)
                        changed |= ReplaceStrings(item, map, properties);
                }

                break;
        }

        return changed;
    }

    // ---- materialize-entities ---------------------------------------------------------------------------------------------------

    /// <summary>Plans <c>materialize-entities</c>.</summary>
    /// <param name="databaseId">The database.</param>
    /// <param name="tableIds">The designed or imported tables and views.</param>
    /// <param name="packageId">The package the entities go to.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan.</returns>
    public async Task<MaterializeResult> EntitiesAsync(string databaseId, IReadOnlyList<string> tableIds, string? packageId, CancellationToken ct)
    {
        if (snapshot.Get<Database>(databaseId) is not { } db)
            return Refused($"'{databaseId}' is not a database.");
        if (packageId is null || snapshot.Get<Package>(packageId) is not { } package)
            return Refused($"'{packageId}' is not a package: name the package the entities go to.");
        var boundBy = BoundSources(db);
        var selected = new List<Element>();
        foreach (var id in tableIds.Distinct(StringComparer.Ordinal))
        {
            var element = snapshot.Get<Element>(id);
            switch (element)
            {
                case Table { Origin: TableOrigin.Synthesized } synthesized when synthesized.Database == db.Id:
                    return Refused($"Table '{ChangePlanner.ReadableName(snapshot, synthesized)}' is projected from an entity: materialize the entity's table instead (materialize-tables).");
                case Table t when t.Database == db.Id:
                case View v when v.Database == db.Id:
                    break;
                default:
                    return Refused($"'{id}' is not a designed or imported table or a view of database '{db.Name}'.");
            }

            if (boundBy.TryGetValue(id, out var binder))
                return Refused($"{element.KindName} '{element.Name}' is already bound by entity '{binder.Name}': edit that binding, or bind another entity to it by hand (with constants that tell them apart).");
            selected.Add(element);
        }

        if (selected.Count == 0)
            return Refused("Name at least one table or view to materialize.");

        var resolved = await resolver.ResolveAsync(snapshot, null, ct).ConfigureAwait(false);
        var rdb = resolved.Databases.First(d => d.Id == db.Id);
        var inflector = new Inflector(snapshot.Settings.Inflection);
        var dialectMap = DialectTypeMaps.Effective(db.Dialect, snapshot.Settings);
        var names = new HashSet<string>(snapshot.All<Entity>().Where(e => e.Package == package.Id).Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        var made = new Dictionary<string, (string EntityId, string Name, JsonObject Node, Dictionary<string, (string Id, string Name)> Attributes)>(StringComparer.Ordinal);
        foreach (var element in selected)
        {
            var name = Casing.Pascal(inflector.Singularize(element.Name));
            if (name.Length == 0 || !char.IsAsciiLetter(name[0]))
                name = "E" + name;
            if (!names.Add(name))
                return Refused($"Package '{package.Name}' already has an entity named '{name}' (from {element.KindName} '{element.Name}'): rename the table, or create the entity by hand.");
            var entityId = ids.NewId();
            var attributes = new JsonArray();
            var fields = new JsonArray();
            var byColumn = new Dictionary<string, (string Id, string Name)>(StringComparer.Ordinal);
            var attributeNames = new HashSet<string>(StringComparer.Ordinal);
            var key = new JsonArray();
            string? strategy = null;
            var columns = element is Table
                ? rdb.Tables.First(t => t.Key == element.Id).Columns
                    .Select(c => new SourceColumn(c.Key, c.Name, c.Type == "reference" ? c.CodeType : c.Type, c.NativeType, c.Length, c.Precision, c.Scale, c.Nullable, c.IsPrimaryKey, c.Identity)).ToList()
                : rdb.Views.First(v => v.Id == element.Id).Columns
                    .Select(c => new SourceColumn(c.Name, c.Name, c.Type, c.NativeType, null, null, null, c.Nullable, false, false)).ToList();
            foreach (var (columnKey, columnName, columnType, nativeType, length, precision, scale, nullable, isKey, identity) in columns)
            {
                var attributeName = Casing.Camel(columnName);
                if (attributeName.Length == 0 || !(char.IsAsciiLetter(attributeName[0]) || attributeName[0] == '_'))
                    attributeName = "c" + Casing.Pascal(columnName);
                var unique = attributeName;
                for (var n = 2; !attributeNames.Add(unique); n++)
                    unique = attributeName + n.ToString(CultureInfo.InvariantCulture);
                var type = columnType is not null && BuiltinTypes.All.Contains(columnType) ? columnType : Reverse(dialectMap, nativeType) ?? "string";
                var attributeId = ids.NewId();
                var attribute = new JsonObject { ["id"] = attributeId, ["name"] = unique, ["type"] = type };
                if (length is { } l && type is "string" or "binary")
                    attribute["length"] = l;
                if (type == "decimal")
                {
                    if (precision is { } p)
                        attribute["precision"] = p;
                    if (scale is { } s)
                        attribute["scale"] = s;
                }

                if (!nullable)
                    attribute["required"] = true;
                attributes.Add(attribute);
                fields.Add(new JsonObject { ["attribute"] = attributeId, ["column"] = columnKey });
                byColumn[columnKey] = (attributeId, unique);
                if (isKey)
                {
                    key.Add(attributeId);
                    if (identity)
                        strategy = "database-identity";
                }
            }

            if (key.Count == 0 && byColumn.Count > 0)
            {
                var chosen = columns.FirstOrDefault(c => string.Equals(c.Name, "id", StringComparison.OrdinalIgnoreCase)) ?? columns[0];
                key.Add(byColumn[chosen.Key].Id);
                _notes.Add($"{element.KindName} '{element.Name}' has no primary key: entity '{name}' takes column '{chosen.Name}' as its key.");
            }

            var entity = new JsonObject { ["kind"] = "entity", ["id"] = entityId, ["name"] = name, ["package"] = package.Id };
            if (key.Count > 0)
                entity["key"] = strategy is null ? new JsonObject { ["attributes"] = key } : new JsonObject { ["attributes"] = key, ["strategy"] = strategy };
            entity["attributes"] = attributes;
            entity["bindings"] = new JsonArray(new JsonObject { ["id"] = ids.NewId(), ["database"] = db.Id, ["source"] = element.Id, ["fields"] = fields });
            made[element.Id] = (entityId, name, entity, byColumn);
            Put(entityId, null, entity, BatchOp.Create, $"an entity for {element.KindName} {element.Name}, bound to it");
        }

        // Foreign keys between the selected tables, or towards a table an entity is already bound to, become many-to-one relations.
        foreach (var element in selected.OfType<Table>())
        {
            var table = rdb.Tables.First(t => t.Key == element.Id);
            var dependent = made[element.Id];
            foreach (var fk in table.ForeignKeys.Where(f => f.Id is not null))
            {
                string principalId, principalName;
                if (made.TryGetValue(fk.ReferencedTable.Key, out var principal))
                {
                    (principalId, principalName) = (principal.EntityId, principal.Name);
                }
                else if (boundBy.TryGetValue(fk.ReferencedTable.Key, out var binder))
                {
                    (principalId, principalName) = (binder.Id, binder.Name);
                }
                else
                {
                    _notes.Add($"Foreign key '{fk.Name}' of table '{table.Name}' references table '{fk.ReferencedTable.Name}', which no entity is bound to: no relation is made for it.");
                    continue;
                }

                var role = Casing.Camel(principalName);
                var navigation = dependent.Attributes.Values.Any(a => a.Name == role) ? role + "Ref" : role;
                var relationId = ids.NewId();
                var required = fk.Columns.All(c => !c.Nullable);
                var principalEnd = new JsonObject { ["id"] = ids.NewId(), ["entity"] = principalId, ["role"] = role, ["navigation"] = navigation, ["max"] = 1 };
                if (required)
                    principalEnd["min"] = 1;
                var onDelete = fk.OnDelete switch { "cascade" => "cascade", "restrict" => "restrict", "set-null" => "set-null", _ => null };
                if (onDelete is not null)
                    principalEnd["onDelete"] = onDelete;
                var relationName = fk.Name.Length > 0 ? fk.Name : table.Name + "_" + fk.ReferencedTable.Name;
                Put(relationId, null, new JsonObject
                {
                    ["kind"] = "relation", ["id"] = relationId, ["name"] = relationName, ["package"] = package.Id,
                    ["ends"] = new JsonArray(principalEnd, new JsonObject { ["id"] = ids.NewId(), ["entity"] = dependent.EntityId, ["role"] = Casing.Camel(inflector.Pluralize(dependent.Name)) }),
                }, BatchOp.Create, $"foreign key {fk.Name} of table {table.Name} as a many-to-one relation");
                var mappingId = ids.NewId();
                Put(mappingId, null, new JsonObject
                {
                    ["kind"] = "mapping", ["id"] = mappingId, ["name"] = relationName + " in " + db.Name, ["database"] = db.Id, ["relation"] = relationId, ["foreignKey"] = fk.Id,
                }, BatchOp.Create, $"relation {relationName} names foreign key {fk.Name}");
            }
        }

        return Result();
    }

    private sealed record SourceColumn(string Key, string Name, string? Type, string? NativeType, int? Length, int? Precision, int? Scale, bool Nullable,
        bool IsKey, bool Identity);

    /// <summary>The entity bound to each table or view of the database (first by name), by the source and write table ids.</summary>
    private Dictionary<string, Entity> BoundSources(Database db)
    {
        var result = new Dictionary<string, Entity>(StringComparer.Ordinal);
        foreach (var entity in snapshot.All<Entity>())
        {
            foreach (var binding in entity.Bindings.Where(b => b.Database == db.Id))
            {
                result.TryAdd(binding.Source, entity);
                if (binding.Write?.Table is { } table)
                    result.TryAdd(table, entity);
            }
        }

        return result;
    }

    /// <summary>The built-in keyword whose native type pattern for the dialect starts with the native type's base name.</summary>
    private static string? Reverse(IReadOnlyDictionary<string, string> map, string? nativeType)
    {
        if (nativeType is null)
            return null;
        var paren = nativeType.IndexOf('(', StringComparison.Ordinal);
        var name = (paren < 0 ? nativeType : nativeType[..paren]).Trim();
        foreach (var keyword in BuiltinTypes.All)
        {
            if (map.TryGetValue(keyword, out var pattern))
            {
                var p = pattern.IndexOf('(', StringComparison.Ordinal);
                if (string.Equals((p < 0 ? pattern : pattern[..p]).Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return keyword;
            }
        }

        return null;
    }

    // ---- ids stored before -------------------------------------------------------------------------------------------------------

    private readonly HashSet<string> _reused = new(StringComparer.Ordinal);

    /// <summary>The committed snapshot's alias of a synthesized key (the last one recorded), or <see langword="null"/>.</summary>
    private SnapshotAlias? AliasOf(string key, string kind) => aliases?.LastOrDefault(a => a.Key == key && a.Kind == kind);

    /// <summary>
    /// The id a table or sequence file had when the key was stored before (the alias), when no element holds it now and this operation
    /// has not given it out already; otherwise <see langword="null"/>.
    /// </summary>
    private string? Reused(string key, string kind) =>
        AliasOf(key, kind) is { } alias && snapshot.GetDocument(alias.Alias) is null && !_work.ContainsKey(alias.Alias) && _reused.Add(alias.Alias) ? alias.Alias : null;

    // ---- working set ------------------------------------------------------------------------------------------------------------

    private MaterializeResult Refused(string why) => new(why, [], []);

    private void Put(string id, string? hash, JsonObject? node, BatchOp op, string because)
    {
        if (!_work.ContainsKey(id))
            _order.Add(id);
        _work[id] = (hash, node ?? [], op, because);
    }

    private void PutRewrite(string id, JsonObject node, string because)
    {
        if (_work.ContainsKey(id))
            return;
        Put(id, snapshot.GetDocument(id)!.Hash, node, BatchOp.Update, because);
    }

    /// <summary>The working copy of an existing element, read once.</summary>
    private JsonObject Node(string id, BatchOp op, string because)
    {
        if (_work.TryGetValue(id, out var working))
            return working.Node;
        var node = Raw(id)!;
        Put(id, snapshot.GetDocument(id)!.Hash, node, op, because);
        return node;
    }

    private MaterializeResult Result()
    {
        var ops = new List<MaterializeOp>();
        foreach (var id in _order)
        {
            var (hash, node, op, because) = _work[id];
            var kind = node["kind"]?.GetValue<string>() ?? snapshot.GetDocument(id)?.Element.KindName ?? "";
            var name = node["name"]?.GetValue<string>() is { Length: > 0 } n ? n
                : snapshot.Get<Element>(id) is { } element ? ChangePlanner.ReadableName(snapshot, element) : id;
            ops.Add(new MaterializeOp(op, id, hash, op == BatchOp.Delete ? null : node, kind, name, because));
        }

        return new MaterializeResult(null, ops, [.. _notes], _databaseName, [.. _rekeys], _sequenceKeys);
    }
}
