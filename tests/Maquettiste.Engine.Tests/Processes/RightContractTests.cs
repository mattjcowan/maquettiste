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
/// The contract of rights, part 1 (rights-design.md sections 4 to 7): the schemas of a lifecycle's rights and default, a named right, a verb
/// step and an expected refusal; a model holding them loads, indexes its parts and references, names its inline rights and keeps their keys
/// in schema order; the trace's and the step's new members are left out when unused; and the rule catalog holds the rights rules.
/// </summary>
public sealed class RightContractTests
{
    private const string P = "01JPRC00000000000000000001"; // OrderLifecycle, bound to Order.status
    private const string Order = "01JENT00000000000000000001";
    private const string OrderId = "01JATT00000000000000000001";
    private const string Clerk = "01JACT00000000000000000001";
    private const string Manager = "01JACT00000000000000000002";
    private const string Draft = "01JSTA00000000000000000001";
    private const string Package = "01JPKG00000000000000000001";
    private const string Named = "01JRGT00000000000000000001"; // SecretOnlyOwner
    private const string Inline = "01JRGT00000000000000000011"; // ClerksEditDrafts
    private const string Deny = "01JRGT00000000000000000012"; // NoDeleteInDraft
    private const string Use = "01JRGT00000000000000000013"; // a use of SecretOnlyOwner
    private const string Scenario = "01JSCN00000000000000000031";
    private const string ModifyStep = "01JSTP00000000000000000031";
    private const string RefusedStep = "01JSTP00000000000000000032";
    private const string ProcessFile = "model/processes/order-lifecycle.json";
    private const string RightFile = "model/rights/secret-only-owner.json";
    private const string ScenarioFile = "model/scenarios/order-lifecycle/verb-steps.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string NamedRight = $$"""
        {"kind":"right","id":"{{Named}}","name":"SecretOnlyOwner","package":"{{Package}}","description":"Only its owner may change a secret item.",
         "verbs":["modify","delete","change-owner"],"except":["{{Manager}}"],"access":"deny",
         "condition":{"op":"isNotNull","args":[{"path":["{{OrderId}}"]}]} }
        """;

    private static readonly string Rights = $$"""
        [{"id":"{{Inline}}","states":["{{Draft}}"],"verbs":["read","modify"],"actors":["{{Clerk}}"],"access":"allow","name":"ClerksEditDrafts",
          "description":"Clerks shape their drafts."},
         {"id":"{{Deny}}","name":"NoDeleteInDraft","access":"deny","verbs":["delete"],"states":["{{Draft}}"]},
         {"id":"{{Use}}","right":"{{Named}}"}]
        """;

    private static readonly string VerbScenario = $$"""
        {"kind":"scenario","id":"{{Scenario}}","name":"VerbSteps","process":"{{P}}",
         "steps":[
           {"id":"{{ModifyStep}}","input":"verb","verb":"modify","actor":"{{Clerk}}","values":{"{{OrderId}}":"9b2e0e8a-50a5-4a4c-8a37-5a5a5e3c7e11"} },
           {"id":"{{RefusedStep}}","input":"verb","verb":"delete","actor":"{{Clerk}}",
            "expect":{"refusedBy":"{{Deny}}","refusal":"right","accepted":false} }]}
        """;

    private static string Canonical(string json, string schema, string modelPath) =>
        Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse(json)!, schema, ".maquettiste/" + modelPath));

    private static IReadOnlyList<Diagnostic> Evaluate(string schema, string json)
    {
        using var document = JsonDocument.Parse(json);
        return TestServices.Schemas.Evaluate(schema, document.RootElement, "model/x.json");
    }

    private static void Valid(string schema, string json) =>
        Assert.True(Evaluate(schema, json) is { Count: 0 } found, string.Join("; ", Evaluate(schema, json).Select(d => d.JsonPointer + " " + d.Message)));

    private static void Invalid(string schema, string json, string pointer)
    {
        var found = Evaluate(schema, json);
        Assert.True(found.Any(d => d.Rule == "MQ1002" && (d.JsonPointer ?? "").StartsWith(pointer, StringComparison.Ordinal)),
            pointer + ": " + string.Join("; ", found.Select(d => d.JsonPointer + " " + d.Message)));
    }

    private static string Lifecycle(string rights, string extra = "") =>
        $$"""{"kind":"process","id":"{{P}}","name":"Flow","use":"lifecycle"{{extra}},"rights":{{rights}}}""";

    private static IReadOnlyList<NameEntry> Find(ModelSnapshot model, string text)
    {
        Assert.True(QualifiedName.TryParse(text, out var name, out var error), error);
        return model.Names(name.Kind).Find(name.Path);
    }

    // ---- schemas ----

    [Fact]
    public void A_lifecycles_rights_take_both_forms_and_leave_mixing_them_to_MQ9901()
    {
        Valid("process.json", Lifecycle(Rights, $$""","rightsDefault":"deny" """));
        Valid("process.json", Lifecycle("[]", ""","rightsDefault":"allow" """));

        // Both forms in one entry, or neither, pass the schema: MQ9901 reports them, with the entry's id to point at.
        Valid("process.json", Lifecycle($$"""[{"id":"{{Use}}","right":"{{Named}}","name":"Both","access":"deny","verbs":["delete"]}]"""));
        Valid("process.json", Lifecycle($$"""[{"id":"{{Use}}"}]"""));

        Invalid("process.json", Lifecycle($$"""[{"id":"{{Inline}}","name":"R","access":"deny","verbs":[]}]"""), "/rights/0/verbs");
        Invalid("process.json", Lifecycle($$"""[{"id":"{{Inline}}","name":"R","access":"deny","verbs":["delete","delete"]}]"""), "/rights/0/verbs");
        Invalid("process.json", Lifecycle($$"""[{"id":"{{Inline}}","name":"R","access":"maybe","verbs":["delete"]}]"""), "/rights/0/access");
        Invalid("process.json", Lifecycle($$"""[{"id":"{{Inline}}","name":"R","access":"deny","verbs":["delete"],"actors":["{{Clerk}}","{{Clerk}}"]}]"""), "/rights/0/actors");
        Invalid("process.json", Lifecycle($$"""[{"id":"{{Inline}}","name":"R","access":"deny","verbs":["delete"],"except":["clerk"]}]"""), "/rights/0/except");
        Invalid("process.json", Lifecycle($$"""[{"id":"{{Inline}}","name":"R","access":"deny","verbs":["Delete"]}]"""), "/rights/0/verbs");
        Invalid("process.json", Lifecycle($$"""[{"name":"R","access":"deny","verbs":["delete"]}]"""), "/rights/0");
        Invalid("process.json", Lifecycle("[]", ""","rightsDefault":"sometimes" """), "/rightsDefault");
        // Part 2's keys: whileFrozen and attributes (decisions 18 and 21).
        Valid("process.json", Lifecycle($$"""[{"id":"{{Inline}}","name":"R","access":"deny","verbs":["change-owner"],"whileFrozen":true}]"""));
        Valid("process.json", Lifecycle($$"""[{"id":"{{Inline}}","name":"R","access":"deny","verbs":["read"],"attributes":["{{Clerk}}"]}]"""));
        Invalid("process.json", Lifecycle($$"""[{"id":"{{Inline}}","name":"R","access":"deny","verbs":["read"],"whileFrozen":"yes"}]"""), "/rights/0/whileFrozen");
        Invalid("process.json", Lifecycle($$"""[{"id":"{{Inline}}","name":"R","access":"deny","verbs":["read"],"attributes":["{{Clerk}}","{{Clerk}}"]}]"""), "/rights/0/attributes");
    }

    [Fact]
    public void A_named_right_says_who_what_and_when_but_not_where()
    {
        Valid("right.json", NamedRight);
        Valid("right.json", $$"""{"kind":"right","id":"{{Named}}","name":"EveryoneReads","access":"allow","verbs":["read"]}""");

        Invalid("right.json", $$"""{"kind":"right","id":"{{Named}}","name":"R","access":"deny"}""", "");
        Invalid("right.json", $$"""{"kind":"right","id":"{{Named}}","name":"R","verbs":["read"]}""", "");
        Invalid("right.json", $$"""{"kind":"right","id":"{{Named}}","name":"R","access":"deny","verbs":["read"],"states":["{{Draft}}"]}""", "");
        // Rights on types with no lifecycle come later (decision 8): part 1 has no subjects key.
        Invalid("right.json", $$"""{"kind":"right","id":"{{Named}}","name":"R","access":"deny","verbs":["read"],"subjects":["{{Order}}"]}""", "");
        Invalid("right.json", $$"""{"kind":"right","id":"{{Named}}","name":"R","access":"deny","verbs":["read"],"ends":["end"]}""", "/ends");
    }

    [Fact]
    public void A_verb_step_and_an_expected_refusal_fit_the_scenario_schema()
    {
        Valid("scenario.json", VerbScenario);
        Valid("scenario.json", $$"""
            {"kind":"scenario","id":"{{Scenario}}","name":"S","process":"{{P}}",
             "steps":[{"id":"{{ModifyStep}}","input":"verb","verb":"connect","end":"{{Order}}","object":"{{Clerk}}",
               "expect":{"accepted":false,"refusal":"default","refusedBy":"{{P}}"} }]}
            """);

        string Step(string step) => $$"""{"kind":"scenario","id":"{{Scenario}}","name":"S","process":"{{P}}","steps":[{"id":"{{ModifyStep}}",{{step}}}]}""";
        Invalid("scenario.json", Step("""  "input":"verb","verb":"Modify" """), "/steps/0/verb");
        Invalid("scenario.json", Step("""  "input":"verb","verb":"modify","values":{"title":"x"} """), "/steps/0/values");
        Invalid("scenario.json", Step("""  "input":"verb","verb":"connect","end":"components" """), "/steps/0/end");
        // Part 2: the frozen refusal, a read step's attributes, and frozen objects and links.
        Valid("scenario.json", Step("""  "expect":{"accepted":false,"refusal":"frozen"} """));
        Valid("scenario.json", Step($$"""  "input":"verb","verb":"read","attributes":["{{Clerk}}"] """));
        Invalid("scenario.json", Step("""  "expect":{"accepted":false,"refusal":"thawed"} """), "/steps/0/expect/refusal");
        Invalid("scenario.json", Step("""  "expect":{"accepted":false,"refusedBy":"SecretOnlyOwner"} """), "/steps/0/expect/refusedBy");
        Invalid("scenario.json", Step("""  "input":"verb","verb":"read","attributes":["salary"] """), "/steps/0/attributes");
        Valid("scenario.json", $$"""
            {"kind":"scenario","id":"{{Scenario}}","name":"S","process":"{{P}}","subject":"{{Order}}",
             "objects":[{"id":"{{Order}}","name":"o","entity":"{{Clerk}}","frozen":true,"links":[{"end":"{{Draft}}","object":"{{Order}}","frozen":true}]}],
             "steps":[{"id":"{{ModifyStep}}"}]}
            """);
        Invalid("scenario.json", $$"""
            {"kind":"scenario","id":"{{Scenario}}","name":"S","process":"{{P}}","subject":"{{Order}}",
             "objects":[{"id":"{{Order}}","name":"o","entity":"{{Clerk}}","frozen":1}],"steps":[{"id":"{{ModifyStep}}"}]}
            """, "/objects/0/frozen");
    }

    // ---- loading ----

    [Fact]
    public async Task Rights_named_rights_and_verb_steps_load_index_and_keep_their_keys_in_schema_order()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        var process = JsonNode.Parse(h.Read(ProcessFile))!.AsObject();
        process["rights"] = JsonNode.Parse(Rights);
        process["rightsDefault"] = "deny";
        h.Write(ProcessFile, Canonical(process.ToJsonString(), "process.json", ProcessFile));
        h.Write(RightFile, Canonical(NamedRight, "right.json", RightFile));
        h.Write(ScenarioFile, Canonical(VerbScenario, "scenario.json", ScenarioFile));
        await using var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        var model = store.Current!;

        foreach (var file in new[] { ProcessFile, RightFile, ScenarioFile })
            Assert.DoesNotContain(model.LoadDiagnostics, d => (d.FilePath ?? "").EndsWith(file, StringComparison.Ordinal) && d.Rule is "MQ1002" or "MQ1003");
        Assert.DoesNotContain(model.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);

        // The files keep their keys in schema order: the default after the subject, the rights after the actions, and each entry's own.
        var written = JsonNode.Parse(h.Read(ProcessFile))!.AsObject().Select(p => p.Key).ToList();
        Assert.True(written.IndexOf("boundAttribute") < written.IndexOf("rightsDefault") && written.IndexOf("rightsDefault") < written.IndexOf("description"));
        Assert.True(written.IndexOf("actions") < written.IndexOf("rights") && written.IndexOf("rights") < written.IndexOf("states"));
        var entry = JsonNode.Parse(h.Read(ProcessFile))!["rights"]![0]!.AsObject().Select(p => p.Key);
        Assert.Equal(["id", "name", "access", "actors", "verbs", "states", "description"], entry);
        Assert.Equal(["$schema", "kind", "id", "name", "package", "access", "except", "verbs", "condition", "description"],
            JsonNode.Parse(h.Read(RightFile))!.AsObject().Select(p => p.Key));
        Assert.Equal(["id", "input", "verb", "actor", "expect"], JsonNode.Parse(h.Read(ScenarioFile))!["steps"]![1]!.AsObject().Select(p => p.Key));
        Assert.Equal(["accepted", "refusal", "refusedBy"], JsonNode.Parse(h.Read(ScenarioFile))!["steps"]![1]!["expect"]!.AsObject().Select(p => p.Key));

        // The records.
        var lifecycle = Assert.IsType<Process>(model.GetDocument(P)!.Element);
        Assert.Equal(RightAccess.Deny, lifecycle.RightsDefault);
        Assert.Equal([Inline, Deny, Use], lifecycle.Rights.Select(r => r.Id));
        Assert.Equal((false, "ClerksEditDrafts", RightAccess.Allow), (lifecycle.Rights[0].IsUse, lifecycle.Rights[0].Name, lifecycle.Rights[0].Access));
        Assert.Equal(["read", "modify"], lifecycle.Rights[0].Verbs);
        Assert.Equal((true, Named, (RightAccess?)null), (lifecycle.Rights[2].IsUse, lifecycle.Rights[2].Right, lifecycle.Rights[2].Access));
        var named = Assert.IsType<Right>(model.GetDocument(Named)!.Element);
        Assert.Equal((ElementKind.Right, RightAccess.Deny, Package), (named.Kind, named.Access, named.Package));
        Assert.Equal(["modify", "delete", "change-owner"], named.Verbs);
        Assert.Equal([Manager], named.Except);
        Assert.Empty(named.Actors);
        Assert.NotNull(named.Condition);
        var steps = Assert.IsType<Maquettiste.Engine.Model.Scenario>(model.GetDocument(Scenario)!.Element).Steps;
        Assert.Equal((StepInput.Verb, "modify"), (steps[0].Input, steps[0].Verb));
        Assert.Equal([OrderId], steps[0].Values!.Keys);
        Assert.Equal((false, Refusals.Right, Deny), (steps[1].Expect!.Accepted, steps[1].Expect!.Refusal, steps[1].Expect!.RefusedBy));

        // The index: every entry of the rights is a part of kind process-right; a named right is an element of kind right.
        Assert.Equal(("process-right", P, "/rights/0"), (model.Index.Entries[Inline].Kind, model.Index.Entries[Inline].OwnerId, model.Index.Entries[Inline].JsonPointer));
        Assert.Equal(("process-right", "/rights/2"), (model.Index.Entries[Use].Kind, model.Index.Entries[Use].JsonPointer));
        Assert.Equal("right", model.Index.Entries[Named].Kind);
        Assert.Equal("right", Assert.Single(model.Summaries(), s => s.Id == Named).Kind);
        Assert.EndsWith(RightFile, model.GetDocument(Named)!.Path, StringComparison.Ordinal);

        // The ids a right names are references.
        Assert.Contains(await store.GetReferencesAsync(Clerk, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (P, Inline, "/rights/0/actors/0"));
        Assert.Contains(await store.GetReferencesAsync(Draft, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (P, Deny, "/rights/1/states/0"));
        Assert.Contains(await store.GetReferencesAsync(Named, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (P, Use, "/rights/2/right"));
        Assert.Contains(await store.GetReferencesAsync(Manager, Ct), r => (r.FromElementId, r.JsonPointer) == (Named, "/except/0"));
        Assert.Contains(await store.GetReferencesAsync(OrderId, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (Scenario, ModifyStep, "/steps/0/values/" + OrderId));

        // Inline rights take their lifecycle's forms; a named right takes its package's.
        Assert.Equal(Inline, Assert.Single(Find(model, "@process-right:OrderLifecycle.ClerksEditDrafts")).Id);
        Assert.Equal(Deny, Assert.Single(Find(model, "@process-right:Sales.OrderLifecycle.NoDeleteInDraft")).Id);
        Assert.Equal(Named, Assert.Single(Find(model, "@right:SecretOnlyOwner")).Id);
        Assert.Empty(Find(model, "@process-right:OrderLifecycle.SecretOnlyOwner"));
    }

    [Fact]
    public async Task A_named_right_created_through_the_store_lands_in_the_rights_folder()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        await using var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);

        var created = await store.CreateAsync(Encoding.UTF8.GetBytes("""{"kind":"right","name":"EveryoneReads","access":"allow","verbs":["read"]}"""), ChangeSource.Editor, Ct);

        Assert.True(created.Outcome == SaveOutcome.Saved, string.Join("; ", created.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.EndsWith("model/rights/everyone-reads.json", store.Current!.GetDocument(created.Id!)!.Path, StringComparison.Ordinal);
        Assert.IsType<Right>(store.Current.GetDocument(created.Id!)!.Element);
    }

    // ---- the trace and the step ----

    [Fact]
    public void The_new_trace_step_and_expectation_members_are_left_out_when_unused()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var step = new ScenarioStep { Id = ModifyStep, Event = "01JPRX00000000000000000001", Expect = new StepExpectation { Accepted = false } };
        var trace = new StepTrace(0, step, false, Refusals.NoTransition, [], [], [], [Draft], new Dictionary<string, JsonElement>(),
            new Dictionary<string, JsonElement>(), "2000-01-01T00:00:00Z", false, []);

        var plain = JsonNode.Parse(JsonSerializer.Serialize(trace, web))!.AsObject();
        Assert.False(plain.ContainsKey("refusedBy") || plain.ContainsKey("rights"));
        var echo = plain["input"]!.AsObject();
        Assert.False(echo.ContainsKey("verb") || echo.ContainsKey("values") || echo.ContainsKey("end") || echo.ContainsKey("object"));
        Assert.False(echo["expect"]!.AsObject().ContainsKey("refusal") || echo["expect"]!.AsObject().ContainsKey("refusedBy"));

        var refused = trace with { Refusal = Refusals.Right, RefusedBy = Named, Rights = [new RightEvaluation(Inline, P, RightAccess.Allow), new RightEvaluation(Named, P, RightAccess.Deny)] };
        var json = JsonNode.Parse(JsonSerializer.Serialize(refused, web))!.AsObject();
        Assert.Equal(Named, json["refusedBy"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""
            [{"right":"{{Inline}}","lifecycle":"{{P}}","access":"allow"},{"right":"{{Named}}","lifecycle":"{{P}}","access":"deny"}]
            """), json["rights"]), json["rights"]!.ToJsonString());

        // The engine's list of refusals, in the order the checks run: what expect.refusal may name.
        Assert.Equal(["frozen", "actor", "right", "default", "no-transition", "guard", "gate-signer", "gate-repeat", "gate-reason"], Refusals.All);
        var schema = JsonNode.Parse(TestServices.Schemas.GetFileBytes("scenario.json").Span)!["$defs"]!["step"]!["properties"]!["expect"]!["properties"]!["refusal"]!["enum"]!;
        Assert.Equal(Refusals.All.Order(StringComparer.Ordinal), schema.AsArray().Select(n => n!.GetValue<string>()).Order(StringComparer.Ordinal));
    }

    // ---- rules ----

    [Fact]
    public void The_rule_catalog_holds_the_rights_rules_in_their_own_family()
    {
        Assert.Equal(
            [("MQ9901", DiagnosticSeverity.Error), ("MQ9902", DiagnosticSeverity.Error), ("MQ9903", DiagnosticSeverity.Error),
             ("MQ9904", DiagnosticSeverity.Warning), ("MQ9905", DiagnosticSeverity.Warning), ("MQ9906", DiagnosticSeverity.Warning)],
            RuleCatalog.All.Where(r => r.Id.StartsWith("MQ99", StringComparison.Ordinal)).Select(r => (r.Id, r.DefaultSeverity)));
        Assert.Equal("Rights", RuleCatalog.FamilyLabel("MQ99xx"));
        Assert.Equal((DiagnosticSeverity.Error, "refresh-scenario"), (RuleCatalog.Get("MQ9308").DefaultSeverity, RuleCatalog.Get("MQ9308").QuickFix));
        Assert.True(RuleCatalog.IsReplayFinding("MQ9308"));
        Assert.False(RuleCatalog.IsReplayFinding("MQ9901"));
        Assert.Contains("right", RuleCatalog.Get("MQ3104").Description, StringComparison.Ordinal);
        Assert.Contains("verb step", RuleCatalog.Get("MQ9305").Description, StringComparison.Ordinal);
    }
}
