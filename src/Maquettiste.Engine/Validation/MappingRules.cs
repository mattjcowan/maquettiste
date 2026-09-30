using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// Mapping rules: MQ4004 (two mappings for one target), MQ4009 (option invalid for the element), MQ4011 (bound relations),
/// MQ4012 (an entity in no database), MQ4013 (convention packages a database does not use).
/// </summary>
internal static class MappingRules
{
    /// <summary>MQ4012: an entity that no database holds, when the model has a database (a conceptual-only model is fine).</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="report">The report.</param>
    public static void CheckEntityPlacement(ValidationContext context, Entity entity, Report report)
    {
        var any = false;
        foreach (var database in context.Model.All<Database>())
        {
            any = true;
            var mappings = context.MappingsOf(database.Id, entity.Id);
            var mapping = mappings.Length > 0 ? mappings[0].Element as Mapping : null;
            if (Resolution.DatabaseScope.Places(context.Model, database, entity.Package, mapping))
                return;
        }

        if (any)
        {
            report.Add("MQ4012",
                $"'{entity.Name}' lands in no database: no database takes its domain by convention and no mapping names it; map it from the entity's or the domain's menu (Map to database).",
                "");
        }
    }

    /// <summary>MQ4013: a database lists packages although <c>byConvention</c> is <c>all</c> or <c>none</c>, so the list is not used.</summary>
    /// <param name="database">The database.</param>
    /// <param name="report">The report.</param>
    public static void CheckConventionPackages(Database database, Report report)
    {
        if (database.Packages.Count > 0 && database.ByConvention is ConventionMapping.All or ConventionMapping.None)
        {
            var value = database.ByConvention == ConventionMapping.All ? "all" : "none";
            report.Add("MQ4013",
                $"byConvention is '{value}', so the packages listed here are not used; set byConvention to 'packages' or clear the list.",
                "/packages");
        }

        for (var i = 0; i < database.Packages.Count; i++)
        {
            if (database.Packages[i].Schema is { } schema && !database.Schemas.Any(s => string.Equals(s.Id, schema, StringComparison.Ordinal)))
            {
                report.Add("MQ4014",
                    $"The convention entry for package '{database.Packages[i].Package}' names schema '{schema}', which this database does not declare; add it to schemas or pick one of them.",
                    Ptr.At("/packages", i) + "/schema");
            }
        }
    }

    /// <summary>MQ4014 and MQ4009 for a mapping's <c>schema</c>: an entity mapping may name a schema of its own database.</summary>
    private static void CheckMappingSchema(ValidationContext context, Mapping mapping, Report report)
    {
        if (mapping.Schema is not { } schema)
            return;
        if (mapping.Entity is null)
        {
            report.Add("MQ4009", "Only an entity mapping places a table in a schema.", "/schema");
            return;
        }

        if (context.Model.Get<Database>(mapping.Database) is { } database && !database.Schemas.Any(s => string.Equals(s.Id, schema, StringComparison.Ordinal)))
            report.Add("MQ4014", $"Schema '{schema}' is not declared in database '{database.Name}'; add it to the database's schemas or pick one of them.", "/schema");
    }

    /// <summary>Checks a mapping file.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="mapping">The mapping.</param>
    /// <param name="report">The report.</param>
    public static void CheckMapping(ValidationContext context, Mapping mapping, Report report)
    {
        CheckMappingSchema(context, mapping, report);
        if (mapping.Entity is not null && mapping.Relation is not null)
        {
            report.Add("MQ4009", "A mapping names either an entity or a relation, not both.", "/relation");
            return;
        }

        var targetId = mapping.Entity ?? mapping.Relation;
        if (targetId is null)
        {
            report.Add("MQ4009", "A mapping names an entity or a relation.", "");
            return;
        }

        var targetPointer = mapping.Entity is not null ? "/entity" : "/relation";
        var mappings = context.MappingsOf(mapping.Database, targetId);
        if (mappings.Length > 1 && mappings[0] != report.Document)
        {
            var targetName = context.Model.GetDocument(targetId)?.Element.Name ?? targetId;
            report.Add("MQ4004", $"'{targetName}' is already mapped to this database by {mappings[0].Path}; one mapping per element and database.", targetPointer);
        }

        if (mapping.Entity is not null)
            CheckEntityMapping(context, mapping, report);
        else
            CheckRelationMapping(context, mapping, report);
    }

    private static void Invalid(Report report, string pointer, string message) => report.Add("MQ4009", message, pointer);

