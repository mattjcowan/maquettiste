using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Tests.Resolution;
using static Maquettiste.Engine.Tests.Validation.SeveralTypesKit;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// Several types at both ends of a relation (several-types-design.md decision 53, section 19.12), over <see cref="Validation.SeveralTypesKit"/>
/// with one more relation, Links: its source end covers Comment and Part (navigation <c>sources</c>), its target end Part and Assembly
/// (navigation <c>linkedTo</c>, to-one). MQ3041 no longer refuses it; navigations go both ways, and Part, covered by both ends, holds both;
/// guard paths and <c>in_state</c> read through it from any holder; scenario links, connect steps and a run fit an object of a covered entity;
/// a right's ends are held by every entity the other end covers.
/// </summary>
public sealed class RelationBothEndsTests
{
    private const string Links = "01JRXN00000000000000000951";
    private const string SourceEnd = "01JEND00000000000000000951";
    private const string TargetEnd = "01JEND00000000000000000952";
    private const string TriageFile = "model/processes/triage.json";

    private static void WriteLinks(LoaderHarness h) => h.Write("model/relations/links.json", $$"""
        {"kind":"relation","id":"{{Links}}","name":"Links","package":"{{Sales}}","relationKind":"association","ends":[
          {"id":"{{SourceEnd}}","types":[{"entity":"{{Comment}}"},{"entity":"{{Part}}"}],"role":"source","navigation":"sources","max":"*"},
          {"id":"{{TargetEnd}}","types":[{"entity":"{{Part}}"},{"entity":"{{Assembly}}"}],"role":"target","navigation":"linkedTo","max":1}]}
        """);

    private static Action<LoaderHarness> With(Action<LoaderHarness>? more = null) => h =>
    {
        WriteLinks(h);
        more?.Invoke(h);
    };

    private static Action<LoaderHarness> WithGuard(string condition) => With(h => h.Write(TriageFile, h.Read(TriageFile).Replace("\"guards\":[",
        "\"guards\":[{\"id\":\"01JPRX00000000000000000959\",\"name\":\"extra\",\"condition\":" + condition + "},", StringComparison.Ordinal)));

    private static async Task<List<(string? Pointer, string Message)>> GuardFindings(string condition, string rule) =>
        [.. Of(await ValidateAsync(WithGuard(condition)), rule, "/processes/triage").Where(f => f.Pointer?.StartsWith("/guards/0", StringComparison.Ordinal) == true)];

    private static string Path(params string[] ids) => "{\"op\":\"eq\",\"args\":[{\"path\":[" + string.Join(",", ids.Select(i => "\"" + i + "\"")) + "]},{\"value\":\"x\"}]}";

