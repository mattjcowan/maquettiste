using System.Collections.Immutable;
using System.Text.Json;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Planning;

/// <summary>In-memory models, resolved models and packs for planner tests.</summary>
internal static class PlanningKit
{
    public static EngineOptions Options { get; } = new() { RepoRoot = "/repo", CacheDirectory = "/cache", MaxDegreeOfParallelism = 2 };

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static ResolvedModel Resolve(ModelSnapshot model) => new ModelResolver(Options).ResolveAsync(model, null, Ct).GetAwaiter().GetResult();

    /// <summary>A pack over in-memory units (templates are not read by the planner).</summary>
    public static LoadedPack Pack(string name, IReadOnlyList<PackUnit> units, IReadOnlyList<ScriptSource>? scripts = null, int order = 0) => new(
        name, order, "/nowhere/" + name, ".maquettiste/templates/" + name,
        new PackManifest { Name = name, Version = "1.0.0", Engine = ">=1.0", Units = units },
        new PackSettings(), ImmutableSortedDictionary<string, JsonElement>.Empty, scripts ?? [], "scripts-" + name,
        ImmutableSortedDictionary<string, IReadOnlyDictionary<string, string>>.Empty);

    public static PackUnit Unit(string id, string @for, UnitWhere? where = null) => new() { Id = id, Template = "t.tpl", For = @for, Where = where, Output = "out/x" };

    public static ScriptSource Script(string code) => new(".maquettiste/templates/p/helpers.js", code, ContentHash.Of(code));

    /// <summary>
    /// Packages Sales, Sales.Orders, Catalog; categories Core ⊃ Billing; stereotype audited; entities Customer (Sales, tag pii,
    /// audited, Billing), Order (Sales.Orders), Product (Catalog, tag external, ignored in main), Party (abstract, root), Hidden
    /// (<c>generation["*"].skip</c>); database main.
    /// </summary>
    public static ModelBuilder Shop()
    {
        var b = new ModelBuilder(seed: 3);
        var sales = b.Package("Sales");
        var orders = b.Package("Orders", sales);
        var catalog = b.Package("Catalog");
        var core = b.Category("Core");
        var billing = b.Category("Billing", core);
        b.Stereotype("audited");
        b.Entity("Customer", sales).Key("id", "uuid").Tag("pii").Stereotype("audited").Category(billing);
        b.Entity("Order", orders).Key("id", "uuid");
        var product = b.Entity("Product", catalog).Key("id", "uuid").Tag("external");
        b.Entity("Party").Abstract().Attr("name", "string");
        var hiddenKey = b.NewId();
        b.Add(new Entity
        {
            Id = b.NewId(),
            Name = "Hidden",
            Key = new EntityKey { Attributes = [hiddenKey] },
            Attributes = [new ModelAttribute { Id = hiddenKey, Name = "id", Type = new TypeRef { Builtin = "uuid" } }],
            Generation = new Dictionary<string, GenerationHints> { ["*"] = new() { Skip = true } },
        });
        var main = b.Database("main", Dialect.PostgreSql);
        b.Mapping(main, product).Ignore();
        _ = core;
        return b;
    }
}
