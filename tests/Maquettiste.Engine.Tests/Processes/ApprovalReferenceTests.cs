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
/// Approvals in the model's bookkeeping (signoff-design.md section 9): what a named signature names (signers, required actors, notBy verbs and
/// transitions, the paths its when reads) and a step's signature and expected signatures are references; deletes drop a signer or a required
/// actor as a gate's today, take a signature with its last signer and the gate with its last signature under delete-dependents, drop a notBy
/// entry and the key with its last entry, clear a step's signature and an expected signature, and the map with its last key; a when that names
/// what goes refuses remove-references and goes alone under delete-dependents; rename-verb and remove-verb walk notBy; process rows count
/// approvals and signatures by patch and by full build; stages and signatures localize; get_trigger_sequence answers reject, ignore and
/// withdraw at an approval. A model whose gates use only today's fields keeps its references, rows and answers.
/// </summary>
public sealed class ApprovalReferenceTests
{
    private const string P = "01JPRC00000000000000000001"; // OrderLifecycle in the process-basics fixture
    private const string Order = "01JENT00000000000000000001";
    private const string Clerk = "01JACT00000000000000000001";
    private const string Manager = "01JACT00000000000000000002";
    private const string Finance = "01JACT00000000000000000003";
    private const string Reviewer = "01JAPR00000000000000000001"; // an actor only the approvals name
    private const string Submit = "01JTRN00000000000000000001"; // Draft -> Submitted, no gate
    private const string Approve = "01JTRN00000000000000000006"; // the gated transition, /transitions/5
    private const string ApproveEvent = "01JPRX00000000000000000002";
    private const string Stage = "01JAPS00000000000000000001";
    private const string Managers = "01JAPG00000000000000000001";
    private const string FinanceCheck = "01JAPG00000000000000000002";
    private const string ClerkCheck = "01JAPG00000000000000000003";
    private const string Policies = "01JAPY00000000000000000001"; // a library
    private const string PolicyGate = "01JAPY00000000000000000002";
    private const string PolicyStage = "01JAPY00000000000000000003";
    private const string Director = "01JAPY00000000000000000004";
    private const string Other = "01JAPQ00000000000000000001"; // an orchestration whose move a gate policy's notBy names
    private const string OtherMove = "01JAPQ00000000000000000005";
    private const string Vocabulary = "01JAPV00000000000000000001";
    private const string Escalate = "01JAPV00000000000000000002";
    private const string Record = "01JAPE00000000000000000001"; // a base of Order
    private const string Title = "01JAPA00000000000000000001"; // Record's attribute, which FinanceCheck's when reads
    private const string Scenario = "01JAPC00000000000000000001";
    private const string RejectCheck = "01JAPK00000000000000000001";
    private const string ProcessFile = "model/processes/order-lifecycle.json";
    private const string Gate = "/transitions/5/gate";
    private const string Signatures = Gate + "/stages/0/signatures";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string Stages = $$"""
        [{"id":"{{Stage}}","name":"Review","displayName":"Manager review","description":"Who reviews the order.","signatures":[
           {"id":"{{Managers}}","name":"Managers","signers":["{{Reviewer}}"],"notBy":[{"verb":"escalate"},{"transition":"{{Submit}}"},{"verb":"promote"}],
            "description":"A manager who did not submit it."},
           {"id":"{{FinanceCheck}}","name":"FinanceCheck","displayName":"Finance check","signers":["{{Finance}}","{{Clerk}}"],"requiredActors":["{{Finance}}"],
            "when":{"op":"isNotNull","args":[{"path":["{{Title}}"]}]} },
           {"id":"{{ClerkCheck}}","name":"ClerkCheck","signers":["{{Clerk}}"],"notBy":[{"verb":"escalate"}]}]}]
        """;

