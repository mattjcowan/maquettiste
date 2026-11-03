using System.Data;
using System.Data.Common;
using System.Reflection;
using Maquettiste.Engine.Ledger;

namespace Maquettiste.Engine.Tests.Ledger;

/// <summary>
/// Ledger contract rule 7 (next-phase-1-design.md section 3.3, docs/ledger.md): <c>validate --against</c> and the rest of the engine
/// never read the ledger. The engine holds no database connection, so it cannot: run-once guards read the ledger in the database, and
/// a team's own migrator may read it.
/// </summary>
public sealed class EngineBoundaryTests
{
    private const BindingFlags Every = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void The_engine_holds_no_database_connection_type()
    {
        var engine = typeof(LedgerContract).Assembly;
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in engine.GetTypes())
        {
            void Check(Type? used, string where)
            {
                if (used is not null && UsesConnection(used))
                    found.Add(type.FullName + " " + where + ": " + used);
            }

            Check(type.BaseType, "base");
            foreach (var contract in type.GetInterfaces())
                Check(contract, "interface");
            foreach (var field in type.GetFields(Every))
                Check(field.FieldType, "field " + field.Name);
            foreach (var property in type.GetProperties(Every))
                Check(property.PropertyType, "property " + property.Name);
            foreach (var method in type.GetMethods(Every).Cast<MethodBase>().Concat(type.GetConstructors(Every)))
            {
                Check((method as MethodInfo)?.ReturnType, "returned by " + method.Name);
                foreach (var parameter in method.GetParameters())
                    Check(parameter.ParameterType, "parameter " + parameter.Name + " of " + method.Name);
            }
        }

        Assert.True(found.Count == 0, "The engine never reads a ledger, so no engine signature holds a database connection:\n" + string.Join("\n", found));

        // Nor does it reference a database driver, whose connection would reach a ledger.
        var drivers = engine.GetReferencedAssemblies().Select(a => a.Name ?? "")
            .Where(n => n.StartsWith("Npgsql", StringComparison.Ordinal) || n.StartsWith("Microsoft.Data.", StringComparison.Ordinal)
                || n.StartsWith("MySql", StringComparison.Ordinal) || n.StartsWith("Oracle.", StringComparison.Ordinal) || n == "System.Data.SqlClient")
            .ToList();
        Assert.Empty(drivers);
    }

    /// <summary>Whether a type is, holds or is built from a connection type (an element type, a generic argument).</summary>
    private static bool UsesConnection(Type type)
    {
        if (type.IsByRef || type.IsArray || type.IsPointer)
            return type.GetElementType() is { } element && UsesConnection(element);
        if (type.IsGenericParameter)
            return false;
        if (typeof(DbConnection).IsAssignableFrom(type) || typeof(IDbConnection).IsAssignableFrom(type))
            return true;
        return type.IsGenericType && type.GetGenericArguments().Any(UsesConnection);
    }
}