    private static void CheckEntityMapping(ValidationContext context, Mapping mapping, Report report)
    {
        var model = context.Model;
        const string relationOnly = "applies to relation mappings only";
        if (mapping.Shape is not null)
            Invalid(report, "/shape", "'shape' " + relationOnly + ".");
        if (mapping.ForeignKeyEnd is not null)
            Invalid(report, "/foreignKeyEnd", "'foreignKeyEnd' " + relationOnly + ".");
        if (mapping.ForeignKey is not null)
            Invalid(report, "/foreignKey", "'foreignKey' " + relationOnly + ".");
        if (mapping.JunctionTable is not null)
            Invalid(report, "/junctionTable", "'junctionTable' " + relationOnly + ".");
        if (mapping.Ends.Count > 0)
            Invalid(report, "/ends", "'ends' " + relationOnly + ".");
        if (mapping.PromotedName is not null)
            Invalid(report, "/promotedName", "'promotedName' " + relationOnly + ".");

        if (model.Get<Entity>(mapping.Entity!) is not { } entity)
            return; // MQ2001 or MQ2002

        Table? bound = null;
        if (mapping.Table is { } tableId && model.Get<Table>(tableId) is { } table)
        {
            if (table.Origin == TableOrigin.Synthesized)
                Invalid(report, "/table", $"'table' binds to '{table.Name}', a synthesized table's overlay; bind to a designed or imported table.");
            else if (table.Database != mapping.Database)
                Invalid(report, "/table", $"'table' binds to '{table.Name}', which belongs to another database.");
            else
                bound = table;
        }

        if (mapping.Inheritance is not null && entity.Base is not null)
            Invalid(report, "/inheritance", $"Entity '{entity.Name}' derives from another entity; the inheritance strategy is set on the hierarchy root only.");
        if (mapping.DiscriminatorValue is not null && entity.Base is null && !context.HasDerived(entity.Id))
            Invalid(report, "/discriminatorValue", $"Entity '{entity.Name}' is not part of an inheritance hierarchy, so it has no discriminator.");

        var flat = context.Flatten(entity);
        for (var i = 0; i < mapping.Attributes.Count; i++)
        {
            var item = mapping.Attributes[i];
            var pointer = Ptr.At("/attributes", i);
            var attribute = flat.FirstOrDefault(a => a.Attribute.Id == item.Attribute)?.Attribute;
            if (attribute is null)
            {
                if (model.TryGetEntry(item.Attribute, out _))
                    Invalid(report, pointer + "/attribute", $"'{item.Attribute}' is not an attribute of entity '{entity.Name}'.");
                continue;
            }

            CheckAttributeMapping(context, item, attribute, bound, pointer, report);
        }
    }

    private static void CheckAttributeMapping(ValidationContext context, AttributeMapping item, ModelAttribute attribute, Table? bound, string pointer, Report report)
    {
        var type = AttributeRules.Resolve(context.Model, attribute.Type);
        if (item.Storage is { } storage)
        {
            StorageKind[] allowed = type.Enum is not null && !attribute.Collection ? [StorageKind.Int, StorageKind.String]
                : attribute.Collection ? [StorageKind.Table, StorageKind.Json]
                : type.ValueObject is not null ? [StorageKind.Embedded, StorageKind.Table, StorageKind.Json]
                : [];
            if (!allowed.Contains(storage))
            {
                var options = allowed.Length == 0 ? "none" : string.Join(", ", allowed.Select(Kebab));
                Invalid(report, pointer + "/storage", $"Storage '{Kebab(storage)}' does not fit attribute '{attribute.Name}' ({type.Describe()}{(attribute.Collection ? ", collection" : "")}); valid: {options}.");
            }
        }

        if (item.Prefix is not null && type.ValueObject is null)
            Invalid(report, pointer + "/prefix", $"'prefix' applies to embedded value objects; attribute '{attribute.Name}' is {type.Describe()}.");

        if (item.Column is { } columnId)
        {
            if (bound is null)
                Invalid(report, pointer + "/column", "'column' needs the mapping to bind a designed or imported 'table'.");
            else if (!bound.Columns.Any(c => c.Id == columnId))
                Invalid(report, pointer + "/column", $"'{columnId}' is not a column of the bound table '{bound.Name}'.");
        }
    }

