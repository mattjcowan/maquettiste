using System.Globalization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// Physical rules on table, view and sequence files (MQ4001 to MQ4008, MQ4010, and MQ3013, MQ3017, MQ3019 on designed columns).
/// These rules see only what files state: names produced by conventions for synthesized tables, columns and constraints are not
/// checked against the identifier limit here (they need the resolver's casing and inflection; see README, open contract gap).
/// </summary>
internal static class PhysicalRules
{
    /// <summary>Column keys that are not attribute or end ids (engine-design.md section 7.3).</summary>
    private static readonly string[] SpecialColumnKeys = ["id", "position", "discriminator"];

    /// <summary>Checks an identifier against the database's limit (MQ4001).</summary>
    /// <param name="database">The database.</param>
    /// <param name="limit">The limit, or <see langword="null"/> for none.</param>
    /// <param name="name">The identifier.</param>
    /// <param name="what">What is named, for the message.</param>
    /// <param name="pointer">The pointer of the name.</param>
    /// <param name="elementId">The element or sub-element id.</param>
    /// <param name="report">The report.</param>
    public static void CheckIdentifierLength(Database database, int? limit, string? name, string what, string pointer, string? elementId, Report report)
    {
        if (limit is not { } max || name is not { Length: > 0 })
            return;
        var length = DialectInfo.IdentifierLength(database.Dialect, name);
        if (length > max)
        {
            var unit = database.Dialect is Dialect.PostgreSql or Dialect.Oracle ? "bytes" : "characters";
            report.Add("MQ4001", string.Create(CultureInfo.InvariantCulture,
                $"{what} name '{name}' is {length} {unit} long; database '{database.Name}' ({DialectInfo.Name(database.Dialect)}) allows {max}."), pointer, elementId);
        }
    }

    /// <summary>Checks a table file.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="table">The table.</param>
    /// <param name="report">The report.</param>
    public static void CheckTable(ValidationContext context, Table table, Report report)
    {
        var model = context.Model;
        var database = model.Get<Database>(table.Database);
        var limit = database is null ? null : DialectInfo.IdentifierLimit(database);
        var overlay = table.Origin == TableOrigin.Synthesized;

        CheckPhysicalName(context, table, "Table", report);
        CheckSameDatabase(context, table.Database, table.Schema, report);
        if (database is not null)
        {
            CheckIdentifierLength(database, limit, table.Name, "Table", "/name", table.Id, report);
            CheckIdentifierLength(database, limit, table.PrimaryKey?.Name, "Primary key", "/primaryKey/name", table.Id, report);
            for (var i = 0; i < table.Uniques.Count; i++)
                CheckIdentifierLength(database, limit, table.Uniques[i].Name, "Unique constraint", Ptr.At("/uniques", i) + "/name", table.Uniques[i].Id, report);
            for (var i = 0; i < table.ForeignKeys.Count; i++)
                CheckIdentifierLength(database, limit, table.ForeignKeys[i].Name, "Foreign key", Ptr.At("/foreignKeys", i) + "/name", table.ForeignKeys[i].Id, report);
            for (var i = 0; i < table.Checks.Count; i++)
                CheckIdentifierLength(database, limit, table.Checks[i].Name, "Check constraint", Ptr.At("/checks", i) + "/name", table.Checks[i].Id, report);
            for (var i = 0; i < table.Indexes.Count; i++)
                CheckIdentifierLength(database, limit, table.Indexes[i].Name, "Index", Ptr.At("/indexes", i) + "/name", table.Indexes[i].Id, report);
        }

        // MQ4004: two overlay files for one synthesized table.
        if (ValidationContext.OverlayTarget(table) is { } target)
        {
            var overlays = context.OverlaysOf(target);
            if (overlays.Length > 1 && overlays[0] != report.Document)
                report.Add("MQ4004", $"Synthesized table '{target}' already has the overlay {overlays[0].Path}; one overlay file per synthesized table.", "");
            CheckOverlayTarget(context, table, report);
        }

        // Columns.
        var columnIds = new HashSet<string>(StringComparer.Ordinal);
        var overlayKeys = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var column = table.Columns[i];
            var pointer = Ptr.At("/columns", i);
            columnIds.Add(column.Id);
            BuiltinRules.CheckCommon(context, column, pointer, "column", report);

            if (column.Name.Length > 0 || column.Attribute is null)
                BuiltinRules.CheckLabel(column.Name, "column", pointer + "/name", column.Id, report);
            if (column.Name.Length > 0 && !names.TryAdd(column.Name, i))
            {
                report.Add("MQ4003", string.Create(CultureInfo.InvariantCulture, $"Column name '{column.Name}' is already used at index {names[column.Name]}."), pointer + "/name", column.Id);
            }

            if (database is not null)
            {
                CheckIdentifierLength(database, limit, column.Name, "Column", pointer + "/name", column.Id, report);
                if (column.NativeType is { } native
                    && !DialectInfo.IsKnownNativeType(database.Dialect, native, model.Settings.TypeMaps.GetValueOrDefault(DialectInfo.Name(database.Dialect))))
                {
                    report.Add("MQ4006", $"Native type '{native}' is not known to the {DialectInfo.Name(database.Dialect)} type map.", pointer + "/nativeType", column.Id);
                }
            }

            if (column.Attribute is { } key)
            {
                if (!overlay)
                    report.Add("MQ4007", $"Column '{column.Name}' overrides column key '{key}', but only a synthesized table's overlay has column keys.", pointer + "/attribute", column.Id);
                else if (!IsValidColumnKey(context, table, key))
                    report.Add("MQ4007", $"Column key '{key}' does not name a column of the synthesized table.", pointer + "/attribute", column.Id);
                else if (!overlayKeys.TryAdd(key, i))
                    report.Add("MQ4003", string.Create(CultureInfo.InvariantCulture, $"Column key '{key}' is already overridden at index {overlayKeys[key]}."), pointer + "/attribute", column.Id);
            }

            CheckColumnValues(column, pointer, report);
            if (column.Sequence is { } sequenceId && model.Get<Sequence>(sequenceId) is { } sequence && sequence.Database != table.Database)
                report.Add("MQ2002", $"Column '{column.Name}' uses sequence '{sequence.Name}' of another database.", pointer + "/sequence", column.Id);
        }

