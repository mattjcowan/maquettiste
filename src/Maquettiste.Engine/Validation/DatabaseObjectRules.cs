using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// Rules on routine, database type and SQL object files (added 2026-10-01): MQ4017 (nothing to create for the database's dialect),
/// MQ4018 (a type that is neither built-in nor a database type of the same database), MQ4019 (a database object of another database)
/// and MQ4020 (a dependency cycle), plus the schema check (MQ2002) and identifier lengths (MQ4001) the other physical files have.
/// </summary>
internal static class DatabaseObjectRules
{
    /// <summary>Checks a routine file.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="routine">The routine.</param>
    /// <param name="report">The report.</param>
    public static void CheckRoutine(ValidationContext context, Routine routine, Report report)
    {
        PhysicalRules.CheckSameDatabase(context, routine.Database, routine.Schema, report);
        var database = context.Model.Get<Database>(routine.Database);
        if (database is not null)
        {
            var limit = DialectInfo.IdentifierLimit(database);
            PhysicalRules.CheckIdentifierLength(database, limit, routine.Name, "Routine", "/name", routine.Id, report);
            for (var i = 0; i < routine.Parameters.Count; i++)
                PhysicalRules.CheckIdentifierLength(database, limit, routine.Parameters[i].Name, "Parameter", Ptr.At("/parameters", i) + "/name", routine.Id, report);
            var dialect = DialectInfo.Name(database.Dialect);
            if (!routine.Body.ContainsKey(dialect) && !routine.Body.ContainsKey("*"))
                report.Add("MQ4017", $"Routine '{routine.Name}' has no body for {dialect} (database '{database.Name}') and no \"*\" body, so it is not created there.", "/body");
            CheckRoutineAttributes(routine, database, report);
        }

        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < routine.Parameters.Count; i++)
        {
            var parameter = routine.Parameters[i];
            if (!names.TryAdd(parameter.Name, i))
                report.Add("MQ3001", $"Parameter name '{parameter.Name}' is already used at index {names[parameter.Name]}.", Ptr.At("/parameters", i) + "/name");
            CheckType(context, routine.Database, parameter.Type, Ptr.At("/parameters", i) + "/type", $"Parameter '{parameter.Name}'", report);
        }

        if (routine.Returns is { } returns)
        {
            CheckType(context, routine.Database, returns.Type, "/returns/type", "The result", report);
            for (var i = 0; i < (returns.Table?.Count ?? 0); i++)
                CheckType(context, routine.Database, returns.Table![i].Type, Ptr.At("/returns/table", i) + "/type", $"Result column '{returns.Table[i].Name}'", report);
        }

