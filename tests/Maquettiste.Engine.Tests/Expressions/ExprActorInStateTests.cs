using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Tests.Expressions;

/// <summary>
/// <c>current_actor()</c>, <c>in_state</c> and an action's sets in the language (object-effects-design.md decisions 1 to 11, section 14): text to tree
/// and back, renames reprinted, typing (current_actor() takes the type it meets; in_state over the subject, a to-one path and the running chart),
/// refusals at checks and values, the engine-only JavaScript and no SQL; the set rules MQ9801 to MQ9803, MQ3116 and the new cases of
/// MQ3103, MQ3104, MQ9020 and MQ9305; and no change for a project that uses none of this.
/// </summary>
public sealed class ExprActorInStateTests
{
    private const string P = "01JPRC00000000000000000001"; // OrderLifecycle, bound to Order.status
    private const string Credit = "01JPRC00000000000000000002"; // a lifecycle of Customer
    private const string Order = "01JENT00000000000000000001";
    private const string Customer = "01JENT00000000000000000005";
    private const string Package = "01JPKG00000000000000000001";
    private const string Status = "01JATT00000000000000000002";
    private const string SubmittedBy = "01JATT00000000000000000031";
    private const string OwnerNo = "01JATT00000000000000000032";
    private const string Token = "01JATT00000000000000000033";
    private const string Label = "01JATT00000000000000000034"; // derived
    private const string Tags = "01JATT00000000000000000035"; // a collection
    private const string Amount = "01JATT00000000000000000036"; // a decimal
    private const string CustomerEnd = "01JEND00000000000000000002";
    private const string Good = "01JSTA00000000000000000101";
    private const string Blocked = "01JSTA00000000000000000102";
    private const string Hold = "01JSTA00000000000000000103";
    private const string Checking = "01JSTA00000000000000000104";
    private const string Review = "01JSTA00000000000000000003"; // OrderLifecycle's compound Review
    private const string Pending = "01JSTA00000000000000000004";
    private const string Resume = "01JSTA00000000000000000006"; // a history state
    private const string Shipping = "01JSTA00000000000000000008";
    private const string Billing = "01JSTA00000000000000000011";
    private const string Packing = "01JSTA00000000000000000009";
    private const string Invoicing = "01JSTA00000000000000000012";
    private const string Record = "01JPRX00000000000000000021"; // recordSubmission, on submit
    private const string Notify = "01JPRX00000000000000000022"; // notifyClerk, Pending's entry
    private const string Submit = "01JPRX00000000000000000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Attrs => $$"""
        {"id":"01JATT00000000000000000001","name":"id","type":"uuid","required":true},
        {"id":"{{Status}}","name":"status","type":{"ref":"01JENM00000000000000000001"},"required":true,"default":"Draft"},
        {"id":"{{SubmittedBy}}","name":"submittedBy","type":"string","length":40},
        {"id":"{{OwnerNo}}","name":"ownerNo","type":"int64"},
        {"id":"{{Token}}","name":"token","type":"uuid"},
        {"id":"{{Label}}","name":"label","type":"string","derived":{"value":{"value":"x"} } },
        {"id":"{{Tags}}","name":"tags","type":"string","collection":true},
        {"id":"{{Amount}}","name":"amount","type":"decimal","precision":12,"scale":2}
        """;

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(Action<LoaderHarness>? prepare = null)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "process-basics");
        harness.Write("model/entities/order.json", $$"""
            {"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"{{Order}}","name":"Order","package":"{{Package}}","lifecycle":"{{P}}",
             "key":{"attributes":["01JATT00000000000000000001"],"strategy":"uuid-v7"},"attributes":[{{Attrs}}]}
            """);
        harness.Write("model/entities/customer.json", $$"""
            {"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"{{Customer}}","name":"Customer","package":"{{Package}}",
             "key":{"attributes":["01JATT00000000000000000051"]},"attributes":[{"id":"01JATT00000000000000000051","name":"id","type":"uuid"}]}
            """);
        harness.Write("model/relations/order-customer.json", $$"""
            {"$schema":"../../.schema/v1/relation.json","kind":"relation","id":"01JRXN00000000000000000001","name":"OrderCustomer",
             "ends":[{"id":"01JEND00000000000000000001","entity":"{{Order}}","role":"orders","navigation":"orders"},
                     {"id":"{{CustomerEnd}}","entity":"{{Customer}}","role":"customer","navigation":"customer","max":1}]}
            """);
        harness.Write("model/processes/credit.json", $$"""
            {"$schema":"../../.schema/v1/process.json","kind":"process","id":"{{Credit}}","name":"Credit","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JPRX00000000000000000201","entity":"{{Customer}}"}],
             "states":[{"id":"{{Good}}","name":"Good"},
                       {"id":"{{Blocked}}","name":"Blocked","type":"compound","states":[{"id":"{{Hold}}","name":"Hold"},{"id":"{{Checking}}","name":"Checking"}]}],
             "initial":"{{Good}}"}
            """);
        prepare?.Invoke(harness);
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        return (harness, store);
    }

    private static Task<ExpressionPreview> PreviewAsync(ModelStore store, string text, string site = "guard", string element = P, string? attribute = null) =>
        store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = element, Text = text, Site = site, Attribute = attribute }, Ct);

    private static async Task<ExpressionPreviewDetail> TypedAsync(ModelStore store, string text, string site = "guard", string element = P, string? attribute = null)
    {
        var preview = await PreviewAsync(store, text, site, element, attribute);
        Assert.True(preview.Preview is not null, string.Join("; ", preview.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        return preview.Preview!;
    }

    private static async Task<List<Diagnostic>> FindingsAsync(ModelStore store) =>
        [.. (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics];

    // Rewrites the process file with one edit to its JSON text.
    private static void EditProcess(LoaderHarness h, string from, string to)
    {
        var path = "model/processes/order-lifecycle.json";
        var json = h.Read(path);
        Assert.Contains(from, json, StringComparison.Ordinal);
        h.Write(path, json.Replace(from, to, StringComparison.Ordinal));
    }

    private static string Sets(params string[] sets) => "\"sets\": [" + string.Join(",", sets) + "],";

    private static string Set(string id, string attribute, string value) => $$"""{"id":"{{id}}","attribute":"{{attribute}}","value":{{value}}}""";

    // ---- text and printing ----

    [Fact]
    public async Task Actor_and_in_state_parse_print_and_reprint_after_renames()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var guard = await TypedAsync(store, "subject.submittedBy <> current_actor() and in_state(subject.customer, Credit.Blocked.Hold, Credit.Good)");
        Assert.Equal("subject.submittedBy <> current_actor() and in_state(subject.customer, Credit.Blocked.Hold, Credit.Good)", guard.Text);
        Assert.Equal("bool", guard.Type);
        Assert.Null(guard.Sql);
        Assert.Null(guard.Answer);

        var own = await TypedAsync(store, "in_state(subject, OrderLifecycle.Review)");
        Assert.Equal("in_state(subject, OrderLifecycle.Review)", own.Text);
        Assert.False(own.Nullable); // the running chart always answers

        // A tree keeps ids, so renaming the lifecycle, a state and the end reprints the text.
        var scope = ExprSites.Scope(store.Current!, store.Current!.Get<Process>(P)!, null, ExprSites.Free(store.Current!, store.Current!.Get<Process>(P)!, ExprSiteKind.Guard)!, text: true);
        var tree = ExprText.Parse("in_state(subject.customer, Credit.Blocked.Hold)", scope).Tree!;
        Assert.Equal([Hold], tree.InState);
        Assert.Equal([CustomerEnd], tree.Of);
        h.Write("model/processes/credit.json", h.Read("model/processes/credit.json").Replace("\"Credit\"", "\"Standing\"", StringComparison.Ordinal)
            .Replace("\"Hold\"", "\"OnHold\"", StringComparison.Ordinal));
        h.Write("model/relations/order-customer.json", h.Read("model/relations/order-customer.json").Replace("\"navigation\":\"customer\"", "\"navigation\":\"buyer\"", StringComparison.Ordinal));
        var model = await store.GetSnapshotAsync(Ct);
        var renamed = ExprSites.Scope(model, model.Get<Process>(P)!, null, ExprSites.Free(model, model.Get<Process>(P)!, ExprSiteKind.Guard)!, text: false);
        Assert.Equal("in_state(subject.buyer, Standing.Blocked.OnHold)", ExprText.Print(tree, renamed));
        Assert.Equal("current_actor()", ExprText.Print(new Expr { CurrentActor = true }, renamed));
    }

    // ---- typing ----

    [Fact]
    public async Task Actor_takes_the_type_it_meets_and_is_a_string_alone()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        Assert.Contains("mq.str(mq.currentActor())", (await TypedAsync(store, "subject.submittedBy = current_actor()")).JavaScript, StringComparison.Ordinal);
        Assert.Contains("mq.dec(mq.currentActor())", (await TypedAsync(store, "current_actor() = subject.ownerNo")).JavaScript, StringComparison.Ordinal);
        Assert.Contains("mq.uuid(mq.currentActor())", (await TypedAsync(store, "coalesce(subject.token, current_actor()) is not null")).JavaScript, StringComparison.Ordinal);
        Assert.Contains("mq.dec(mq.currentActor())", (await TypedAsync(store, "case when true then current_actor() else subject.ownerNo end = 1")).JavaScript, StringComparison.Ordinal);
        // Alone, a string: so it does not compare with a number through another string.
        var misfit = await PreviewAsync(store, "length(current_actor()) > 0 and subject.ownerNo = current_actor() || 'x'");
        Assert.Contains(misfit.Diagnostics, d => d.Rule == "MQ3103");
        // A set's attribute gives its type.
        var set = await TypedAsync(store, "current_actor()", "set", Record, OwnerNo);
        Assert.Equal("int64", set.Type);
        Assert.StartsWith("(row) => mq.out(mq.dec(mq.currentActor()), \"int\")", set.JavaScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_state_lowers_to_the_engine_helper_with_the_states_inside()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        Assert.Equal($$"""(row) => mq.holds(mq.inState(mq.at(row, "subject", "customer"), "{{Credit}}", ["{{Blocked}}", "{{Hold}}", "{{Checking}}"]))""",
            (await TypedAsync(store, "in_state(subject.customer, Credit.Blocked)")).JavaScript);
        Assert.Equal($$"""(row) => mq.holds(mq.inState(row, "{{P}}", ["{{Review}}", "{{Pending}}", "01JSTA00000000000000000005"]))""",
            (await TypedAsync(store, "in_state(subject, OrderLifecycle.Review)")).JavaScript);
        // The helper lives beside mqAt, never in the runtime packs copy.
        Assert.DoesNotContain("inState", ExprRuntime.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("actor", ExprRuntime.Source, StringComparison.Ordinal);
        Assert.Contains("const mqInState", ExprRuntime.Clock, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Engine_helpers_answer_in_the_sandbox()
    {
        using var evaluator = ExprEvaluator.Create([
            KeyValuePair.Create("a", "(row) => ((mq) => mq.inState(mq.at(row, \"subject\"), \"L\", [\"s1\", \"s2\"]))(mqAt(\"2026-01-01T00:00:00Z\", \"bob\"))"),
            KeyValuePair.Create("b", "(row) => ((mq) => mq.str(mq.currentActor()))(mqAt(\"2026-01-01T00:00:00Z\", \"bob\"))"),
            KeyValuePair.Create("c", "(row) => ((mq) => mq.dec(mq.currentActor()))(mqAt(\"2026-01-01T00:00:00Z\", \"bob\"))")], null, Ct);
        using var some = JsonDocument.Parse("""{"subject":{"$states":{"L":["s2"]}}}""");
        using var none = JsonDocument.Parse("""{"subject":{"$states":{"L":["s3"]}}}""");
        using var missing = JsonDocument.Parse("""{"subject":{}}""");
        Assert.Equal(true, evaluator.Evaluate("a", some.RootElement, Ct));
        Assert.Equal(false, evaluator.Evaluate("a", none.RootElement, Ct));
        Assert.Null(evaluator.Evaluate("a", missing.RootElement, Ct));
        Assert.Equal("bob", evaluator.Evaluate("b", missing.RootElement, Ct));
        Assert.Null(evaluator.Evaluate("c", missing.RootElement, Ct)); // an identity that does not convert reads null
    }

    [Fact]
    public async Task Refusals_at_checks_values_and_in_sql()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        Assert.Contains((await PreviewAsync(store, "current_actor() = 'x'", "check", Order)).Diagnostics, d => d.Rule == "MQ3104");
        Assert.Contains((await PreviewAsync(store, "current_actor()", "value", Order)).Diagnostics, d => d.Rule == "MQ3104");
        Assert.Contains((await PreviewAsync(store, "in_state(subject, Credit.Good)", "check", Order)).Diagnostics, d => d.Rule == "MQ3104");

        var model = store.Current!;
        var site = ExprSites.Free(model, model.Get<Process>(P)!, ExprSiteKind.Guard)!;
        var scope = ExprSites.Scope(model, model.Get<Process>(P)!, null, site, text: true);
        var typed = ExprTyper.Type(ExprText.Parse("subject.submittedBy = current_actor()", scope).Tree!, scope, new List<Diagnostic>())!;
        Assert.True(ExprLowering.IsEngineOnly(typed));
        var problems = new List<Diagnostic>();
        Assert.Null(ExprLowering.ToPostgreSql(typed, ExprColumns.None, ExprSiteKind.Guard, problems));
        Assert.Contains(problems, d => d.Rule == "MQ3104");
    }

    // ---- the rules ----

    [Fact]
    public async Task In_state_refusals_are_MQ3116()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        // Two lifecycles; a history state; a lifecycle that cannot govern the subject; a to-many path.
        Assert.Contains((await PreviewAsync(store, "in_state(subject, Credit.Good, OrderLifecycle.Review)")).Diagnostics, d => d.Rule == "MQ3116");
        Assert.Contains((await PreviewAsync(store, "in_state(subject, OrderLifecycle.Review.Resume)")).Diagnostics, d => d.Rule == "MQ3116");
        Assert.Contains((await PreviewAsync(store, "in_state(subject, Credit.Good)")).Diagnostics, d => d.Rule == "MQ3116");
        Assert.Contains((await PreviewAsync(store, "in_state(subject, Nothing.Good)")).Diagnostics, d => d.Rule == "MQ3116");
        // Parallel regions of the running chart pass.
        Assert.NotNull((await PreviewAsync(store, "in_state(subject, OrderLifecycle.Fulfilment.Shipping.Packing) and in_state(subject, OrderLifecycle.Fulfilment.Billing)")).Preview);
    }

    [Fact]
    public async Task Sets_type_against_their_attribute_and_read_what_a_guard_reads()
    {
        var (h, store) = await OpenAsync(h => EditProcess(h, "\"name\": \"recordSubmission\",",
            "\"name\": \"recordSubmission\"," + Sets(
                Set("01JPRX00000000000000000301", SubmittedBy, """{"currentActor":true}"""),
                Set("01JPRX00000000000000000302", OwnerNo, """{"value":"x"}"""),
                Set("01JPRX00000000000000000303", Token, """{"path":["01JATT00000000000000000021"]}"""))));
        using var _ = h;
        await using var __ = store;

        var findings = await FindingsAsync(store);
        // The payload of submit reads; a string set to an int64 is MQ3103 at the value.
        Assert.Contains(findings, d => d.Rule == "MQ3103" && d.JsonPointer == "/actions/0/sets/1/value" && d.Message.Contains("set's value", StringComparison.Ordinal));
        Assert.Contains(findings, d => d.Rule == "MQ3103" && d.JsonPointer == "/actions/0/sets/2/value");
        Assert.DoesNotContain(findings, d => d.JsonPointer == "/actions/0/sets/0/value");
        Assert.Equal("01JPRX00000000000000000301", store.Current!.Index.Entries["01JPRX00000000000000000301"].Id);
        Assert.Equal("set", store.Current!.Index.Entries["01JPRX00000000000000000301"].Kind);

        // An entry action reads no payload (MQ3102); a stored set previews by its id.
        Assert.Contains((await PreviewAsync(store, "event.note", "set", Notify, SubmittedBy)).Diagnostics, d => d.Rule == "MQ3102");
        Assert.Equal("current_actor()", (await store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = "01JPRX00000000000000000301" }, Ct)).Preview!.Text);
        // now() reads at a set (no MQ3104), and must fit the attribute.
        var clock = await PreviewAsync(store, "now()", "set", Record, SubmittedBy);
        Assert.Contains(clock.Diagnostics, d => d.Rule == "MQ3103");
        Assert.DoesNotContain(clock.Diagnostics, d => d.Rule == "MQ3104");

        // current_actor() is an identity: a string, an integer or a uuid. Set to a decimal it is MQ3103, which the preview shows; beside a decimal in a
        // comparison it stays a string, which doesn't fit.
        var money = await PreviewAsync(store, "current_actor()", "set", Record, Amount);
        Assert.Contains(money.Diagnostics, d => d.Rule == "MQ3103" && d.Message.Contains("identity", StringComparison.Ordinal));
        Assert.DoesNotContain((await PreviewAsync(store, "current_actor()", "set", Record, OwnerNo)).Diagnostics, d => d.Rule == "MQ3103");
        Assert.Contains((await PreviewAsync(store, "current_actor() = subject.amount")).Diagnostics, d => d.Rule == "MQ3103");
    }

    [Fact]
    public async Task Set_rules_MQ9801_to_MQ9803()
    {
        var (h, store) = await OpenAsync(h => EditProcess(h, "\"name\": \"recordSubmission\",",
            "\"name\": \"recordSubmission\"," + Sets(
                Set("01JPRX00000000000000000311", SubmittedBy, """{"currentActor":true}"""),
                Set("01JPRX00000000000000000312", SubmittedBy, """{"value":"b"}"""),
                Set("01JPRX00000000000000000313", "01JATT00000000000000000051", """{"null":true}"""),
                Set("01JPRX00000000000000000314", CustomerEnd, """{"null":true}"""),
                Set("01JPRX00000000000000000315", Tags, """{"null":true}"""),
                Set("01JPRX00000000000000000316", Label, """{"value":"a"}"""),
                Set("01JPRX00000000000000000317", Status, """{"null":true}"""))));
        using var _ = h;
        await using var __ = store;

        var findings = (await FindingsAsync(store)).Where(d => d.Rule.StartsWith("MQ98", StringComparison.Ordinal)).Select(d => (d.Rule, d.JsonPointer)).ToList();
        Assert.Equal([
            ("MQ9801", "/actions/0/sets/2/attribute"), // another entity's attribute
            ("MQ9801", "/actions/0/sets/3/attribute"), // a relation end
            ("MQ9801", "/actions/0/sets/4/attribute"), // a collection
            ("MQ9802", "/actions/0/sets/5/attribute"), // derived
            ("MQ9802", "/actions/0/sets/6/attribute"), // the bound attribute
            ("MQ9803", "/actions/0/sets/1/attribute")], findings.Order());
    }

    [Fact]
    public async Task A_process_without_subjects_and_a_library_refuse_sets()
    {
        var (h, store) = await OpenAsync(h =>
        {
            EditProcess(h, "\"name\": \"recordSubmission\",", "\"name\": \"recordSubmission\"," + Sets(Set("01JPRX00000000000000000321", SubmittedBy, """{"currentActor":true}""")));
            EditProcess(h, "\"subject\": \"01JENT00000000000000000001\",", "");
            EditProcess(h, "\"boundAttribute\": \"01JATT00000000000000000002\",", "");
            h.Write("model/processes/shared.json", $$"""
                {"$schema":"../../.schema/v1/process.json","kind":"process","id":"01JPRC00000000000000000003","name":"Shared","package":"{{Package}}","use":"library",
                 "actions":[{"id":"01JPRX00000000000000000331","name":"stamp","sets":[{"id":"01JPRX00000000000000000332","attribute":"{{SubmittedBy}}","value":{"currentActor":true} }]}]}
                """);
        });
        using var _ = h;
        await using var __ = store;

        var findings = await FindingsAsync(store);
        Assert.Contains(findings, d => d.Rule == "MQ9801" && d.ElementId == "01JPRX00000000000000000321" && d.Message.Contains("no subjects", StringComparison.Ordinal));
        Assert.Contains(findings, d => d.Rule == "MQ9020" && d.JsonPointer == "/actions/0/sets");
        Assert.DoesNotContain(findings, d => d.Rule.StartsWith("MQ98", StringComparison.Ordinal) && d.ElementId == "01JPRX00000000000000000332");
        // With no subjects, in_state reads only the running chart.
        Assert.NotNull((await PreviewAsync(store, "in_state(subject, OrderLifecycle.Review)")).Preview);
        Assert.Contains((await PreviewAsync(store, "in_state(subject, Credit.Good)")).Diagnostics, d => d.Rule == "MQ3116" && d.Message.Contains("no subjects", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Object_states_are_checked_MQ9305()
    {
        static string Scenario(string orderStates, string customerStates) => $$"""
            {"$schema":"../../../.schema/v1/scenario.json","kind":"scenario","id":"01JSCN00000000000000000091","name":"WithStates","process":"{{P}}",
             "objects":[{"id":"01JXBJ00000000000000000091","name":"order","entity":"{{Order}}","states":[{{orderStates}}],"links":[{"end":"{{CustomerEnd}}","object":"01JXBJ00000000000000000092"}]},
                        {"id":"01JXBJ00000000000000000092","name":"customer","entity":"{{Customer}}","states":[{{customerStates}}]}],
             "subject":"01JXBJ00000000000000000091",
             "steps":[{"id":"01JSTP00000000000000000091","event":"{{Submit}}"}]}
            """;
        async Task<string?> ProblemAsync(string orderStates, string customerStates)
        {
            var (h, store) = await OpenAsync(x => x.Write("model/scenarios/order-lifecycle/with-states.json", Scenario(orderStates, customerStates)));
            using var _ = h;
            await using var __ = store;
            var model = store.Current!;
            var scenario = model.Get<Scenario>("01JSCN00000000000000000091")!;
            return Maquettiste.Engine.Processes.ScenarioObjects.Check(model, model.Get<Process>(P)!, scenario.Objects, scenario.Subject, (_, _) => null)?.Message;
        }

        Assert.Null(await ProblemAsync("", $"\"{Hold}\""));
        Assert.Contains("works out", await ProblemAsync($"\"{Pending}\"", ""), StringComparison.Ordinal);
        Assert.Contains("does not govern", await ProblemAsync("", $"\"{Pending}\""), StringComparison.Ordinal);
        Assert.Contains("cannot be active together", await ProblemAsync("", $"\"{Good}\", \"{Hold}\""), StringComparison.Ordinal);
        Assert.Null(await ProblemAsync("", $"\"{Blocked}\", \"{Hold}\""));
        _ = (Shipping, Billing, Packing, Invoicing, Resume);
    }

    // ---- no change ----

    [Fact]
    public async Task A_project_that_uses_none_of_this_lowers_and_finds_as_before()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        // An existing guard's JavaScript and the runtime are unchanged; no new rule fires.
        var plain = await TypedAsync(store, "subject.submittedBy = 'x' and now() > datetimeoffset '2020-01-01T00:00:00Z'");
        Assert.Equal("""(row) => mq.holds(mq.and(mq.eq(mq.str(mq.at(row, "subject", "submittedBy")), "x"), mq.gt(mq.now(), mq.tsz("2020-01-01T00:00:00Z"))))""", plain.JavaScript);
        Assert.False(ExprRuntime.Source.Contains("$states", StringComparison.Ordinal));
        var findings = await FindingsAsync(store);
        Assert.DoesNotContain(findings, d => d.Rule is "MQ3116" or "MQ9801" or "MQ9802" or "MQ9803" or "MQ9307");
        Assert.Null(ExprSites.SubjectDepth(store.Current!, store.Current!.Get<Process>(P)!));
    }
}
