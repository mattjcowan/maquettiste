using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Stale output of a relation's storage (several-types-design.md section 19.8, "Covered entities that aren't placed"): a covered entity can
/// gain or lose its placement while no key the storage reads moves, and <c>relation.mappings</c> renders again. Over the
/// <c>process-basics</c> fixture with an invented model: a comment attached to a part or anything approvable (Document and Memo carry it),
/// stored as a type and an id in table comments; Memo starts with no placement. A pack writes each relation's links, left-out entities and
/// type codes in database main.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class RelationTypeStorageStaleTests
{
    private const string Sales = "01JPKG00000000000000000001";
    private const string Main = "01JDBS00000000000000000701";
    private const string Approvable = "01JSTR00000000000000000701";
    private const string Part = "01JENT00000000000000000701", Document = "01JENT00000000000000000702", Memo = "01JENT00000000000000000703";
    private const string Comment = "01JENT00000000000000000704";
    private const string AttachedTo = "01JRXN00000000000000000701";
    private const string MemoMapping = "01JMAP00000000000000000703", DocumentMapping = "01JMAP00000000000000000702";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Entity(string id, string name, string extra = "") =>
        $$"""{"kind":"entity","id":"{{id}}","name":"{{name}}","package":"{{Sales}}"{{extra}},"key":{"attributes":["{{id[..^3]}}9{{id[^2..]}}"]},"attributes":[{"id":"{{id[..^3]}}9{{id[^2..]}}","name":"id","type":"uuid"}]}""";

    private static string Table(string id, string name, string more = "") =>
        $$"""{"kind":"table","id":"{{id}}","name":"{{name}}","database":"{{Main}}","columns":[{"id":"{{id[..^3]}}8{{id[^2..]}}","name":"id","type":"uuid","nullable":false}{{more}}],"primaryKey":{"columns":["{{id[..^3]}}8{{id[^2..]}}"]} }""";

    private static string Mapping(string id, string entity, string table, string extra = "") =>
        $$"""{"kind":"mapping","id":"{{id}}","name":"{{id[^3..]}} in main","database":"{{Main}}","entity":"{{entity}}","table":"{{table}}"{{extra}}}""";

    [Fact]
    public async Task A_covered_entity_gaining_or_losing_its_placement_renders_the_relations_storage_again()
    {
        using var repo = new TempRepo();
        E2ERepo.CopyTree(Fixtures.Path("models", "process-basics"), repo.RepoRoot);
        void Model(string path, string text) => repo.WriteFile(".maquettiste/model/" + path, text);
        Model("databases/main/database.json", $$"""{"kind":"database","id":"{{Main}}","name":"main","dialect":"postgresql","byConvention":"none"}""");
        Model("vocabularies/stereotypes/approvable.json", $$"""{"kind":"stereotype","id":"{{Approvable}}","key":"approvable","name":"approvable","appliesTo":["entity"]}""");
        Model("entities/part.json", Entity(Part, "Part"));
        Model("entities/document.json", Entity(Document, "Document", ",\"stereotypes\":[\"approvable\"]"));
        Model("entities/memo.json", Entity(Memo, "Memo", ",\"stereotypes\":[\"approvable\"]"));
        Model("entities/comment.json", Entity(Comment, "Comment"));
        Model("databases/main/tables/parts.json", Table("01JTAB00000000000000000701", "parts"));
        Model("databases/main/tables/documents.json", Table("01JTAB00000000000000000702", "documents"));
        Model("databases/main/tables/memos.json", Table("01JTAB00000000000000000703", "memos"));
        Model("databases/main/tables/comments.json", Table("01JTAB00000000000000000704", "comments",
            """,{"id":"01JCMN00000000000000000705","name":"attached_type","type":"string","length":20},{"id":"01JCMN00000000000000000706","name":"attached_id","type":"uuid"}"""));
        Model("mappings/part.json", Mapping("01JMAP00000000000000000701", Part, "01JTAB00000000000000000701"));
        Model("mappings/document.json", Mapping(DocumentMapping, Document, "01JTAB00000000000000000702"));
        Model("mappings/comment.json", Mapping("01JMAP00000000000000000704", Comment, "01JTAB00000000000000000704"));
        Model("relations/attached-to.json", $$"""
            {"kind":"relation","id":"{{AttachedTo}}","name":"AttachedTo","package":"{{Sales}}","ends":[
              {"id":"01JEND00000000000000000701","entity":"{{Comment}}","role":"comment","max":"*","navigation":"comments"},
              {"id":"01JEND00000000000000000702","types":[{"entity":"{{Part}}"},{"stereotype":"approvable"}],"role":"attachedTo","max":1,"navigation":"attachedTo"}]}
            """);
        Model("mappings/attached-to.json", $$"""
            {"kind":"mapping","id":"01JMAP00000000000000000705","name":"AttachedTo in main","database":"{{Main}}","relation":"{{AttachedTo}}",
             "ends":[{"end":"01JEND00000000000000000702","typeColumn":"01JCMN00000000000000000705","idColumn":"01JCMN00000000000000000706"}]}
            """);
        var pack = Path.Combine(repo.ModelRoot, "templates", "storage");
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, "pack.json"), """
            {"name": "storage", "version": "1.0.0", "engine": ">=1.0", "units": [
              {"id": "relation", "template": "relation.scriban", "for": "each relation", "output": "relations/{{ relation.name }}.txt"}]}
            """);
        File.WriteAllText(Path.Combine(pack, "relation.scriban"),
            """{{ m = relation.mappings["main"] }}{{ if m }}{{ for l in m.links }}{{ l.ends[1].type.name }}={{ l.ends[1].codes | array.join "," }};{{ end }}|{{ for x in m.left_out }}{{ x.entity.name }};{{ end }}|{{ for c in m.type_codes }}{{ c.entity.name }}:{{ c.code }};{{ end }}{{ end }}""");
        var settingsPath = Path.Combine(repo.ModelRoot, "maquettiste.json");
        var settings = JsonNode.Parse(File.ReadAllBytes(settingsPath))!.AsObject();
        settings["outputs"] = new JsonObject { ["allow"] = new JsonArray(new JsonObject { ["path"] = "gen" }) };
        settings["packs"] = new JsonObject { ["storage"] = new JsonObject { ["output"] = "gen" } };
        File.WriteAllBytes(settingsPath, TestServices.Json.Write(settings, "maquettiste.json", "maquettiste.json"));

        await using var store = new ModelStore(repo.Options);
        var service = new GenerationService(store, repo.Options);
        async Task ApplyAsync(bool force = false) =>
            E2ERepo.AssertOutcome(RunOutcome.Succeeded, await service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Jobs = 2, Force = force }, null, Ct));
        async Task<List<string>> RenderedAsync()
        {
            var plan = await service.PlanAsync(new GenerationRequest { Jobs = 2 }, null, Ct);
            Assert.Equal(RunOutcome.Succeeded, plan.Outcome);
            return [.. plan.Plan!.Units.Where(u => !u.Skipped).Select(u => u.Key).Order(StringComparer.Ordinal)];
        }

        async Task SaveAsync(string id, string text)
        {
            var current = store.Current?.GetDocument(id);
            var saved = current is null
                ? await store.CreateAsync(Encoding.UTF8.GetBytes(text), ChangeSource.Editor, Ct)
                : await store.SaveAsync(id, Encoding.UTF8.GetBytes(text), current.Hash, ChangeSource.Editor, Ct);
            Assert.True(saved.Outcome == SaveOutcome.Saved, saved.Outcome + ": " + string.Join("; ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        }

        async Task AssertRendersAgainAsync(string expected)
        {
            Assert.Contains("storage/relation:" + AttachedTo, await RenderedAsync());
            await ApplyAsync();
            Assert.Equal(expected, repo.ReadFile("gen/relations/AttachedTo.txt"));
            await ApplyAsync(force: true);
            Assert.Equal(expected, repo.ReadFile("gen/relations/AttachedTo.txt"));
        }

        await ApplyAsync();
        Assert.Empty(await RenderedAsync());
        Assert.Equal("Part=Part;Document=Document;|Memo;|Document:Document;Part:Part;", repo.ReadFile("gen/relations/AttachedTo.txt"));

        // Memo gets a mapping: it is placed, so it is a target with a code, and no longer left out.
        await SaveAsync(MemoMapping, Mapping(MemoMapping, Memo, "01JTAB00000000000000000703"));
        await AssertRendersAgainAsync("Part=Part;Document=Document;Memo=Memo;||Document:Document;Memo:Memo;Part:Part;");

        // Its mapping set to ignore: left out again.
        await SaveAsync(MemoMapping, Mapping(MemoMapping, Memo, "01JTAB00000000000000000703", ",\"ignore\":true"));
        await AssertRendersAgainAsync("Part=Part;Document=Document;|Memo;|Document:Document;Part:Part;");

        // A code set in a covered entity's mapping.
        await SaveAsync(DocumentMapping, Mapping(DocumentMapping, Document, "01JTAB00000000000000000702", ",\"discriminatorValue\":\"DOC\""));
        await AssertRendersAgainAsync("Part=Part;Document=DOC;|Memo;|Document:DOC;Part:Part;");
    }
}