        CheckDependsOn(context, routine.Database, routine.DependsOn, report);
        CheckCycle(context, routine, "Routine", report);
    }

    /// <summary>
    /// A routine's volatility and settings: MQ4062 (deterministic but not immutable, or a volatility on a procedure), MQ4063 (a
    /// PostgreSQL security-definer function that does not pin search_path, so a caller's schema can shadow what it calls) and MQ4056
    /// (SQL Server, which the sql-ddl pack writes routines for, has neither).
    /// </summary>
    private static void CheckRoutineAttributes(Routine routine, Database database, Report report)
    {
        if (routine.Volatility is { } volatility)
        {
            if (routine.RoutineKind == RoutineKind.Procedure)
                report.Add("MQ4062", $"Procedure '{routine.Name}' sets a volatility, which only functions have.", "/volatility", routine.Id);
            else if (routine.Deterministic && volatility != RoutineVolatility.Immutable)
                report.Add("MQ4062", $"Function '{routine.Name}' is deterministic but {Resolution.ResolutionValues.Kebab(volatility)}: a deterministic function is immutable; leave deterministic out or make it immutable.",
                    "/volatility", routine.Id);
        }

        var name = DialectInfo.Name(database.Dialect);
        if (database.Dialect == Dialect.PostgreSql && routine.Security == RoutineSecurity.Definer && !routine.Settings.ContainsKey("search_path"))
            report.Add("MQ4063", $"Security-definer {(routine.RoutineKind == RoutineKind.Procedure ? "procedure" : "function")} '{routine.Name}' does not set search_path: a caller's schema can shadow the tables and functions it names. Set settings.search_path (\"app, pg_temp\").",
                "/security", routine.Id);
        if (database.Dialect != Dialect.SqlServer)
            return;
        // deterministic alone stays quiet, as it always has: SQL Server works out a function's determinism itself.
        if (routine.Volatility is RoutineVolatility.Stable or RoutineVolatility.Immutable)
            report.Add("MQ4056", $"Routine '{routine.Name}' has a volatility, which {name} (database '{database.Name}') does not have; the DDL leaves it out.", "/volatility", routine.Id);
        if (routine.Settings.Count > 0)
            report.Add("MQ4056", $"Routine '{routine.Name}' has settings, which {name} (database '{database.Name}') does not have; the DDL leaves them out.", "/settings", routine.Id);
    }

    /// <summary>Checks a database type file.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="type">The database type.</param>
    /// <param name="report">The report.</param>
    public static void CheckDatabaseType(ValidationContext context, DatabaseType type, Report report)
    {
        PhysicalRules.CheckSameDatabase(context, type.Database, type.Schema, report);
        if (context.Model.Get<Database>(type.Database) is { } database)
        {
            PhysicalRules.CheckIdentifierLength(database, DialectInfo.IdentifierLimit(database), type.Name, "Database type", "/name", type.Id, report);
            var dialect = DialectInfo.Name(database.Dialect);
            var (complete, missing) = type.TypeKind switch
            {
                DatabaseTypeKind.Domain => (type.Base is not null, "a base"),
                DatabaseTypeKind.Enum => (type.Members.Count > 0, "members"),
                DatabaseTypeKind.Composite => (type.Fields.Count > 0, "fields"),
                _ => (type.Subtype is not null, "a subtype"),
            };
            if (!complete && !type.Definition.ContainsKey(dialect) && !type.Definition.ContainsKey("*"))
            {
                report.Add("MQ4017", $"Database type '{type.Name}' ({PlainKind(type.TypeKind)}) has no {missing} and no definition for {dialect} (database '{database.Name}'), so there is nothing to create.",
                    "/typeKind");
            }
        }

        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < type.Fields.Count; i++)
        {
            var field = type.Fields[i];
            if (!names.TryAdd(field.Name, i))
                report.Add("MQ3001", $"Field name '{field.Name}' is already used at index {names[field.Name]}.", Ptr.At("/fields", i) + "/name");
            CheckType(context, type.Database, field.Type, Ptr.At("/fields", i) + "/type", $"Field '{field.Name}'", report);
        }

        CheckCycle(context, type, "Database type", report);
    }

    /// <summary>Checks a SQL object file.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="obj">The SQL object.</param>
    /// <param name="report">The report.</param>
    public static void CheckSqlObject(ValidationContext context, SqlObject obj, Report report)
    {
        PhysicalRules.CheckSameDatabase(context, obj.Database, obj.Schema, report);
        if (context.Model.Get<Database>(obj.Database) is { } database)
        {
            var dialect = DialectInfo.Name(database.Dialect);
            if (!obj.Body.ContainsKey(dialect) && !obj.Body.ContainsKey("*"))
                report.Add("MQ4017", $"SQL object '{obj.Name}' has no statements for {dialect} (database '{database.Name}') and no \"*\" ones, so it is not created there.", "/body");
        }

        CheckDependsOn(context, obj.Database, obj.DependsOn, report);
        CheckCycle(context, obj, "SQL object", report);
    }

    /// <summary>
    /// MQ4019 for a column whose <c>nativeType</c> names a database type of another database; returns whether the native type names a
    /// database type at all (the dialect's type list then has nothing to say about it).
    /// </summary>
    /// <param name="context">The validation context.</param>
    /// <param name="table">The table.</param>
    /// <param name="column">The column.</param>
    /// <param name="pointer">The column's pointer.</param>
    /// <param name="report">The report.</param>
    /// <returns><see langword="true"/> when the native type names a database type.</returns>
    public static bool CheckColumnType(ValidationContext context, Table table, Column column, string pointer, Report report)
    {
        if (column.NativeType is not { } native || context.DatabaseObjects.TypeOfNative(context.Model, table.Database, native) is not { } type)
            return false;
        if (type.Database != table.Database)
        {
            var other = context.Model.Get<Database>(type.Database)?.Name ?? type.Database;
            report.Add("MQ4019", $"Column '{column.Name}' uses database type '{type.Name}' of database '{other}'; a column can only use a type of its own database.",
                pointer + "/nativeType", column.Id);
        }

        return true;
    }

    /// <summary>MQ4018: a type slot is a built-in keyword or the id of a database type of the same database.</summary>
    private static void CheckType(ValidationContext context, string databaseId, string? type, string pointer, string what, Report report)
    {
        if (type is null || BuiltinTypes.IsBuiltin(type))
            return;
        if (context.Model.Get<DatabaseType>(type) is not { } dbType)
        {
            report.Add("MQ4018", $"{what} has type '{type}', which is neither a built-in type nor a database type.", pointer);
            return;
        }

        if (dbType.Database != databaseId)
        {
            var other = context.Model.Get<Database>(dbType.Database)?.Name ?? dbType.Database;
            report.Add("MQ4018", $"{what} has database type '{dbType.Name}' of database '{other}'; use a type of the same database.", pointer);
        }
    }

    /// <summary>MQ4019: every <c>dependsOn</c> entry names an object of the same database (a missing id or a wrong kind is MQ2001 or MQ2002).</summary>
    internal static void CheckDependsOn(ValidationContext context, string databaseId, IReadOnlyList<string> dependsOn, Report report)
    {
        for (var i = 0; i < dependsOn.Count; i++)
        {
            var target = context.Model.Get<Element>(dependsOn[i]);
            var targetDatabase = target switch
            {
                Table t => t.Database,
                View v => v.Database,
                Sequence s => s.Database,
                Routine r => r.Database,
                DatabaseType d => d.Database,
                SqlObject o => o.Database,
                _ => null,
            };
            if (targetDatabase is not null && targetDatabase != databaseId)
            {
                var other = context.Model.Get<Database>(targetDatabase)?.Name ?? targetDatabase;
                report.Add("MQ4019", $"'dependsOn' names {target!.KindName} '{target.Name}' of database '{other}'; an object can only depend on objects of its own database.",
                    Ptr.At("/dependsOn", i));
            }
        }
    }

    /// <summary>MQ4020: the element is part of a dependency cycle.</summary>
    internal static void CheckCycle(ValidationContext context, Element element, string what, Report report)
    {
        if (context.DatabaseObjects.InCycle(element.Id, out var names))
        {
            var pointer = element is DatabaseType ? "/fields" : "/dependsOn";
            report.Add("MQ4020", $"{what} '{element.Name}' is part of a dependency cycle ({names}), so no creation order exists.", pointer);
        }
    }

    private static string PlainKind(DatabaseTypeKind kind) => kind switch
    {
        DatabaseTypeKind.Domain => "domain",
        DatabaseTypeKind.Composite => "composite",
        DatabaseTypeKind.Enum => "enum",
        _ => "range",
    };
}
