using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Model;

/// <summary>
/// The <c>x-ref</c> keyword of custom property schemas (next-phase-1-design.md section 4.1): a well-formed keyword becomes a site
/// of the property, and a malformed or misplaced one is MQ2009 on the extension file, at the keyword or the key at fault.
/// </summary>
public sealed class PropertySchemaSetTests
{
    private const string Path = ".maquettiste/extensions/links.json";

    [Theory]
    // Well formed: no MQ2009, and the property has a site.
    [InlineData("""{ "type": "string", "x-ref": { "kind": "entity" } }""", null)]
    [InlineData("""{ "type": "string", "x-ref": { "kind": ["entity", "value-object"], "optional": true } }""", null)]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "attribute", "of": "$element" } }""", null)]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "row", "of": "<CUSTOMER>" } }""", null)]
    [InlineData("""{ "type": "string", "x-ref": { "kind": ["operation", "outcome", "error", "member", "custom", "custom:billing-run"] } }""", null)]
    [InlineData("""{ "type": "string", "x-ref": { "kind": ["column", "table"], "sameDatabase": true } }""", null)]
    [InlineData("""{ "type": "array", "items": { "x-ref": { "kind": "entity" } } }""", null)]
    [InlineData("""{ "type": "object", "additionalProperties": { "type": "string", "x-ref": { "kind": "entity" } } }""", null)]
    [InlineData("""{ "type": "object", "properties": { "lead": { "type": "string", "x-ref": { "kind": "actor" } } } }""", null)]
    [InlineData("""{ "type": ["string", "null"], "x-ref": { "kind": "entity" } }""", null)]
    // Malformed: the value, an unknown key, kind, custom:<type>, of, sameDatabase, optional.
    [InlineData("""{ "type": "string", "x-ref": "entity" }""", "/properties/p/x-ref")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "entity", "scope": "billing" } }""", "/properties/p/x-ref/scope")]
    [InlineData("""{ "type": "string", "x-ref": { "optional": true } }""", "/properties/p/x-ref")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "widget" } }""", "/properties/p/x-ref/kind")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": [] } }""", "/properties/p/x-ref/kind")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": ["entity", "entity"] } }""", "/properties/p/x-ref/kind")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": ["entity", "custom:Billing_Run"] } }""", "/properties/p/x-ref/kind/1")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "entity", "of": "$element" } }""", "/properties/p/x-ref/of")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "attribute", "of": "Customer" } }""", "/properties/p/x-ref/of")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "row", "of": "01J92P0V0000000000000000ZZ" } }""", "/properties/p/x-ref/of")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "entity", "sameDatabase": true } }""", "/properties/p/x-ref/sameDatabase")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "table", "sameDatabase": "yes" } }""", "/properties/p/x-ref/sameDatabase")]
    [InlineData("""{ "type": "string", "x-ref": { "kind": "entity", "optional": 1 } }""", "/properties/p/x-ref/optional")]
    // Misplaced: not on a string, or under an applicator the walk does not follow.
    [InlineData("""{ "type": "integer", "x-ref": { "kind": "entity" } }""", "/properties/p/x-ref")]
    [InlineData("""{ "type": "array", "x-ref": { "kind": "entity" } }""", "/properties/p/x-ref")]
    [InlineData("""{ "type": "array", "items": { "type": "object", "x-ref": { "kind": "entity" } } }""", "/properties/p/items/x-ref")]
    [InlineData("""{ "type": "string", "allOf": [ { "x-ref": { "kind": "entity" } } ] }""", "/properties/p/allOf/0/x-ref")]
    [InlineData("""{ "type": "string", "if": { "x-ref": { "kind": "entity" } } }""", "/properties/p/if/x-ref")]
    [InlineData("""{ "type": "object", "patternProperties": { "^a": { "type": "string", "x-ref": { "kind": "entity" } } } }""", "/properties/p/patternProperties/^a/x-ref")]
    [InlineData("""{ "type": "string", "$defs": { "id": { "type": "string", "x-ref": { "kind": "entity" } } } }""", "/properties/p/$defs/id/x-ref")]
    public void Xref_keywords_parse_and_malformed_ones_are_MQ2009(string schema, string? pointer)
    {
        var b = new ModelBuilder(21);
        var customer = b.Entity("Customer").Key("id", "uuid");
        var properties = "{ \"p\": " + schema.Replace("<CUSTOMER>", customer.Id, StringComparison.Ordinal) + " }";
        var model = ModelSnapshot.Create(b.BuildDocuments(), new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "",
            [Extension(properties)], [], 1);

        var diagnostics = new List<Diagnostic>();
        _ = new ExtensionSet(model, diagnostics);

        var found = diagnostics.Where(d => d.Rule == "MQ2009").ToList();
        if (pointer is null)
        {
            Assert.Empty(found);
            Assert.True(model.PropertySchemas.Sources.Single().Refs.ContainsKey("p"));
            return;
        }

        var problem = Assert.Single(found);
        Assert.Equal((Path, pointer, DiagnosticSeverity.Error), (problem.FilePath, problem.JsonPointer, problem.Severity));
        Assert.StartsWith("x-ref", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Patterns_beside_a_typed_additionalProperties_match_without_backtracking()
    {
        // ^(a+)+$ backtracks exponentially on a run of a then b; the link walk matches it in linear time, with no
        // timeout.
        const string properties = """
            { "p": { "type": "object", "patternProperties": { "^(a+)+$": { "type": "string" } },
                     "additionalProperties": { "type": "string", "x-ref": { "kind": "entity" } } } }
            """;
        var node = PropertySchemaSet.Create([Extension(properties)], null).Sources.Single().Refs["p"];

        Assert.NotNull(node.Member(new string('a', 4096) + "b"));
        Assert.Null(node.Member(new string('a', 4096)));
        Assert.NotNull(node.Member("owner"));
    }

    [Fact]
    public void A_pattern_that_needs_backtracking_beside_a_typed_additionalProperties_is_MQ2009()
    {
        const string properties = """
            { "p": { "type": "object", "patternProperties": { "^(?!x-)": { "type": "string" } },
                     "additionalProperties": { "type": "string", "x-ref": { "kind": "entity" } } } }
            """;
        var b = new ModelBuilder(22);
        _ = b.Entity("Customer").Key("id", "uuid");
        var model = ModelSnapshot.Create(b.BuildDocuments(), new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "",
            [Extension(properties)], [], 1);

        var diagnostics = new List<Diagnostic>();
        _ = new ExtensionSet(model, diagnostics);

        var problem = Assert.Single(diagnostics, d => d.Rule == "MQ2009");
        Assert.Equal((Path, "/properties/p/patternProperties/^(?!x-)"), (problem.FilePath, problem.JsonPointer));
        Assert.False(model.PropertySchemas.Sources.Single().Refs.ContainsKey("p"));
    }

    [Fact]
    public void An_unchanged_set_of_files_reuses_the_set_and_marks_translatable_names()
    {
        const string properties = """{ "title": { "type": "string", "x-translatable": true }, "owner": { "type": "string", "x-ref": { "kind": "entity" } } }""";
        var first = PropertySchemaSet.Create([Extension(properties)], null);
        var again = PropertySchemaSet.Create([Extension(properties)], first);
        var changed = PropertySchemaSet.Create([Extension(properties.Replace("entity", "actor", StringComparison.Ordinal))], first);

        Assert.Same(first, again);
        Assert.NotSame(first, changed);
        Assert.NotEqual(first.Hash, changed.Hash);
        Assert.Equal(["title"], first.Sources.Single().Translatable);
        Assert.Equal(["owner"], first.Sources.Single().Refs.Keys);
        Assert.Equal("extension:links", first.Sources.Single().Schema);
        Assert.Equal(PropertySchemaSet.Empty.Hash, PropertySchemaSet.Create([], null).Hash);
    }

    [Fact]
    public async Task A_package_default_types_the_properties_of_its_members()
    {
        // next-phase-2-design.md section 3.1: a stereotype a package default gives types its members' properties as an own one does:
        // For and Sites take the element's effective keys, so the index records the link and the validator checks the parameters.
        var b = new ModelBuilder(23);
        b.Stereotype("owned").AppliesTo("entity").Parameters("""{ "owner": { "type": "string", "x-ref": { "kind": "entity" } } }""", "owner");
        var billing = b.Package("Billing");
        var customer = b.Entity("Customer").Key("id", "uuid");
        var invoice = b.Entity("Invoice", billing).Key("id", "uuid").Property("owner", customer.Id);
        var payment = b.Entity("Payment", billing).Key("id", "uuid");
        var stray = b.Entity("Stray").Key("id", "uuid").Property("owner", customer.Id);
        var model = StereotypeDefaultsTests.Rebuild(b, e => e is Package p && p.Id == billing.Id ? p with { DefaultStereotypes = ["owned"] } : e);

        var element = model.Get<Entity>(invoice.Id)!;
        var keys = model.PropertyKeys(element);
        Assert.Equal(["owned"], keys);
        Assert.Contains(model.PropertySchemas.For(PropertyHost.Of(element, keys).First()), s => s.StereotypeKey == "owned");
        Assert.Equal(["/properties/owner"], model.PropertySchemas.Sites(element, keys).Select(site => site.Pointer));
        Assert.Empty(model.PropertySchemas.Sites(element, null)); // its own list alone types nothing
        Assert.Empty(model.PropertyKeys(model.Get<Entity>(stray.Id)!)!); // outside the package: no default

        Assert.Contains(model.ReferencesTo(customer.Id), r => r is { Field: "owner", Schema: "stereotype:owned" } && r.FromElementId == invoice.Id);
        Assert.DoesNotContain(model.ReferencesTo(customer.Id), r => r.FromElementId == stray.Id);

        var report = await Validation.ValidationFixture.Validator().ValidateAsync(model, ValidationScope.All, null, TestContext.Current.CancellationToken);
        var missing = Assert.Single(report.Diagnostics, d => d.Rule == "MQ2018");
        Assert.Equal((payment.Id, ""), (missing.ElementId, missing.JsonPointer ?? ""));
    }

    internal static ExtensionDocument Extension(string properties, string name = "links", IReadOnlyList<string>? kinds = null,
        IReadOnlyList<string>? stereotypes = null, string? path = null)
    {
        using var document = JsonDocument.Parse(properties);
        var schema = new ExtensionSchema
        {
            Name = name,
            AppliesTo = new ExtensionTarget { Kinds = kinds ?? ["entity"], Stereotypes = stereotypes ?? [] },
            Properties = document.RootElement.Clone(),
        };
        return new ExtensionDocument(schema, path ?? Path, ContentHash.Of(string.Join("\n", name, string.Join(",", schema.AppliesTo.Kinds),
            string.Join(",", schema.AppliesTo.Stereotypes), properties)));
    }
}
