using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Planning;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Planning;

/// <summary>
/// The <c>x-ref</c> checks of pack parameter values (next-phase-1-design.md section 4.1), which judge a value as the
/// validator judges a model value.
/// </summary>
public sealed class UnitRulesTests
{
    [Fact]
    public void A_keyed_parameter_value_reports_MQ2001_per_missing_id()
    {
        var b = new ModelBuilder(41);
        var customer = b.Entity("Customer").Key("id", "uuid").Attr("total", "decimal");
        var (first, second) = (b.NewId(), b.NewId());
        var model = ModelSnapshot.Create(b.BuildDocuments(), new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "", [], [], 1);
        using var schema = JsonDocument.Parse("""
            { "properties": {
                "both": { "type": "string", "x-ref": { "kind": "attribute" } },
                "last": { "type": "string", "x-ref": { "kind": "attribute" } },
                "twice": { "type": "string", "x-ref": { "kind": "attribute" } },
                "valid": { "type": "string", "x-ref": { "kind": "attribute" } } } }
            """);
        var manifest = new PackManifest { Name = "reports", Version = "1.0.0", Engine = ">=1.0", Units = [], ParameterSchema = schema.RootElement.Clone() };
        var settings = new PackSettings
        {
            Parameters = new Dictionary<string, JsonElement>
            {
                ["both"] = JsonSerializer.SerializeToElement(first + "." + second),
                ["last"] = JsonSerializer.SerializeToElement(customer.Id + "." + second),
                ["twice"] = JsonSerializer.SerializeToElement(first + "." + first),
                ["valid"] = JsonSerializer.SerializeToElement(customer.Id + "." + customer.AttrId("total")),
            },
        };

        var found = UnitRules.Parameters("reports", manifest, settings, ".maquettiste/maquettiste.json", model, ".maquettiste/templates/reports/pack.json");

        Assert.Equal(
            [
                ("MQ2001", "/packs/reports/parameters/both", first),
                ("MQ2001", "/packs/reports/parameters/both", second),
                ("MQ2001", "/packs/reports/parameters/last", second),
                ("MQ2001", "/packs/reports/parameters/twice", first),
            ],
            found.Select(d => (d.Rule, d.JsonPointer!, d.Message.Split('\'')[5])));
    }

    /// <summary><c>each custom:&lt;type&gt;</c> (next-phase-2-design.md section 4.2): the pack schema takes a custom kind's type key,
    /// <see cref="UnitRules.CustomTypeOf"/> reads it, and MQ6021's message names the pattern.</summary>
    [Fact]
    public void Each_custom_scope_is_known()
    {
        var schemas = new Engine.Json.SchemaRegistry();
        bool Valid(string scope)
        {
            using var document = JsonDocument.Parse($$"""{ "name": "p", "version": "1.0.0", "engine": ">=1.0", "units": [ { "id": "a", "template": "t.tpl", "for": "{{scope}}" } ] }""");
            return schemas.Evaluate("pack.json", document.RootElement, "pack.json").Count == 0;
        }

        Assert.True(Valid("each custom:setting"));
        Assert.True(Valid("each custom:payment-term"));
        Assert.False(Valid("each custom:"));
        Assert.False(Valid("each custom:Setting"));
        Assert.False(Valid("each custom"));
        Assert.Equal("setting", UnitRules.CustomTypeOf("each custom:setting"));
        Assert.Null(UnitRules.CustomTypeOf("each custom:"));
        Assert.Null(UnitRules.CustomTypeOf("each entity"));
        Assert.Contains("'each custom:<type>'", UnitRules.UnknownScope("u", "each customs"), StringComparison.Ordinal);
    }
}
