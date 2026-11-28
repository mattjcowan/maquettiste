using System.Globalization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// Relation mapping rules: MQ4004 (two mappings for one relation), MQ4009 (option invalid for the relation), MQ4011 (a designed junction
/// that does not bind every end), MQ4146 (a database that laid out tables by convention before 0.29.0), and MQ4115 through
/// <see cref="RelationTypeRules.CheckStoredNothing"/>.
/// </summary>
internal static class MappingRules
{
    /// <summary>
    /// MQ4146, a warning: a database file with no <c>byConvention</c> laid out a table for every entity not bound to it before 0.29.0, so
    /// while an entity of the model is not bound to the database, a model brought from an older release would drop those tables at the next
    /// migration (retire-projection-design.md section 9). It never blocks a save: a database converted on 0.28.0 has no key either, and
    /// gets the finding as soon as a new entity is added. Its quick fix writes <c>"byConvention": "none"</c>.
    /// </summary>
    /// <param name="context">The validation context.</param>
    /// <param name="database">The database.</param>
    /// <param name="report">The report.</param>
    public static void CheckConventionLayout(ValidationContext context, Database database, Report report)
    {
        if (database.ByConvention is not null)
            return;
        var entities = context.Model.All<Entity>();
        var unbound = entities
            .Where(e => !e.Bindings.Any(b => string.Equals(b.Database, database.Id, StringComparison.Ordinal)))
            .Select(e => e.Name)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (unbound.Count == 0)
            return;
        var listed = string.Join(", ", unbound.Take(5).Select(n => "'" + n + "'"));
        var more = unbound.Count > 5 ? " (and " + (unbound.Count - 5).ToString(CultureInfo.InvariantCulture) + " more)" : "";
        var bound = unbound.Count < entities.Count;
        var what = bound
            ? $"Database '{database.Name}' has no byConvention, and {(unbound.Count == 1 ? "entity" : "entities")} {listed}{more} {(unbound.Count == 1 ? "is" : "are")} not bound to it, "
                + "as a new entity is until it gets a table. "
            : $"Database '{database.Name}' has no byConvention, and no entity is bound to it ({listed}{more}). ";
        report.Add("MQ4146",
            what + "Before 0.29.0 a database with no byConvention laid out a table for every entity not bound to it; since then it holds only the tables "
            + "written for it. Write \"byConvention\": \"none\" (the quick fix) to say so. If this model still lays out those tables on an older "
            + "release, bind each entity to its table first (Create tables on 0.28.0), or the next migration drops them.",
            "");
    }

    /// <summary>Checks a mapping file.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="mapping">The mapping.</param>
    /// <param name="report">The report.</param>
    public static void CheckMapping(ValidationContext context, Mapping mapping, Report report)
    {
        var mappings = context.MappingsOf(mapping.Database, mapping.Relation);
        if (mappings.Length > 1 && mappings[0] != report.Document)
        {
            var targetName = context.Model.GetDocument(mapping.Relation)?.Element.Name ?? mapping.Relation;
            report.Add("MQ4004", $"'{targetName}' is already mapped to this database by {mappings[0].Path}; one mapping per relation and database.", "/relation");
        }

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

    /// <summary>MQ4009 for a relation attribute's column when the mapping names neither a designed junction table nor a foreign key that holds it.</summary>
    private static void CheckAttributeMapping(AttributeMapping item, string pointer, Report report)
    {
        if (item.Column is not null)
            Invalid(report, pointer + "/column", "'column' needs the mapping to name a designed 'junctionTable' or the 'foreignKey' whose table holds the column.");
    }

    private static void CheckRelationMapping(ValidationContext context, Mapping mapping, Report report)
    {
        var model = context.Model;
        if (model.Get<Relation>(mapping.Relation) is not { } relation)
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
                CheckAttributeColumn(context, item, attribute, linkTable, Ptr.At("/attributes", i), i, report);
            }
            else if (item.Column is not null && mapping.JunctionTable is null && ForeignKeyOwner(context, mapping) is { } holder)
            {
                // On the foreign-key shape, the column is one of the table that holds the foreign key (retire-projection-design.md section 6).
                CheckForeignKeyAttributeColumn(context, item, attribute, holder, mapping.ForeignKey!, Ptr.At("/attributes", i), i, report);
            }
            else
            {
                CheckAttributeMapping(item, Ptr.At("/attributes", i), report);
            }
        }

