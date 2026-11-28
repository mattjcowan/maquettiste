using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Text;
using Maquettiste.Engine.Validation;

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
/// <param name="Rekeys">The tables whose column keys a fold into a stereotype moved, with their keys before and after (the snapshot's aliases).</param>
/// <param name="MemberRekeys">The constraint ids a fold into a stereotype moved (<c>index</c>, <c>unique</c>, <c>foreign-key</c> and
/// <c>check</c> entries, each with its table's key), which the write's <c>rekey</c> lists after its table's columns.</param>
internal sealed record MaterializeResult(string? Refusal, IReadOnlyList<MaterializeOp> Ops, IReadOnlyList<string> Notes, string? Database = null,
    IReadOnlyList<SchemaDiff.TableRekey>? Rekeys = null, IReadOnlyList<MigrationRekey>? MemberRekeys = null)
{
    /// <summary>The rule a refusal is reported under when it is not the operation's own (MQ4055, MQ4089).</summary>
    public string? RefusalRule { get; init; }

    /// <summary>For <c>materialize-tables</c>: the hierarchies and lists the request could shape, with what it used and the defaults.</summary>
    public MaterializeChoiceSet? Choices { get; init; }

    /// <summary>For <c>materialize-tables</c>: the tables it stored that the database already holds though the committed snapshot does not
    /// (a reference type's lookup table, which the sql-ddl pack created), which the snapshot takes in as they are.</summary>
    public IReadOnlyList<MigrationRekey>? Adopted { get; init; }
}

/// <summary>
/// The two materialize operations (erratum E43, engine-design.md section 7, "Bindings and materialize"): <c>materialize-tables</c>
/// writes, for each entity, a designed table its own designer proposes and binds the entity to it (retire-projection-design.md section
/// 7); <c>materialize-entities</c> writes, for each designed table or view, an entity with one attribute per column, its key from the
/// primary key, a binding, and many-to-one relations for the foreign keys between the tables (or towards tables an entity is already
/// bound to). Everything is planned against one snapshot; the caller applies the changes all or nothing. A table (or key sequence) a
/// committed snapshot's alias recorded before 0.29.0 under the proposal's key takes back the ids it had, whenever no file holds them now
/// (<paramref name="aliases"/>), so the next migration sees one table.
/// </summary>
internal sealed partial class Materializer(ModelSnapshot snapshot, IModelResolver resolver, IIdGenerator ids, IReadOnlyList<SnapshotAlias>? aliases = null)
{
    private readonly Dictionary<string, (string? Hash, JsonObject Node, BatchOp Op, string Because)> _work = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly List<string> _notes = [];
    private readonly List<MigrationRekey> _adopted = [];
    private string? _databaseName;

    /// <summary>The ids of a stereotype's members, in member-kind then file order.</summary>
    private static IEnumerable<string> MemberIds(Stereotype stereotype)
    {
        IEnumerable<string?> ids = [
            .. stereotype.Columns.Select(c => c.Id),
            stereotype.PrimaryKey?.Id,
            .. stereotype.Uniques.Select(u => u.Id),
            .. stereotype.ForeignKeys.Select(f => f.Id),
            .. stereotype.Checks.Select(c => c.Id),
            .. stereotype.Indexes.Select(i => i.Id),
        ];
        return ids.OfType<string>().Where(id => id.Length > 0);
    }


    private static readonly string[] AnnotationNames = ["displayName", "pluralName", "description", "stereotypes", "tags", "category", "properties", "generation", "source"];

