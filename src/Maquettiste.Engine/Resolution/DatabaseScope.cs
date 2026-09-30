using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// Which entities a database holds (D6, D46): the entities its convention takes (<see cref="Database.ByConvention"/> over its
/// <see cref="Database.Packages"/>), plus every entity a mapping element names, less the entities a mapping ignores. The resolver
/// places entities with it and the validator's MQ4012 asks it, so both agree.
/// </summary>
internal static class DatabaseScope
{
    /// <summary>Whether the database's convention takes the entities of a package.</summary>
    /// <param name="model">The model (for the package parents).</param>
    /// <param name="database">The database.</param>
    /// <param name="packageId">The entity's package; <see langword="null"/> for the model root.</param>
    public static bool ByConvention(ModelSnapshot model, Database database, string? packageId)
    {
        switch (database.ByConvention)
        {
            case ConventionMapping.All:
            case null when database.Packages.Count == 0:
                return true;
            case ConventionMapping.None:
                return false;
        }

        return NearestEntry(model, database, packageId) is not null;
    }

    /// <summary>The database's convention package entry nearest to a package, walking up the package tree.</summary>
    /// <param name="model">The model (for the package parents).</param>
    /// <param name="database">The database.</param>
    /// <param name="packageId">The package; <see langword="null"/> for the model root.</param>
    public static ConventionPackage? NearestEntry(ModelSnapshot model, Database database, string? packageId)
    {
        if (database.Packages.Count == 0)
            return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var id = packageId; id is not null && seen.Add(id); id = model.Get<Package>(id)?.Parent)
        {
            for (var i = 0; i < database.Packages.Count; i++)
            {
                if (string.Equals(database.Packages[i].Package, id, StringComparison.Ordinal))
                    return database.Packages[i];
            }
        }

        return null;
    }

    /// <summary>
    /// The schema id a conventional entity table goes to (erratum E26): the entity's mapping element, else the nearest convention
    /// package entry that names a schema, else <see langword="null"/> (the database's default schema).
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="database">The database.</param>
    /// <param name="packageId">The entity's package.</param>
    /// <param name="mapping">The entity's mapping for the database, if any.</param>
    public static string? SchemaFor(ModelSnapshot model, Database database, string? packageId, Mapping? mapping)
    {
        if (mapping?.Schema is { } explicitSchema)
            return explicitSchema;
        if (database.Packages.Count == 0)
            return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var id = packageId; id is not null && seen.Add(id); id = model.Get<Package>(id)?.Parent)
        {
            for (var i = 0; i < database.Packages.Count; i++)
            {
                if (string.Equals(database.Packages[i].Package, id, StringComparison.Ordinal) && database.Packages[i].Schema is { } schema)
                    return schema;
            }
        }

        return null;
    }

    /// <summary>Whether an entity lands in the database: named by a mapping that does not ignore it, or taken by convention.</summary>
    /// <param name="model">The model.</param>
    /// <param name="database">The database.</param>
    /// <param name="packageId">The entity's package.</param>
    /// <param name="mapping">The entity's mapping for the database, if any.</param>
    public static bool Places(ModelSnapshot model, Database database, string? packageId, Mapping? mapping) =>
        mapping is null ? ByConvention(model, database, packageId) : !mapping.Ignore;
}
