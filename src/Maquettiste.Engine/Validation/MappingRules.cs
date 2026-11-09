using System.Globalization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// Mapping rules: MQ4004 (two mappings for one target), MQ4009 (option invalid for the element), MQ4011 (bound relations),
/// MQ4012 (an entity in no database), MQ4013 (convention packages a database does not use), and MQ4115 through
/// <see cref="RelationTypeRules.CheckStoredNothing"/>.
/// </summary>
internal static class MappingRules
{
    /// <summary>MQ4012: an entity that no database holds, when the model has a database (a conceptual-only model is fine).</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="report">The report.</param>
    public static void CheckEntityPlacement(ValidationContext context, Entity entity, Report report)
    {
        // A binding places the entity in its database (erratum E43).
        if (entity.Bindings.Any(b => context.Model.Get<Database>(b.Database) is not null))
            return;
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

    /// <summary>
    /// Whether a relation end with several types covers the entity: its <c>discriminatorValue</c> is then the type code a type column holds
    /// (several-types-design.md decision 42), so it is allowed outside an inheritance hierarchy.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="entityId">The entity's id.</param>
    /// <returns>Whether such an end covers it.</returns>
    internal static bool CoveredBySeveralTypes(ModelSnapshot model, string entityId)
    {
        if (!EndCoverage.Of(model).Any)
            return false;
        foreach (var relation in model.All<Relation>())
        {
            foreach (var end in relation.Ends)
            {
                if (end.HasSeveralTypes && EndCoverage.Covers(model, end, entityId))
                    return true;
            }
        }

        return false;
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
        if (mapping.Constants.Count > 0)
            Invalid(report, "/constants", "'constants' " + relationOnly + ".");
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
        if (mapping.DiscriminatorValue is not null && entity.Base is null && !context.HasDerived(entity.Id) && !CoveredBySeveralTypes(context.Model, entity.Id))
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

        // A relation with an end of several types (several-types-design.md decision 37): the per-end entries describe its storage, and the keys
        // that shape engine-made storage don't apply (decision 50).
        if (EndCoverage.HasSeveralTypes(relation))
        {
            CheckSeveralTypesMapping(context, mapping, relation, report);
            return;
        }

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

            // A relation attribute's column is one of the designed junctionTable's (several-types-design.md decision 45, answer 9).
            if (item.Column is not null && DesignedJunction(context, mapping) is { } linkTable)
            {
                CheckAttributeMapping(context, item with { Column = null }, attribute, null, Ptr.At("/attributes", i), report);
                CheckAttributeColumn(context, item, attribute, linkTable, Ptr.At("/attributes", i), i, report);
            }
            else
            {
                CheckAttributeMapping(context, item, attribute, null, Ptr.At("/attributes", i), report);
            }
        }

        CheckConstants(mapping, DesignedJunction(context, mapping), [], report);

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

        // The per-entry ways (several-types-design.md decision 38) hold whether or not the mapping stores the relation: the schema required
        // each entry's foreignKey, and refused the other keys, on an ignored mapping too (MQ1002). Its junction is the named designed table.
        CheckOneTypeEntryWays(mapping, relation, mapping.Ignore ? DesignedJunction(context, mapping) : null, report);
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
            if (junction is not null && !end.HasWay)
                MissingForeignKey(report, pointer, i, junction);
            if (junction is not null && end.ForeignKey is { } endForeignKey && !junction.ForeignKeys.Any(f => f.Id == endForeignKey) && model.TryGetEntry(endForeignKey, out _))
                Invalid(report, pointer + "/foreignKey", $"'{endForeignKey}' is not a foreign key of junction table '{junction.Name}'.");
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

    /// <summary>
    /// MQ4009 on the <c>ends</c> entries of a one-type relation's mapping (several-types-design.md decision 38): the per-end ways of an end with
    /// several types don't apply, and with <paramref name="junction"/> (an ignored mapping's designed junction) an entry with no way keeps the
    /// words of a missing key, which the schema reported before (MQ1002). A stored mapping's missing keys are checked against its junction later.
    /// </summary>
    private static void CheckOneTypeEntryWays(Mapping mapping, Relation relation, Table? junction, Report report)
    {
        for (var i = 0; i < mapping.Ends.Count; i++)
        {
            var end = mapping.Ends[i];
            var pointer = Ptr.At("/ends", i);
            foreach (var (key, set) in new (string, bool)[]
            {
                ("foreignKeys", end.ForeignKeys.Count > 0), ("types", end.Types.Count > 0), ("idColumn", end.IdColumn is not null),
                ("typeColumn", end.TypeColumn is not null), ("referencesColumn", end.ReferencesColumn is not null),
            })
            {
                if (set)
                    Invalid(report, pointer + "/" + key, $"End entry {i.ToString(CultureInfo.InvariantCulture)} of relation '{relation.Name}' sets '{key}', which needs a relation with an end of several types.");
            }

            if (junction is not null && !end.HasWay)
                MissingForeignKey(report, pointer, i, junction);
        }
    }

    /// <summary>MQ4009 for a one-type entry with no key in a designed junction, in the words of a missing key.</summary>
    private static void MissingForeignKey(Report report, string pointer, int index, Table junction) =>
        Invalid(report, pointer, $"End entry {index.ToString(CultureInfo.InvariantCulture)} names no 'foreignKey'; junction table '{junction.Name}' is designed, so the entry needs the foreign key that references its end's table.");

    /// <summary>
    /// MQ4009 and MQ4011 on a mapping of a relation with an end of several types (several-types-design.md section 19.7): the per-end entries,
    /// the keys that shape engine-made storage, <c>shape</c> against the entries (decision 51), and <c>constants</c> and attribute columns
    /// against the designed <c>junctionTable</c>. What needs placements and resolved tables is MQ4117, from the database run.
    /// </summary>
    private static void CheckSeveralTypesMapping(ValidationContext context, Mapping mapping, Relation relation, Report report)
    {
        var model = context.Model;
        var stores = mapping.JunctionTable is not null || mapping.Ends.Count > 0;
        foreach (var (pointer, set) in new (string, bool)[]
        {
            ("/foreignKeyEnd", mapping.ForeignKeyEnd is not null), ("/foreignKey", mapping.ForeignKey is not null), ("/promotedName", mapping.PromotedName is not null),
            ("/shape", mapping.Shape == RelationShape.Promoted || (mapping.Shape is not null && !stores)), ("/attributes", !stores && mapping.Attributes.Count > 0),
            ("/constants", !stores && mapping.Constants.Count > 0),
        })
        {
            if (set)
                Invalid(report, pointer, $"Relation '{relation.Name}' has an end with several types, so a mapping of it doesn't take '{pointer[1..]}'; its 'ends' entries say how it is stored. Remove it.");
        }

        if (!stores)
            return;
        var inLinkTables = mapping.JunctionTable is not null || mapping.Ends.Any(e => e.Types.Any(t => t.ForeignKey is not null));
        if (mapping.Shape is RelationShape.Junction or RelationShape.ForeignKey && (mapping.Shape == RelationShape.Junction) != inLinkTables)
        {
            Invalid(report, "/shape", mapping.Shape == RelationShape.Junction
                ? $"Relation '{relation.Name}' keeps its links in the tables of its ends, so shape 'junction' disagrees with its entries; remove 'shape'."
                : $"Relation '{relation.Name}' keeps its links in link tables, so shape 'foreign-key' disagrees with its entries; remove 'shape'.");
        }

        Table? junction = null;
        if (mapping.JunctionTable is { } junctionId && model.Get<Table>(junctionId) is { } junctionTable)
        {
            if (junctionTable.Origin == TableOrigin.Synthesized)
                Invalid(report, "/junctionTable", $"'junctionTable' names '{junctionTable.Name}', a synthesized table's overlay; name a designed or imported table.");
            else if (junctionTable.Database != mapping.Database)
                Invalid(report, "/junctionTable", $"'junctionTable' names '{junctionTable.Name}', which belongs to another database.");
            else
                junction = junctionTable;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pointerColumns = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < mapping.Ends.Count; i++)
        {
            var entry = mapping.Ends[i];
            var at = Ptr.At("/ends", i);
            var n = i.ToString(CultureInfo.InvariantCulture);
            var end = relation.Ends.FirstOrDefault(e => e.Id == entry.End);
            var role = end?.Role ?? entry.End;
            if (end is null && model.TryGetEntry(entry.End, out _))
                Invalid(report, at + "/end", $"'{entry.End}' is not an end of relation '{relation.Name}'.");
            if (!seen.Add(entry.End))
                Invalid(report, at + "/end", $"End '{role}' of relation '{relation.Name}' has two entries; keep one.");

            var ways = new List<string>();
            if (entry.ForeignKey is not null)
                ways.Add("foreignKey");
            if (entry.ForeignKeys.Count > 0)
                ways.Add("foreignKeys");
            if (entry.Types.Any(t => t.ForeignKey is not null))
                ways.Add("types");
            if (entry.IdColumn is not null)
                ways.Add("idColumn");
            if (ways.Count == 0)
                Invalid(report, at, $"End entry {n} of relation '{relation.Name}' names no way to point at end '{role}'; give it a foreignKey, foreignKeys, types with foreignKey items or an idColumn.");
            else if (ways.Count > 1)
                Invalid(report, at, $"End entry {n} of relation '{relation.Name}' names more than one way to point at end '{role}' ({string.Join(", ", ways)}); keep one.");

            if (entry.TypeColumn is not null && entry.IdColumn is null)
                Invalid(report, at + "/typeColumn", $"End entry {n} of relation '{relation.Name}' sets 'typeColumn' with no 'idColumn'; it describes the id column.");
            if (entry.ReferencesColumn is not null && entry.IdColumn is null)
                Invalid(report, at + "/referencesColumn", $"End entry {n} of relation '{relation.Name}' sets 'referencesColumn' with no 'idColumn'; it describes the id column.");
            var keyed = entry.Types.Count(t => t.ForeignKey is not null);
            var refers = entry.Types.Count(t => t.ReferencesColumn is not null);
            if (keyed > 0 && refers > 0)
                Invalid(report, at + "/types", $"End entry {n} of relation '{relation.Name}' mixes types items with foreignKey and with referencesColumn; use one kind.");
            else if (keyed > 0 && entry.IdColumn is not null)
                Invalid(report, at + "/types", $"End entry {n} of relation '{relation.Name}' has types items with foreignKey beside 'idColumn'; use one way.");
            else if (refers > 0 && entry.IdColumn is null)
                Invalid(report, at + "/types", $"End entry {n} of relation '{relation.Name}' has types items with referencesColumn but no 'idColumn'.");
            if (entry.ReferencesColumn is not null && refers > 0)
                Invalid(report, at + "/referencesColumn", $"End entry {n} of relation '{relation.Name}' sets 'referencesColumn' on the entry and in types items; keep one.");
            if (entry.TypeColumn is not null && string.Equals(entry.TypeColumn, entry.IdColumn, StringComparison.Ordinal))
                Invalid(report, at + "/typeColumn", $"End entry {n} of relation '{relation.Name}' uses one column for 'typeColumn' and 'idColumn'.");
            if (entry.ForeignKeys.Count > 0 && mapping.JunctionTable is not null)
                Invalid(report, at + "/foreignKeys", $"End entry {n} of relation '{relation.Name}' sets 'foreignKeys' beside 'junctionTable'; in a link table an end is pointed at by one key, a key per type or an id.");
            // Each type is listed once (decision 38): a second item for it would give its rows a second pointer.
            var listed = new HashSet<string>(StringComparer.Ordinal);
            for (var k = 0; k < entry.Types.Count; k++)
            {
                var type = entry.Types[k].Entity;
                if (!listed.Add(type))
                    Invalid(report, Ptr.At(at + "/types", k) + "/entity", $"End entry {n} of relation '{relation.Name}' lists entity '{model.Get<Entity>(type)?.Name ?? type}' in two types items; keep one.");
            }

            // Each named key is a foreign key, and each named column a column, of a designed or imported table of the database.
            if (entry.ForeignKey is { } foreignKey)
                CheckNamedForeignKey(model, mapping, foreignKey, at + "/foreignKey", pointerColumns, report);
            for (var k = 0; k < entry.ForeignKeys.Count; k++)
                CheckNamedForeignKey(model, mapping, entry.ForeignKeys[k], Ptr.At(at + "/foreignKeys", k), pointerColumns, report);
            for (var k = 0; k < entry.Types.Count; k++)
            {
                if (entry.Types[k].ForeignKey is { } typeKey)
                    CheckNamedForeignKey(model, mapping, typeKey, Ptr.At(at + "/types", k) + "/foreignKey", pointerColumns, report);
            }

            if (entry.IdColumn is { } idColumn && CheckNamedColumn(model, mapping, idColumn, at + "/idColumn", report))
                pointerColumns.Add(idColumn);
            if (entry.TypeColumn is { } typeColumn && CheckNamedColumn(model, mapping, typeColumn, at + "/typeColumn", report))
                pointerColumns.Add(typeColumn);
        }

        // MQ4011: a link table holds every link, so every end says how the table points at it.
        if (junction is not null)
        {
            foreach (var end in relation.Ends)
            {
                if (!mapping.Ends.Any(e => e.End == end.Id))
                    report.Add("MQ4011", $"Link table '{junction.Name}' holds relation '{relation.Name}', so end '{end.Role}' needs an 'ends' entry that says how the table points at it.", "/junctionTable");
            }
        }

        for (var i = 0; i < mapping.Attributes.Count; i++)
        {
            var item = mapping.Attributes[i];
            var pointer = Ptr.At("/attributes", i);
            var attribute = relation.Attributes.FirstOrDefault(a => a.Id == item.Attribute);
            if (attribute is null)
            {
                if (model.TryGetEntry(item.Attribute, out _))
                    Invalid(report, pointer + "/attribute", $"'{item.Attribute}' is not an attribute of relation '{relation.Name}'.");
                continue;
            }

            foreach (var (key, set) in new (string, bool)[] { ("storage", item.Storage is not null), ("prefix", item.Prefix is not null) })
            {
                if (set)
                    Invalid(report, pointer + "/" + key, $"Relation '{relation.Name}' has an end with several types, so '{key}' of attribute {i.ToString(CultureInfo.InvariantCulture)} can't shape engine-made columns; remove it.");
            }

            // Without a link table, the column is checked against the end's table that holds the links (MQ4117, from the run).
            if (item.Column is not null)
            {
                if (junction is not null)
                    CheckAttributeColumn(context, item, attribute, junction, pointer, i, report);
                else if (IsCompound(context, attribute))
                    Invalid(report, pointer + "/column", $"Attribute {i.ToString(CultureInfo.InvariantCulture)} is a collection or value object, so it can't map one column.");
            }
        }

        CheckConstants(mapping, junction, pointerColumns, report);
    }

    /// <summary>MQ4009 for a key an entry names: a foreign key of a designed or imported table of the mapping's database. Adds its columns.</summary>
    private static void CheckNamedForeignKey(ModelSnapshot model, Mapping mapping, string foreignKeyId, string pointer, HashSet<string> pointerColumns, Report report)
    {
        if (model.GetDocument(foreignKeyId)?.Element is not Table owner)
            return; // MQ2001 or MQ2002
        if (owner.ForeignKeys.FirstOrDefault(f => f.Id == foreignKeyId) is not { } foreignKey)
            Invalid(report, pointer, $"'{foreignKeyId}' is a key of table '{owner.Name}' but not a foreign key.");
        else if (owner.Origin == TableOrigin.Synthesized)
            Invalid(report, pointer, $"Foreign key '{foreignKey.Name ?? foreignKeyId}' belongs to '{owner.Name}', a synthesized table's overlay; name a key of a designed or imported table.");
        else if (owner.Database != mapping.Database)
            Invalid(report, pointer, $"Foreign key '{foreignKey.Name ?? foreignKeyId}' belongs to table '{owner.Name}' of another database.");
        else
            pointerColumns.UnionWith(foreignKey.Columns);
    }

    /// <summary>MQ4009 for a column an entry names: a column of a designed or imported table of the mapping's database.</summary>
    private static bool CheckNamedColumn(ModelSnapshot model, Mapping mapping, string columnId, string pointer, Report report)
    {
        if (model.GetDocument(columnId)?.Element is not Table owner)
            return false; // MQ2001 or MQ2002
        if (owner.Columns.FirstOrDefault(c => c.Id == columnId) is not { } column)
        {
            Invalid(report, pointer, $"'{columnId}' is an element of table '{owner.Name}' but not a column.");
            return false;
        }

        if (owner.Origin == TableOrigin.Synthesized)
            Invalid(report, pointer, $"Column '{column.Name}' belongs to '{owner.Name}', a synthesized table's overlay; name a column of a designed or imported table.");
        else if (owner.Database != mapping.Database)
            Invalid(report, pointer, $"Column '{column.Name}' belongs to table '{owner.Name}' of another database.");
        else
            return true;
        return false;
    }

    /// <summary>The mapping's <c>junctionTable</c> when it is a designed or imported table of the mapping's database; else <see langword="null"/>.</summary>
    private static Table? DesignedJunction(ValidationContext context, Mapping mapping) =>
        mapping.JunctionTable is { } id && context.Model.Get<Table>(id) is { Origin: not TableOrigin.Synthesized } table && table.Database == mapping.Database ? table : null;

    /// <summary>Whether an attribute is a collection or typed by a value object: one column can't hold it (decision 45).</summary>
    private static bool IsCompound(ValidationContext context, ModelAttribute attribute) =>
        attribute.Collection || AttributeRules.Resolve(context.Model, attribute.Type).ValueObject is not null;

    /// <summary>MQ4009 for a relation attribute's column in a link table (several-types-design.md decision 45).</summary>
    private static void CheckAttributeColumn(ValidationContext context, AttributeMapping item, ModelAttribute attribute, Table linkTable, string pointer, int index,
        Report report)
    {
        if (!linkTable.Columns.Any(c => c.Id == item.Column))
            Invalid(report, pointer + "/column", $"'{item.Column}' is not a column of link table '{linkTable.Name}'.");
        if (IsCompound(context, attribute))
            Invalid(report, pointer + "/column", $"Attribute {index.ToString(CultureInfo.InvariantCulture)} is a collection or value object, so it can't map one column.");
    }

    /// <summary>
    /// MQ4009 for a relation mapping's <c>constants</c> (several-types-design.md decision 44): each needs a designed <c>junctionTable</c> that
    /// holds its column, which no end entry uses as a pointer.
    /// </summary>
    private static void CheckConstants(Mapping mapping, Table? junction, HashSet<string> pointerColumns, Report report)
    {
        for (var i = 0; i < mapping.Constants.Count; i++)
        {
            var at = Ptr.At("/constants", i);
            var column = mapping.Constants[i].Column;
            if (junction is null)
                Invalid(report, at, $"Constant {i.ToString(CultureInfo.InvariantCulture)} needs a designed 'junctionTable' that holds its column.");
            else if (!junction.Columns.Any(c => c.Id == column))
                Invalid(report, at + "/column", $"'{column}' is not a column of link table '{junction.Name}'.");
            else if (pointerColumns.Contains(column) || mapping.Ends.Any(e => e.ForeignKey is not null && junction.ForeignKeys.FirstOrDefault(f => f.Id == e.ForeignKey)?.Columns.Contains(column) == true))
                Invalid(report, at + "/column", $"Constant {i.ToString(CultureInfo.InvariantCulture)} names a column that an end entry already uses as a pointer; a constant needs a column of its own.");
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

        // A relation with an end of several types never gets here: its mapping sets only ignore (MQ4009, above).
        if (DependentEnd(relation, mapping) is { } dependent && relation.Ends[dependent].SingleEntity is { } dependentEntity
            && BoundTable(context, mapping.Database, dependentEntity) is { } boundId && boundId != owner.Id)
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
        // A relation with an end of several types is never bound in part 1, so it never asks for a mapping; where its types are placed, the
        // database stores nothing for it (MQ4115, several-types-design.md decision 22).
        if (EndCoverage.HasSeveralTypes(relation))
        {
            RelationTypeRules.CheckStoredNothing(context, relation, report);
            return;
        }

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

    // An end with several types is never bound in part 1, so such a relation never asks for a mapping (MQ4011; CheckRelationBindings skips it).
    private static bool BothEndsBound(ValidationContext context, Relation relation, string databaseId) =>
        relation.Ends.Count == 2 && relation.Ends.All(e => e.SingleEntity is { } entityId && BoundTable(context, databaseId, entityId) is not null);

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
