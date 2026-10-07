using System.Globalization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// Physical rules on table, view and sequence files (MQ4001 to MQ4008, MQ4010, MQ4016, MQ4056, MQ4057, MQ4059, MQ4060, and MQ3013, MQ3017, MQ3019 on
/// designed columns).
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
                if (column.NativeType is { } native && !DatabaseObjectRules.CheckColumnType(context, table, column, pointer, report))
                    CheckNativeType(context, database, table, i, native, pointer, report);
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
        for (var i = 0; i < table.Checks.Count; i++)
        {
            if (table.Checks[i].Column is { } checkColumn && !Resolves(checkColumn))
                report.Add("MQ4008", $"Check constraint names column '{checkColumn}', which is not a column of this table.", Ptr.At("/checks", i) + "/column", table.Checks[i].Id);
        }

        if (database is not null)
            CheckDialectFeatures(database, table, report);
    }

    /// <summary>MQ4056: the DDL features of a table file its database's dialect does not have (the sql-ddl pack leaves them out).</summary>
    private static void CheckDialectFeatures(Database database, Table table, Report report)
    {
        var d = database.Dialect;
        var dialect = DialectInfo.Name(d);
        void Add(string what, string pointer, string? id, string leaves = "it") =>
            report.Add("MQ4056", $"{what}, which {dialect} (database '{database.Name}') does not have; the DDL leaves {leaves} out.", pointer, id);

        if (table.PrimaryKey is { Clustered: not null } && d != Dialect.SqlServer)
            Add("The primary key sets clustered", "/primaryKey/clustered", table.Id);
        for (var i = 0; i < table.ForeignKeys.Count; i++)
        {
            var fk = table.ForeignKeys[i];
            var pointer = Ptr.At("/foreignKeys", i);
            var name = fk.Name ?? fk.Id;
            if (fk.Deferrable != Deferrability.NotDeferrable && d is Dialect.SqlServer or Dialect.MySql)
                Add($"Foreign key '{name}' is deferrable", pointer + "/deferrable", fk.Id);
            // Oracle has ON DELETE CASCADE and ON DELETE SET NULL only; MySQL's InnoDB refuses SET DEFAULT.
            if (d == Dialect.Oracle && fk.OnUpdate != ReferentialAction.NoAction)
                Add($"Foreign key '{name}' has ON UPDATE {ResolutionActionName(fk.OnUpdate)}", pointer + "/onUpdate", fk.Id);
            if (d == Dialect.Oracle && fk.OnDelete is ReferentialAction.Restrict or ReferentialAction.SetDefault)
                Add($"Foreign key '{name}' has ON DELETE {ResolutionActionName(fk.OnDelete)}", pointer + "/onDelete", fk.Id);
            if (fk.OnDeleteColumns.Count > 0 && fk.OnDelete is ReferentialAction.SetNull or ReferentialAction.SetDefault && d != Dialect.PostgreSql)
                report.Add("MQ4056", $"Foreign key '{name}' sets only some of its columns on delete (onDeleteColumns), which {dialect} (database '{database.Name}') does not have; the DDL leaves the column list out, so the action sets every column of the key.",
                    pointer + "/onDeleteColumns", fk.Id);
            if (d == Dialect.MySql && fk.OnDelete == ReferentialAction.SetDefault)
                Add($"Foreign key '{name}' has ON DELETE set-default", pointer + "/onDelete", fk.Id);
            if (d == Dialect.MySql && fk.OnUpdate == ReferentialAction.SetDefault)
                Add($"Foreign key '{name}' has ON UPDATE set-default", pointer + "/onUpdate", fk.Id);
        }

        for (var i = 0; i < table.Uniques.Count; i++)
        {
            if (table.Uniques[i].NullsNotDistinct && d != Dialect.PostgreSql)
                Add($"Unique constraint '{table.Uniques[i].Name ?? table.Uniques[i].Id}' sets nullsNotDistinct", Ptr.At("/uniques", i) + "/nullsNotDistinct", table.Uniques[i].Id);
        }

        for (var i = 0; i < table.Columns.Count; i++)
        {
            var column = table.Columns[i];
            var pointer = Ptr.At("/columns", i);
            if (column.Computed is not null && column.ComputedStored && d == Dialect.Oracle)
                Add($"Column '{column.Name}' is a stored computed column (Oracle computes every virtual column on read)", pointer + "/computedStored", column.Id);
            if (column.Generated != ColumnGeneration.Identity || column.Identity is not { } identity)
                continue;
            if (identity.Seed is not null && d == Dialect.Sqlite)
                Add($"Column '{column.Name}' sets an identity seed", pointer + "/identity/seed", column.Id);
            if (identity.Increment is not null && d is Dialect.Sqlite or Dialect.MySql)
                Add($"Column '{column.Name}' sets an identity increment", pointer + "/identity/increment", column.Id);
            if (identity.Always && d is Dialect.Sqlite or Dialect.MySql)
                Add($"Column '{column.Name}' is an identity generated always", pointer + "/identity/always", column.Id);
        }

        for (var i = 0; i < table.Indexes.Count; i++)
        {
            var index = table.Indexes[i];
            var pointer = Ptr.At("/indexes", i);
            var name = index.Name ?? index.Id;
            for (var j = 0; j < index.Columns.Count; j++)
            {
                var ic = index.Columns[j];
                var columnPointer = Ptr.At(pointer + "/columns", j);
                if (ic.Column is null && d == Dialect.SqlServer)
                    Add($"Index '{name}' indexes an expression (index a computed column instead)", columnPointer + "/expression", index.Id, "the index");
                if (ic.Length is not null && d != Dialect.MySql)
                    Add($"Index '{name}' sets a key prefix length", columnPointer + "/length", index.Id);
                if (ic.Length is null && d == Dialect.MySql && ic.Column is { } key
                    && table.Columns.FirstOrDefault(c => c.Id == key) is { } indexed && NeedsKeyLength(indexed))
                    report.Add("MQ4056", $"Index '{name}' indexes the {indexed.Type ?? indexed.NativeType} column '{indexed.Name}' without a key prefix length, which {dialect} (database '{database.Name}') requires; set the index column's length, or the DDL leaves the index out.", columnPointer + "/length", index.Id);
            }

            if (index.Include.Count > 0 && d is not (Dialect.PostgreSql or Dialect.SqlServer))
                Add($"Index '{name}' has include columns", pointer + "/include", index.Id);
            if (index.Where is not null && d is Dialect.MySql or Dialect.Oracle)
                Add($"Index '{name}' is partial (where)", pointer + "/where", index.Id);
            var method = index.Method switch
            {
                IndexMethod.Default => true,
                IndexMethod.Clustered => d is Dialect.SqlServer or Dialect.PostgreSql,
                IndexMethod.Btree => d is Dialect.PostgreSql or Dialect.MySql,
                IndexMethod.Hash => d is Dialect.PostgreSql or Dialect.MySql,
                _ => d == Dialect.PostgreSql,
            };
            if (!method)
                Add($"Index '{name}' uses the method {ResolutionMethodName(index.Method)}", pointer + "/method", index.Id);
        }
    }

    private static string ResolutionMethodName(IndexMethod method) => Resolution.ResolutionValues.Kebab(method);

    private static string ResolutionActionName(ReferentialAction action) => Resolution.ResolutionValues.Kebab(action);

    /// <summary>Whether MySQL indexes a column only with a key prefix length: text and blob types (text, binary that is not fixed).</summary>
    private static bool NeedsKeyLength(Column column)
    {
        if (column.NativeType is { } native)
        {
            var lower = native.ToLowerInvariant();
            return lower.Contains("text", StringComparison.Ordinal) || lower.Contains("blob", StringComparison.Ordinal);
        }

        return column.Type == "text" || (column.Type == "binary" && !column.FixedLength);
    }

    /// <summary>
    /// MQ4006 (warning) for a plain native type name the dialect does not know; MQ4016 (info) for a quoted or qualified name that is
    /// neither a native type nor a name of the model's reference types and enums, reported once per type and database on its first
    /// column (in document path order) with the number of columns that use it.
    /// </summary>
    private static void CheckNativeType(ValidationContext context, Database database, Table table, int index, string native, string pointer, Report report)
    {
        var column = table.Columns[index];
        var dialect = DialectInfo.Name(database.Dialect);
        switch (context.ClassifyNativeType(database.Dialect, native, out var parsed))
        {
            case NativeTypeStatus.Unknown:
                report.Add("MQ4006", $"Native type '{native}' is not known to the {dialect} type map.", pointer + "/nativeType", column.Id);
                break;
            case NativeTypeStatus.UserDefined:
                var uses = context.UserDefinedTypeUses(database.Id, ValidationContext.UserDefinedTypeKey(database, parsed));
                if (uses.Length == 0 || uses[0].Document != report.Document || uses[0].Column != index)
                    break;
                var tables = uses.Select(u => u.Document).Distinct().Count();
                report.Add("MQ4016", string.Create(CultureInfo.InvariantCulture,
                    $"Native type '{native}' names a type the database defines, which the {dialect} type map cannot check; it is used by {Count(uses.Length, "column")} in {Count(tables, "table")} of database '{database.Name}'."),
                    pointer + "/nativeType", column.Id);
                break;
        }
    }

    private static string Count(int n, string noun) => string.Create(CultureInfo.InvariantCulture, $"{n} {noun}{(n == 1 ? "" : "s")}");

    private static void CheckColumnValues(Column column, string pointer, Report report)
    {
        if (column.Type is not { } keyword)
            return;
        if (!BuiltinTypes.IsBuiltin(keyword))
        {
            report.Add("MQ3013", $"Column '{column.Name}' has the unknown type keyword '{keyword}'.", pointer + "/type", column.Id);
            return;
        }

        if (column.Unicode is not null && keyword is not ("string" or "text"))
            report.Add("MQ4057", $"Column '{column.Name}' sets unicode, which only string and text columns have.", pointer + "/unicode", column.Id);
        if (column.FixedLength && keyword is not ("string" or "binary"))
            report.Add("MQ4057", $"Column '{column.Name}' sets fixedLength, which only string and binary columns have.", pointer + "/fixedLength", column.Id);
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

    private static void CheckColumnList(IReadOnlyList<string?> columns, string pointer, string what, string elementId, Func<string, bool> resolves, Report report, string suffix = "")
    {
        for (var i = 0; i < columns.Count; i++)
        {
            // An index expression has no column.
            if (columns[i] is { } column && !resolves(column))
                report.Add("MQ4008", $"{what} names column '{columns[i]}', which is not a column of this table.", Ptr.At(pointer, i) + suffix, elementId);
        }
    }

    private static void CheckForeignKey(ValidationContext context, Table table, int index, Func<string, bool> resolves, Report report)
    {
        var model = context.Model;
        var fk = table.ForeignKeys[index];
        var pointer = Ptr.At("/foreignKeys", index);
        CheckColumnList(fk.Columns, pointer + "/columns", "Foreign key", fk.Id, resolves, report);
        CheckOnDeleteColumns(fk, pointer, report);

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
            if (fk.ReferencesColumns.All(ids.ContainsKey))
                CheckReferencedKey(model, fk, pointer, referenced, ids, report);
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

    /// <summary>
    /// MQ4060: onDeleteColumns is for set-null and set-default, and names columns of the key itself, each once (a database refuses
    /// any other column list).
    /// </summary>
    private static void CheckOnDeleteColumns(ForeignKey fk, string pointer, Report report)
    {
        if (fk.OnDeleteColumns.Count == 0)
            return;
        var name = fk.Name ?? fk.Id;
        if (fk.OnDelete is not (ReferentialAction.SetNull or ReferentialAction.SetDefault))
        {
            report.Add("MQ4060", $"Foreign key '{name}' lists onDeleteColumns, but its onDelete is {ResolutionActionName(fk.OnDelete)}: only set-null and set-default set columns.",
                pointer + "/onDeleteColumns", fk.Id);
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < fk.OnDeleteColumns.Count; i++)
        {
            var column = fk.OnDeleteColumns[i];
            if (!fk.Columns.Contains(column, StringComparer.Ordinal))
                report.Add("MQ4060", $"Foreign key '{name}' lists '{column}' in onDeleteColumns, which is not one of the key's columns.", Ptr.At(pointer + "/onDeleteColumns", i), fk.Id);
            else if (!seen.Add(column))
                report.Add("MQ4060", $"Foreign key '{name}' lists '{column}' twice in onDeleteColumns.", Ptr.At(pointer + "/onDeleteColumns", i), fk.Id);
        }
    }

    /// <summary>
    /// MQ4059: the referenced columns are the referenced table's primary key or one of its unique keys (a unique constraint, or a
    /// unique index without a filter; Oracle takes a constraint only), in any order, each named once; MySQL (and MariaDB) also needs an
    /// index whose first columns are the referenced columns in the order the foreign key names them (the primary key, a unique
    /// constraint or any index). Without referenced columns the key is the primary key (a table without one is the resolver's MQ4008).
    /// </summary>
    private static void CheckReferencedKey(ModelSnapshot model, ForeignKey fk, string pointer, Table referenced, Dictionary<string, Column> ids, Report report)
    {
        if (fk.ReferencesColumns.Count == 0)
            return;
        var names = string.Join(", ", fk.ReferencesColumns.Select(c => ids[c].Name));
        var wanted = fk.ReferencesColumns.ToHashSet(StringComparer.Ordinal);
        if (wanted.Count != fk.ReferencesColumns.Count)
        {
            var twice = fk.ReferencesColumns.GroupBy(c => c, StringComparer.Ordinal).First(g => g.Count() > 1).Key;
            report.Add("MQ4059",
                $"Foreign key '{fk.Name ?? fk.Id}' references ({names}) of table '{referenced.Name}', naming column '{ids[twice].Name}' more than once; reference each column of the key once.",
                pointer + "/referencesColumns", fk.Id);
            return;
        }

        bool Same(IReadOnlyCollection<string> columns) => columns.Count == wanted.Count && columns.ToHashSet(StringComparer.Ordinal).SetEquals(wanted);

        var dialect = model.Get<Database>(referenced.Database)?.Dialect;
        var oracle = dialect == Dialect.Oracle;
        if (!((referenced.PrimaryKey is { } pk && Same(pk.Columns)) || referenced.Uniques.Any(u => Same(u.Columns))
            || (!oracle && referenced.Indexes.Any(i => i.Unique && i.Where is null && i.Columns.All(c => c.Column is not null)
                && Same([.. i.Columns.Select(c => c.Column!)])))))
        {
            report.Add("MQ4059",
                $"Foreign key '{fk.Name ?? fk.Id}' references ({names}) of table '{referenced.Name}', which is neither its primary key nor one of its unique keys; reference its key, or add a unique constraint on those columns.",
                pointer + "/referencesColumns", fk.Id);
            return;
        }

        if (dialect != Dialect.MySql)
            return;
        bool Leads(IReadOnlyList<string> columns) => columns.Count >= fk.ReferencesColumns.Count
            && columns.Take(fk.ReferencesColumns.Count).SequenceEqual(fk.ReferencesColumns, StringComparer.Ordinal);
        if ((referenced.PrimaryKey is { } key && Leads(key.Columns)) || referenced.Uniques.Any(u => Leads(u.Columns))
            || referenced.Indexes.Any(i => i.Columns.TakeWhile(c => c.Column is not null).Select(c => c.Column!).ToList() is var leading && Leads(leading)))
            return;
        report.Add("MQ4059",
            $"Foreign key '{fk.Name ?? fk.Id}' references ({names}) of table '{referenced.Name}' in an order no index of it starts with; MySQL needs an index whose first columns are the referenced ones in the same order: reference them in the key's order, or add such an index.",
            pointer + "/referencesColumns", fk.Id);
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
    internal static void CheckSameDatabase(ValidationContext context, string databaseId, string? schemaId, Report report)
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
        DatabaseObjectRules.CheckDependsOn(context, view.Database, view.DependsOn, report);
        DatabaseObjectRules.CheckCycle(context, view, "View", report);
        void Add(string what, string pointer) =>
            report.Add("MQ4056", $"View '{view.Name}' {what}, which {dialect} (database '{database.Name}') does not have; the DDL leaves it out.", pointer, view.Id);
        if (view.Materialized && database.Dialect is not (Dialect.PostgreSql or Dialect.Oracle))
            Add("is materialized", "/materialized");
        if (view.WithCheckOption && database.Dialect == Dialect.Sqlite)
            Add("has WITH CHECK OPTION", "/withCheckOption");
        else if (view.WithCheckOption && view.Materialized && database.Dialect is Dialect.PostgreSql or Dialect.Oracle)
            Add("is materialized with WITH CHECK OPTION (a materialized view is not written through)", "/withCheckOption");
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
        if (context.Model.Get<Database>(sequence.Database) is { Dialect: Dialect.Sqlite or Dialect.MySql } noSequences)
            report.Add("MQ4056", $"Sequence '{sequence.Name}' is in database '{noSequences.Name}', and {DialectInfo.Name(noSequences.Dialect)} has no sequences; the DDL leaves it out.", "/name", sequence.Id);
    }
}