    private static void CheckRelationMapping(ValidationContext context, Mapping mapping, Report report)
    {
        var model = context.Model;
        const string entityOnly = "applies to entity mappings only";
        if (mapping.Table is not null)
            Invalid(report, "/table", "'table' " + entityOnly + "; a relation binds a designed junction with 'junctionTable'.");
        if (mapping.Inheritance is not null)
            Invalid(report, "/inheritance", "'inheritance' " + entityOnly + ".");
        if (mapping.DiscriminatorValue is not null)
            Invalid(report, "/discriminatorValue", "'discriminatorValue' " + entityOnly + ".");

        if (model.Get<Relation>(mapping.Relation!) is not { } relation)
            return;

        for (var i = 0; i < mapping.Attributes.Count; i++)
        {
            var item = mapping.Attributes[i];
            var attribute = relation.Attributes.FirstOrDefault(a => a.Id == item.Attribute);
            if (attribute is null)
            {
                if (model.TryGetEntry(item.Attribute, out _))
                    Invalid(report, Ptr.At("/attributes", i) + "/attribute", $"'{item.Attribute}' is not an attribute of relation '{relation.Name}'.");
                continue;
            }

            CheckAttributeMapping(context, item, attribute, null, Ptr.At("/attributes", i), report);
        }

        var binary = relation.Ends.Count == 2;
        var manyToMany = binary && relation.Ends.All(e => e.Max == MaxCardinality.Many);
        if (mapping.Shape == RelationShape.ForeignKey && (!binary || manyToMany))
            Invalid(report, "/shape", $"Relation '{relation.Name}' is {(binary ? "many to many" : "n-ary")} and cannot be a foreign key.");
        if (mapping.Shape == RelationShape.Promoted && !binary)
            Invalid(report, "/shape", $"Only a binary relation can be promoted; '{relation.Name}' has more ends.");
        if (mapping.PromotedName is not null && mapping.Shape != RelationShape.Promoted)
            Invalid(report, "/promotedName", "'promotedName' needs 'shape': 'promoted'.");

        if (mapping.ForeignKeyEnd is { } fkEnd)
        {
            if (!relation.Ends.Any(e => e.Id == fkEnd))
                Invalid(report, "/foreignKeyEnd", $"'{fkEnd}' is not an end of relation '{relation.Name}'.");
            else if (!binary || relation.Ends.Any(e => e.Max != MaxCardinality.One))
                Invalid(report, "/foreignKeyEnd", "'foreignKeyEnd' breaks one-to-one ties only; the relation is not one to one.");
        }

        if (mapping.Ignore)
            return; // not stored in this database: nothing to bind, so no foreign key or junction checks (MQ4011 included)

        var shape = EffectiveShape(context, relation, mapping.Database, mapping);

        if (mapping.ForeignKey is { } foreignKeyId)
            CheckForeignKeyBinding(context, relation, mapping, foreignKeyId, shape, report);

        Table? junction = null;
        if (mapping.JunctionTable is { } junctionId && model.Get<Table>(junctionId) is { } junctionTable)
        {
            if (junctionTable.Origin == TableOrigin.Synthesized)
                Invalid(report, "/junctionTable", $"'junctionTable' names '{junctionTable.Name}', a synthesized table's overlay; name a designed or imported table.");
            else if (junctionTable.Database != mapping.Database)
                Invalid(report, "/junctionTable", $"'junctionTable' names '{junctionTable.Name}', which belongs to another database.");
            else if (shape != RelationShape.Junction)
                Invalid(report, "/junctionTable", $"'junctionTable' needs the junction shape; the relation maps as {Kebab(shape)}.");
            else
                junction = junctionTable;
        }

        if (mapping.Ends.Count > 0 && mapping.JunctionTable is null)
            Invalid(report, "/ends", "'ends' binds the foreign keys of a designed 'junctionTable', which the mapping does not name.");
        for (var i = 0; i < mapping.Ends.Count; i++)
        {
            var end = mapping.Ends[i];
            var pointer = Ptr.At("/ends", i);
            if (!relation.Ends.Any(e => e.Id == end.End) && model.TryGetEntry(end.End, out _))
                Invalid(report, pointer + "/end", $"'{end.End}' is not an end of relation '{relation.Name}'.");
            if (junction is not null && !junction.ForeignKeys.Any(f => f.Id == end.ForeignKey) && model.TryGetEntry(end.ForeignKey, out _))
                Invalid(report, pointer + "/foreignKey", $"'{end.ForeignKey}' is not a foreign key of junction table '{junction.Name}'.");
        }

        // MQ4011: a designed junction binds every end.
        if (junction is not null)
        {
            foreach (var end in relation.Ends)
            {
                if (!mapping.Ends.Any(e => e.End == end.Id))
                    report.Add("MQ4011", $"Junction table '{junction.Name}' is designed, so end '{end.Role}' needs an 'ends' entry naming its foreign key.", "/junctionTable");
            }
        }

        // MQ4011: both ends bound to designed or imported tables, foreign-key shape, and no foreign key named.
        if (shape == RelationShape.ForeignKey && mapping.ForeignKey is null && BothEndsBound(context, relation, mapping.Database))
        {
            report.Add("MQ4011", $"Both ends of relation '{relation.Name}' are bound to designed or imported tables in this database; name the existing foreign key with 'foreignKey'.", "/relation");
        }
    }