    private JsonObject? Raw(string id) =>
        snapshot.GetDocument(id) is { } document ? JsonNode.Parse(document.Json.GetRawText()) as JsonObject : null;

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
                ? rdb.Tables.First(t => t.Key == element.Id).Columns.Select(Source).ToList()
                : rdb.Views.First(v => v.Id == element.Id).Columns.Select(Source).ToList();
            foreach (var column in columns)
            {
                var attribute = AttributeFor(column, dialectMap, attributeNames);
                var attributeId = attribute["id"]!.GetValue<string>();
                attributes.Add(attribute);
                fields.Add(new JsonObject { ["attribute"] = attributeId, ["column"] = column.Key });
                byColumn[column.Key] = (attributeId, attribute["name"]!.GetValue<string>());
                if (column.IsKey)
                {
                    key.Add(attributeId);
                    if (column.Identity)
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
            var binding = new JsonObject { ["id"] = ids.NewId(), ["database"] = db.Id, ["source"] = element.Id, ["fields"] = fields };
            if (element is Table file)
                RecordProvenance(binding, BindingDrift.ColumnsOf(snapshot, file), written: null);
            entity["bindings"] = new JsonArray(binding);
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

    // ---- materialize-columns ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Plans <c>materialize-columns</c>: for each entity bound to the database whose source is a designed or imported table it writes,
    /// a column of that table per attribute the binding leaves unmapped, with the shape the entity's projection gives it (the project's
    /// naming and type conventions, as <c>materialize-tables</c> writes them), and a field mapping it. Without
    /// <paramref name="attributes"/>, every attribute a column can hold that no field maps; with them (one entity only), those, as
    /// binding field references (an attribute id, <c>attributeId.memberId</c>, or a to-one end id). A column whose name the table has
    /// already is left out (map it instead), and a foreign key is not added (a note says so). An attribute whose column name a column
    /// the table inherits from a stereotype has is mapped to that column's keyed id, with no column added (next-phase-2-design.md
    /// section 3.1). Refused for an entity with no binding,
    /// a source that is not a designed or imported table, a binding that writes another table, an attribute already mapped or that no
    /// column holds, and when nothing is left to add.
    /// </summary>
    /// <param name="databaseId">The database.</param>
    /// <param name="entityIds">The entities.</param>
    /// <param name="attributes">The field references (one entity only), or <see langword="null"/> for every unmapped one.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan.</returns>
    public async Task<MaterializeResult> ColumnsAsync(string databaseId, IReadOnlyList<string> entityIds, IReadOnlyList<string>? attributes, CancellationToken ct)
    {
        if (snapshot.Get<Database>(databaseId) is not { } db)
            return Refused($"'{databaseId}' is not a database.");
        var selected = entityIds.Distinct(StringComparer.Ordinal).ToList();
        if (selected.Count == 0)
            return Refused("Name at least one entity to add columns for.");
        if (attributes is { Count: > 0 } && selected.Count > 1)
            return Refused("Name the attributes for one entity at a time.");
        var entities = new List<(Entity Entity, int Index, Table Table)>();
        foreach (var id in selected)
        {
            if (snapshot.Get<Entity>(id) is not { } entity)
                return Refused($"'{id}' is not an entity.");
            var index = entity.Bindings.ToList().FindIndex(b => b.Database == db.Id);
            if (index < 0)
                return Refused($"Entity '{entity.Name}' has no binding to database '{db.Name}': add one first, or create its table (materialize-tables).");
            var binding = entity.Bindings[index];
            if (snapshot.Get<Element>(binding.Source) is not Table table || table.Database != db.Id)
                return Refused($"Entity '{entity.Name}' does not read a table of database '{db.Name}': columns are added to a table file.");
            if (binding.Write?.Table is { } other && other != table.Id)
                return Refused($"Entity '{entity.Name}' writes another table than it reads: add the columns to the tables by hand.");
            entities.Add((entity, index, table));
        }

        var projected = await resolver.ResolveAsync(snapshot, null, ct).ConfigureAwait(false);
        var rdb = projected.Databases.First(d => d.Id == db.Id);
        var conventions = EffectiveConventions.For(snapshot.Settings, db.Name);
        var typeMap = DialectTypeMaps.Effective(db.Dialect, snapshot.Settings);
        foreach (var (entity, index, table) in entities)
        {
            // The shape the entity's own table would have: the designer proposes it alone, outside any hierarchy.
            var designer = new TableDesigner(snapshot, projected, db, _ => null, _ => DesignHierarchy.OneTable, _ => null,
                id => Outside(db, rdb, id)) { ColumnsOnly = true };
            var proposal = designer.Design(new HashSet<string>(StringComparer.Ordinal) { entity.Id });
            var shape = proposal.Tables.FirstOrDefault(t => t.Kind == DesignedTableKind.Entity);
            if (shape is null)
                return Refused($"Entity '{entity.Name}' has no table of its own in database '{db.Name}' to take the columns' shapes from.");
            var rentity = projected.Entities.First(e => e.Id == entity.Id);
            var mapped = entity.Bindings[index].Fields.Select(f => f.Attribute).ToHashSet(StringComparer.Ordinal);
            var candidates = shape.Columns
                .Select(c => (Column: c, Target: FieldTarget(rentity, c.AttributePath ?? c.Key, projected)))
                .Where(c => c.Target is not null)
                .ToList();
            var names = table.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // A column the table inherits from a stereotype (next-phase-2-design.md section 3.1) is not added again: an attribute whose
            // column would have its name maps to its keyed id instead (an own column of that name wins, as below).
            var inherited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in TableMembers.Of(snapshot, table).Columns)
            {
                if (!names.Contains(member.Member.Name))
                    inherited.TryAdd(member.Member.Name, member.Key);
            }

            var chosen = new List<(DesignedColumn Column, string Target)>();
            var toInherited = new List<(string Name, string Column, string Target)>();
            if (attributes is { Count: > 0 })
            {
                foreach (var target in attributes.Distinct(StringComparer.Ordinal))
                {
                    if (mapped.Contains(target))
                        return Refused($"'{target}' is already mapped in the binding of entity '{entity.Name}'.");
                    if (candidates.FirstOrDefault(c => c.Target == target) is not { Column: not null } candidate)
                        return Refused($"'{target}' is not an attribute of entity '{entity.Name}' that a column of table '{table.Name}' can hold.");
                    if (names.Contains(candidate.Column.Name))
                        return Refused($"Table '{table.Name}' already has a column named '{candidate.Column.Name}': map the attribute to it instead.");
                    if (inherited.TryGetValue(candidate.Column.Name, out var keyed))
                        toInherited.Add((candidate.Column.Name, keyed, target));
                    else
                        chosen.Add((candidate.Column, target));
                }
            }
            else
            {
                foreach (var (column, target) in candidates.Where(c => !mapped.Contains(c.Target!)))
                {
                    if (names.Contains(column.Name))
                        _notes.Add($"Table '{table.Name}' already has a column named '{column.Name}': attribute '{target}' of entity '{entity.Name}' is left for you to map to it.");
                    else if (inherited.TryGetValue(column.Name, out var keyed))
                        toInherited.Add((column.Name, keyed, target!));
                    else
                        chosen.Add((column, target!));
                }

                if (chosen.Count == 0 && toInherited.Count == 0)
                    return Refused($"Every attribute of entity '{entity.Name}' a column can hold is mapped, or table '{table.Name}' has its column already: nothing to add.");
            }

            var columns = new JsonArray();
            if (chosen.Count > 0)
            {
                var tableNode = Node(table.Id, BatchOp.Update, $"columns for {chosen.Count} {(chosen.Count == 1 ? "attribute" : "attributes")} of entity {entity.Name}");
                columns = tableNode["columns"] as JsonArray ?? [];
                tableNode["columns"] = columns;
            }

            var entityNode = Node(entity.Id, BatchOp.Update, chosen.Count > 0
                ? $"maps {(chosen.Count == 1 ? "its new column" : "the new columns")} of table {table.Name}"
                : $"maps {(toInherited.Count == 1 ? "an attribute to the column" : "attributes to the columns")} table {table.Name} inherits");
            var bindingNode = (JsonObject)entityNode["bindings"]![index]!;
            var fields = bindingNode["fields"] as JsonArray ?? [];
            bindingNode["fields"] = fields;
            var written = new List<string>();
            foreach (var (column, target) in chosen)
            {
                var columnId = ids.NewId();
                columns.Add(ColumnNode(db, table.Schema, column, columnId, conventions, typeMap));
                fields.Add(new JsonObject { ["attribute"] = target, ["column"] = columnId });
                written.Add(columnId);
                if (shape.ForeignKeys.Any(fk => fk.Columns.Contains(column.Key)))
                    _notes.Add($"Column '{column.Name}' of table '{table.Name}' holds a foreign key in the table its entity's own table would have: add the foreign key in the table if you want one.");
            }

            foreach (var (name, column, target) in toInherited)
            {
                fields.Add(new JsonObject { ["attribute"] = target, ["column"] = column });
                written.Add(column);
                _notes.Add($"Table '{table.Name}' inherits column '{name}' from a stereotype: attribute '{target}' of entity '{entity.Name}' maps to it ({column}), and no column is added.");
            }

            // The new columns are hashed from the entries this operation writes (decision 4).
            var shapes = chosen.Count > 0 ? ShapesOf(_work[table.Id].Node) : BindingDrift.ColumnsOf(snapshot, table);
            if (shapes is not null)
                RecordProvenance(bindingNode, shapes, written);
        }

        return Result();
    }

    /// <summary>A column as materialize reads it: its physical type is all it says (a table names no type of the model).</summary>
    private sealed record SourceColumn(string Key, string Name, string? Type, string? NativeType, int? Length, int? Precision, int? Scale, bool Nullable,
        bool IsKey, bool Identity);

    private static SourceColumn Source(RColumn c) =>
        new(c.Key, c.Name, c.Type, c.NativeType, c.Length, c.Precision, c.Scale, c.Nullable, c.IsPrimaryKey, c.Identity);

    private static SourceColumn Source(RViewColumn c) => new(c.Name, c.Name, c.Type, c.NativeType, null, null, null, c.Nullable, false, false);

    /// <summary>
    /// A column of a table file as materialize reads it, from the file alone (the binding operations never resolve the model): its own
    /// type and facets, nullable when it does not say; <paramref name="key"/> is its id or an inherited column's keyed id. A table file
    /// names no type of the model, so the column holds none here.
    /// </summary>
    private static SourceColumn Source(Column column, string key) =>
        new(key, column.Name, column.Type, column.NativeType, column.Length, column.Precision, column.Scale, column.Nullable ?? true, IsKey: false,
            column.Generated == ColumnGeneration.Identity);

    // ---- provenance (next-phase-2-design.md section 3.3, decision 4) --------------------------------------------------------------

    /// <summary>
    /// The columns of a table node this operation writes, as provenance sees them (<see cref="BindingDrift.ColumnsOf"/>: own entries in
    /// order, then inherited members), so the parts hash the entries it writes; <see langword="null"/> when the node does not read as a
    /// table.
    /// </summary>
    private IReadOnlyList<ShapeColumn>? ShapesOf(JsonObject tableNode)
    {
        try
        {
            return ElementReader.ReadElement(JsonElement.Parse(tableNode.ToJsonString(), DocumentReader.ParseOptions)) is Table table
                ? BindingDrift.ColumnsOf(snapshot, table)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records provenance on a binding node: with <paramref name="written"/> <see langword="null"/>, or when the binding has none (or one
    /// none of whose ids is a column of the source, which is read as none), every column of the source; otherwise the entries of the
    /// columns whose fields the operation writes, the others left as they are, so an operation never refreshes an entry it does not
    /// write; a written id the source no longer has loses its entry. The keys are written in ordinal order, as the canonical writer
    /// sorts them.
    /// </summary>
    /// <param name="binding">The binding node.</param>
    /// <param name="columns">The source's columns now.</param>
    /// <param name="written">The column ids (keyed ids for inherited columns) whose fields the operation writes.</param>
    private static void RecordProvenance(JsonObject binding, IReadOnlyList<ShapeColumn> columns, IReadOnlyCollection<string>? written)
    {
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in columns)
            byId.TryAdd(column.Id, column.Shape);
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (written is not null && binding["provenance"]?["columns"] is JsonObject recorded
            && (recorded.Count == 0 || recorded.Any(p => byId.ContainsKey(p.Key))))
        {
            foreach (var (id, value) in recorded)
            {
                if (value is JsonValue v && v.TryGetValue<string>(out var text))
                    map[id] = text;
            }

            foreach (var id in written)
            {
                if (byId.TryGetValue(id, out var shape))
                    map[id] = shape;
                else
                    map.Remove(id);
            }
        }
        else
        {
            foreach (var (id, shape) in byId)
                map[id] = shape;
        }

        var node = new JsonObject();
        foreach (var (id, shape) in map)
            node[id] = shape;
        binding["provenance"] = new JsonObject { ["columns"] = node };
    }

    /// <summary>
    /// The attribute a column becomes (materialize-entities, materialize-attributes): its name the column's camel-cased (made unique
    /// among <paramref name="taken"/>, which it joins), required when the column is not nullable; its type is the column's built-in
    /// keyword or the dialect's keyword for its native type (else string), with the column's length, precision and scale.
    /// </summary>
    private JsonObject AttributeFor(SourceColumn column, IReadOnlyDictionary<string, string> dialectMap, HashSet<string> taken)
    {
        var attribute = new JsonObject { ["id"] = ids.NewId(), ["name"] = AttributeName(column.Name, taken) };
        TypeAttribute(attribute, column, dialectMap);
        return attribute;
    }

    /// <summary>
    /// The name materialize gives the attribute of a column: the column's name camel-cased (<c>c</c> and the Pascal-cased name when that
    /// does not start with a letter or <c>_</c>), made unique among <paramref name="taken"/> with a number, and added to it.
    /// </summary>
    private static string AttributeName(string columnName, HashSet<string> taken)
    {
        var attributeName = Casing.Camel(columnName);
        if (attributeName.Length == 0 || !(char.IsAsciiLetter(attributeName[0]) || attributeName[0] == '_'))
            attributeName = "c" + Casing.Pascal(columnName);
        var unique = attributeName;
        for (var n = 2; !taken.Add(unique); n++)
            unique = attributeName + n.ToString(CultureInfo.InvariantCulture);
        return unique;
    }

    /// <summary>
    /// Types an attribute node as materialize types the attribute of a column: by its built-in keyword (or the dialect's keyword for its
    /// native type, else string) with its length, precision and scale; <c>required</c> when the column is not nullable. Keys it does not set are
    /// removed, so a re-sync can retype an attribute in place.
    /// </summary>
    private static void TypeAttribute(JsonObject attribute, SourceColumn column, IReadOnlyDictionary<string, string> dialectMap)
    {
        foreach (var facet in new[] { "length", "precision", "scale" })
            attribute.Remove(facet);
        var type = column.Type is not null && BuiltinTypes.All.Contains(column.Type) ? column.Type : Reverse(dialectMap, column.NativeType) ?? "string";
        attribute["type"] = type;
        if (column.Length is { } l && type is "string" or "binary")
            attribute["length"] = l;
        if (type == "decimal")
        {
            if (column.Precision is { } p)
                attribute["precision"] = p;
            if (column.Scale is { } s)
                attribute["scale"] = s;
        }

        Require(attribute, column);
    }

    /// <summary><c>required</c> follows the column: set when it is not nullable, else removed.</summary>
    private static void Require(JsonObject attribute, SourceColumn column)
    {
        if (!column.Nullable)
            attribute["required"] = true;
        else
            attribute.Remove("required");
    }

    // ---- materialize-attributes -------------------------------------------------------------------------------------------------

    /// <summary>The column statuses a column has when nothing in the binding names it (its own: none, identity, a default).</summary>
    private static readonly HashSet<string> Unnamed = new(["unaccounted", "identity", "default"], StringComparer.Ordinal);

    /// <summary>
    /// Plans <c>materialize-attributes</c>: for each entity, an attribute per column of the source of its binding to the database, mapped
    /// to it in the binding (an ignored listing of the column goes; filled by the database or computed stays, as it does beside a field). Without <paramref name="columns"/>, the
    /// columns nothing in the binding names (unaccounted, identity, a default); with them (one entity only), those columns, as their keys
    /// or physical names, whatever their listing. The entity's key stays as it is. Refused for
    /// an entity with no binding to the database, a query source, a column a field, a constant or the soft delete already uses, and
    /// when no column is left to add.
    /// </summary>
    /// <param name="databaseId">The database.</param>
    /// <param name="entityIds">The entities.</param>
    /// <param name="columns">The columns (one entity only), or <see langword="null"/> for every column nothing names.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan.</returns>
    public async Task<MaterializeResult> AttributesAsync(string databaseId, IReadOnlyList<string> entityIds, IReadOnlyList<string>? columns, CancellationToken ct)
    {
        if (snapshot.Get<Database>(databaseId) is not { } db)
            return Refused($"'{databaseId}' is not a database.");
        var selected = entityIds.Distinct(StringComparer.Ordinal).ToList();
        if (selected.Count == 0)
            return Refused("Name at least one entity to add attributes to.");
        if (columns is { Count: > 0 } && selected.Count > 1)
            return Refused("Name the columns for one entity at a time.");
        var resolved = await resolver.ResolveAsync(snapshot, null, ct).ConfigureAwait(false);
        var dialectMap = DialectTypeMaps.Effective(db.Dialect, snapshot.Settings);
        foreach (var id in selected)
        {
            if (snapshot.Get<Entity>(id) is not { } entity)
                return Refused($"'{id}' is not an entity.");
            var index = entity.Bindings.ToList().FindIndex(b => b.Database == db.Id);
            if (index < 0)
                return Refused($"Entity '{entity.Name}' has no binding to database '{db.Name}': add one first, or create its table (materialize-tables).");
            var rentity = resolved.Entities.First(e => e.Id == entity.Id);
            if (!rentity.Bindings.TryGetValue(db.Name, out var binding) || binding.SourceKind is null)
                return Refused($"The source of entity '{entity.Name}' in database '{db.Name}' does not resolve: pick a table or a view first.");
            if (binding.SourceKind == "query")
                return Refused($"Entity '{entity.Name}' reads query '{binding.SourceName}' in database '{db.Name}': add attributes for a query's fields by hand.");

            var chosen = new List<(RBindingColumn Bound, SourceColumn Column)>();
            SourceColumn Of(RBindingColumn c) => c.Column is { } column ? Source(column) : Source(binding.SourceView!.Columns.First(v => v.Name == c.Name));
            if (columns is { Count: > 0 })
            {
                foreach (var name in columns.Distinct(StringComparer.Ordinal))
                {
                    var bound = binding.Columns.FirstOrDefault(c => c.Column?.Key == name) ?? binding.Columns.FirstOrDefault(c => c.Name == name);
                    if (bound is null)
                        return Refused($"'{name}' is not a column of {binding.SourceKind} '{binding.SourceName}'.");
                    if (bound.Status is "field" or "constant" or "soft-delete")
                        return Refused($"Column '{bound.Name}' of {binding.SourceKind} '{binding.SourceName}' is already {(bound.Status == "field" ? "mapped" : bound.Status == "constant" ? "a constant" : "the soft delete column")} in the binding of entity '{entity.Name}'.");
                    chosen.Add((bound, Of(bound)));
                }
            }
            else
            {
                chosen.AddRange(binding.Columns.Where(c => Unnamed.Contains(c.Status)).Select(c => (c, Of(c))));
                if (chosen.Count == 0)
                    return Refused($"Every column of {binding.SourceKind} '{binding.SourceName}' is accounted for in the binding of entity '{entity.Name}': nothing to add.");
            }

            var node = Node(entity.Id, BatchOp.Update, $"attributes for {chosen.Count} {(chosen.Count == 1 ? "column" : "columns")} of {binding.SourceKind} {binding.SourceName}, mapped in its binding");
            var tableFile = BindingDrift.SourceOf(snapshot, entity.Bindings[index], out var sourceFile) == DriftSource.TableFile ? sourceFile : null;
            var attributes = node["attributes"] as JsonArray ?? [];
            node["attributes"] = attributes;
            var taken = new HashSet<string>(rentity.Attributes.Select(a => a.Name), StringComparer.Ordinal);
            var bindingNode = (JsonObject)node["bindings"]![index]!;
            var fields = bindingNode["fields"] as JsonArray ?? [];
            bindingNode["fields"] = fields;
            foreach (var (_, column) in chosen)
            {
                var attribute = AttributeFor(column, dialectMap, taken);
                var attributeId = attribute["id"]!.GetValue<string>();
                attributes.Add(attribute);
                fields.Add(new JsonObject { ["attribute"] = attributeId, ["column"] = column.Key });
                if (bindingNode["columns"] is JsonArray listed)
                {
                    foreach (var entry in listed.OfType<JsonObject>().Where(e => e["status"]?.GetValue<string>() == "ignored"
                        && e["column"]?.GetValue<string>() is { } c && (c == column.Key || c == column.Name)).ToList())
                        listed.Remove(entry);
                    if (listed.Count == 0)
                        bindingNode.Remove("columns");
                }
            }

            if (tableFile is not null)
                RecordProvenance(bindingNode, BindingDrift.ColumnsOf(snapshot, tableFile), [.. chosen.Select(c => c.Column.Key)]);
        }

        return Result();
    }

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

    private MaterializeResult Refused(string why, string? rule = null) => new(why, [], []) { RefusalRule = rule };

    private void Put(string id, string? hash, JsonObject? node, BatchOp op, string because)
    {
        if (!_work.ContainsKey(id))
            _order.Add(id);
        _work[id] = (hash, node ?? [], op, because);
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

        return new MaterializeResult(null, ops, [.. _notes], _databaseName) { Choices = _choices, Adopted = _adopted };
    }
}
