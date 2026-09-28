using Maquettiste.Engine.Json;

namespace Maquettiste.Testing;

/// <summary>Engine services shared by tests: the schema registry is built once per test process.</summary>
public static class TestServices
{
    private static readonly Lazy<SchemaRegistry> LazySchemas = new(() => new SchemaRegistry());

    /// <summary>The schema registry.</summary>
    public static ISchemaRegistry Schemas => LazySchemas.Value;

    /// <summary>A canonical writer over <see cref="Schemas"/>.</summary>
    public static ICanonicalJson Json { get; } = new CanonicalJson(LazySchemas.Value);
}
