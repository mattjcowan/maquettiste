using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;
using static Maquettiste.Engine.Tests.Validation.SeveralTypesKit;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// Expressions, processes and scenarios through relation ends with several types (several-types-design.md decisions 13 to 18, section 8):
/// guard paths read what every type holds under one id; <c>in_state</c> is valid when the lifecycle can govern one covered entity, and a run
/// still answers unknown for an object it doesn't govern, through both kinds of end; owners and actor paths at and beyond such an end; scenario
/// links and connect steps; rights' ends held through a covered type. Over <see cref="Validation.SeveralTypesKit"/>.
/// </summary>
public sealed class RelationTypesProcessTests
{
    private const string TriageFile = "model/processes/triage.json";

    private static Action<LoaderHarness> WithGuard(string condition) => h => h.Write(TriageFile, h.Read(TriageFile).Replace("\"guards\":[",
        "\"guards\":[{\"id\":\"01JPRX00000000000000000929\",\"name\":\"extra\",\"condition\":" + condition + "},", StringComparison.Ordinal));

    private static async Task<List<(string? Pointer, string Message)>> GuardFindings(string condition, string rule)
    {
        var found = await ValidateAsync(WithGuard(condition));
        return [.. Of(found, rule, "/processes/triage").Where(f => f.Pointer?.StartsWith("/guards/0", StringComparison.Ordinal) == true)];
    }

    private static string Path(params string[] ids) => "{\"op\":\"eq\",\"args\":[{\"path\":[" + string.Join(",", ids.Select(i => "\"" + i + "\"")) + "]},{\"value\":\"x\"}]}";

    [Fact]
    public async Task A_guard_reads_what_every_type_holds_and_no_stereotype_attribute_through_the_end()
    {
        Assert.Empty(await GuardFindings(Path(AttachedEnd, Label), "MQ3102"));
        Assert.NotEmpty(await GuardFindings(Path(AttachedEnd, PartNo), "MQ3102"));
        Assert.NotEmpty(await GuardFindings(Path(ConcernsEnd, ApprovalCode), "MQ3102"));
        Assert.NotEmpty(await GuardFindings(Path(TaggedEnd, Label), "MQ3102")); // a to-many end, as today
    }

    [Fact]
    public async Task In_state_through_the_end_is_valid_when_the_lifecycle_can_govern_one_covered_entity()
    {
        // The model reads Review through Concerns (Document carries approvable) and through AttachedTo (Part's derived PurchasedPart is governed).
        Assert.Empty(await GuardFindings($$"""{"inState":["{{Open}}"],"of":["{{AttachedEnd}}"]}""", "MQ3116"));
        var found = await ValidateAsync(h =>
        {
            h.Write("model/processes/tooling.json", $$"""
                {"kind":"process","id":"01JPRC00000000000000000903","name":"Tooling","package":"{{Sales}}","use":"lifecycle",
                 "subjects":[{"id":"01JSBJ00000000000000000931","entity":"{{Tool}}"}],"states":[{"id":"01JSTA00000000000000000931","name":"Ready"}],"initial":"01JSTA00000000000000000931"}
                """);
            WithGuard($$"""{"inState":["01JSTA00000000000000000931"],"of":["{{AttachedEnd}}"]}""")(h);
        });
        Assert.Equal([("/guards/0/condition", "Lifecycle Tooling can't govern any type the object in_state reaches: Part, Assembly, or an entity derived from one.")],
            Of(found, "MQ3116", "/processes/triage"));
    }

    [Fact]
    public async Task An_owner_at_such_an_end_and_an_actor_path_beyond_it()
    {
        // Triage's subject names its owner at AttachedTo's end with several types.
        Assert.Empty(Of(await ValidateAsync(), "MQ9606"));
        const string Actor = "01JACT00000000000000000901";
        Action<LoaderHarness> Using(string step) => h =>
        {
            h.Write("model/actors/holder.json", $$"""{"kind":"actor","id":"{{Actor}}","name":"Holder","type":"person","relative":{"path":["{{AttachedEnd}}","{{step}}"]} }""");
            h.Write(TriageFile, h.Read(TriageFile).Replace("\"name\":\"go\"}", "\"name\":\"go\",\"actors\":[\"" + Actor + "\"]}", StringComparison.Ordinal));
        };
        Assert.Empty(Of(await ValidateAsync(Using(Label)), "MQ9604"));
        var beyond = Of(await ValidateAsync(Using(PartNo)), "MQ9604");
        Assert.Contains(beyond, f => f.Message.Contains("names 'Part.partNo', which entities 'Part', 'Assembly' do not all hold under one id", StringComparison.Ordinal));
    }

