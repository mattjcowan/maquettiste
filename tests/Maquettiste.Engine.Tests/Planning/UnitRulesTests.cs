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
}
