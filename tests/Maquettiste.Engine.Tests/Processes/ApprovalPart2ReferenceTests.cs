using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// The references and deletes of approvals, part 2 (signoff-design.md section 9): an actor that goes from a signature's delegateTo, or from its
/// escalateTo with escalateAfter beside its last entry; a signature that goes from a step's expect.notices, and the list with its last entry;
/// and a stage's when, which refuses remove-references and goes alone under delete-dependents; and get_trigger_sequence, which answers
/// delegate where a signature has delegateTo. The model is the process-basics fixture with two rounds; the names are invented.
/// </summary>
public sealed class ApprovalPart2ReferenceTests
{
    private const string P = "01JPRC00000000000000000001"; // OrderLifecycle
    private const string Order = "01JENT00000000000000000001";
    private const string Clerk = "01JACT00000000000000000001";
    private const string Finance = "01JACT00000000000000000003";
    private const string Reviewer = "01JAPR00000000000000000001"; // Managers' only signer
    private const string Auditor = "01JAPR00000000000000000002"; // in Managers' delegateTo and escalateTo, beside others
    private const string Deputy = "01JAPR00000000000000000003"; // Counsel's only delegateTo
    private const string Director = "01JAPR00000000000000000004"; // Counsel's only escalateTo
    private const string ApproveEvent = "01JPRX00000000000000000002";
    private const string Review = "01JAPS00000000000000000001";
    private const string Legal = "01JAPS00000000000000000002";
    private const string Managers = "01JAPG00000000000000000001";
    private const string Counsel = "01JAPG00000000000000000002";
    private const string Record = "01JAPE00000000000000000001"; // a base of Order
    private const string Title = "01JAPA00000000000000000001"; // Record's attribute, which the legal stage's when reads
    private const string Scenario = "01JAPC00000000000000000001";
    private const string ProcessFile = "model/processes/order-lifecycle.json";
    private const string Submit = "01JTRN00000000000000000001"; // Draft -> Submitted, no gate
    private const string Approve = "01JTRN00000000000000000006"; // the gated transition
    private const string DelegateCheck = "01JAPK00000000000000000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string Stages = $$"""
        [{"id":"{{Review}}","name":"Review","signatures":[
           {"id":"{{Managers}}","name":"Managers","signers":["{{Reviewer}}"],"delegateTo":["{{Auditor}}","{{Finance}}"],"remindAfter":"P2D",
            "escalateAfter":"P4D","escalateTo":["{{Auditor}}","{{Clerk}}"]}]},
         {"id":"{{Legal}}","name":"Legal","round":2,"when":{"op":"isNotNull","args":[{"path":["{{Title}}"]}]},"signatures":[
           {"id":"{{Counsel}}","name":"Counsel","signers":["{{Finance}}"],"delegateTo":["{{Deputy}}"],"remindAfter":"P1D","escalateAfter":"P3D",
            "escalateTo":["{{Director}}"]}]}]
        """;

    private static readonly string Steps = $$"""
        {"kind":"scenario","id":"{{Scenario}}","name":"Notices","process":"{{P}}",
         "steps":[
           {"id":"01JAPT00000000000000000001","input":"time","after":"P3D","expect":{"notices":[{"signature":"{{Managers}}","kind":"remind"},{"signature":"{{Counsel}}","kind":"remind"}]} },
           {"id":"01JAPT00000000000000000002","input":"time","after":"P2D","expect":{"notices":[{"signature":"{{Managers}}","kind":"escalate"}]} }]}
        """;

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(string? stages = null, string? triggerPoints = null)
    {
        var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        foreach (var (id, name) in new[] { (Reviewer, "Reviewer"), (Auditor, "Auditor"), (Deputy, "Deputy"), (Director, "Director") })
            Write(h, "model/actors/" + name.ToLowerInvariant() + ".json", $$"""{"kind":"actor","id":"{{id}}","name":"{{name}}","type":"role"}""", "actor.json");
        Write(h, "model/entities/record.json", $$"""
            {"kind":"entity","id":"{{Record}}","name":"Record","attributes":[{"id":"{{Title}}","name":"title","type":"string","length":80}]}
            """, "entity.json");
        var order = JsonNode.Parse(h.Read("model/entities/order.json"))!.AsObject();
        order["base"] = Record;
        Write(h, "model/entities/order.json", order.ToJsonString(), "entity.json");
        var process = JsonNode.Parse(h.Read(ProcessFile))!.AsObject();
        process["events"]![1]!["actors"]!.AsArray().Add(Reviewer);
        var gate = process["transitions"]![5]!["gate"]!.AsObject();
        foreach (var key in new[] { "required", "signers", "requiredActors" })
            gate.Remove(key);
        gate["stages"] = JsonNode.Parse(stages ?? Stages);
        if (triggerPoints is not null)
            process["triggerPoints"] = JsonNode.Parse(triggerPoints);
        Write(h, ProcessFile, process.ToJsonString(), "process.json");
        Write(h, "model/scenarios/order-lifecycle/notices.json", Steps, "scenario.json");
        var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        return (h, store);
    }

    private static void Write(LoaderHarness h, string modelPath, string json, string schema) =>
        h.Write(modelPath, Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse(json)!, schema, ".maquettiste/" + modelPath)));

    private static NamedSignature SignatureOf(ModelStore store, string id) =>
        store.Current!.Get<Process>(P)!.Transitions[5].Gate!.AllSignatures.Single(s => s.Id == id);

    private static async Task DeleteAsync(ModelStore store, string id, DeleteResolution resolution)
    {
        var result = await store.DeleteAsync(id, store.Current!.GetDocument(id)!.Hash, resolution, ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved,
            result.Outcome + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)) + string.Join("; ", result.Referrers.Select(r => r.FromElementId + r.JsonPointer)));
    }

    private static async Task<string> PlanAsync(ModelStore store, string id, DeleteResolution resolution) =>
        JsonSerializer.Serialize(await store.GetDeletePlanAsync([id], resolution, Ct), EngineJson.Options);

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task An_actor_that_goes_leaves_delegateTo_and_escalateTo_with_their_other_actors(DeleteResolution resolution)
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        await DeleteAsync(store, Auditor, resolution);

        var managers = SignatureOf(store, Managers);
        Assert.Equal([Finance], managers.DelegateTo);
        Assert.Equal([Clerk], managers.EscalateTo);
        Assert.Equal(("P2D", "P4D"), (managers.RemindAfter, managers.EscalateAfter));
    }

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task A_last_delegateTo_actor_drops_the_key_and_a_last_escalateTo_actor_drops_escalateAfter_with_it(DeleteResolution resolution)
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        Assert.Contains("\"what\":\"escalate to entry\"", await PlanAsync(store, Director, resolution), StringComparison.Ordinal);
        Assert.Contains("\"what\":\"escalation delay (escalateAfter)\"", await PlanAsync(store, Director, resolution), StringComparison.Ordinal);
        await DeleteAsync(store, Director, resolution);
        await DeleteAsync(store, Deputy, resolution);

        // The signature stays: no longer delegated, reminded but never escalated, and its signers and round as they were.
        var counsel = SignatureOf(store, Counsel);
        Assert.Equal((0, 0, "P1D", (string?)null), (counsel.DelegateTo.Count, counsel.EscalateTo.Count, counsel.RemindAfter, counsel.EscalateAfter));
        Assert.Equal([Finance], counsel.Signers);
        var written = JsonNode.Parse(h.Read(ProcessFile))!["transitions"]![5]!["gate"]!["stages"]![1]!["signatures"]![0]!.AsObject();
        Assert.Equal(["id", "name", "signers", "remindAfter"], written.Select(p => p.Key));
        Assert.DoesNotContain((await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ8204");
    }

    [Fact]
    public async Task A_signature_that_goes_drops_its_expected_notices_and_the_list_with_its_last_entry()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        Assert.Contains(await store.GetReferencesAsync(Managers, Ct), r => (r.FromElementId, r.JsonPointer) == (Scenario, "/steps/1/expect/notices/0/signature"));
        // Managers' only signer takes the signature with it.
        await DeleteAsync(store, Reviewer, DeleteResolution.DeleteDependents);

        var steps = store.Current!.Get<Maquettiste.Engine.Model.Scenario>(Scenario)!.Steps;
        Assert.Equal([(Counsel, NoticeKind.Remind)], steps[0].Expect!.Notices!.Select(n => (n.Signature, n.Kind)));
        Assert.Null(steps[1].Expect?.Notices);
        Assert.Equal([Counsel], store.Current!.Get<Process>(P)!.Transitions[5].Gate!.AllSignatures.Select(s => s.Id));
    }

    [Fact]
    public async Task A_stages_when_that_names_what_goes_refuses_remove_references_and_goes_alone_under_delete_dependents()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        Assert.Contains(await store.GetReferencesAsync(Title, Ct), r => r.FromId == Legal && r.JsonPointer.StartsWith("/transitions/5/gate/stages/1/when/", StringComparison.Ordinal));
        Assert.Contains("delete with delete-dependents to remove the stage's condition (when) that holds it.",
            await PlanAsync(store, Record, DeleteResolution.RemoveReferences), StringComparison.Ordinal);
        await DeleteAsync(store, Record, DeleteResolution.DeleteDependents);

        // The stage stays and always runs: the safe choice when its condition can't be judged.
        var legal = store.Current!.Get<Process>(P)!.Transitions[5].Gate!.Stages![1];
        Assert.Equal((Legal, 2, true), (legal.Id, legal.Round, legal.When is null));
        Assert.Equal([Counsel], legal.Signatures.Select(s => s.Id));
    }

    private static async Task<IReadOnlyList<string>> SequenceAsync(ModelStore store, string verb, string? transition = null)
    {
        var call = await store.GetTriggerSequenceAsync(P, verb, new TriggerSequenceFilter { Transition = transition }, Ct);
        Assert.Equal(ProcessCallStatus.Ok, call.Status);
        return [.. call.Value!.Steps.Select(s => string.Join(' ', new[] { s.Order.ToString(System.Globalization.CultureInfo.InvariantCulture), s.Step, s.TriggerPoint }.OfType<string>()))];
    }

    [Fact]
    public async Task The_trigger_sequence_answers_delegate_where_a_signature_has_delegateTo()
    {
        var (h, store) = await OpenAsync(triggerPoints: $$"""
            [{"id":"{{DelegateCheck}}","name":"DelegateCheck","type":"check","verbs":["delegate"],"transitions":["{{Approve}}"]}]
            """);
        using var _ = h;
        await using var __ = store;

        // The signatures row (the delegator's checks, then the person it goes to) and the effect, with no transition and at the approval's move.
        Assert.Equal(["2 closed-lists", "5 no-move", "6 guards", "7 signatures", $"9 checks {DelegateCheck}", "10 effect"], await SequenceAsync(store, "delegate"));
        Assert.Equal(["2 closed-lists", "5 no-move", "6 guards", "7 signatures", $"9 checks {DelegateCheck}", "10 effect"], await SequenceAsync(store, "delegate", Approve));
        // A move without a gate takes no delegation: no signatures row, and the point narrowed to the approval's move doesn't run there.
        Assert.Equal(["2 closed-lists", "5 no-move", "6 guards", "10 effect"], await SequenceAsync(store, "delegate", Submit));

        // An approval whose signatures have no delegateTo takes no delegate: with no transition there is no signatures row, as before part 2,
        // and a transition given answers as it did.
        var (other, plain) = await OpenAsync(stages: System.Text.RegularExpressions.Regex.Replace(Stages, "\"delegateTo\":\\[[^\\]]*\\],", ""));
        using var ___ = other;
        await using var ____ = plain;
        Assert.Empty(SignatureOf(plain, Managers).DelegateTo);
        Assert.Empty(SignatureOf(plain, Counsel).DelegateTo);
        Assert.Equal(["2 closed-lists", "5 no-move", "6 guards", "10 effect"], await SequenceAsync(plain, "delegate"));
        Assert.Equal(["2 closed-lists", "5 no-move", "6 guards", "7 signatures", "10 effect"], await SequenceAsync(plain, "delegate", Approve));
    }
}