    // ---- in_state answers, pinned (decision 15) ----

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static async Task<StepTrace> RunAsync(ModelStore store, string ev, string concerned, string concernedEntity)
    {
        JsonElement[] objects =
        [
            Json($$"""{"id":"01JXBJ00000000000000000901","name":"c1","entity":"{{Comment}}","links":[{"end":"{{ConcernsEnd}}","object":"01JXBJ00000000000000000902"},{"end":"{{OnPartEnd}}","object":"01JXBJ00000000000000000903"}]}"""),
            Json($$"""{"id":"01JXBJ00000000000000000902","name":"{{concerned}}","entity":"{{concernedEntity}}"}"""),
            Json($$"""{"id":"01JXBJ00000000000000000903","name":"p2","entity":"{{Part}}"}"""),
        ];
        var call = await store.SimulateProcessAsync(Triage, new SimulateRequest { Steps = [Json($$"""{"event":"{{ev}}"}""")], Objects = objects, Subject = "01JXBJ00000000000000000901" }, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(call.Value!.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return call.Value!.Trace[^1];
    }

    [Fact]
    public async Task In_state_of_an_object_the_lifecycle_does_not_govern_is_unknown_through_both_kinds_of_end()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        // Through Concerns to a Part, and through the one-type OnPart to a Part whose derived PurchasedPart is governed: unknown, with no note.
        var go = await RunAsync(store, Go, "p1", Part);
        Assert.Equal("unknown", go.ValuesChanged![TextAttribute].GetString());
        Assert.Equal("unknown", go.ValuesChanged![Note2].GetString());
        Assert.Null(go.Notes);

        // A guard reading in_state(…) or not in_state(…) is false for such an object.
        Assert.False((await RunAsync(store, Check, "p1", Part)).Accepted);
        Assert.False((await RunAsync(store, CheckNot, "p1", Part)).Accepted);
        Assert.False((await RunAsync(store, CheckOnPart, "p1", Part)).Accepted);

        // A governed object reached through the same end reads its state.
        var governed = await RunAsync(store, Go, "d1", Document);
        Assert.Equal("yes", governed.ValuesChanged![TextAttribute].GetString());
        Assert.True((await RunAsync(store, Check, "d1", Document)).Accepted);
    }

    // ---- scenarios ----

    private static string Scenario(string objects, string step = "") => $$"""
        {"kind":"scenario","id":"01JSCN00000000000000000901","name":"Links","process":"{{Triage}}",
         "objects":[{{objects}}],"subject":"01JXBJ00000000000000000901","steps":[{{(step.Length > 0 ? step : "{\"id\":\"01JSTP00000000000000000901\",\"event\":\"" + Go + "\"}")}}]}
        """;

    private static string Obj(string id, string name, string entity, string links = "") =>
        $$"""{"id":"01JXBJ0000000000000000090{{id}}","name":"{{name}}","entity":"{{entity}}","links":[{{links}}]}""";

    private static string Link(string end, string id) => $$"""{"end":"{{end}}","object":"01JXBJ0000000000000000090{{id}}"}""";

    private static async Task<List<(string? Pointer, string Message)>> ScenarioFindings(string objects, string step = "") =>
        Of(await ValidateAsync(h => h.Write("model/scenarios/triage/links.json", Scenario(objects, step))), "MQ9305", "/scenarios/");

    [Fact]
    public async Task A_link_through_such_an_end_fits_an_object_of_a_covered_entity()
    {
        var tool = Obj("2", "t1", Tool);
        Assert.Equal([("/objects/0/links/0/end", "Scenario 'Links': Object 'c1' links to 't1' through end 'AttachedTo.attachedTo', which leads to Part, Assembly, or an entity derived from one, not to entity 'Tool'; link to an object of one of those, or through another end.")],
            await ScenarioFindings(Obj("1", "c1", Comment, Link(AttachedEnd, "2")) + "," + tool));
        Assert.Empty(await ScenarioFindings(Obj("1", "c1", Comment, Link(AttachedEnd, "2")) + "," + Obj("2", "pp1", PurchasedPart)));

        // The other side: a covered entity holds the comment end; another entity doesn't.
        Assert.Empty(await ScenarioFindings(Obj("1", "c1", Comment) + "," + Obj("2", "a1", Assembly, Link(CommentsEnd, "1"))));
        Assert.Equal(["/objects/1/links/0/end"], (await ScenarioFindings(Obj("1", "c1", Comment) + "," + Obj("2", "t1", Tool, Link(CommentsEnd, "1")))).Select(f => f.Pointer));
    }

    [Fact]
    public async Task A_connect_step_through_such_an_end_checks_the_objects_entity_and_a_one_type_end_keeps_todays_checks()
    {
        var objects = Obj("1", "c1", Comment, Link(AttachedEnd, "3")) + "," + Obj("2", "t1", Tool) + "," + Obj("3", "p1", Part);
        string Step(string verb, string end, string id) => $$"""{"id":"01JSTP00000000000000000901","input":"verb","verb":"{{verb}}","end":"{{end}}","object":"01JXBJ0000000000000000090{{id}}"}""";
        Assert.Equal([("/steps/0/object", "Step 1: object 't1' is an object of entity 'Tool', but end 'AttachedTo.attachedTo' leads to Part, Assembly, or an entity derived from one; connect an object of one of those, or use another end.")],
            (await ScenarioFindings(objects, Step("connect", AttachedEnd, "2"))).Select(f => (f.Pointer, f.Message.Replace("Step 1 ", "Step 1: ", StringComparison.Ordinal))).ToList());
        Assert.Empty(await ScenarioFindings(objects, Step("connect", AttachedEnd, "3")));
        Assert.Empty(await ScenarioFindings(objects, Step("disconnect", AttachedEnd, "3")));
        Assert.Empty(await ScenarioFindings(objects, Step("freeze", AttachedEnd, "3").Replace(",\"end\":\"" + AttachedEnd + "\",\"object\":\"01JXBJ00000000000000000903\"", "", StringComparison.Ordinal)));

        // Through the one-type OnPart end, connecting a Tool gets no new finding, as before.
        Assert.Empty(await ScenarioFindings(objects, Step("connect", OnPartEnd, "2")));
    }

    [Fact]
    public async Task A_rights_ends_count_the_ends_held_through_a_covered_type()
    {
        string Parts(string end) => $$"""
            {"kind":"process","id":"01JPRC00000000000000000904","name":"Parts","package":"{{Sales}}","use":"lifecycle",
             "subjects":[{"id":"01JSBJ00000000000000000941","entity":"{{PurchasedPart}}"},{"id":"01JSBJ00000000000000000942","entity":"{{Assembly}}"}],
             "rights":[{"id":"01JRGT00000000000000000941","name":"NoLinks","access":"deny","verbs":["connect"],"ends":["{{end}}"]}],
             "states":[{"id":"01JSTA00000000000000000941","name":"Draft"}],"initial":"01JSTA00000000000000000941"}
            """;

        // Both subjects hold AttachedTo's comment end through its types; only PurchasedPart holds Concerns' (Assembly is no type of it).
        var held = await ValidateAsync(h => h.Write("model/processes/parts.json", Parts(CommentsEnd)));
        Assert.DoesNotContain(held, d => d.Severity == DiagnosticSeverity.Error && (d.FilePath ?? "").Contains("/processes/parts", StringComparison.Ordinal));
        var notHeld = await ValidateAsync(h => h.Write("model/processes/parts.json", Parts("01JEND00000000000000000921")));
        Assert.Contains(notHeld, d => d.Severity == DiagnosticSeverity.Error && (d.FilePath ?? "").Contains("/processes/parts", StringComparison.Ordinal)
            && (d.JsonPointer ?? "").StartsWith("/rights/0", StringComparison.Ordinal));
    }
}
