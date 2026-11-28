using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Loading;

/// <summary>The files <c>materialize-tables</c> writes from a proposal: table files, bindings and relation mappings.</summary>
internal sealed partial class Materializer
{
    private readonly Dictionary<string, string> _createdSequences = new(StringComparer.Ordinal);

    /// <summary>The name of a database's default schema: its own, else PostgreSQL <c>public</c>, SQL Server <c>dbo</c>.</summary>
    private static string? DefaultSchemaName(Database db) => db.DefaultSchema ?? db.Dialect switch
    {
        Dialect.PostgreSql => "public",
        Dialect.SqlServer => "dbo",
        _ => null,
    };

    /// <summary>
    /// The designed table a proposed table becomes: its own members with the proposal's shape, and the members its stereotypes contribute
    /// left inherited (next-phase-2-design.md section 3.1, decision 15). The table file holds no inherited column, key, index or check; a
    /// stereotype whose attributes the table holds as columns of its own gets an <c>excludes</c> entry for each of its members.
    /// </summary>
    private JsonObject TableNode(Database db, string? schemaId, DesignedTable t, Dictionary<DesignedTable, string> tableIds,
        Dictionary<DesignedTable, Dictionary<string, string>> columnIds, Dictionary<DesignedForeignKey, string> fkIds,
        EffectiveConventions conventions, IReadOnlyDictionary<string, string> typeMap, Dictionary<DesignedCheck, string> checkIds)
    {
        var tableId = tableIds[t];
        var columns = columnIds[t];
        var node = new JsonObject { ["kind"] = "table", ["id"] = tableId, ["name"] = t.Name, ["database"] = db.Id };
        var schema = SchemaOf(db, schemaId, t);
        if (schema is not null)
            node["schema"] = schema;
        var package = PackageOf(t);
        if (package is not null)
            node["package"] = package;
        if (t.Comment is { Length: > 0 } comment)
            node["comment"] = comment;
        ExcludeRealizedStereotypes(db, t, schema, package, node);

        var columnArray = new JsonArray();
        foreach (var column in t.Columns)
            columnArray.Add(ColumnNode(db, schema, column, columns[column.Key], conventions, typeMap));

        node["columns"] = columnArray;
        string Col(DesignedColumn column) => columns[column.Key];
        if (t.ResolvedPrimaryKey is { } pk)
        {
            var key = new JsonObject { ["name"] = pk.Name, ["columns"] = new JsonArray([.. pk.Columns.Select(c => (JsonNode)Col(c))]) };
            if (t.PrimaryKeyClustered is { } clustered)
                key["clustered"] = clustered;
            node["primaryKey"] = key;
        }

        node["uniques"] = new JsonArray([.. t.ResolvedUniques.Select(u =>
        {
            var x = new JsonObject { ["id"] = u.Id ?? ids.NewId(), ["name"] = u.Name, ["columns"] = new JsonArray([.. u.Columns.Select(c => (JsonNode)Col(c))]) };
            if (u.NullsNotDistinct)
                x["nullsNotDistinct"] = true;
            return (JsonNode)x;
        })]);

        var foreignKeys = new JsonArray();
        foreach (var fk in t.ForeignKeys.Where(f => f.Resolved))
        {
            var fkId = fk.Id ?? ids.NewId();
            fkIds[fk] = fkId;
            var target = fk.Target!;
            string referencesTable;
            IEnumerable<string> referencesColumns;
            if (target.Designed is { } designed)
            {
                referencesTable = tableIds[designed];
                referencesColumns = fk.ReferencedKeys.Select(k => columnIds[designed].GetValueOrDefault(k, k));
            }
            else
            {
                referencesTable = target.ExistingKey!;
                referencesColumns = fk.ReferencedKeys;
            }

            var fkNode = new JsonObject
            {
                ["id"] = fkId,
                ["name"] = fk.Name,
                ["columns"] = new JsonArray([.. fk.ResolvedColumns.Select(c => (JsonNode)Col(c))]),
                ["referencesTable"] = referencesTable,
                ["referencesColumns"] = new JsonArray([.. referencesColumns.Select(c => (JsonNode)c)]),
                ["onDelete"] = fk.OnDelete,
                ["onUpdate"] = fk.OnUpdate,
            };
            if (fk.OnDelete is "set-null" or "set-default")
            {
                var listed = fk.OnDeleteColumns.Select(t.Resolve).Where(c => c is not null && fk.ResolvedColumns.Contains(c)).Distinct().Select(c => (JsonNode)Col(c!)).ToList();
                if (listed.Count > 0)
                    fkNode["onDeleteColumns"] = new JsonArray([.. listed]);
            }

            if (fk.Deferrable != "not-deferrable")
                fkNode["deferrable"] = fk.Deferrable;
            foreignKeys.Add(fkNode);
        }

        node["foreignKeys"] = foreignKeys;
        var checks = new JsonArray();
        foreach (var check in t.Checks)
        {
            var checkId = ids.NewId();
            checkIds[check] = checkId;
            checks.Add(new JsonObject
            {
                ["id"] = checkId, ["name"] = check.Name, ["expression"] = new JsonObject { [DialectTypeMaps.Name(db.Dialect)] = check.Expression },
                ["column"] = columns[check.Column],
            });
        }

        node["checks"] = checks;
        JsonNode IndexColumnNode(DesignedColumn? column, DesignedIndexColumn spec)
        {
            var x = column is not null
                ? new JsonObject { ["column"] = Col(column) }
                : new JsonObject { ["expression"] = new JsonObject { [DialectTypeMaps.Name(db.Dialect)] = spec.Expression } };
            x["descending"] = spec.Descending;
            if (spec.Length is { } length)
                x["length"] = length;
            return x;
        }

        node["indexes"] = new JsonArray([.. t.ResolvedIndexes.Select(index =>
        {
            var x = new JsonObject
            {
                ["id"] = index.Index.Id ?? ids.NewId(),
                ["name"] = index.Name,
                ["columns"] = new JsonArray([.. index.Columns.Select(c => IndexColumnNode(c.Column, c.Spec))]),
                ["include"] = new JsonArray([.. index.Include.Select(c => (JsonNode)Col(c))]),
                ["unique"] = index.Index.Unique,
                ["method"] = index.Index.Method,
            };
            if (index.Index.Where is { } where)
                x["where"] = where;
            return (JsonNode)x;
        })]);
        return node;
    }