        CheckConstants(mapping, DesignedJunction(context, mapping), [], report);

        var binary = relation.Ends.Count == 2;
        var manyToMany = binary && relation.Ends.All(e => e.Max == MaxCardinality.Many);
        if (mapping.Shape == RelationShape.ForeignKey && (!binary || manyToMany))
            Invalid(report, "/shape", $"Relation '{relation.Name}' is {(binary ? "many to many" : "n-ary")} and cannot be a foreign key.");

        if (mapping.ForeignKeyEnd is { } fkEnd)
        {
            if (!relation.Ends.Any(e => e.Id == fkEnd))
                Invalid(report, "/foreignKeyEnd", $"'{fkEnd}' is not an end of relation '{relation.Name}'.");
            else if (!binary || relation.Ends.Any(e => e.Max != MaxCardinality.One))
                Invalid(report, "/foreignKeyEnd", "'foreignKeyEnd' breaks one-to-one ties only; the relation is not one to one.");
        }

        // The per-entry ways (several-types-design.md decision 38).
        CheckOneTypeEntryWays(mapping, relation, report);
        var shape = EffectiveShape(relation, mapping);

        if (mapping.ForeignKey is { } foreignKeyId)
            CheckForeignKeyBinding(context, relation, mapping, foreignKeyId, shape, report);

        Table? junction = null;
        if (mapping.JunctionTable is { } junctionId && model.Get<Table>(junctionId) is { } junctionTable)
        {
            if (junctionTable.Database != mapping.Database)
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
    }

    /// <summary>
    /// MQ4009 on the <c>ends</c> entries of a one-type relation's mapping (several-types-design.md decision 38): the per-end ways of an end with
    /// several types don't apply. Missing keys are checked against the mapping's junction later.
    /// </summary>
    private static void CheckOneTypeEntryWays(Mapping mapping, Relation relation, Report report)
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
            ("/foreignKeyEnd", mapping.ForeignKeyEnd is not null), ("/foreignKey", mapping.ForeignKey is not null),
            ("/shape", mapping.Shape is not null && !stores), ("/attributes", !stores && mapping.Attributes.Count > 0),
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
            if (junctionTable.Database != mapping.Database)
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

