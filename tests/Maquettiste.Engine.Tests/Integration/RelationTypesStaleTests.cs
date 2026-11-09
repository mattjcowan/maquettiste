using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Stale output with relation ends of several types (several-types-design.md decision 20 and section 7), through the planner: generate, edit,
/// plan, and the units that read an end's types render again, so incremental output equals a forced run's. An end's types can change while
/// every entry's own keys stay the same: an entity moved under another base inside what the end covers, or a package rename that reorders the
/// roots of <c>anyEntity</c>. Over the <c>process-basics</c> fixture with an invented model: Document and Memo carry <c>approvable</c> (Memo
/// derives from Document), PurchasedPart derives from Part, Comment, all in Sales, and Tool in Purchasing. AttachedTo links a comment to
/// anything approvable or a part; Mentions to any entities. A pack writes how many types each relation end has (which reads the list, not
/// its entities, so only the types key follows it), and each entity's navigations with their targets and its relations.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class RelationTypesStaleTests
{
    private const string Pack = "ends";
    private const string Sales = "01JPKG00000000000000000001";
    private const string Purchasing = "01JPKG00000000000000000601";
    private const string Approvable = "01JSTR00000000000000000601";
    private const string Document = "01JENT00000000000000000601";
    private const string Memo = "01JENT00000000000000000602";
    private const string Part = "01JENT00000000000000000603";
    private const string PurchasedPart = "01JENT00000000000000000604";
    private const string Comment = "01JENT00000000000000000605";
    private const string Tool = "01JENT00000000000000000606";
    private const string AttachedTo = "01JRXN00000000000000000601";
    private const string Mentions = "01JRXN00000000000000000602";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Entity(string id, string name, string package = Sales, string? @base = null, string extra = "") => @base is null
        ? $$"""{"kind":"entity","id":"{{id}}","name":"{{name}}","package":"{{package}}"{{extra}},"key":{"attributes":["{{id[..^3]}}9{{id[^2..]}}"]},"attributes":[{"id":"{{id[..^3]}}9{{id[^2..]}}","name":"id","type":"uuid"}]}"""
        : $$"""{"kind":"entity","id":"{{id}}","name":"{{name}}","package":"{{package}}","base":"{{@base}}"{{extra}}}""";

    private static string Unit(string kind, string id) => Pack + "/" + kind + ":" + id;

    // An entity that no longer derives from its base, with a key of its own.
    private static void Rooted(JsonObject entity)
    {
        var id = entity["id"]!.GetValue<string>();
        var key = id[..^3] + "9" + id[^2..];
        entity.Remove("base");
        entity["key"] = new JsonObject { ["attributes"] = new JsonArray(key) };
        entity["attributes"] = new JsonArray(new JsonObject { ["id"] = key, ["name"] = "id", ["type"] = "uuid" });
    }

    [Fact]
    public async Task A_carrier_moved_from_under_another_carrier_renders_the_relation_and_its_navigations_again()
    {
        await using var repo = await EndsRepo.CreateAsync();
        Assert.Equal("1;2;", repo.Output("relations/AttachedTo.txt"));

        // Memo no longer derives from Document: it carries approvable itself, so it becomes one of the types. Only Memo's file changes.
        await repo.EditAsync(Memo, Rooted);
        var rendered = await repo.RenderedAsync();
        Assert.Contains(Unit("relation", AttachedTo), rendered);
        Assert.Contains(Unit("entity", Comment), rendered);
        await repo.ApplyAsync();
        Assert.Equal("1;3;", repo.Output("relations/AttachedTo.txt"));
        await repo.AssertIncrementalEqualsForcedAsync();
    }

    [Fact]
    public async Task An_entity_moved_from_under_its_base_renders_an_any_entity_relation_again()
    {
        await using var repo = await EndsRepo.CreateAsync();
        Assert.Equal("1;5;", repo.Output("relations/Mentions.txt")); // Order, Document, Part, Comment and Tool

        // PurchasedPart no longer derives from Part: it becomes a root, so one of the types of any entity.
        await repo.EditAsync(PurchasedPart, Rooted);
        var rendered = await repo.RenderedAsync();
        Assert.Contains(Unit("relation", Mentions), rendered);
        Assert.Contains(Unit("entity", Comment), rendered);
        await repo.ApplyAsync();
        Assert.Equal("1;6;", repo.Output("relations/Mentions.txt"));
        await repo.AssertIncrementalEqualsForcedAsync();
    }

    [Fact]
    public async Task A_package_rename_that_reorders_the_roots_renders_an_any_entity_relation_again()
    {
        await using var repo = await EndsRepo.CreateAsync();
        Assert.Contains("mentions:Tool,", repo.Output("entities/Comment.txt"), StringComparison.Ordinal);

        // By qualified name: Purchasing.Tool came first; Zeta.Tool comes last.
        await repo.EditAsync(Purchasing, n => n["name"] = "Zeta");
        Assert.Contains(Unit("entity", Comment), await repo.RenderedAsync());
        await repo.ApplyAsync();
        Assert.Contains(",Tool,;", repo.Output("entities/Comment.txt"), StringComparison.Ordinal);
        await repo.AssertIncrementalEqualsForcedAsync();
    }

    private sealed class EndsRepo : IAsyncDisposable
    {
        private EndsRepo()
        {
            Repo = new TempRepo();
            E2ERepo.CopyTree(Fixtures.Path("models", "process-basics"), Repo.RepoRoot);
            Repo.WriteFile(".maquettiste/model/packages/purchasing.json", $$"""{"kind":"package","id":"{{Purchasing}}","name":"Purchasing"}""");
            Repo.WriteFile(".maquettiste/model/vocabularies/stereotypes/approvable.json",
                $$"""{"kind":"stereotype","id":"{{Approvable}}","key":"approvable","name":"approvable","appliesTo":["entity"]}""");
            Repo.WriteFile(".maquettiste/model/entities/document.json", Entity(Document, "Document", extra: ",\"stereotypes\":[\"approvable\"]"));
            Repo.WriteFile(".maquettiste/model/entities/memo.json", Entity(Memo, "Memo", @base: Document, extra: ",\"stereotypes\":[\"approvable\"]"));
            Repo.WriteFile(".maquettiste/model/entities/part.json", Entity(Part, "Part"));
            Repo.WriteFile(".maquettiste/model/entities/purchased-part.json", Entity(PurchasedPart, "PurchasedPart", @base: Part));
            Repo.WriteFile(".maquettiste/model/entities/comment.json", Entity(Comment, "Comment"));
            Repo.WriteFile(".maquettiste/model/entities/tool.json", Entity(Tool, "Tool", Purchasing));
            Repo.WriteFile(".maquettiste/model/relations/attached-to.json", $$"""
                {"kind":"relation","id":"{{AttachedTo}}","name":"AttachedTo","package":"{{Sales}}","ends":[
                  {"id":"01JEND00000000000000000601","entity":"{{Comment}}","role":"comment","max":"*","navigation":"comments"},
                  {"id":"01JEND00000000000000000602","types":[{"stereotype":"approvable"},{"entity":"{{Part}}"}],"role":"attachedTo","max":1,"navigation":"attachedTo"}]}
                """);
            Repo.WriteFile(".maquettiste/model/relations/mentions.json", $$"""
                {"kind":"relation","id":"{{Mentions}}","name":"Mentions","package":"{{Sales}}","ends":[
                  {"id":"01JEND00000000000000000603","entity":"{{Comment}}","role":"comment","max":"*","navigation":""},
                  {"id":"01JEND00000000000000000604","anyEntity":true,"role":"mentioned","max":"*","navigation":"mentions"}]}
                """);

            var pack = Path.Combine(Repo.ModelRoot, "templates", Pack);
            Directory.CreateDirectory(pack);
            File.WriteAllText(Path.Combine(pack, "pack.json"), """
                {"name": "ends", "version": "1.0.0", "engine": ">=1.0", "units": [
                  {"id": "relation", "template": "relation.scriban", "for": "each relation", "output": "relations/{{ relation.name }}.txt"},
                  {"id": "entity", "template": "entity.scriban", "for": "each entity", "output": "entities/{{ entity.name }}.txt"}]}
                """);
            File.WriteAllText(Path.Combine(pack, "relation.scriban"),
                "{{ for e in relation.ends }}{{ e.types.size }};{{ end }}");
            File.WriteAllText(Path.Combine(pack, "entity.scriban"),
                "{{ for n in entity.navigations }}{{ n.name }}:{{ for t in n.targets }}{{ t.name }},{{ end }};{{ end }}/{{ for r in entity.relations }}{{ r.name }} {{ end }}");

            var settingsPath = Path.Combine(Repo.ModelRoot, "maquettiste.json");
            var node = JsonNode.Parse(File.ReadAllBytes(settingsPath))!.AsObject();
            node["outputs"] = new JsonObject { ["allow"] = new JsonArray(new JsonObject { ["path"] = "gen" }) };
            node["packs"] = new JsonObject { [Pack] = new JsonObject { ["output"] = "gen" } };
            File.WriteAllBytes(settingsPath, TestServices.Json.Write(node, "maquettiste.json", "maquettiste.json"));
            Store = new ModelStore(Repo.Options);
            Service = new GenerationService(Store, Repo.Options);
        }

        public static async Task<EndsRepo> CreateAsync()
        {
            var repo = new EndsRepo();
            await repo.ApplyAsync();
            Assert.Empty(await repo.RenderedAsync());
            return repo;
        }

        private TempRepo Repo { get; }

        private ModelStore Store { get; }

        private GenerationService Service { get; }

        public string Output(string path) => Repo.ReadFile("gen/" + path);

        public async Task ApplyAsync(bool force = false)
        {
            var result = await Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Jobs = 2, Force = force }, null, Ct);
            E2ERepo.AssertOutcome(RunOutcome.Succeeded, result);
        }

        public async Task<List<string>> RenderedAsync()
        {
            var result = await Service.PlanAsync(new GenerationRequest { Jobs = 2 }, null, Ct);
            Assert.Equal(RunOutcome.Succeeded, result.Outcome);
            return [.. result.Plan!.Units.Where(u => !u.Skipped).Select(u => u.Key).Order(StringComparer.Ordinal)];
        }

        public async Task EditAsync(string id, Action<JsonObject> edit)
        {
            var document = await Store.GetElementAsync(id, Ct);
            Assert.NotNull(document);
            var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
            edit(node);
            var result = await Store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
            Assert.True(result.Outcome == SaveOutcome.Saved, result.Outcome + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        }

        /// <summary>The incremental outputs equal a forced run's: nothing was left stale.</summary>
        public async Task AssertIncrementalEqualsForcedAsync()
        {
            var before = Outputs();
            await ApplyAsync(force: true);
            Assert.Equal(before, Outputs());
        }

        private SortedDictionary<string, string> Outputs()
        {
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in Repo.ListFiles().Where(p => p.StartsWith("gen/", StringComparison.Ordinal)))
                files[path] = Repo.ReadFile(path);
            return files;
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            Repo.Dispose();
        }
    }
}
