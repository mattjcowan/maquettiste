using System.Text.Json.Nodes;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Loading;

/// <summary>The billing fixture (tests/fixtures/models/billing) loads cleanly: canonical, conventional, schema-valid.</summary>
public sealed class BillingFixtureTests
{
    private static string ModelRoot => Fixtures.Path("models", "billing", ".maquettiste");

    [Fact]
    public async Task Billing_fixture_loads_without_any_diagnostic()
    {
        RewriteFixtureWhenRequested();
        using var harness = new LoaderHarness();
        harness.CopyFixture("models", "billing");
        var loader = harness.NewLoader();

        var result = await loader.LoadAsync(new LoadRequest(null, null, false), null, TestContext.Current.CancellationToken);
        var model = result.Snapshot;

        Assert.Empty(model.LoadDiagnostics);
        Assert.Equal(29, model.Documents.Count);
        Assert.Equal(5, model.All<Entity>().Count);
        Assert.Equal(4, model.All<Relation>().Count);
        Assert.Equal(3, model.All<Stereotype>().Count);
        Assert.Single(model.All<Database>());
        Assert.NotNull(model.Tags);
        Assert.NotNull(model.Categories);
        Assert.Equal("billing", model.Settings.Name);
        Assert.Single(model.Extensions);
        Assert.Equal(".maquettiste/extensions/retention.json", model.Extensions[0].Path);

        var invoice = model.GetDocument(Billing.IdOf(model, "entity", "Invoice"))!;
        Assert.Equal(".maquettiste/model/entities/invoice.json", invoice.Path);
        Assert.StartsWith("# Invoice", invoice.SidecarText, StringComparison.Ordinal);
        Assert.NotEqual(Engine.Hashing.HashBuilder.Of(invoice.Hash, null), invoice.DependencyHash);
        Assert.Equal(Engine.Hashing.ContentHash.Of(await File.ReadAllBytesAsync(harness.Model("model/entities/invoice.json"), TestContext.Current.CancellationToken)), invoice.Hash);

        // References resolve through the index: the invoice is referenced by relations, mappings, the overlay table, the diagram and the
        // query whose result rows are invoices.
        var referrers = model.ReferencesTo(invoice.Element.Id).Select(r => model.GetDocument(r.FromElementId)!.Element.KindName).Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(["diagram", "mapping", "query", "relation", "table"], referrers);
        Assert.Contains(result.Changes.Changed, c => c.Id == invoice.Element.Id);
    }

    [Fact]
    public void Billing_fixture_files_are_canonical()
    {
        var json = TestServices.Json;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(ModelRoot, "model"), "*.json", SearchOption.AllDirectories).Append(Path.Combine(ModelRoot, "maquettiste.json")))
        {
            var bytes = File.ReadAllBytes(file);
            var schemaFile = SchemaFileOf(file, bytes);
            var repoPath = ".maquettiste/" + Path.GetRelativePath(ModelRoot, file).Replace('\\', '/');
            Assert.True(json.IsCanonical(bytes, schemaFile, repoPath), repoPath + " is not canonical; run the tests with MAQUETTISTE_UPDATE_GOLDEN=1 to rewrite it.");
        }
    }

    /// <summary>With <c>MAQUETTISTE_UPDATE_GOLDEN=1</c>, rewrites the fixture's model files in canonical form (maintenance only).</summary>
    private static void RewriteFixtureWhenRequested()
    {
        if (Environment.GetEnvironmentVariable("MAQUETTISTE_UPDATE_GOLDEN") != "1")
            return;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(ModelRoot, "model"), "*.json", SearchOption.AllDirectories).Append(Path.Combine(ModelRoot, "maquettiste.json")))
        {
            var bytes = File.ReadAllBytes(file);
            var repoPath = ".maquettiste/" + Path.GetRelativePath(ModelRoot, file).Replace('\\', '/');
            File.WriteAllBytes(file, TestServices.Json.Write(JsonNode.Parse(bytes)!, SchemaFileOf(file, bytes), repoPath));
        }
    }

    private static string SchemaFileOf(string file, byte[] bytes)
    {
        if (Path.GetFileName(file) == "maquettiste.json" && Path.GetDirectoryName(file) == ModelRoot)
            return ModelPaths.SettingsFile;
        var kind = JsonNode.Parse(bytes)!["kind"]!.GetValue<string>();
        return KindInfo.TryGet(kind, out var info) ? info.SchemaFile : throw new InvalidOperationException(file);
    }
}