        // MQ4008: constraints and indexes name existing columns.
        bool Resolves(string key) => columnIds.Contains(key) || overlayKeys.ContainsKey(key) || (overlay && IsValidColumnKey(context, table, key));

        if (table.PrimaryKey is { } primaryKey)
            CheckColumnList(primaryKey.Columns, "/primaryKey/columns", "Primary key", table.Id, Resolves, report);
        for (var i = 0; i < table.Uniques.Count; i++)
            CheckColumnList(table.Uniques[i].Columns, Ptr.At("/uniques", i) + "/columns", "Unique constraint", table.Uniques[i].Id, Resolves, report);
        for (var i = 0; i < table.Indexes.Count; i++)
        {
            var index = table.Indexes[i];
            var pointer = Ptr.At("/indexes", i);
            CheckColumnList(index.Columns.Select(c => c.Column).ToList(), pointer + "/columns", "Index", index.Id, Resolves, report, "/column");
            CheckColumnList(index.Include, pointer + "/include", "Index", index.Id, Resolves, report);
        }

        for (var i = 0; i < table.ForeignKeys.Count; i++)
            CheckForeignKey(context, table, i, Resolves, report);
    }

    private static void CheckColumnValues(Column column, string pointer, Report report)
    {
        if (column.Type is not { } keyword)
            return;
        if (!BuiltinTypes.IsBuiltin(keyword))
        {
            report.Add("MQ3013", $"Column '{column.Name}' has the unknown type keyword '{keyword}'.", pointer + "/type", column.Id);
            return;
        }

        var type = new EffectiveType(keyword, null, null, null);
        AttributeRules.CheckFacets(type, column.Length, column.Precision, column.Scale, null, "Column '" + column.Name + "'", pointer, column.Id, report);
        if (column.Default is { } literal && literal.ValueKind != System.Text.Json.JsonValueKind.Null
            && AttributeRules.Fits(keyword, literal, column.Length, column.Precision, column.Scale) is { } reason)
        {
            report.Add("MQ3019", $"The default of column '{column.Name}' does not fit {keyword}: {reason}.", pointer + "/default", column.Id);
        }

        if (column.Default is { } value && Credentials.LooksLikeCredential(column.Name, value, null) is { } why)
            report.Add("MQ3017", $"The default of column '{column.Name}' looks like a credential ({why}); secrets never belong in the model.", pointer + "/default", column.Id);
    }

    private static void CheckColumnList(IReadOnlyList<string> columns, string pointer, string what, string elementId, Func<string, bool> resolves, Report report, string suffix = "")
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (!resolves(columns[i]))
                report.Add("MQ4008", $"{what} names column '{columns[i]}', which is not a column of this table.", Ptr.At(pointer, i) + suffix, elementId);
        }
    }

    private static void CheckForeignKey(ValidationContext context, Table table, int index, Func<string, bool> resolves, Report report)
    {
        var model = context.Model;
        var fk = table.ForeignKeys[index];
        var pointer = Ptr.At("/foreignKeys", index);
        CheckColumnList(fk.Columns, pointer + "/columns", "Foreign key", fk.Id, resolves, report);

        // The referenced table: a table file id, or a synthesized table key.
        var referenced = model.Get<Table>(fk.ReferencesTable);
        if (referenced is null && !IsSynthesizedTableKey(model, fk.ReferencesTable))
        {
            report.Add("MQ2001", $"Foreign key references table '{fk.ReferencesTable}', which is neither a table file nor a synthesized table key.", pointer + "/referencesTable", fk.Id);
            return;
        }

        // Referenced columns, when the referenced table's columns are known.
        if (referenced is { Origin: not TableOrigin.Synthesized })
        {
            var ids = ColumnsById(referenced);
            for (var i = 0; i < fk.ReferencesColumns.Count; i++)
            {
                if (!ids.ContainsKey(fk.ReferencesColumns[i]))
                    report.Add("MQ4008", $"Foreign key references column '{fk.ReferencesColumns[i]}', which is not a column of table '{referenced.Name}'.", Ptr.At(pointer + "/referencesColumns", i), fk.Id);
            }

            if (table.Origin != TableOrigin.Synthesized)
                CheckForeignKeyTypes(table, fk, pointer, referenced, ids, report);
        }
        else
        {
            var target = referenced;
            for (var i = 0; i < fk.ReferencesColumns.Count; i++)
            {
                var key = fk.ReferencesColumns[i];
                var ok = target is not null
                    ? target.Columns.Any(c => c.Id == key || c.Attribute == key) || IsValidColumnKey(context, target, key)
                    : IsValidColumnKeySegments(model, key);
                if (!ok)
                    report.Add("MQ4008", $"Foreign key references column key '{key}', which does not name a column of the referenced table.", Ptr.At(pointer + "/referencesColumns", i), fk.Id);
            }
        }
    }

    private static void CheckForeignKeyTypes(Table table, ForeignKey fk, string pointer, Table referenced, Dictionary<string, Column> referencedIds, Report report)
    {
        var referencedColumns = fk.ReferencesColumns.Count > 0 ? fk.ReferencesColumns : referenced.PrimaryKey?.Columns ?? [];
        if (referencedColumns.Count == 0)
            return;
        if (referencedColumns.Count != fk.Columns.Count)
        {
            report.Add("MQ4005", string.Create(CultureInfo.InvariantCulture,
                $"Foreign key has {fk.Columns.Count} columns but references {referencedColumns.Count} in table '{referenced.Name}'."), pointer + "/columns", fk.Id);
            return;
        }

        var own = ColumnsById(table);
        for (var i = 0; i < fk.Columns.Count; i++)
        {
            if (own.TryGetValue(fk.Columns[i], out var column) && referencedIds.TryGetValue(referencedColumns[i], out var target)
                && column.Type is { } a && target.Type is { } b && a != b)
            {
                report.Add("MQ4005", $"Foreign key column '{column.Name}' is {a} but references '{target.Name}', which is {b}.", Ptr.At(pointer + "/columns", i), fk.Id);
            }
        }
    }

    /// <summary>A table's columns by id; the first wins when an id repeats (a malformed in-memory table must not throw).</summary>
    private static Dictionary<string, Column> ColumnsById(Table table)
    {
        var ids = new Dictionary<string, Column>(StringComparer.Ordinal);
        foreach (var column in table.Columns)
            ids.TryAdd(column.Id, column);
        return ids;
    }

    private static bool IsSynthesizedTableKey(ModelSnapshot model, string key)
    {
        var at = key.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || model.Get<Database>(key[(at + 1)..]) is null)
            return false;
        var target = key[..at].Split('.');
        if (target.Length == 1)
            return model.Get<Entity>(target[0]) is not null || model.Get<Relation>(target[0]) is not null || model.Get<EnumType>(target[0]) is not null;
        return target.Length == 2 && model.Get<Entity>(target[0]) is not null
            && model.TryGetEntry(target[1], out var entry) && entry.Kind == "attribute";
    }

    private static void CheckOverlayTarget(ValidationContext context, Table table, Report report)
    {
        if (table.Entity is not { } entityId || table.Attribute is not { } attributeId || context.Model.Get<Entity>(entityId) is not { } entity)
            return;
        var attribute = context.Flatten(entity).FirstOrDefault(a => a.Attribute.Id == attributeId)?.Attribute;
        if (attribute is null)
            report.Add("MQ4007", $"Overlay names attribute '{attributeId}', which is not an attribute of entity '{entity.Name}'.", "/attribute");
        else if (AttributeRules.Resolve(context.Model, attribute.Type).ValueObject is null && !attribute.Collection)
            report.Add("MQ4007", $"Attribute '{attribute.Name}' of entity '{entity.Name}' has no child table: it is neither a value object nor a collection.", "/attribute");
    }

    /// <summary>
    /// Whether a column key can name a column of a synthesized table (engine-design.md section 7.3). The first segment must fit
    /// the table's target: an attribute of the entity (own, inherited or virtual) or an end of a relation that touches it, an
    /// attribute or end of the relation for a junction, a member attribute of the value object for a child table; later segments
    /// must be attributes; <c>id</c>, <c>position</c>, <c>discriminator</c>, <c>code</c> and <c>name</c> are always allowed.
    /// </summary>
    /// <param name="context">The validation context.</param>
    /// <param name="table">The synthesized table's overlay.</param>
    /// <param name="key">The column key.</param>
    /// <returns><see langword="true"/> when it can resolve.</returns>
    public static bool IsValidColumnKey(ValidationContext context, Table table, string key)
    {
        var model = context.Model;
        var segments = key.Split('.');
        if (segments.Length == 1 && SpecialColumnKeys.Contains(segments[0], StringComparer.Ordinal))
            return true;
        if (!IsValidColumnKeySegments(model, key))
            return false;
        var first = segments[0];
        if (!model.TryGetEntry(first, out var entry))
            return false;
        if (table.Relation is { } relationId)
        {
            return model.Get<Relation>(relationId) is { } relation
                && (relation.Ends.Any(e => e.Id == first) || relation.Attributes.Any(a => a.Id == first));
        }

        if (table.Entity is not { } entityId || model.Get<Entity>(entityId) is not { } entity)
            return false;
        if (entry.Kind == "end")
            return EndTouches(context, first, entity);
        if (table.Attribute is { } attributeId)
        {
            // Child table: the owner's key columns, or members of the contained value object.
            var owner = context.Flatten(entity).FirstOrDefault(a => a.Attribute.Id == attributeId)?.Attribute;
            if (owner is not null && AttributeRules.Resolve(model, owner.Type).ValueObject is { } vo && vo.Attributes.Any(a => a.Id == first))
                return true;
            return entity.Key?.Attributes.Contains(first, StringComparer.Ordinal) == true || context.Flatten(entity).Any(a => a.Attribute.Id == first);
        }

        return context.Flatten(entity).Any(a => a.Attribute.Id == first) || DerivedAttributes(context, entity).Contains(first);
    }

    private static HashSet<string> DerivedAttributes(ValidationContext context, Entity entity)
    {
        // TPH: the root table also holds the attributes of every derived entity.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var doc in context.Documents)
        {
            if (doc.Element is Entity derived && derived.Id != entity.Id && Inherits(context.Model, derived, entity.Id))
            {
                foreach (var attribute in context.Flatten(derived))
                    ids.Add(attribute.Attribute.Id);
            }
        }

        return ids;
    }

    private static bool Inherits(ModelSnapshot model, Entity entity, string ancestorId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = entity.Base; current is not null && seen.Add(current); current = model.Get<Entity>(current)?.Base)
        {
            if (current == ancestorId)
                return true;
        }

        return false;
    }

    private static bool EndTouches(ValidationContext context, string endId, Entity entity)
    {
        if (context.Model.GetDocument(endId)?.Element is not Relation relation)
            return false;
        var lineage = new HashSet<string>(StringComparer.Ordinal) { entity.Id };
        for (var current = entity.Base; current is not null && lineage.Add(current);)
            current = context.Model.Get<Entity>(current)?.Base;
        return relation.Ends.Any(e => lineage.Contains(e.Entity));
    }

    private static bool IsValidColumnKeySegments(ModelSnapshot model, string key)
    {
        var segments = key.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segments.Length == 1 && SpecialColumnKeys.Contains(segment, StringComparer.Ordinal))
                return true;
            if (!model.TryGetEntry(segment, out var entry))
                return false;
            if (i == 0 ? entry.Kind is not ("attribute" or "end") : entry.Kind != "attribute")
                return false;
        }

        return segments.Length > 0;
    }

    private static void CheckPhysicalName(ValidationContext context, Element element, string what, Report report)
    {
        if (context.PhysicalNameKey(element) is not { } key)
            return;
        var first = context.FirstWithPhysicalName(key);
        if (first != report.Document)
        {
            var schema = context.SchemaName(DatabaseOf(element), SchemaOf(element));
            var where = schema.Length == 0 ? "the database" : "schema '" + schema.ToLowerInvariant() + "'";
            report.Add("MQ4002", $"{what} name '{element.Name}' is already used in {where} by {first.Path}.", "/name");
        }
    }

    /// <summary>A physical element's schema, and a column's sequence, must belong to its database (MQ2002).</summary>
    private static void CheckSameDatabase(ValidationContext context, string databaseId, string? schemaId, Report report)
    {
        if (schemaId is not null && context.Model.TryGetEntry(schemaId, out var entry) && entry.Kind == "schema" && entry.OwnerId != databaseId)
            report.Add("MQ2002", $"Schema '{schemaId}' belongs to another database.", "/schema");
    }

    private static string DatabaseOf(Element element) => element switch { Table t => t.Database, View v => v.Database, Sequence s => s.Database, _ => "" };

    private static string? SchemaOf(Element element) => element switch { Table t => t.Schema, View v => v.Schema, Sequence s => s.Schema, _ => null };

    /// <summary>Checks a view file.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="view">The view.</param>
    /// <param name="report">The report.</param>
    public static void CheckView(ValidationContext context, View view, Report report)
    {
        CheckPhysicalName(context, view, "View", report);
        CheckSameDatabase(context, view.Database, view.Schema, report);
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < view.Columns.Count; i++)
        {
            var name = view.Columns[i].Name;
            if (!names.TryAdd(name, i))
                report.Add("MQ4003", string.Create(CultureInfo.InvariantCulture, $"View column name '{name}' is already used at index {names[name]}."), Ptr.At("/columns", i) + "/name");
        }

        if (context.Model.Get<Database>(view.Database) is not { } database)
            return;
        var limit = DialectInfo.IdentifierLimit(database);
        CheckIdentifierLength(database, limit, view.Name, "View", "/name", view.Id, report);
        for (var i = 0; i < view.Columns.Count; i++)
            CheckIdentifierLength(database, limit, view.Columns[i].Name, "View column", Ptr.At("/columns", i) + "/name", view.Id, report);

        var dialect = DialectInfo.Name(database.Dialect);
        if (!view.Body.ContainsKey(dialect) && !view.Body.ContainsKey("*"))
            report.Add("MQ4010", $"View '{view.Name}' has no body for {dialect} (database '{database.Name}') and no \"*\" body.", "/body");
    }

    /// <summary>Checks a sequence file.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="sequence">The sequence.</param>
    /// <param name="report">The report.</param>
    public static void CheckSequence(ValidationContext context, Sequence sequence, Report report)
    {
        CheckPhysicalName(context, sequence, "Sequence", report);
        CheckSameDatabase(context, sequence.Database, sequence.Schema, report);
        if (context.Model.Get<Database>(sequence.Database) is { } database)
            CheckIdentifierLength(database, DialectInfo.IdentifierLimit(database), sequence.Name, "Sequence", "/name", sequence.Id, report);
        if (sequence.Type is not ("int16" or "int32" or "int64" or "decimal"))
            report.Add("MQ3013", $"Sequence '{sequence.Name}' has type '{sequence.Type}'; a sequence is int16, int32, int64 or decimal.", "/type");
        if (sequence.Min is { } min && sequence.Max is { } max && min > max)
            report.Add("MQ3013", $"Sequence '{sequence.Name}' has min greater than max.", "/min");
    }
}