    private static readonly string Steps = $$"""
        {"kind":"scenario","id":"{{Scenario}}","name":"Approval","process":"{{P}}",
         "steps":[
           {"id":"01JAPT00000000000000000001","event":"{{ApproveEvent}}","actor":"{{Manager}}","signer":"ann","signature":"{{Managers}}","reason":"Fine.",
            "expect":{"signatures":{"{{Managers}}":1} } },
           {"id":"01JAPT00000000000000000002","event":"{{ApproveEvent}}","actor":"{{Finance}}","signer":"dee","response":"reject","reason":"No.",
            "expect":{"signatures":{"{{Managers}}":0,"{{FinanceCheck}}":0} } }]}
        """;

    private static readonly string Library = $$"""
        {"kind":"process","id":"{{Policies}}","name":"Policies","use":"library",
         "gates":[{"id":"{{PolicyGate}}","name":"Release","meanings":[{"id":"01JAPY00000000000000000005","name":"released"}],
           "stages":[{"id":"{{PolicyStage}}","name":"Final","signatures":[
             {"id":"{{Director}}","name":"Director","signers":["{{Reviewer}}"],"notBy":[{"verb":"escalate"}]}]}]}]}
        """;

    // A gate policy may name no transition in notBy (MQ8202), so only an invalid model has a move another element's delete takes; the
    // transition comes first, so dropping the verb entry leaves that error where it was rather than showing it as one the delete adds.
    private static readonly string LibraryNamingAMove = Library.Replace(
        """[{"verb":"escalate"}]""", $$"""[{"transition":"{{OtherMove}}"},{"verb":"escalate"}]""", StringComparison.Ordinal);

    private static readonly string OtherProcess = $$"""
        {"kind":"process","id":"{{Other}}","name":"Other","use":"orchestration",
         "events":[{"id":"01JAPQ00000000000000000004","name":"go"}],
         "states":[{"id":"01JAPQ00000000000000000002","name":"A"},{"id":"01JAPQ00000000000000000003","name":"B"}],
         "initial":"01JAPQ00000000000000000002",
         "transitions":[{"id":"{{OtherMove}}","source":"01JAPQ00000000000000000002","event":"01JAPQ00000000000000000004","targets":["01JAPQ00000000000000000003"]}]}
        """;

    /// <summary>The process-basics fixture with the approve gate's stages, a library, another process, an own verb, Order's base and a scenario.</summary>
    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(string? stages = null, string? triggerPoints = null, bool policy = false, string? steps = null, string? library = null,
        Action<JsonObject>? edit = null)
    {
        var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        Write(h, "model/actors/reviewer.json", $$"""{"kind":"actor","id":"{{Reviewer}}","name":"Reviewer","type":"role"}""", "actor.json");
        Write(h, "model/entities/record.json", $$"""
            {"kind":"entity","id":"{{Record}}","name":"Record","attributes":[{"id":"{{Title}}","name":"title","type":"string","length":80}]}
            """, "entity.json");
        var order = JsonNode.Parse(h.Read("model/entities/order.json"))!.AsObject();
        order["base"] = Record;
        Write(h, "model/entities/order.json", order.ToJsonString(), "entity.json");
        Write(h, "model/vocabularies/verbs.json", $$"""
            {"kind":"verb-vocabulary","id":"{{Vocabulary}}","name":"verbs","verbs":[{"id":"{{Escalate}}","key":"escalate"}]}
            """, "verb-vocabulary.json");
        var process = JsonNode.Parse(h.Read(ProcessFile))!.AsObject();
        // The signers may raise the approve event (MQ9105), and the ship event names the own verb, so the lifecycle's runs see it (MQ8202).
        process["events"]![1]!["actors"]!.AsArray().Add(Clerk);
        process["events"]![1]!["actors"]!.AsArray().Add(Reviewer);
        process["events"]![2]!["verb"] = "escalate";
        var gate = process["transitions"]![5]!["gate"]!.AsObject();
        foreach (var key in new[] { "required", "signers", "requiredActors" })
            gate.Remove(key);
        gate["stages"] = JsonNode.Parse(stages ?? Stages);
        if (policy)
        {
            // The approve move uses the library's gate policy with stages in place of its own gate.
            var transition = process["transitions"]![5]!.AsObject();
            transition.Remove("gate");
            transition["gatePolicy"] = PolicyGate;
        }

        if (triggerPoints is not null)
            process["triggerPoints"] = JsonNode.Parse(triggerPoints);
        edit?.Invoke(process);
        Write(h, ProcessFile, process.ToJsonString(), "process.json");
        Write(h, "model/processes/policies.json", library ?? Library, "process.json");
        Write(h, "model/processes/other.json", OtherProcess, "process.json");
        Write(h, "model/scenarios/order-lifecycle/approval.json", steps ?? Steps, "scenario.json");
        return (h, await LoadAsync(h));
    }