    /// <summary>The schema a proposed table goes to: the request's (a lookup table keeps its own), else the table's, else the database's default.</summary>
    private static string? SchemaOf(Database db, string? schemaId, DesignedTable t) =>
        (t.Kind == DesignedTableKind.Lookup ? null : schemaId) ?? t.SchemaId ?? db.Schemas.FirstOrDefault(s => s.Name == DefaultSchemaName(db))?.Id;

    /// <summary>
    /// The package a proposed table's file names: its entity's (or relation's) when that package's chain gives tables a default stereotype,
    /// so the designed table takes the defaults; else none.
    /// </summary>
    private string? PackageOf(DesignedTable t) =>
        t.PackageId is { } owning && snapshot.EffectiveStereotypes(new StereotypeHolder(ElementKind.Table, [], [], owning, null)).Count > 0 ? owning : null;

    /// <summary>The holder a proposed table's file will have: its package and schema.</summary>
    private StereotypeHolder HolderOf(Database db, string? schemaId, DesignedTable t) =>
        new(ElementKind.Table, [], [], PackageOf(t), snapshot.StereotypeDefaults.SchemaOf(db.Id, SchemaOf(db, schemaId, t)));

    /// <summary>
    /// The columns a proposed table inherits from its stereotypes: each member's key under the proposal's key and under the table's id
    /// (next-phase-2-design.md section 3.1, decision 15), with the member.
    /// </summary>
    private IEnumerable<(string From, string To, Column Member)> InheritedColumns(Database db, string? schemaId, DesignedTable t, string tableId)
    {
        foreach (var member in MembersOf(db, schemaId, t)?.Columns ?? [])
            yield return (member.Key, ContributedMembers.KeyOf(tableId, member.MemberId), member.Member);
    }