    private static void CheckForeignKeyBinding(ValidationContext context, Relation relation, Mapping mapping, string foreignKeyId, RelationShape shape, Report report)
    {
        var model = context.Model;
        if (shape != RelationShape.ForeignKey)
        {
            Invalid(report, "/foreignKey", $"'foreignKey' needs the foreign-key shape; the relation maps as {Kebab(shape)}.");
            return;
        }

        if (model.GetDocument(foreignKeyId)?.Element is not Table owner)
            return; // MQ2001 or MQ2002
        if (!owner.ForeignKeys.Any(f => f.Id == foreignKeyId))
        {
            Invalid(report, "/foreignKey", $"'{foreignKeyId}' is a key of table '{owner.Name}' but not a foreign key.");
            return;
        }

        if (owner.Database != mapping.Database)
        {
            Invalid(report, "/foreignKey", $"Foreign key '{foreignKeyId}' belongs to table '{owner.Name}' of another database.");
            return;
        }

        if (DependentEnd(relation, mapping) is { } dependent && BoundTable(context, mapping.Database, relation.Ends[dependent].Entity) is { } boundId
            && boundId != owner.Id)
        {
            Invalid(report, "/foreignKey", $"Foreign key '{foreignKeyId}' belongs to table '{owner.Name}', not to the table bound to the dependent end '{relation.Ends[dependent].Role}'.");
        }
    }

    /// <summary>Checks, on a relation file, the databases where both ends are bound but no mapping of the relation exists (MQ4011).</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="relation">The relation.</param>
    /// <param name="report">The report.</param>
    public static void CheckRelationBindings(ValidationContext context, Relation relation, Report report)
    {
        if (relation.Ends.Count != 2)
            return;
        foreach (var database in context.Model.All<Database>())
        {
            if (!context.MappingsOf(database.Id, relation.Id).IsEmpty)
                continue; // the mapping file reports
            if (EffectiveShape(context, relation, database.Id, null) == RelationShape.ForeignKey && BothEndsBound(context, relation, database.Id))
            {
                report.Add("MQ4011", $"Both ends of relation '{relation.Name}' are bound to designed or imported tables in database '{database.Name}'; add a mapping that names the existing foreign key.", "/ends");
            }
        }
    }

    private static bool BothEndsBound(ValidationContext context, Relation relation, string databaseId) =>
        relation.Ends.Count == 2 && relation.Ends.All(e => BoundTable(context, databaseId, e.Entity) is not null);

    private static string? BoundTable(ValidationContext context, string databaseId, string entityId)
    {
        foreach (var doc in context.MappingsOf(databaseId, entityId))
        {
            if (doc.Element is Mapping { Ignore: false, Table: { } table } && context.Model.Get<Table>(table) is { Origin: not TableOrigin.Synthesized })
                return table;
        }

        return null;
    }

    /// <summary>The shape a relation maps to in a database: the mapping's, else the S7 default (engine-design.md section 7.4).</summary>
    private static RelationShape EffectiveShape(ValidationContext context, Relation relation, string databaseId, Mapping? mapping)
    {
        if (mapping?.Shape is { } shape)
            return shape;
        if (relation.Ends.Count != 2)
            return RelationShape.Junction;
        if (relation.Attributes.Count > 0)
        {
            var settings = context.Model.Settings;
            var name = context.Model.Get<Database>(databaseId)?.Name;
            var perDatabase = name is not null && settings.Databases.TryGetValue(name, out var conventions) ? conventions.RelationsWithAttributes : null;
            return perDatabase ?? settings.Conventions.RelationsWithAttributes ?? RelationShape.Junction;
        }

        return relation.Ends.All(e => e.Max == MaxCardinality.Many) ? RelationShape.Junction : RelationShape.ForeignKey;
    }

    /// <summary>The end whose table holds the foreign key of a binary foreign-key relation (engine-design.md section 7.4).</summary>
    private static int? DependentEnd(Relation relation, Mapping mapping)
    {
        if (relation.Ends.Count != 2)
            return null;
        var (a, b) = (relation.Ends[0], relation.Ends[1]);
        if (a.Max == MaxCardinality.Many && b.Max == MaxCardinality.One)
            return 0;
        if (b.Max == MaxCardinality.Many && a.Max == MaxCardinality.One)
            return 1;
        if (a.Max != MaxCardinality.One || b.Max != MaxCardinality.One)
            return null;
        if (a.Min != b.Min)
            return a.Min == 1 ? 1 : 0; // the principal is the end with min 1
        return mapping.ForeignKeyEnd == a.Id ? 0 : 1;
    }

    private static string Kebab(StorageKind kind) => kind.ToString().ToLowerInvariant();

    private static string Kebab(RelationShape shape) => shape switch
    {
        RelationShape.ForeignKey => "foreign-key",
        RelationShape.Junction => "junction",
        _ => "promoted",
    };
}
