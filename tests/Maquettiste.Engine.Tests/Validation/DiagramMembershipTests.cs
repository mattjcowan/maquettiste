using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// A diagram's membership (explorer-redesign.md, the domain diagram): <c>explicit</c> (the default, left out of the canonical form)
/// or <c>package</c>, which follows the diagram's package and so needs one (MQ3022). The name never decides it.
/// </summary>
public sealed class DiagramMembershipTests
{
    private const string Path = ".maquettiste/model/diagrams/billing.json";
    private static readonly ICanonicalJson Canonical = TestServices.Json;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<ValidationReport> Validate(ModelSnapshot model) =>
        ValidationFixture.Validator().ValidateAsync(model, ValidationScope.All, null, Ct);

    private static string Write(string json) => Encoding.UTF8.GetString(Canonical.Write(JsonNode.Parse(json)!, "diagram.json", Path));

    private static IReadOnlyList<string> SchemaPointers(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return [.. TestServices.Schemas.Evaluate("diagram.json", doc.RootElement, "model/x.json").Select(d => d.JsonPointer ?? "")];
    }

    [Fact]
    public void The_canonical_form_leaves_out_the_default_and_keeps_package_after_process()
    {
        var id = new ModelBuilder(seed: 22).NewId();
        var package = new ModelBuilder(seed: 23).NewId();
        var explicitJson = Write($$"""{"kind":"diagram","id":"{{id}}","name":"Billing","membership":"explicit","members":[]}""");
        Assert.DoesNotContain("membership", explicitJson, StringComparison.Ordinal);

        var packageJson = Write($$"""{"members":[{"element":"{{package}}","x":10}],"membership":"package","package":"{{package}}","name":"Billing","id":"{{id}}","kind":"diagram"}""");
        Assert.Contains("\"membership\": \"package\"", packageJson, StringComparison.Ordinal);
        Assert.True(packageJson.IndexOf("\"package\":", StringComparison.Ordinal) < packageJson.IndexOf("\"membership\"", StringComparison.Ordinal));
        Assert.True(packageJson.IndexOf("\"membership\"", StringComparison.Ordinal) < packageJson.IndexOf("\"members\"", StringComparison.Ordinal));
    }

    [Fact]
    public void The_schema_accepts_the_two_values_only()
    {
        var id = new ModelBuilder(seed: 24).NewId();
        Assert.Empty(SchemaPointers($$"""{"kind":"diagram","id":"{{id}}","name":"Billing","membership":"package"}"""));
        Assert.NotEmpty(SchemaPointers($$"""{"kind":"diagram","id":"{{id}}","name":"Billing","membership":"domain"}"""));
    }

    [Fact]
    public void The_record_reads_membership_and_defaults_to_explicit()
    {
        var read = JsonSerializer.Deserialize<Diagram>("""{"kind":"diagram","id":"x","name":"Any","membership":"package"}""", EngineJson.Options)!;
        Assert.Equal(DiagramMembership.Package, read.Membership);
        var plain = JsonSerializer.Deserialize<Diagram>("""{"kind":"diagram","id":"x","name":"Any"}""", EngineJson.Options)!;
        Assert.Equal(DiagramMembership.Explicit, plain.Membership);
    }

    [Fact]
    public async Task MQ3022_reports_a_package_diagram_without_a_package()
    {
        var b = new ModelBuilder(seed: 22);
        var diagram = new Diagram { Id = b.NewId(), Name = "Overview", Membership = DiagramMembership.Package };
        b.Add(diagram);

        var diagnostics = (await Validate(b.Build())).Diagnostics;

        var found = Assert.Single(diagnostics, d => d.Rule == "MQ3022");
        Assert.Equal(diagram.Id, found.ElementId);
        Assert.Equal("/membership", found.JsonPointer);
    }

    [Fact]
    public async Task MQ3022_accepts_a_package_diagram_with_its_package_and_an_explicit_diagram_without_one()
    {
        var b = new ModelBuilder(seed: 22);
        var billing = b.Package("Billing");
        b.Add(new Diagram { Id = b.NewId(), Name = "Anything", Package = billing.Id, Membership = DiagramMembership.Package });
        b.Add(new Diagram { Id = b.NewId(), Name = "Loose" });

        var diagnostics = (await Validate(b.Build())).Diagnostics;

        Assert.DoesNotContain(diagnostics, d => d.Rule == "MQ3022");
    }
}