    /// <summary>The members a proposed table inherits from its stereotypes, or <see langword="null"/> when none reaches it.</summary>
    private TableMemberSet? MembersOf(Database db, string? schemaId, DesignedTable t)
    {
        var effective = snapshot.EffectiveStereotypes(HolderOf(db, schemaId, t));
        if (effective.Count == 0)
            return null;
        var skip = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in t.Columns)
        {
            if (column.Attribute?.FromStereotype is { } from)
                skip.Add(from.Key);
        }

        return TableMembers.Of(snapshot, t.Key, effective, [], [], skip);
    }

    /// <summary>
    /// Leaves out an index an indexed attribute gives the table when an index the table inherits from a stereotype covers the same
    /// columns (an inherited index counts as a declared one): the designed table would otherwise hold two indexes of one name.
    /// </summary>
    private void DropIndexesInheritedOnes(Database db, string? schemaId, DesignedTable t)
    {
        if (MembersOf(db, schemaId, t) is not { Indexes.Count: > 0 } set)
            return;
        string? NameOf(string key) => t.Resolve(key)?.Name ?? set.Columns.FirstOrDefault(c => c.Key == key)?.Member.Name;
        var inherited = set.Indexes
            .Select(m => m.Member.Columns.Select(c => c.Column is { } key ? NameOf(key) : null).ToList())
            .Where(names => names.Count > 0 && names.All(n => n is not null))
            .Select(names => string.Join('_', names))
            .ToHashSet(StringComparer.Ordinal);
        t.ResolvedIndexes.RemoveAll(x => !x.Index.FromFile && x.Columns.All(c => c.Column is not null)
            && inherited.Contains(string.Join('_', x.Columns.Select(c => c.Column!.Name))));
    }

    /// <summary>
    /// Excludes every member of a stereotype the table realizes through attributes it stores as columns of its own (decision 1), when that
    /// stereotype reaches the designed table.
    /// </summary>
    private void ExcludeRealizedStereotypes(Database db, DesignedTable t, string? schema, string? package, JsonObject node)
    {
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in t.Columns)
        {
            if (column.Attribute?.FromStereotype is { } from)
                skipped.Add(from.Key);
        }

        if (skipped.Count == 0)
            return;
        var excludes = new SortedSet<string>(StringComparer.Ordinal);
        var holder = new StereotypeHolder(ElementKind.Table, [], [], package, snapshot.StereotypeDefaults.SchemaOf(db.Id, schema));
        foreach (var applied in snapshot.EffectiveStereotypes(holder))
        {
            if (!skipped.Contains(applied.Stereotype.Key))
                continue;
            foreach (var id in MemberIds(applied.Stereotype))
                excludes.Add(id);
        }

        if (excludes.Count > 0)
            node["excludes"] = new JsonArray([.. excludes.Select(e => (JsonNode)e)]);
    }

    /// <summary>
    /// A designed table's column with the shape the proposal gives it: its type and facets, the native type when it is not the one the
    /// dialect map renders (a scalar type's, or the referenced column's for a foreign key column), nullability, defaults (an attribute's
    /// default expression among them, as SQL for the dialect), identity or a key sequence (written as a sequence file), computed,
    /// collation and comment. The attribute's sensitivity is not written: the attribute keeps saying it, and the bound column shows it.
    /// </summary>
    private JsonObject ColumnNode(Database db, string? schema, DesignedColumn column, string columnId, EffectiveConventions conventions,
        IReadOnlyDictionary<string, string> typeMap)
    {
        var dialect = DialectTypeMaps.Name(db.Dialect);
        var c = new JsonObject { ["id"] = columnId, ["name"] = column.Name };
        var type = column.Type;
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
        if (column.NativeType is { } computedNative && !string.Equals(computedNative, rendered, StringComparison.Ordinal))
            c["nativeType"] = computedNative;
        if (!column.Nullable)
            c["nullable"] = false;
        if (column.Default is { } value)
            c["default"] = JsonSerializer.SerializeToNode(value);
        if (column.DefaultSql is { } sql)
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
            c["sequence"] = CreateSequence(db, sequence, schema);
        }

        if (column.Computed is { } computed)
        {
            c["computed"] = computed;
            if (column.ComputedStored)
                c["computedStored"] = true;
        }

        if (column.StableId)
            c["stableId"] = true;
        if (column.Collation is { } collation)
            c["collation"] = collation;
        if (column.Comment is { Length: > 0 } columnComment)
            c["comment"] = columnComment;
        return c;
    }

    /// <summary>A sequence file for a key sequence the proposal gives a hierarchy's key, written once.</summary>
    private string CreateSequence(Database db, DesignedSequence sequence, string? schema)
    {
        if (_createdSequences.TryGetValue(sequence.Key, out var made))
            return made;
        var id = Reused(sequence.Key, "sequence") ?? ids.NewId();
        _createdSequences[sequence.Key] = id;
        var node = new JsonObject { ["kind"] = "sequence", ["id"] = id, ["name"] = sequence.Name, ["database"] = db.Id, ["type"] = sequence.Type };
        if (schema is not null)
            node["schema"] = schema;
        Put(id, null, node, BatchOp.Create, $"the key sequence {sequence.Name} as a sequence of its own");
        return id;
    }

    /// <summary>
    /// The binding of a materialized entity: every column of its table accounted for, write and delete by default. One table for a
    /// hierarchy: the root's binding names the type column; the columns of the other entities in the table are listed as ignored. A table per type: the derived binding names its base foreign key and maps only
    /// the entity's own attributes. Each attribute stored in a table of its own gets a <c>childTables</c> entry.
    /// </summary>
    private JsonObject BindingFor(Database db, DesignedEntity e, DesignedTable table, Dictionary<DesignedTable, string> tableIds,
        Dictionary<DesignedTable, Dictionary<string, string>> columnIds, Dictionary<DesignedForeignKey, string> fkIds, ResolvedModel resolved,
        List<(string From, string To, Column Member)> inherited)
    {
        var columns = columnIds[table];
        var fields = new JsonArray();
        var listed = new JsonArray();
        var rentity = e.Entity;
        var baseKeys = e.Shape == DesignHierarchy.PerType && e.BaseForeignKey is { } baseFk ? baseFk.Columns.ToHashSet(StringComparer.Ordinal) : [];
        var shared = e.Shape == DesignHierarchy.OneTable;
        foreach (var column in table.Columns)
        {
            if (ReferenceEquals(column, e.Discriminator) || baseKeys.Contains(column.Key))
                continue;
            var target = FieldTarget(rentity, column.AttributePath ?? column.Key, resolved);
            if (target is not null)
            {
                fields.Add(new JsonObject { ["attribute"] = target, ["column"] = columns[column.Key] });
                continue;
            }

            if (column.Identity || column.Computed is not null || column.Default is not null || column.DefaultSql is not null)
                continue;
            listed.Add(new JsonObject { ["column"] = columns[column.Key], ["status"] = "ignored" });
            if (!shared || column.Attribute is null)
                _notes.Add($"Column '{column.Name}' of table '{table.Name}' maps to no attribute of entity '{rentity.Name}' a binding can hold: it is listed as ignored.");
        }

        // A column the table inherits from a stereotype fills itself or is listed as ignored.
        var dialect = DialectTypeMaps.Name(db.Dialect);
        foreach (var (_, to, member) in inherited)
        {
            if (member.Generated == ColumnGeneration.Identity || member.Computed is not null || member.Default is not null
                || member.DefaultSql.ContainsKey(dialect) || member.DefaultSql.ContainsKey("*"))
                continue;
            listed.Add(new JsonObject { ["column"] = to, ["status"] = "ignored" });
            _notes.Add($"Column '{member.Name}' of table '{table.Name}' maps to no attribute of entity '{rentity.Name}' a binding can hold: it is listed as ignored.");
        }

        var binding = new JsonObject { ["id"] = ids.NewId(), ["database"] = db.Id, ["source"] = tableIds[table] };
        if (shared && ReferenceEquals(e.Root, e) && e.Discriminator is { } discriminator)
            binding["discriminatorColumn"] = columns[discriminator.Key];
        if (e.Shape == DesignHierarchy.PerType && e.BaseForeignKey is { } foreignKey && fkIds.TryGetValue(foreignKey, out var fkId))
            binding["baseForeignKey"] = fkId;
        binding["fields"] = fields;
        if (listed.Count > 0)
            binding["columns"] = listed;

        var childTables = new JsonArray();
        foreach (var child in table.Children)
        {
            if (child.Attribute is not { } attribute || child.Relation is not null)
                continue;
            if (!rentity.Attributes.Any(a => a.Id == attribute.Id))
                continue;
            if (child.ForeignKeys.FirstOrDefault(f => f.IsOwner && f.Resolved) is not { } owner || !fkIds.TryGetValue(owner, out var ownerFkId))
                continue;
            var childColumns = columnIds[child];
            var ownerKeys = owner.Columns.ToHashSet(StringComparer.Ordinal);
            var entry = new JsonObject
            {
                ["id"] = ids.NewId(), ["attribute"] = attribute.Id, ["table"] = tableIds[child], ["ownerForeignKey"] = ownerFkId,
            };
            var childFields = new JsonArray();
            var childListed = new JsonArray();
            string? order = null;
            foreach (var column in child.Columns)
            {
                if (ownerKeys.Contains(column.Key))
                    continue;
                if (child.IsList && !child.IsReferenceList && column.Key == "position")
                {
                    order = childColumns[column.Key];
                    continue;
                }

                if (column.AttributePath is { } path)
                {
                    childFields.Add(new JsonObject { ["attribute"] = path, ["column"] = childColumns[column.Key] });
                    continue;
                }

                if (column.Identity || column.Computed is not null || column.Default is not null || column.DefaultSql is not null)
                    continue;
                childListed.Add(new JsonObject { ["column"] = childColumns[column.Key], ["status"] = "ignored" });
            }

            entry["fields"] = childFields;
            if (order is not null)
                entry["orderColumn"] = order;
            if (childListed.Count > 0)
                entry["columns"] = childListed;
            childTables.Add(entry);
        }

        if (childTables.Count > 0)
            binding["childTables"] = childTables;
        return binding;
    }

    /// <summary>What a binding field names for a proposed column: an attribute (a list in a JSON column included), a member to any depth,
    /// or a to-one end; null when none fits.</summary>
    private static string? FieldTarget(REntity entity, string path, ResolvedModel resolved)
    {
        var segments = path.Split('.');
        if (entity.Attributes.FirstOrDefault(a => a.Id == segments[0]) is { } attribute)
        {
            if (segments.Length == 1)
                return path;
            if (attribute.Collection)
                return null;
            // A member of an embedded value object, to any depth; one that is itself a value object or a list is a JSON column
            // (retire-projection-design.md section 6).
            var current = attribute;
            for (var i = 1; i < segments.Length; i++)
            {
                if (current.Collection || current.Type.ValueObject is not { } vo || vo.Attributes.FirstOrDefault(m => m.Id == segments[i]) is not { } member)
                    return null;
                current = member;
            }

            return path;
        }

        if (segments.Length == 2)
        {
            var end = EndsById.GetValue(resolved, IndexEnds).GetValueOrDefault(segments[0]);
            if (end is { IsMany: false, Opposite.Entity: { } opposite, Entity.Key: { Attributes.Count: 1 } key } && IsOrDerives(entity, opposite) && key.Attributes[0].Id == segments[1])
                return end.Id;
        }

        return null;
    }

    /// <summary>
    /// The ends of a resolved model's relations by id, the first in relation order winning (what a scan of every end found): a binding field per
    /// foreign key column looked its end up by scanning every end of every relation, which made Create tables quadratic in the model.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ResolvedModel, Dictionary<string, REnd>> EndsById = new();

    private static Dictionary<string, REnd> IndexEnds(ResolvedModel resolved)
    {
        var ends = new Dictionary<string, REnd>(StringComparer.Ordinal);
        foreach (var relation in resolved.Relations)
        {
            foreach (var end in relation.Ends)
                ends.TryAdd(end.Id, end);
        }

        return ends;
    }

    /// <summary>Whether an entity is another or derives from it.</summary>
    private static bool IsOrDerives(REntity entity, REntity other)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var e = entity; e is not null && seen.Add(e.Id); e = e.Base)
        {
            if (e.Id == other.Id)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The relation mapping of a relation the proposal stores: a foreign key shape names its foreign key (and each relation attribute's
    /// column on its table); a junction names its table, each end's foreign key and each relation attribute's column. An existing mapping
    /// of the relation to the database is updated, never duplicated.
    /// </summary>
    private void WriteRelationMapping(Database db, DesignedRelation stored, List<Mapping> mappings, Dictionary<DesignedTable, string> tableIds,
        Dictionary<DesignedTable, Dictionary<string, string>> columnIds, Dictionary<DesignedForeignKey, string> fkIds)
    {
        var relation = stored.Relation;
        var attributes = new JsonArray();
        foreach (var group in stored.AttributeColumns.GroupBy(c => c.Path.Split('.')[0], StringComparer.Ordinal))
        {
            var list = group.ToList();
            // A list kept as JSON is a column of the junction a relation mapping does not name (it maps single values only).
            if (relation.Attributes.FirstOrDefault(a => a.Id == group.Key) is { Collection: true })
                continue;
            if (list.Count != 1 || list[0].Path.Contains('.', StringComparison.Ordinal))
            {
                _notes.Add($"Attribute '{relation.Attributes.FirstOrDefault(a => a.Id == group.Key)?.Name}' of relation '{relation.Name}' is stored in several columns, which a relation mapping cannot name: map it by hand.");
                continue;
            }

            attributes.Add(new JsonObject { ["attribute"] = group.Key, ["column"] = columnIds[list[0].Table][list[0].Column.Key] });
        }

        var existing = mappings.FirstOrDefault(m => m.Relation == relation.Id);
        JsonObject node;
        if (stored.Junction is { } junction)
        {
            var ends = new JsonArray();
            foreach (var end in relation.Ends)
            {
                if (stored.EndKeys.TryGetValue(end.Id, out var key) && fkIds.TryGetValue(key, out var fkId))
                    ends.Add(new JsonObject { ["end"] = end.Id, ["foreignKey"] = fkId });
            }

            node = MappingNode(db, relation, existing, $"relation {relation.Name} names its junction table {junction.Name}");
            node.Remove("shape");
            node.Remove("foreignKey");
            node.Remove("foreignKeyEnd");
            node["junctionTable"] = tableIds[junction];
            node["ends"] = ends;
        }
        else
        {
            var keys = stored.ForeignKeys.Where(fk => fkIds.ContainsKey(fk)).ToList();
            if (keys.Count > 1)
            {
                // A table per concrete entity: each table holds the key; the mapping names the one of the end's own entity, and the keys
                // of the derived entities' tables stay in their tables.
                var own = keys.Where(fk => fk.Host?.Entity?.Id == fk.End?.Entity?.Id).ToList();
                _notes.Add($"Relation '{relation.Name}' has a foreign key in each of {keys.Count} tables (a table per concrete entity): its mapping names "
                    + (own.Count == 1 ? $"the one of table '{own[0].Host!.Name}'" : "none") + ", and the others stay in their tables.");
                keys = own;
            }

            if (keys.Count != 1)
                return;

            node = MappingNode(db, relation, existing, $"relation {relation.Name} names its foreign key in the designed table");
            node.Remove("shape");
            node["foreignKey"] = fkIds[keys[0]];
        }

        if (attributes.Count > 0)
        {
            var kept = new JsonArray();
            if (node["attributes"] is JsonArray old)
            {
                foreach (var entry in old.OfType<JsonObject>())
                {
                    if (entry["attribute"]?.GetValue<string>() is { } id && attributes.OfType<JsonObject>().Any(a => a["attribute"]!.GetValue<string>() == id))
                        continue;
                    kept.Add(entry.DeepClone());
                }
            }

            foreach (var entry in attributes)
                kept.Add(entry!.DeepClone());
            node["attributes"] = kept;
        }
    }

    /// <summary>
    /// Binds a reference type to the lookup table proposed for it (reference-data-and-seeds.md section 10): its code, label, row
    /// descriptions and fields map to the table's columns, so the type's rows go into that table and the stored columns' foreign keys
    /// point at its code column.
    /// </summary>
    private void BindReferenceType(Database db, DesignedTable lookup, string tableId, Dictionary<string, string> columns)
    {
        var type = lookup.ReferenceType!;
        var node = Node(type.Id, BatchOp.Update, $"binds reference type {type.Name} to its lookup table {lookup.Name}");
        var bindings = node["bindings"] as JsonArray ?? [];
        node["bindings"] = bindings;
        bindings.Add(new JsonObject
        {
            ["id"] = ids.NewId(), ["database"] = db.Id, ["table"] = tableId,
            ["fields"] = new JsonArray([.. lookup.LookupFields.Select(f => (JsonNode)new JsonObject { ["attribute"] = f.Field, ["column"] = columns[f.Column] })]),
        });
        KeepLookupRowsConverging(db, type);
    }

    /// <summary>
    /// The sql-ddl pack kept a lookup table's rows as the type's seeds have them on every run (upserted by code, retired codes deleted),
    /// whatever the seeds' <c>apply</c> said; bound to its table, the type's rows follow its seeds' <c>apply</c> and <c>delete</c>, like any
    /// seed rows. So the type's seeds converge, and a single seed also deletes what it no longer holds: storing the table changes no row
    /// on a later run. A seed the type has in another database's bound table keeps its own behavior there, so it is left as it is (noted).
    /// </summary>
    private void KeepLookupRowsConverging(Database db, RReferenceType type)
    {
        var seeds = snapshot.All<Seed>().Where(s => string.Equals(s.Target, type.Id, StringComparison.Ordinal)).OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        if (seeds.Count == 0)
            return;
        if (type.Bindings.Keys.Any(name => !string.Equals(name, db.Name, StringComparison.Ordinal)))
        {
            _notes.Add($"Reference type '{type.Name}' is bound to a table of another database, where its seeds' apply decides how its rows are written: its seeds are left as they are, so in database '{db.Name}' its rows follow them too (the lookup table's rows were upserted by code before).");
            return;
        }

        foreach (var seed in seeds)
        {
            var delete = seeds.Count == 1;
            if (seed.Apply == SeedApply.Converge && seed.Delete == delete)
                continue;
            var node = Node(seed.Id, BatchOp.Update, $"keeps the rows of reference type {type.Name} as its seeds have them, as its lookup table's rows were");
            node["apply"] = "converge";
            if (delete)
                node["delete"] = true;
        }

        if (seeds.Count > 1)
            _notes.Add($"Reference type '{type.Name}' has {seeds.Count} seeds: each converges, but none deletes the codes it does not hold (the others' rows), so a retired code stays in its lookup table until you delete it.");
    }

    private JsonObject MappingNode(Database db, RRelation relation, Mapping? existing, string because)
    {
        if (existing is not null)
            return Node(existing.Id, BatchOp.Update, because);
        var id = ids.NewId();
        var node = new JsonObject { ["kind"] = "mapping", ["id"] = id, ["name"] = relation.Name + " in " + db.Name, ["database"] = db.Id, ["relation"] = relation.Id };
        Put(id, null, node, BatchOp.Create, because);
        return node;
    }

}
