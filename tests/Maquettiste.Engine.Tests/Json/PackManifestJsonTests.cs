using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Json;

/// <summary>pack.json <c>uses</c> and an optional <c>units</c> (scope 5.1, library packs; design 5.2 F20).</summary>
public sealed class PackManifestJsonTests
{
    private const string DocumentPath = "templates/records/pack.json";

    [Fact]
    public void Uses_round_trips_and_units_may_be_absent()
    {
        const string library = """{ "name": "naming", "version": "1.4.0", "engine": ">=1.0 <2.0", "parameters": { "suffix": "Record" } }""";
        var naming = JsonSerializer.Deserialize<PackManifest>(library, EngineJson.Options)!;
        Assert.Empty(naming.Units);
        Assert.Empty(naming.Uses);
        Assert.Empty(Evaluate(library, "templates/naming/pack.json"));

        const string records = """
            { "units": [{ "id": "record", "template": "record.scriban", "for": "each entity", "output": "{{ entity.name }}.cs" }],
              "uses": [{ "version": "^1.2.0", "pack": "naming" }], "engine": ">=1.0 <2.0", "version": "1.0.0", "name": "records" }
            """;
        var manifest = JsonSerializer.Deserialize<PackManifest>(records, EngineJson.Options)!;
        var use = Assert.Single(manifest.Uses);
        Assert.Equal(("naming", "^1.2.0"), (use.Pack, use.Version));

        var text = Encoding.UTF8.GetString(TestServices.Json.Serialize(manifest, "pack.json", DocumentPath));
        Assert.True(text.IndexOf("\"description\"", StringComparison.Ordinal) < 0);
        Assert.True(text.IndexOf("\"engine\"", StringComparison.Ordinal) < text.IndexOf("\"uses\"", StringComparison.Ordinal));
        Assert.True(text.IndexOf("\"uses\"", StringComparison.Ordinal) < text.IndexOf("\"units\"", StringComparison.Ordinal));
        Assert.True(text.IndexOf("\"pack\"", StringComparison.Ordinal) < text.IndexOf("\"version\": \"^1.2.0\"", StringComparison.Ordinal));
        var again = JsonSerializer.Deserialize<PackManifest>(text, EngineJson.Options)!;
        Assert.Equal(manifest.Uses, again.Uses);

        var bare = Encoding.UTF8.GetString(TestServices.Json.Serialize(naming, "pack.json", "templates/naming/pack.json"));
        Assert.DoesNotContain("\"uses\"", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("\"units\"", bare, StringComparison.Ordinal);

        const string extra = """{ "name": "records", "version": "1.0.0", "engine": ">=1.0", "uses": [{ "pack": "naming" }] }""";
        Assert.NotEmpty(Evaluate(extra, DocumentPath));
    }

    [Fact]
    public void Exports_take_a_template_or_each_and_value()
    {
        const string json = """
            { "name": "records", "version": "1.0.0", "engine": ">=1.0",
              "units": [{ "id": "record", "template": "record.scriban", "for": "each entity", "output": "{{ exports.class_name }}.cs",
                "exports": { "members": { "value": "{{ pascal item.name }}", "each": "attributes" }, "class_name": "{{ pascal entity.name }}Record" } }] }
            """;
        Assert.Empty(Evaluate(json, DocumentPath));
        var manifest = JsonSerializer.Deserialize<PackManifest>(json, EngineJson.Options)!;
        var exports = Assert.Single(manifest.Units).Exports;
        Assert.Equal(new PackExport { Value = "{{ pascal entity.name }}Record" }, exports["class_name"]);
        Assert.Equal(new PackExport { Value = "{{ pascal item.name }}", Each = "attributes" }, exports["members"]);

        var text = Encoding.UTF8.GetString(TestServices.Json.Serialize(manifest, "pack.json", DocumentPath));
        Assert.Contains("\"class_name\": \"{{ pascal entity.name }}Record\"", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("\"each\"", StringComparison.Ordinal) < text.IndexOf("\"value\"", StringComparison.Ordinal));
        Assert.Equal(exports, JsonSerializer.Deserialize<PackManifest>(text, EngineJson.Options)!.Units[0].Exports);

        var bare = manifest with { Units = [manifest.Units[0] with { Exports = new Dictionary<string, PackExport>() }] };
        Assert.DoesNotContain("\"exports\"", Encoding.UTF8.GetString(TestServices.Json.Serialize(bare, "pack.json", DocumentPath)), StringComparison.Ordinal);

        Assert.NotEmpty(Evaluate(json.Replace("\"class_name\"", "\"ClassName\"", StringComparison.Ordinal), DocumentPath));
        Assert.NotEmpty(Evaluate(json.Replace("\"each\": \"attributes\"", "\"each\": \"attributes\", \"x\": 1", StringComparison.Ordinal), DocumentPath));
        Assert.NotEmpty(Evaluate(json.Replace("\"{{ pascal entity.name }}Record\"", "\"\"", StringComparison.Ordinal), DocumentPath));
    }

    private static IReadOnlyList<Maquettiste.Engine.Diagnostics.Diagnostic> Evaluate(string json, string path)
    {
        using var document = JsonDocument.Parse(json);
        return TestServices.Schemas.Evaluate("pack.json", document.RootElement.Clone(), path);
    }
}
