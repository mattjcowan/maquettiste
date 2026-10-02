using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The reference-data model fixture (step 2) in a temporary repo, with two databases and the <c>reference-data</c> fixture pack:
/// <c>main</c> (postgresql) keeps the project's <c>lookup-table</c> choice, <c>reporting</c> (sqlite) overrides it with
/// <c>check</c>; the <c>check</c> declaration supports collections here so the model validates in both.
/// </summary>
internal sealed class ReferenceDataRepo : IAsyncDisposable
{
    public const string MainDatabaseId = "01JRDB00000000000000000001";
    public const string ReportingDatabaseId = "01JRDB00000000000000000002";
    public const string UnitOfMeasure = "01JBM9S346Q3D25VT4F5V37E3S";
    public const string UnitSeed = "01JB9EE0ZBGJ09TQM83XSSSS6Y";
    public const string KgRow = "01JBS3C4DWA7N36096Q14DR9GP";
    public const string Allergen = "01JRDA00000000000000000001";
    public const string AllergenSeed = "01JRDA00000000000000000010";
    public const string Recipe = "01JRDE00000000000000000001";
    public const string Ingredient = "01JRDE00000000000000000002";
    public const string Contains = "01JRDR00000000000000000001";
    public const string RecipeSeed = "01JRDS00000000000000000001";
    public const string IngredientSeed = "01JRDS00000000000000000002";
    public const string ContainsSeed = "01JRDS00000000000000000003";
    public const string Pack = "reference-data";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ReferenceDataRepo(TempRepo repo)
    {
        Repo = repo;
        Options = repo.Options;
        Open();
    }

    public TempRepo Repo { get; }

    public EngineOptions Options { get; }

    public ModelStore Store { get; private set; } = null!;

    public GenerationService Service { get; private set; } = null!;

    /// <summary>Creates the repo.</summary>
    /// <param name="settings">Edits maquettiste.json after the defaults of this class.</param>
    /// <param name="pack">Whether to install the fixture pack.</param>
    public static ReferenceDataRepo Create(Action<JsonObject>? settings = null, bool pack = true)
    {
        var repo = new TempRepo();
        E2ERepo.CopyTree(Fixtures.Path("models", "reference-data"), repo.RepoRoot);
        if (pack)
            E2ERepo.CopyTree(Fixtures.Path("integration", "packs", "reference-data"), Path.Combine(repo.ModelRoot, "templates", Pack));
        WriteDatabase(repo, "main", MainDatabaseId, "postgresql");
        WriteDatabase(repo, "reporting", ReportingDatabaseId, "sqlite");

        var settingsPath = Path.Combine(repo.ModelRoot, "maquettiste.json");
        var node = JsonNode.Parse(File.ReadAllBytes(settingsPath))!.AsObject();
        node["outputs"] = new JsonObject { ["allow"] = new JsonArray(new JsonObject { ["path"] = "gen" }) };
        node["packs"] = new JsonObject { [Pack] = new JsonObject { ["output"] = "gen" } };
        node["databases"] = new JsonObject { ["reporting"] = new JsonObject { ["referenceStorage"] = new JsonObject { ["strategy"] = "check" } } };
        node["referenceData"]!["strategies"]!["check"]!["collections"] = true;
        settings?.Invoke(node);
        WriteSettings(repo, node);
        return new ReferenceDataRepo(repo);
    }

    public static void WriteSettings(TempRepo repo, JsonObject node) =>
        File.WriteAllBytes(Path.Combine(repo.ModelRoot, "maquettiste.json"), TestServices.Json.Write(node, "maquettiste.json", "maquettiste.json"));

    /// <summary>Edits maquettiste.json on disk and reopens the store (settings are read at load).</summary>
    public async Task EditSettingsAsync(Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(File.ReadAllBytes(Path.Combine(Repo.ModelRoot, "maquettiste.json")))!.AsObject();
        edit(node);
        WriteSettings(Repo, node);
        await Store.DisposeAsync();
        Open();
    }

    private static void WriteDatabase(TempRepo repo, string name, string id, string dialect)
    {
        var path = "model/databases/" + name + "/database.json";
        var node = new JsonObject
        {
            ["$schema"] = "../../../.schema/v1/database.json", ["kind"] = "database", ["id"] = id, ["name"] = name, ["dialect"] = dialect,
        };
        var file = Path.Combine(repo.ModelRoot, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, TestServices.Json.Write(node, "database.json", path));
    }

    private void Open()
    {
        Store = new ModelStore(Options);
        Service = new GenerationService(Store, Options);
    }

    /// <summary>The resolved model of the current snapshot.</summary>
    public async Task<ResolvedModel> ResolveAsync() => ResolutionKit.Resolve(await Store.GetSnapshotAsync(Ct));

    public async Task<GenerationResult> ApplyAsync(bool force = false)
    {
        var result = await Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Jobs = 2, Force = force }, null, Ct);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, result);
        return result;
    }

    public async Task<GenerationPlan> PlanAsync()
    {
        var result = await Service.PlanAsync(new GenerationRequest { Jobs = 2 }, null, Ct);
        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        return result.Plan!;
    }

    /// <summary>Edits an element through the store with its current hash.</summary>
    public async Task EditAsync(string id, Action<JsonObject> edit)
    {
        var document = await Store.GetElementAsync(id, Ct);
        Assert.NotNull(document);
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        edit(node);
        var result = await Store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, result.Outcome + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
    }

    /// <summary>Every file under <c>gen/</c> with its bytes.</summary>
    public SortedDictionary<string, byte[]> Outputs()
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Repo.ListFiles().Where(p => p.StartsWith("gen/", StringComparison.Ordinal)))
            files[path] = File.ReadAllBytes(Repo.PathOf(path));
        return files;
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        Repo.Dispose();
    }
}