    [Fact]
    public async Task A_relation_with_several_types_at_both_ends_validates()
    {
        var found = await ValidateAsync(With());
        Assert.Empty(Of(found, "MQ3041"));
        Assert.DoesNotContain(found, d => d.Severity == DiagnosticSeverity.Error && (d.FilePath ?? "").Contains("/relations/links", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Navigations_go_both_ways_and_an_entity_covered_by_both_ends_holds_both()
    {
        var (h, store) = await OpenAsync(With());
        using var _ = h;
        await using var __ = store;
        var model = ResolutionKit.Resolve(store.Current!);
        RNavigation Held(string entity, string name) => model.Entities.Single(e => e.Id == entity).Navigations.Single(n => n.Name == name);

        var linkedTo = Held(Comment, "linkedTo");
        Assert.Null(linkedTo.Target);
        Assert.Equal([Part, Assembly], linkedTo.Targets.Select(t => t.Id));
        Assert.False(linkedTo.IsCollection);
        var sources = Held(Assembly, "sources");
        Assert.Equal([Comment, Part], sources.Targets.Select(t => t.Id));
        Assert.True(sources.IsCollection);

        // Part is covered by both ends, so it holds both navigations; the same navigation objects as Comment's and Assembly's.
        Assert.Same(linkedTo, Held(Part, "linkedTo"));
        Assert.Same(sources, Held(Part, "sources"));
        Assert.DoesNotContain(model.Entities.Single(e => e.Id == Assembly).Navigations, n => n.Name == "linkedTo");
        Assert.True(model.Relations.Single(r => r.Id == Links).Ends.All(e => e.HasSeveralTypes && e.Entity is null));
    }

    [Fact]
    public async Task A_guard_and_in_state_read_through_an_end_whose_other_end_has_several_types()
    {
        // Triage's subject Comment holds linkedTo; Part and Assembly hold label under one id (Item's), not partNo.
        Assert.Empty(await GuardFindings(Path(TargetEnd, Label), "MQ3102"));
        Assert.NotEmpty(await GuardFindings(Path(TargetEnd, PartNo), "MQ3102"));
        // Review governs PurchasedPart, which the target end covers through Part.
        Assert.Empty(await GuardFindings($$"""{"inState":["{{Open}}"],"of":["{{TargetEnd}}"]}""", "MQ3116"));
    }

    // ---- scenarios ----

    private static string Scenario(string objects, string step = "") => $$"""
        {"kind":"scenario","id":"01JSCN00000000000000000951","name":"Links","process":"{{Triage}}",
         "objects":[{{objects}}],"subject":"01JXBJ00000000000000000951","steps":[{{(step.Length > 0 ? step : "{\"id\":\"01JSTP00000000000000000951\",\"event\":\"" + Go + "\"}")}}]}
        """;

    private static string Obj(string id, string name, string entity, string links = "") =>
        $$"""{"id":"01JXBJ0000000000000000095{{id}}","name":"{{name}}","entity":"{{entity}}","links":[{{links}}]}""";

    private static string Link(string end, string id) => $$"""{"end":"{{end}}","object":"01JXBJ0000000000000000095{{id}}"}""";

    private static async Task<List<(string? Pointer, string Message)>> ScenarioFindings(string objects, string step = "") =>
        Of(await ValidateAsync(With(h => h.Write("model/scenarios/triage/links.json", Scenario(objects, step)))), "MQ9305", "/scenarios/");

    [Fact]
    public async Task A_scenario_link_fits_an_object_covered_by_the_end_it_holds_and_a_target_covered_by_the_other()
    {
        // A comment or a part links to a part or an assembly; a purchased part through its base.
        Assert.Empty(await ScenarioFindings(Obj("1", "c1", Comment, Link(TargetEnd, "2")) + "," + Obj("2", "pp1", PurchasedPart)));
        Assert.Empty(await ScenarioFindings(Obj("1", "c1", Comment) + "," + Obj("2", "p1", Part, Link(TargetEnd, "3")) + "," + Obj("3", "a1", Assembly)));
        // The other way: an assembly holds the source end and links to a comment.
        Assert.Empty(await ScenarioFindings(Obj("1", "c1", Comment) + "," + Obj("2", "a1", Assembly, Link(SourceEnd, "1"))));
        // A tool is covered by neither end.
        Assert.Equal(["/objects/0/links/0/end"], (await ScenarioFindings(Obj("1", "c1", Comment, Link(TargetEnd, "2")) + "," + Obj("2", "t1", Tool))).Select(f => f.Pointer));
        Assert.Equal(["/objects/1/links/0/end"], (await ScenarioFindings(Obj("1", "c1", Comment) + "," + Obj("2", "t1", Tool, Link(TargetEnd, "1")))).Select(f => f.Pointer));
    }

    [Fact]
    public async Task A_connect_step_through_such_an_end_checks_the_objects_entity()
    {
        var objects = Obj("1", "c1", Comment) + "," + Obj("2", "t1", Tool) + "," + Obj("3", "a1", Assembly);
        string Step(string end, string id) => $$"""{"id":"01JSTP00000000000000000951","input":"verb","verb":"connect","end":"{{end}}","object":"01JXBJ0000000000000000095{{id}}"}""";
        Assert.Equal(["/steps/0/object"], (await ScenarioFindings(objects, Step(TargetEnd, "2"))).Select(f => f.Pointer));
        Assert.Empty(await ScenarioFindings(objects, Step(TargetEnd, "3")));
    }

    [Fact]
    public async Task A_run_reads_a_link_through_the_relation_and_replays_the_same()
    {
        var (h, store) = await OpenAsync(With(x => x.Write(TriageFile, x.Read(TriageFile).Replace(
            "\"name\":\"onPartOpen\",\"condition\":{\"inState\":[\"" + Open + "\"],\"of\":[\"" + OnPartEnd + "\"]}",
            "\"name\":\"onPartOpen\",\"condition\":{\"inState\":[\"" + Open + "\"],\"of\":[\"" + TargetEnd + "\"]}", StringComparison.Ordinal))));
        using var _ = h;
        await using var __ = store;
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);

        static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
        async Task<bool> AcceptedAsync(string linkedEntity)
        {
            JsonElement[] objects =
            [
                Json(Obj("1", "c1", Comment, Link(TargetEnd, "2"))),
                Json(Obj("2", "x1", linkedEntity)),
            ];
            var call = await store.SimulateProcessAsync(Triage, new SimulateRequest
            {
                Steps = [Json($$"""{"event":"{{CheckOnPart}}"}""")], Objects = objects, Subject = "01JXBJ00000000000000000951",
            }, Ct);
            Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
            Assert.DoesNotContain(call.Value!.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            return call.Value!.Trace[^1].Accepted;
        }

        // A purchased part starts Open in Review, so the guard through linkedTo passes; an assembly, which Review doesn't govern, reads unknown.
        Assert.True(await AcceptedAsync(PurchasedPart));
        Assert.False(await AcceptedAsync(Assembly));
        Assert.True(await AcceptedAsync(PurchasedPart)); // the same answer again
    }

    [Fact]
    public async Task A_rights_ends_are_held_by_every_entity_the_other_end_covers()
    {
        string Parts(string end) => $$"""
            {"kind":"process","id":"01JPRC00000000000000000954","name":"Parts","package":"{{Sales}}","use":"lifecycle",
             "subjects":[{"id":"01JSBJ00000000000000000951","entity":"{{PurchasedPart}}"},{"id":"01JSBJ00000000000000000952","entity":"{{Assembly}}"}],
             "rights":[{"id":"01JRGT00000000000000000951","name":"NoLinks","access":"deny","verbs":["connect"],"ends":["{{end}}"]}],
             "states":[{"id":"01JSTA00000000000000000951","name":"Draft"}],"initial":"01JSTA00000000000000000951"}
            """;

        // Both subjects are covered by the target end, so both hold the source end; only PurchasedPart (through Part) is covered by the source end.
        var held = await ValidateAsync(With(h => h.Write("model/processes/parts.json", Parts(SourceEnd))));
        Assert.DoesNotContain(held, d => d.Severity == DiagnosticSeverity.Error && (d.FilePath ?? "").Contains("/processes/parts", StringComparison.Ordinal));
        var notHeld = await ValidateAsync(With(h => h.Write("model/processes/parts.json", Parts(TargetEnd))));
        Assert.Contains(notHeld, d => d.Severity == DiagnosticSeverity.Error && (d.FilePath ?? "").Contains("/processes/parts", StringComparison.Ordinal)
            && (d.JsonPointer ?? "").StartsWith("/rights/0", StringComparison.Ordinal));
    }
}
