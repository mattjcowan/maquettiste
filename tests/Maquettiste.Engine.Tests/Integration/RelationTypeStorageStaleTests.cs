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

    /// <summary>
    /// Any entity, stored as a type and an id in table comments; links in a link table would key the same way. The storage's keys sit on what
    /// reads it (several-types-design.md section 19.8): a unit per entity that reads only the entity and its navigations renders again for its
    /// own entity alone, while units that read <c>type_joins</c> or <c>relation.mappings</c> follow every covered entity. A one-type end's
    /// derived entity (SpecialComment, in Comment's table) feeds the codes of a type column on that end; a package moved under a by-package
    /// database's package places a left-out entity; a binding, and a column rename, move the storage too.
    /// </summary>
    [Fact]
    public async Task The_storage_keys_sit_on_the_members_that_read_them()
    {
        const string Stored = "01JPKG00000000000000000708", Archive = "01JPKG00000000000000000709";
        const string SpecialComment = "01JENT00000000000000000705", SpecialMapping = "01JMAP00000000000000000706";
        using var repo = new TempRepo();
        E2ERepo.CopyTree(Fixtures.Path("models", "process-basics"), repo.RepoRoot);
        void Model(string path, string text) => repo.WriteFile(".maquettiste/model/" + path, text);
        Model("databases/main/database.json", $$"""{"kind":"database","id":"{{Main}}","name":"main","dialect":"postgresql","packages":[{"package":"{{Stored}}"}]}""");
        Model("packages/stored.json", $$"""{"kind":"package","id":"{{Stored}}","name":"Stored"}""");
        Model("packages/archive.json", $$"""{"kind":"package","id":"{{Archive}}","name":"Archive"}""");
        Model("entities/part.json", Entity(Part, "Part"));
        Model("entities/document.json", Entity(Document, "Document"));
        Model("entities/memo.json", Entity(Memo, "Memo").Replace(Sales, Archive, StringComparison.Ordinal));
        Model("entities/comment.json", Entity(Comment, "Comment"));
        Model("entities/special-comment.json", $$"""{"kind":"entity","id":"{{SpecialComment}}","name":"SpecialComment","package":"{{Archive}}","base":"{{Comment}}"}""");
        Model("databases/main/tables/parts.json", Table("01JTAB00000000000000000701", "parts"));
        Model("databases/main/tables/documents.json", Table("01JTAB00000000000000000702", "documents"));
        Model("databases/main/tables/comments.json", Table("01JTAB00000000000000000704", "comments", """,{"id":"01JCMN00000000000000000707","name":"kind","type":"string","length":20}"""));
        Model("databases/main/tables/links.json", Table("01JTAB00000000000000000705", "links",
            """,{"id":"01JCMN00000000000000000701","name":"from_type","type":"string","length":20},{"id":"01JCMN00000000000000000702","name":"from_id","type":"uuid"},{"id":"01JCMN00000000000000000705","name":"attached_type","type":"string","length":20},{"id":"01JCMN00000000000000000706","name":"attached_id","type":"uuid"}"""));
        Model("mappings/part.json", Mapping("01JMAP00000000000000000701", Part, "01JTAB00000000000000000701"));
        Model("mappings/document.json", Mapping(DocumentMapping, Document, "01JTAB00000000000000000702"));
        Model("mappings/comment.json", Mapping("01JMAP00000000000000000704", Comment, "01JTAB00000000000000000704"));
        Model("relations/attached-to.json", $$"""
            {"kind":"relation","id":"{{AttachedTo}}","name":"AttachedTo","package":"{{Sales}}","ends":[
              {"id":"01JEND00000000000000000701","entity":"{{Comment}}","role":"comment","max":"*","navigation":"comments"},
              {"id":"01JEND00000000000000000702","anyEntity":true,"role":"attachedTo","max":"*","navigation":"attachedTo"}]}
            """);
        Model("mappings/attached-to.json", $$"""
            {"kind":"mapping","id":"01JMAP00000000000000000705","name":"AttachedTo in main","database":"{{Main}}","relation":"{{AttachedTo}}","junctionTable":"01JTAB00000000000000000705",
             "ends":[{"end":"01JEND00000000000000000701","typeColumn":"01JCMN00000000000000000701","idColumn":"01JCMN00000000000000000702"},
                     {"end":"01JEND00000000000000000702","typeColumn":"01JCMN00000000000000000705","idColumn":"01JCMN00000000000000000706"}]}
            """);
        var pack = Path.Combine(repo.ModelRoot, "templates", "storage");
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, "pack.json"), """
            {"name": "storage", "version": "1.0.0", "engine": ">=1.0", "units": [
              {"id": "entity", "template": "entity.scriban", "for": "each entity", "output": "entities/{{ entity.name }}.txt"},
              {"id": "joins", "template": "joins.scriban", "for": "each entity", "output": "joins/{{ entity.name }}.txt"},
              {"id": "relation", "template": "relation.scriban", "for": "each relation", "output": "relations/{{ relation.name }}.txt"}]}
            """);
        File.WriteAllText(Path.Combine(pack, "entity.scriban"), """{{ entity.name }} {{ entity.description }}:{{ for n in entity.navigations }}{{ n.name }};{{ end }}""");
        File.WriteAllText(Path.Combine(pack, "joins.scriban"),
            """{{ m = entity.mappings["main"] }}{{ if m }}{{ for j in m.type_joins }}{{ j.key }}={{ for p in j.value }}{{ p.to_type.name }},{{ end }};{{ end }}{{ end }}""");
        File.WriteAllText(Path.Combine(pack, "relation.scriban"),
            """{{ m = relation.mappings["main"] }}{{ if m }}{{ for l in m.links }}{{ l.ends[0].codes | array.join "," }}>{{ l.ends[1].type.name }} {{ l.ends[1].type_column.name }};{{ end }}|{{ for x in m.left_out }}{{ if x.entity.name == "Memo" }}Memo;{{ end }}{{ end }}{{ end }}""");
        var settingsPath = Path.Combine(repo.ModelRoot, "maquettiste.json");
        var settings = JsonNode.Parse(File.ReadAllBytes(settingsPath))!.AsObject();
        settings["outputs"] = new JsonObject { ["allow"] = new JsonArray(new JsonObject { ["path"] = "gen" }) };
        settings["packs"] = new JsonObject { ["storage"] = new JsonObject { ["output"] = "gen" } };
        File.WriteAllBytes(settingsPath, TestServices.Json.Write(settings, "maquettiste.json", "maquettiste.json"));

        await using var store = new ModelStore(repo.Options);
        var service = new GenerationService(store, repo.Options);
        async Task ApplyAsync() =>
            E2ERepo.AssertOutcome(RunOutcome.Succeeded, await service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Jobs = 2 }, null, Ct));
        async Task<List<string>> RenderedAsync()
        {
            var plan = await service.PlanAsync(new GenerationRequest { Jobs = 2 }, null, Ct);
            Assert.True(plan.Outcome == RunOutcome.Succeeded, string.Join("; ", (await store.ValidateAsync(Diagnostics.ValidationScope.All, Ct)).Diagnostics
                .Where(d => d.Severity == Diagnostics.DiagnosticSeverity.Error).Select(d => d.Rule + " " + d.Message)));
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

        async Task AssertStorageRendersAgainAsync(string expected)
        {
            var rendered = await RenderedAsync();
            Assert.Contains("storage/relation:" + AttachedTo, rendered);
            Assert.Contains("storage/joins:" + Comment, rendered);
            Assert.DoesNotContain("storage/entity:" + Part, rendered);
            await ApplyAsync();
            Assert.Equal(expected, repo.ReadFile("gen/relations/AttachedTo.txt"));
        }

        await ApplyAsync();
        Assert.Empty(await RenderedAsync());
        Assert.Equal("Comment>Comment attached_type;Comment>Document attached_type;Comment>Part attached_type;|Memo;", repo.ReadFile("gen/relations/AttachedTo.txt"));
        Assert.Equal("attachedTo=Comment,Document,Part,;", repo.ReadFile("gen/joins/Comment.txt"));

        // One entity's description: its own units render again, and no other entity's.
        await SaveAsync(Part, Entity(Part, "Part", ",\"description\":\"A part.\""));
        var afterDescription = await RenderedAsync();
        Assert.Contains("storage/entity:" + Part, afterDescription);
        Assert.DoesNotContain(afterDescription, k => k.StartsWith("storage/entity:", StringComparison.Ordinal) && !k.EndsWith(Part, StringComparison.Ordinal));
        Assert.DoesNotContain("storage/joins:" + Document, afterDescription);
        await ApplyAsync();

        // A one-type end's derived entity gains a mapping (in its base's table), then a code: the comment end's codes follow.
        await SaveAsync(SpecialMapping, Mapping(SpecialMapping, SpecialComment, "01JTAB00000000000000000704"));
        await AssertStorageRendersAgainAsync("Comment,SpecialComment>Comment attached_type;Comment,SpecialComment>Document attached_type;Comment,SpecialComment>Part attached_type;|Memo;");
        await SaveAsync(SpecialMapping, Mapping(SpecialMapping, SpecialComment, "01JTAB00000000000000000704", ",\"discriminatorValue\":\"SC\""));
        await AssertStorageRendersAgainAsync("Comment,SC>Comment attached_type;Comment,SC>Document attached_type;Comment,SC>Part attached_type;|Memo;");

        // Memo gains a binding to a table of its own, then loses it.
        Model("databases/main/tables/memos.json", Table("01JTAB00000000000000000703", "memo_rows"));
        await store.LoadAsync(Ct);
        await ApplyAsync();
        var memo = Entity(Memo, "Memo").Replace(Sales, Archive, StringComparison.Ordinal);
        await SaveAsync(Memo, memo[..^1] + $$""","bindings":[{"id":"01JBND00000000000000000703","database":"{{Main}}","source":"01JTAB00000000000000000703","fields":[{"attribute":"01JENT00000000000000000903","column":"id"}]}]}""");
        await AssertStorageRendersAgainAsync("Comment,SC>Memo attached_type;Comment,SC>Comment attached_type;Comment,SC>Document attached_type;Comment,SC>Part attached_type;|");
        await SaveAsync(Memo, memo);
        await AssertStorageRendersAgainAsync("Comment,SC>Comment attached_type;Comment,SC>Document attached_type;Comment,SC>Part attached_type;|Memo;");

        // The link table's type column renamed.
        await SaveAsync("01JTAB00000000000000000705", Table("01JTAB00000000000000000705", "links",
            """,{"id":"01JCMN00000000000000000701","name":"from_type","type":"string","length":20},{"id":"01JCMN00000000000000000702","name":"from_id","type":"uuid"},{"id":"01JCMN00000000000000000705","name":"item_type","type":"string","length":20},{"id":"01JCMN00000000000000000706","name":"attached_id","type":"uuid"}"""));
        await AssertStorageRendersAgainAsync("Comment,SC>Comment item_type;Comment,SC>Document item_type;Comment,SC>Part item_type;|Memo;");

        // Memo's package moves under Stored, which the database takes by package: Memo is placed and no longer left out. (A package that
        // changes what a database takes renders every entity's units again on its own, so only the storage's are checked here.)
        await SaveAsync(Archive, $$"""{"kind":"package","id":"{{Archive}}","name":"Archive","parent":"{{Stored}}"}""");
        Assert.Contains("storage/relation:" + AttachedTo, await RenderedAsync());
        await ApplyAsync();
        Assert.Equal("Comment,SC>Comment item_type;Comment,SC>Document item_type;Comment,SC>Part item_type;Comment,SC>Memo item_type;|",
            repo.ReadFile("gen/relations/AttachedTo.txt"));
    }
}