        if (owner.Database != mapping.Database)
            Invalid(report, pointer, $"Column '{column.Name}' belongs to table '{owner.Name}' of another database.");
        else
            return true;
        return false;
    }

    /// <summary>The mapping's <c>junctionTable</c> when it is a designed or imported table of the mapping's database; else <see langword="null"/>.</summary>
    private static Table? DesignedJunction(ValidationContext context, Mapping mapping) =>
        mapping.JunctionTable is { } id && context.Model.Get<Table>(id) is { } table && table.Database == mapping.Database ? table : null;

    /// <summary>Whether an attribute is a collection or typed by a value object: one column can't hold it (decision 45).</summary>
    private static bool IsCompound(ValidationContext context, ModelAttribute attribute) =>
        attribute.Collection || AttributeRules.Resolve(context.Model, attribute.Type).ValueObject is not null;

    /// <summary>The designed or imported table of the mapping's database that declares the mapping's <c>foreignKey</c>, or <see langword="null"/>.</summary>
    private static Table? ForeignKeyOwner(ValidationContext context, Mapping mapping) =>
        mapping.ForeignKey is { } id && context.Model.GetDocument(id)?.Element is Table owner
        && owner.Database == mapping.Database && owner.ForeignKeys.Any(f => f.Id == id) ? owner : null;

    /// <summary>
    /// MQ4009 for a relation attribute's column on the foreign-key shape (retire-projection-design.md section 6): a column of the table that holds
    /// the foreign key, not one of the key's own columns, for an attribute one column can hold.
    /// </summary>
    private static void CheckForeignKeyAttributeColumn(ValidationContext context, AttributeMapping item, ModelAttribute attribute, Table holder, string foreignKeyId,
        string pointer, int index, Report report)
    {
        if (!holder.Columns.Any(c => c.Id == item.Column))
            Invalid(report, pointer + "/column", $"'{item.Column}' is not a column of table '{holder.Name}', which holds the relation's foreign key.");
        else if (holder.ForeignKeys.FirstOrDefault(f => f.Id == foreignKeyId) is { } key && key.Columns.Contains(item.Column!))
            Invalid(report, pointer + "/column", $"Column '{holder.Columns.First(c => c.Id == item.Column).Name}' is a column of the relation's foreign key, so it can't also hold attribute '{attribute.Name}'.");
        if (IsCompound(context, attribute))
            Invalid(report, pointer + "/column", $"Attribute {index.ToString(CultureInfo.InvariantCulture)} is a collection or value object, so it can't map one column.");
    }

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

        // A relation with an end of several types never gets here (MQ4009, above).
        if (DependentEnd(relation, mapping) is { } dependent && relation.Ends[dependent].SingleEntity is { } dependentEntity
            && BoundTable(context, mapping.Database, dependentEntity) is { } boundId && boundId != owner.Id)
        {
            Invalid(report, "/foreignKey", $"Foreign key '{foreignKeyId}' belongs to table '{owner.Name}', not to the table bound to the dependent end '{relation.Ends[dependent].Role}'.");
        }
    }

    /// <summary>
    /// On a relation file: a relation with an end of several types where its types are placed but nothing stores it (MQ4115). A one-type
    /// relation between bound entities with no storage is MQ4011 from the database run.
    /// </summary>
    /// <param name="context">The validation context.</param>
    /// <param name="relation">The relation.</param>
    /// <param name="report">The report.</param>
    public static void CheckRelationBindings(ValidationContext context, Relation relation, Report report)
    {
        if (relation.Ends.Count == 2 && EndCoverage.HasSeveralTypes(relation))
            RelationTypeRules.CheckStoredNothing(context, relation, report);
    }

    /// <summary>
    /// The table an entity's binding to a database writes (else reads), the table whose foreign key stores the entity's to-one relations;
    /// <see langword="null"/> when the entity has no binding there.
    /// </summary>
    private static string? BoundTable(ValidationContext context, string databaseId, string entityId) =>
        context.Model.Get<Entity>(entityId)?.Bindings.FirstOrDefault(b => string.Equals(b.Database, databaseId, StringComparison.Ordinal)) is { } binding
            ? binding.Write?.Table ?? binding.Source
            : null;

    /// <summary>
    /// The shape a relation maps to: the mapping's; else a junction for an n-ary or many-to-many relation, or one that carries attributes and
    /// names no foreign key; else a foreign key (engine-design.md section 7.4).
    /// </summary>
    private static RelationShape EffectiveShape(Relation relation, Mapping mapping)
    {
        if (mapping.Shape is { } shape)
            return shape;
        if (relation.Ends.Count != 2 || relation.Ends.All(e => e.Max == MaxCardinality.Many))
            return RelationShape.Junction;
        if (relation.Attributes.Count > 0)
            return mapping.ForeignKey is not null && mapping.JunctionTable is null ? RelationShape.ForeignKey : RelationShape.Junction;
        return mapping.JunctionTable is not null ? RelationShape.Junction : RelationShape.ForeignKey;
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

    private static string Kebab(RelationShape shape) => shape == RelationShape.ForeignKey ? "foreign-key" : "junction";
}