    private static async Task<ModelStore> LoadAsync(LoaderHarness h)
    {
        var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        return store;
    }

    private static void Write(LoaderHarness h, string modelPath, string json, string schema) =>
        h.Write(modelPath, Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse(json)!, schema, ".maquettiste/" + modelPath)));

    private static async Task<BatchResult> ApplyAsync(ModelStore store, string operation)
    {
        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + operation + "]}"));
        Assert.Empty(parsed.Diagnostics);
        return await store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);
    }

    private static ProcessGate? GateOf(ModelStore store) => store.Current!.Get<Process>(P)!.Transitions[5].Gate;

    private static NamedSignature? SignatureOf(ModelStore store, string id) =>
        GateOf(store)?.AllSignatures.SingleOrDefault(s => s.Id == id);

    private static NamedSignature PolicySignature(ModelStore store) => store.Current!.Get<Process>(Policies)!.Gates[0].AllSignatures.Single();

    private static IReadOnlyList<(string?, string?)> NotBy(NamedSignature signature) => [.. signature.NotBy.Select(n => (n.Verb, n.Transition))];

    private static IReadOnlyList<ScenarioStep> StepsOf(ModelStore store) => store.Current!.Get<Maquettiste.Engine.Model.Scenario>(Scenario)!.Steps;

    private static async Task DeleteAsync(ModelStore store, string id, DeleteResolution resolution)
    {
        var result = await store.DeleteAsync(id, store.Current!.GetDocument(id)!.Hash, resolution, ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved,
            result.Outcome + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)) + string.Join("; ", result.Referrers.Select(r => r.FromElementId + r.JsonPointer)));
    }

    private static async Task<string> PlanAsync(ModelStore store, string id, DeleteResolution resolution) =>
        JsonSerializer.Serialize(await store.GetDeletePlanAsync([id], resolution, Ct), EngineJson.Options);

    [Fact]
    public async Task What_a_named_signature_names_and_a_steps_signature_and_expected_signatures_are_references()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        Assert.Contains(await store.GetReferencesAsync(Clerk, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (P, FinanceCheck, Signatures + "/1/signers/1"));
        Assert.Contains(await store.GetReferencesAsync(Finance, Ct), r => (r.FromId, r.JsonPointer) == (FinanceCheck, Signatures + "/1/requiredActors/0"));
        Assert.Contains(await store.GetReferencesAsync(Escalate, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (P, Managers, Signatures + "/0/notBy/0/verb"));
        Assert.Contains(await store.GetReferencesAsync(Escalate, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (Policies, Director, "/gates/0/stages/0/signatures/0/notBy/0/verb"));
        Assert.Contains(await store.GetReferencesAsync(Submit, Ct), r => (r.FromId, r.JsonPointer) == (Managers, Signatures + "/0/notBy/1/transition"));
        Assert.Contains(await store.GetReferencesAsync(Title, Ct), r => r.FromId == FinanceCheck && r.JsonPointer.StartsWith(Signatures + "/1/when/", StringComparison.Ordinal));
        Assert.Contains(await store.GetReferencesAsync(Managers, Ct), r => (r.FromElementId, r.JsonPointer) == (Scenario, "/steps/0/signature"));
        Assert.Contains(await store.GetReferencesAsync(Managers, Ct), r => (r.FromElementId, r.JsonPointer) == (Scenario, $"/steps/0/expect/signatures/{Managers}"));
        Assert.Contains(await store.GetReferencesAsync(FinanceCheck, Ct), r => (r.FromElementId, r.JsonPointer) == (Scenario, $"/steps/1/expect/signatures/{FinanceCheck}"));
        // A built-in verb in notBy is no reference.
        Assert.DoesNotContain(await store.GetReferencesAsync(Managers, Ct), r => r.FromElementId == P);
    }

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task A_signer_or_required_actor_that_goes_leaves_a_signature_with_other_signers(DeleteResolution resolution)
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        await DeleteAsync(store, Finance, resolution);

        var finance = SignatureOf(store, FinanceCheck)!;
        Assert.Equal([Clerk], finance.Signers);
        Assert.Empty(finance.RequiredActors);
        Assert.Equal([Managers, FinanceCheck, ClerkCheck], GateOf(store)!.AllSignatures.Select(s => s.Id));
    }

    [Fact]
    public async Task A_signatures_last_signer_refuses_remove_references_and_delete_dependents_takes_the_signature_and_clears_what_named_it()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        // As a gate's last signer does today: the list may not be emptied.
        var refused = await PlanAsync(store, Reviewer, DeleteResolution.RemoveReferences);
        Assert.Contains($"at {Signatures}/0/signers/0: the reference is required, so it cannot be cleared.", refused, StringComparison.Ordinal);
        Assert.Contains("at /gates/0/stages/0/signatures/0/signers/0: the reference is required", refused, StringComparison.Ordinal);

        var plan = await PlanAsync(store, Reviewer, DeleteResolution.DeleteDependents);
        Assert.Contains("\"what\":\"signature Managers\"", plan, StringComparison.Ordinal);
        await DeleteAsync(store, Reviewer, DeleteResolution.DeleteDependents);

        // Managers goes, and the gate keeps its other signatures; the library's one signature takes its gate policy.
        Assert.Equal([FinanceCheck, ClerkCheck], GateOf(store)!.AllSignatures.Select(s => s.Id));
        Assert.Empty(store.Current!.Get<Process>(Policies)!.Gates);
        // The step's signature is cleared; an expected signature goes from its map, and the map with its last key.
        var steps = StepsOf(store);
        Assert.Null(steps[0].Signature);
        Assert.Null(steps[0].Expect!.Signatures);
        Assert.Equal([FinanceCheck], steps[1].Expect!.Signatures!.Keys);
        Assert.Equal(SignatureResponse.Reject, steps[1].Response);
    }

    [Fact]
    public async Task A_gates_last_signature_refuses_remove_references_and_delete_dependents_takes_the_gate()
    {
        var (h, store) = await OpenAsync($$"""
            [{"id":"{{Stage}}","name":"Review","signatures":[{"id":"{{Managers}}","name":"Managers","signers":["{{Reviewer}}"]}]}]
            """, steps: $$"""
            {"kind":"scenario","id":"{{Scenario}}","name":"Approval","process":"{{P}}",
             "steps":[{"id":"01JAPT00000000000000000001","event":"{{ApproveEvent}}","actor":"{{Manager}}","signer":"ann","signature":"{{Managers}}",
               "reason":"Fine.","expect":{"signatures":{"{{Managers}}":1} } }]}
            """);
        using var _ = h;
        await using var __ = store;

        Assert.Contains($"at {Signatures}/0/signers/0: the reference is required", await PlanAsync(store, Reviewer, DeleteResolution.RemoveReferences), StringComparison.Ordinal);

        Assert.Contains("\"what\":\"gate approval\"", await PlanAsync(store, Reviewer, DeleteResolution.DeleteDependents), StringComparison.Ordinal);
        await DeleteAsync(store, Reviewer, DeleteResolution.DeleteDependents);

        Assert.Null(GateOf(store));
        Assert.Null(StepsOf(store)[0].Signature);
        Assert.Null(StepsOf(store)[0].Expect!.Signatures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_approval_delete_dependents_takes_drops_the_response_and_signature_of_the_steps_that_answered_it(bool policy)
    {
        // The approve move's own gate with one signature, or the library's gate policy with one; a step that signs, one that rejects, and
        // one at another event, whose response is no answer to the approval.
        var signature = policy ? Director : Managers;
        var (h, store) = await OpenAsync($$"""
            [{"id":"{{Stage}}","name":"Review","signatures":[{"id":"{{Managers}}","name":"Managers","signers":["{{Reviewer}}"]}]}]
            """, policy: policy, steps: $$"""
            {"kind":"scenario","id":"{{Scenario}}","name":"Approval","process":"{{P}}",
             "steps":[
               {"id":"01JAPT00000000000000000001","event":"{{ApproveEvent}}","actor":"{{Manager}}","signer":"ann","signature":"{{signature}}","reason":"Fine."},
               {"id":"01JAPT00000000000000000002","event":"{{ApproveEvent}}","actor":"{{Finance}}","signer":"dee","response":"reject","reason":"No."},
               {"id":"01JAPT00000000000000000003","event":"01JPRX00000000000000000001","actor":"{{Clerk}}","response":"reject"}]}
            """);
        using var _ = h;
        await using var __ = store;

        await DeleteAsync(store, Reviewer, DeleteResolution.DeleteDependents);

        Assert.Null(GateOf(store));
        Assert.Null(store.Current!.Get<Process>(P)!.Transitions[5].GatePolicy);
        var steps = JsonNode.Parse(h.Read("model/scenarios/order-lifecycle/approval.json"))!["steps"]!.AsArray();
        Assert.All(steps.Take(2), s => Assert.False(s!.AsObject().ContainsKey("response") || s.AsObject().ContainsKey("signature")));
        Assert.Equal("Fine.", (string?)steps[0]!["reason"]);
        Assert.Equal("reject", (string?)steps[2]!["response"]);
    }

    [Fact]
    public async Task An_approval_delete_dependents_takes_keeps_the_answers_of_another_approval_on_the_same_event()
    {
        // Two approvals on approve: the approve move's, whose one signature only Reviewer gives, and one on a move out of Draft that stays.
        const string Release = "01JAPG00000000000000000009";
        var (h, store) = await OpenAsync($$"""
            [{"id":"{{Stage}}","name":"Review","signatures":[{"id":"{{Managers}}","name":"Managers","signers":["{{Reviewer}}"]}]}]
            """, steps: $$"""
            {"kind":"scenario","id":"{{Scenario}}","name":"Approval","process":"{{P}}",
             "steps":[
               {"id":"01JAPT00000000000000000001","event":"{{ApproveEvent}}","actor":"{{Finance}}","signer":"dee","response":"reject","signature":"{{Release}}","reason":"No."},
               {"id":"01JAPT00000000000000000002","event":"{{ApproveEvent}}","actor":"{{Manager}}","signer":"ann","response":"ignore","signature":"{{Managers}}","reason":"Fine."},
               {"id":"01JAPT00000000000000000003","event":"{{ApproveEvent}}","actor":"{{Finance}}","signer":"dee","response":"withdraw","reason":"Back."}]}
            """, edit: process => process["transitions"]!.AsArray().Add(JsonNode.Parse($$"""
            {"id":"01JAPG00000000000000000007","source":"01JSTA00000000000000000001","event":"{{ApproveEvent}}","targets":["01JSTA00000000000000000014"],
             "gate":{"id":"01JAPG00000000000000000008","name":"release","meanings":[{"id":"01JAPG00000000000000000010","name":"released"}],
               "stages":[{"id":"01JAPG00000000000000000011","name":"Final","signatures":[{"id":"{{Release}}","name":"Release","signers":["{{Finance}}"]}]}]} }
            """)));
        using var _ = h;
        await using var __ = store;

        await DeleteAsync(store, Reviewer, DeleteResolution.DeleteDependents);

        Assert.Null(GateOf(store));
        var steps = JsonNode.Parse(h.Read("model/scenarios/order-lifecycle/approval.json"))!["steps"]!.AsArray();
        // The other approval's signature and every response stay, as that approval still answers them; only the gone signature is dropped.
        Assert.Equal(("reject", Release), ((string?)steps[0]!["response"], (string?)steps[0]!["signature"]));
        Assert.Equal(("ignore", (string?)null), ((string?)steps[1]!["response"], (string?)steps[1]!["signature"]));
        Assert.Equal("withdraw", (string?)steps[2]!["response"]);
    }

    [Fact]
    public async Task A_verb_or_a_move_that_goes_drops_its_not_by_entry_and_the_key_with_its_last_entry()
    {
        var (h, store) = await OpenAsync(library: LibraryNamingAMove);
        using var _ = h;
        await using var __ = store;
        Assert.Contains(await store.GetReferencesAsync(OtherMove, Ct), r => (r.FromElementId, r.JsonPointer) == (Policies, "/gates/0/stages/0/signatures/0/notBy/0/transition"));

        // The vocabulary goes, and its verb with it: each entry naming it is dropped, in the lifecycle and in the library.
        var plan = await PlanAsync(store, Vocabulary, DeleteResolution.RemoveReferences);
        Assert.Contains("\"what\":\"not by entry", plan, StringComparison.Ordinal);
        await DeleteAsync(store, Vocabulary, DeleteResolution.RemoveReferences);
        Assert.Equal([(null, Submit), ("promote", null)], NotBy(SignatureOf(store, Managers)!));
        Assert.Empty(SignatureOf(store, ClerkCheck)!.NotBy);
        Assert.DoesNotContain("\"notBy\"", JsonNode.Parse(h.Read(ProcessFile))!["transitions"]![5]!["gate"]!["stages"]![0]!["signatures"]![2]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal([(null, OtherMove)], NotBy(PolicySignature(store)));

        // A process whose move a gate policy's notBy names goes: the entry is dropped, and the key with it.
        await DeleteAsync(store, Other, DeleteResolution.RemoveReferences);
        Assert.Empty(PolicySignature(store).NotBy);
        Assert.DoesNotContain("notBy", h.Read("model/processes/policies.json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_when_that_names_what_goes_refuses_remove_references_and_goes_alone_under_delete_dependents()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        Assert.Contains("delete with delete-dependents to remove the signature's condition (when) that holds it.",
            await PlanAsync(store, Record, DeleteResolution.RemoveReferences), StringComparison.Ordinal);

        Assert.Contains("\"what\":\"condition (when)\"", await PlanAsync(store, Record, DeleteResolution.DeleteDependents), StringComparison.Ordinal);
        await DeleteAsync(store, Record, DeleteResolution.DeleteDependents);

        // The signature stays, needed always: the safe choice when its condition can't be judged.
        var finance = SignatureOf(store, FinanceCheck)!;
        Assert.Null(finance.When);
        Assert.Equal([Finance, Clerk], finance.Signers);
    }

    [Fact]
    public async Task Rename_verb_renames_and_merges_not_by_entries_and_leaves_step_responses()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        Assert.Equal(SaveOutcome.Saved, (await ApplyAsync(store, """{"op":"rename-verb","verb":"escalate","to":"raise"}""")).Outcome);
        Assert.Equal([("raise", null), (null, Submit), ("promote", null)], NotBy(SignatureOf(store, Managers)!));
        Assert.Equal([("raise", (string?)null)], NotBy(SignatureOf(store, ClerkCheck)!));
        Assert.Equal([("raise", (string?)null)], NotBy(PolicySignature(store)));

        // Into a built-in verb a list already names: the two merge into one entry.
        Assert.Equal(SaveOutcome.Saved, (await ApplyAsync(store, """{"op":"rename-verb","verb":"raise","to":"promote"}""")).Outcome);
        Assert.Equal([(null, Submit), ("promote", null)], NotBy(SignatureOf(store, Managers)!));
        Assert.Equal([("promote", (string?)null)], NotBy(SignatureOf(store, ClerkCheck)!));
        Assert.Equal([SignatureResponse.Sign, SignatureResponse.Reject], StepsOf(store).Select(s => s.Response));
    }

    [Fact]
    public async Task Remove_verb_drops_not_by_entries_and_the_key_with_the_last_and_leaves_step_responses()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var result = await ApplyAsync(store, """{"op":"remove-verb","verb":"escalate"}""");

        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        Assert.Equal([(null, Submit), ("promote", null)], NotBy(SignatureOf(store, Managers)!));
        Assert.Empty(SignatureOf(store, ClerkCheck)!.NotBy);
        Assert.Empty(PolicySignature(store).NotBy);
        Assert.Equal([SignatureResponse.Sign, SignatureResponse.Reject], StepsOf(store).Select(s => s.Response));
        Assert.DoesNotContain((await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ2001" && d.Message.Contains(Escalate, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Process_rows_count_approvals_and_signatures_by_patch_and_by_full_build()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var rows = store.Current!.Index.Summaries;
        Assert.Equal((1, 3), (rows.Single(s => s.Id == P).Approvals, rows.Single(s => s.Id == P).Signatures));
        Assert.Equal((1, 1), (rows.Single(s => s.Id == Policies).Approvals, rows.Single(s => s.Id == Policies).Signatures));
        Assert.Equal((null, null), (rows.Single(s => s.Id == Other).Approvals, rows.Single(s => s.Id == Other).Signatures));
        Assert.Contains("\"approvals\":1,\"signatures\":3", JsonSerializer.Serialize(rows.Single(s => s.Id == P), EngineJson.Options), StringComparison.Ordinal);

        // A save that adds a signature: the patched row equals a full build's.
        var current = (await store.GetElementAsync(P, Ct))!;
        var node = JsonNode.Parse(current.Json.GetRawText())!.AsObject();
        node["transitions"]![5]!["gate"]!["stages"]![0]!["signatures"]!.AsArray()
            .Add(JsonNode.Parse($$"""{"id":"01JAPG00000000000000000004","name":"Legal","signers":["{{Clerk}}"]}"""));
        var saved = await store.SaveAsync(P, Encoding.UTF8.GetBytes(node.ToJsonString()), current.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.True(store.Current!.Index.Patched);
        var patched = store.Current!.Index.Summaries.Single(s => s.Id == P);
        Assert.Equal((1, 4), (patched.Approvals, patched.Signatures));

        await using var fresh = await LoadAsync(h);
        Assert.Equal(patched, fresh.Current!.Index.Summaries.Single(s => s.Id == P));
    }

    [Fact]
    public async Task Stages_and_signatures_localize_their_display_name_and_description()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var nodes = store.Current!.Localization.Nodes;

        Assert.Equal(("gate-stage", "Manager review", "Who reviews the order."), (nodes[Stage].Kind, nodes[Stage].DisplayName, nodes[Stage].Description));
        Assert.Equal(("named-signature", "Finance check", (string?)null), (nodes[FinanceCheck].Kind, nodes[FinanceCheck].DisplayName, nodes[FinanceCheck].Description));
        Assert.Equal(("ClerkCheck", "A manager who did not submit it."), (nodes[ClerkCheck].DisplayName, nodes[Managers].Description));
        Assert.Contains("gate-stage", (IReadOnlySet<string>)Maquettiste.Engine.Localization.LocaleChains.NodeKinds);
        Assert.Contains("named-signature", (IReadOnlySet<string>)Maquettiste.Engine.Localization.LocaleChains.NodeKinds);
    }

    private static async Task<IReadOnlyList<string>> SequenceAsync(ModelStore store, string verb, string? transition = null)
    {
        var call = await store.GetTriggerSequenceAsync(P, verb, new TriggerSequenceFilter { Transition = transition }, Ct);
        Assert.Equal(ProcessCallStatus.Ok, call.Status);
        return [.. call.Value!.Steps.Select(s => string.Join(' ', new[] { s.Order.ToString(System.Globalization.CultureInfo.InvariantCulture), s.Step, s.TriggerPoint }.OfType<string>()))];
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("ignore")]
    [InlineData("withdraw")]
    public async Task The_trigger_sequence_answers_a_response_at_an_approval_with_the_signatures_row_and_its_effect(string verb)
    {
        var (h, store) = await OpenAsync(triggerPoints: $$"""
            [{"id":"{{RejectCheck}}","name":"RejectCheck","type":"check","verbs":["{{verb}}"],"transitions":["{{Approve}}"]}]
            """);
        using var _ = h;
        await using var __ = store;

        Assert.Equal(["2 closed-lists", "5 no-move", "6 guards", "7 signatures", $"9 checks {RejectCheck}", "10 effect"], await SequenceAsync(store, verb));
        Assert.Equal(["2 closed-lists", "5 no-move", "6 guards", "7 signatures", $"9 checks {RejectCheck}", "10 effect"], await SequenceAsync(store, verb, Approve));
        // A move without a gate takes no response: no signatures row, and the point narrowed to the approval's move doesn't run there.
        Assert.Equal(["2 closed-lists", "5 no-move", "6 guards", "10 effect"], await SequenceAsync(store, verb, Submit));

        // Through a gate policy with stages: the lifecycle has an approval, so the row shows with no transition too.
        var (other, policy) = await OpenAsync(policy: true);
        using var ___ = other;
        await using var ____ = policy;
        Assert.Equal(["2 closed-lists", "5 no-move", "6 guards", "7 signatures", "10 effect"], await SequenceAsync(policy, verb));
        Assert.Equal(["2 closed-lists", "5 no-move", "6 guards", "7 signatures", "10 effect"], await SequenceAsync(policy, verb, Approve));
    }

    [Fact]
    public async Task A_model_whose_gates_use_only_todays_fields_keeps_its_references_rows_and_sequence_answers()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        await using var store = await LoadAsync(h);

        // References: the gate's own signers and required actors, as before.
        Assert.Contains(await store.GetReferencesAsync(Manager, Ct), r => (r.FromElementId, r.JsonPointer) == (P, Gate + "/signers/0"));
        Assert.Contains(await store.GetReferencesAsync(Finance, Ct), r => (r.FromElementId, r.JsonPointer) == (P, Gate + "/requiredActors/0"));

        // Rows: no approvals or signatures member.
        var row = store.Current!.Index.Summaries.Single(s => s.Id == P);
        Assert.Equal((null, null), (row.Approvals, row.Signatures));
        var text = JsonSerializer.Serialize(row, EngineJson.Options);
        Assert.DoesNotContain("\"approvals\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"signatures\"", text, StringComparison.Ordinal);

        // The sequence: a plain gate answers a response as before, and with no transition there is no signatures row.
        foreach (var verb in new[] { "reject", "ignore", "withdraw" })
        {
            Assert.Equal(["2 closed-lists", "5 no-move", "6 guards", "10 effect"], await SequenceAsync(store, verb));
            Assert.Equal(["2 closed-lists", "5 no-move", "6 guards", "7 signatures", "10 effect"], await SequenceAsync(store, verb, Approve));
        }

        Assert.Equal(["2 closed-lists", "5 no-move", "6 guards", "7 signatures", "10 effect"], await SequenceAsync(store, "sign"));

        // Deletes keep the plain gate's behaviour: a signer goes from the list, and the last one refuses remove-references.
        await DeleteAsync(store, Finance, DeleteResolution.RemoveReferences);
        Assert.Equal([Manager], store.Current!.Get<Process>(P)!.Transitions[5].Gate!.Signers);
        Assert.Contains($"at {Gate}/signers/0: the reference is required", await PlanAsync(store, Manager, DeleteResolution.RemoveReferences), StringComparison.Ordinal);
    }
}
