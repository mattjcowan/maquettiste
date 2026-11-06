using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// The rights rules of part 1 (rights-design.md sections 5 and 14): failing and passing cases of MQ9901 to MQ9906, and the new cases of MQ3001,
/// MQ2001, MQ2002, MQ9701, MQ9704, MQ9604, MQ9106, MQ3101, MQ3102, MQ3103, MQ3104, MQ3108 and MQ9305; the right site's scope, a named right's
/// condition read in the scope of its uses and typed at each use, its text kept with no use; and <c>preview_expression</c>'s lifecycle.
/// </summary>
public sealed class RightRuleTests
{
    private const string P = "01JPRC00000000000000000001"; // OrderLifecycle, bound to Order.status, one-subject form
    private const string Credit = "01JPRC00000000000000000002"; // a lifecycle of Customer
    private const string Flow = "01JPRC00000000000000000003"; // an orchestration
    private const string Order = "01JENT00000000000000000001";
    private const string Customer = "01JENT00000000000000000005";
    private const string Package = "01JPKG00000000000000000001";
    private const string SubmittedBy = "01JATT00000000000000000931";
    private const string Amount = "01JATT00000000000000000936";
    private const string Classification = "01JATT00000000000000000901"; // of stereotype classified, carried by Order and Customer
    private const string ContextAmount = "01JATT00000000000000000011"; // OrderLifecycle's context
    private const string OrdersEnd = "01JEND00000000000000000001";
    private const string CustomerEnd = "01JEND00000000000000000002";
    private const string Clerk = "01JACT00000000000000000001";
    private const string Manager = "01JACT00000000000000000002";
    private const string Finance = "01JACT00000000000000000003";
    private const string Submitter = "01JACT00000000000000000901"; // the actor subject.submittedBy names: only Order holds it
    private const string Draft = "01JSTA00000000000000000001";
    private const string Decide = "01JSTA00000000000000000002"; // a choice state
    private const string Review = "01JSTA00000000000000000003"; // compound
    private const string Resume = "01JSTA00000000000000000006"; // a history state
    private const string Good = "01JSTA00000000000000000101"; // Credit's
    private const string Named = "01JRGT00000000000000000901";
    private const string R1 = "01JRGT00000000000000000911";
    private const string R2 = "01JRGT00000000000000000912";
    private const string R3 = "01JRGT00000000000000000913";
    private const string ProcessFile = "model/processes/order-lifecycle.json";
    private const string CreditFile = "model/processes/credit.json";
    private const string RightFile = "model/rights/secret-only-owner.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(Action<LoaderHarness>? prepare = null)
    {
        var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        h.Write("model/vocabularies/stereotypes/classified.json", $$"""
            {"kind":"stereotype","id":"01JSTR00000000000000000901","key":"classified","name":"classified","appliesTo":["entity"],
             "attributes":[{"id":"{{Classification}}","name":"classification","type":"string","length":20}]}
            """);
        h.Write("model/entities/order.json", $$"""
            {"kind":"entity","id":"{{Order}}","name":"Order","package":"{{Package}}","lifecycle":"{{P}}","stereotypes":["classified"],
             "key":{"attributes":["01JATT00000000000000000001"],"strategy":"uuid-v7"},
             "attributes":[{"id":"01JATT00000000000000000001","name":"id","type":"uuid","required":true},
               {"id":"01JATT00000000000000000002","name":"status","type":{"ref":"01JENM00000000000000000001"},"required":true,"default":"Draft"},
               {"id":"{{SubmittedBy}}","name":"submittedBy","type":"string","length":40},
               {"id":"{{Amount}}","name":"amount","type":"decimal","precision":12,"scale":2}]}
            """);
        h.Write("model/entities/customer.json", $$"""
            {"kind":"entity","id":"{{Customer}}","name":"Customer","package":"{{Package}}","stereotypes":["classified"],
             "key":{"attributes":["01JATT00000000000000000051"]},"attributes":[{"id":"01JATT00000000000000000051","name":"id","type":"uuid"}]}
            """);
        h.Write("model/relations/order-customer.json", $$"""
            {"kind":"relation","id":"01JRXN00000000000000000001","name":"OrderCustomer",
             "ends":[{"id":"{{OrdersEnd}}","entity":"{{Order}}","role":"orders","navigation":"orders"},
                     {"id":"{{CustomerEnd}}","entity":"{{Customer}}","role":"customer","navigation":"customer","max":1}]}
            """);
        h.Write(CreditFile, $$"""
            {"kind":"process","id":"{{Credit}}","name":"Credit","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JPRX00000000000000000201","entity":"{{Customer}}"}],
             "events":[{"id":"01JPRX00000000000000000202","name":"block","actors":["{{Finance}}"]}],
             "states":[{"id":"{{Good}}","name":"Good"},{"id":"01JSTA00000000000000000102","name":"Blocked"}],"initial":"{{Good}}",
             "transitions":[{"id":"01JTRN00000000000000000201","source":"{{Good}}","event":"01JPRX00000000000000000202","targets":["01JSTA00000000000000000102"]}]}
            """);
        h.Write("model/actors/submitter.json", $$"""{"kind":"actor","id":"{{Submitter}}","name":"Submitter","type":"person","relative":{"path":["{{SubmittedBy}}"]} }""");
        prepare?.Invoke(h);
        var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        return (h, store);
    }

    /// <summary>Writes rights (and a default) into a process file.</summary>
    private static void Rights(LoaderHarness h, string rights, string? rightsDefault = null, string file = ProcessFile, Action<JsonObject>? more = null)
    {
        var process = JsonNode.Parse(h.Read(file))!.AsObject();
        process["rights"] = JsonNode.Parse(rights);
        if (rightsDefault is not null)
            process["rightsDefault"] = rightsDefault;
        more?.Invoke(process);
        h.Write(file, process.ToJsonString());
    }

    private static string NamedRight(string extra = "", string name = "SecretOnlyOwner", string id = Named) =>
        $$"""{"kind":"right","id":"{{id}}","name":"{{name}}","package":"{{Package}}","access":"deny","verbs":["modify","delete"]{{extra}}}""";

    private static async Task<List<Diagnostic>> FindingsAsync(ModelStore store) => [.. (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics];

    /// <summary>Where a rule fires: file name and pointer, in order.</summary>
    private static List<string> Hits(IEnumerable<Diagnostic> diagnostics, string rule) =>
        [.. diagnostics.Where(d => d.Rule == rule).Select(d => Path.GetFileName(d.FilePath) + ":" + d.JsonPointer).Order(StringComparer.Ordinal)];

    private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(d => d.Rule + " " + Path.GetFileName(d.FilePath) + d.JsonPointer + " " + d.Message));

    private static async Task<List<Diagnostic>> ValidateAsync(Action<LoaderHarness> prepare)
    {
        var (h, store) = await OpenAsync(prepare);
        using var _ = h;
        await using var __ = store;
        return await FindingsAsync(store);
    }

    // ---- no change ----

    [Fact]
    public async Task A_model_without_rights_has_no_rights_findings_and_the_same_findings_as_before()
    {
        var before = await ValidateAsync(_ => { });
        Assert.DoesNotContain(before, d => d.Rule.StartsWith("MQ99", StringComparison.Ordinal));
        // An unused named right adds its own findings only, and nothing anywhere else but the count of texts to translate.
        var after = await ValidateAsync(h => h.Write(RightFile, NamedRight()));
        Assert.Equal(Describe(before.Where(d => !d.Rule.StartsWith("MQ72", StringComparison.Ordinal))),
            Describe(after.Where(d => !(d.FilePath ?? "").EndsWith(RightFile, StringComparison.Ordinal) && !d.Rule.StartsWith("MQ72", StringComparison.Ordinal))));
    }

    // ---- MQ9901 ----

    [Fact]
    public async Task MQ9901_needs_one_form_per_entry_a_named_right_and_a_lifecycle()
    {
        var found = await ValidateAsync(h =>
        {
            h.Write(RightFile, NamedRight());
            Rights(h, $$"""
                [{"id":"{{R1}}","right":"{{Named}}","name":"Both","access":"deny","verbs":["delete"]},
                 {"id":"{{R2}}","name":"NoAccess","verbs":["delete"]},
                 {"id":"{{R3}}"},
                 {"id":"01JRGT00000000000000000914","right":"{{Clerk}}"},
                 {"id":"01JRGT00000000000000000915","right":"01JRGT00000000000000000999"},
                 {"id":"01JRGT00000000000000000916","right":"{{Named}}"},
                 {"id":"01JRGT00000000000000000917","name":"Fine","access":"deny","verbs":["delete"]}]
                """);
            h.Write("model/processes/flow.json", $$"""
                {"kind":"process","id":"{{Flow}}","name":"Flow","package":"{{Package}}","rightsDefault":"deny",
                 "rights":[{"id":"01JRGT00000000000000000921","name":"R","access":"deny","verbs":["delete"]}],
                 "states":[{"id":"01JSTA00000000000000000301","name":"A"}]}
                """);
        });

        Assert.Equal(["flow.json:/rights", "flow.json:/rightsDefault", "order-lifecycle.json:/rights/0", "order-lifecycle.json:/rights/1",
            "order-lifecycle.json:/rights/2", "order-lifecycle.json:/rights/3/right"], Hits(found, "MQ9901"));
        // A use of an actor is MQ9901, not MQ2002; a use of nothing is MQ2001 alone.
        Assert.DoesNotContain(found, d => d.Rule == "MQ2002" && (d.JsonPointer ?? "").StartsWith("/rights", StringComparison.Ordinal));
        Assert.Equal(["order-lifecycle.json:/rights/4/right"], Hits(found, "MQ2001").Where(h => h.Contains("/rights", StringComparison.Ordinal)));
    }

    // ---- MQ9902, MQ9903 ----

    [Fact]
    public async Task MQ9902_names_states_of_the_lifecycle_an_object_can_be_in()
    {
        var found = await ValidateAsync(h => Rights(h, $$"""
            [{"id":"{{R1}}","name":"A","access":"deny","verbs":["delete"],"states":["{{Draft}}","{{Review}}"]},
             {"id":"{{R2}}","name":"B","access":"deny","verbs":["delete"],"states":["{{Good}}","{{Decide}}","{{Resume}}"]}]
            """));

        Assert.Equal(["order-lifecycle.json:/rights/1/states/0", "order-lifecycle.json:/rights/1/states/1", "order-lifecycle.json:/rights/1/states/2"], Hits(found, "MQ9902"));
    }

    [Fact]
    public async Task MQ9903_takes_ends_every_subject_holds_beside_link_verbs_and_a_named_rights_at_each_use()
    {
        var found = await ValidateAsync(h =>
        {
            h.Write(RightFile, NamedRight($$""","ends":["{{CustomerEnd}}"]""").Replace("\"modify\",\"delete\"", "\"connect\",\"change-owner\"", StringComparison.Ordinal));
            Rights(h, $$"""
                [{"id":"{{R1}}","name":"NoBomChanges","access":"deny","verbs":["connect","disconnect","read","modify","freeze","thaw"],"ends":["{{CustomerEnd}}"]},
                 {"id":"{{R2}}","name":"Wrong","access":"deny","verbs":["delete","connect"],"ends":["{{CustomerEnd}}"]},
                 {"id":"{{R3}}","right":"{{Named}}"}]
                """);
            Rights(h, $$"""
                [{"id":"01JRGT00000000000000000931","name":"NotHeld","access":"deny","verbs":["connect"],"ends":["{{CustomerEnd}}","{{OrdersEnd}}"]},
                 {"id":"01JRGT00000000000000000932","right":"{{Named}}"}]
                """, file: CreditFile);
        });

        Assert.Equal(["credit.json:/rights/0/ends/0", "credit.json:/rights/1/right", "order-lifecycle.json:/rights/1/ends", "secret-only-owner.json:/ends"],
            Hits(found, "MQ9903"));
        Assert.Contains("'delete'", found.Single(d => d.Rule == "MQ9903" && d.JsonPointer == "/rights/1/ends").Message, StringComparison.Ordinal);
    }

    // ---- MQ9904, MQ9905, MQ9906 ----

    [Fact]
    public async Task MQ9904_warns_of_an_allow_right_under_an_allow_default_only()
    {
        const string rights = $$"""
            [{"id":"{{R1}}","name":"EveryoneReads","access":"allow","verbs":["read"]},
             {"id":"{{R2}}","name":"NoDelete","access":"deny","verbs":["delete"]},
             {"id":"{{R3}}","right":"01JRGT00000000000000000902"}]
            """;
        void Allow(LoaderHarness h) => h.Write("model/rights/everyone-modifies.json", NamedRight(name: "EveryoneModifies", id: "01JRGT00000000000000000902")
            .Replace("\"deny\"", "\"allow\"", StringComparison.Ordinal));

        var allowing = await ValidateAsync(h => { Allow(h); Rights(h, rights); });
        var explicitAllow = await ValidateAsync(h => { Allow(h); Rights(h, rights, "allow"); });
        var denying = await ValidateAsync(h => { Allow(h); Rights(h, rights, "deny", more: p => p["events"]!.AsArray().Clear()); });

        Assert.Equal(["order-lifecycle.json:/rights/0", "order-lifecycle.json:/rights/2"], Hits(allowing, "MQ9904"));
        Assert.All(allowing.Where(d => d.Rule == "MQ9904"), d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.Equal(Hits(allowing, "MQ9904"), Hits(explicitAllow, "MQ9904"));
        Assert.Empty(Hits(denying, "MQ9904"));
    }

    [Fact]
    public async Task MQ9905_warns_of_an_event_or_task_no_one_can_do_under_a_deny_default()
    {
        // ship and cancel list no actors and no gate; approve has actors and a gated transition; submit and pay have actors.
        var bare = await ValidateAsync(h => Rights(h, "[]", "deny"));
        Assert.Equal(["order-lifecycle.json:/events/2", "order-lifecycle.json:/events/4"], Hits(bare, "MQ9905"));
        Assert.Empty(Hits(await ValidateAsync(h => Rights(h, "[]")), "MQ9905"));

        // An allow right naming the event's verb lets someone through; a raised event is the process's own; a task with no actors needs an
        // allow right for complete.
        var allowed = await ValidateAsync(h => Rights(h, $$"""[{"id":"{{R1}}","name":"ClerksShip","access":"allow","actors":["{{Clerk}}"],"verbs":["promote"]}]""", "deny", more: p =>
        {
            p["events"]![2]!["verb"] = "promote";
            p["actions"]![0]!["raises"] = new JsonArray("01JPRX00000000000000000005");
            p["states"]![2]!["states"]![1]!["invoke"]![0]!.AsObject().Remove("actors");
        }));
        Assert.Equal(["order-lifecycle.json:/states/2/states/1/invoke/0"], Hits(allowed, "MQ9905"));
        var completing = await ValidateAsync(h => Rights(h, $$"""[{"id":"{{R1}}","name":"AnyoneCompletes","access":"allow","verbs":["complete","promote"]}]""", "deny", more: p =>
        {
            p["events"]![2]!["verb"] = "promote";
            p["events"]![4]!["actors"] = new JsonArray(Clerk);
            p["states"]![2]!["states"]![1]!["invoke"]![0]!.AsObject().Remove("actors");
        }));
        Assert.Empty(Hits(completing, "MQ9905"));
    }

    [Fact]
    public async Task MQ9906_warns_of_an_actor_in_both_actors_and_except()
    {
        var found = await ValidateAsync(h =>
        {
            h.Write(RightFile, NamedRight($$""","actors":["{{Manager}}","{{Clerk}}"],"except":["{{Clerk}}"]"""));
            Rights(h, $$"""
                [{"id":"{{R1}}","name":"A","access":"deny","verbs":["delete"],"actors":["{{Clerk}}"],"except":["{{Manager}}","{{Clerk}}"]},
                 {"id":"{{R2}}","name":"B","access":"deny","verbs":["delete"],"except":["{{Clerk}}"]}]
                """);
        });

        Assert.Equal(["order-lifecycle.json:/rights/0/except/1", "secret-only-owner.json:/except/0"], Hits(found, "MQ9906"));
    }

    // ---- changed rules: names, references, verbs, actors ----

    [Fact]
    public async Task Names_references_and_verbs_of_rights_are_checked_as_elsewhere()
    {
        var found = await ValidateAsync(h =>
        {
            h.Write(RightFile, NamedRight().Replace("\"modify\"", "\"escalate\"", StringComparison.Ordinal));
            h.Write("model/rights/secret-again.json", NamedRight(name: "SecretOnlyOwner", id: "01JRGT00000000000000000903"));
            h.Write("model/rights/elsewhere.json", NamedRight(name: "SecretOnlyOwner", id: "01JRGT00000000000000000904").Replace(",\"package\":\"" + Package + "\"", "", StringComparison.Ordinal));
            h.Write("model/vocabularies/verbs.json", """{"kind":"verb-vocabulary","id":"01JVRB00000000000000000901","name":"verbs","hidden":["copy"]}""");
            Rights(h, $$"""
                [{"id":"{{R1}}","name":"Same","access":"deny","verbs":["copy","quarantine"],"except":["01JACT00000000000000000999"]},
                 {"id":"{{R2}}","name":"Same","access":"deny","verbs":["delete"],"except":["{{Draft}}"]}]
                """);
        });

        // The first file by path keeps the name; a named right in another package may share it.
        Assert.Equal(["order-lifecycle.json:/rights/1/name", "secret-only-owner.json:/name"], Hits(found, "MQ3001"));
        Assert.Equal(["order-lifecycle.json:/rights/0/except/0"], Hits(found, "MQ2001"));
        Assert.Equal(["order-lifecycle.json:/rights/1/except/0"], Hits(found, "MQ2002"));
        Assert.Equal(["order-lifecycle.json:/rights/0/verbs/1", "secret-only-owner.json:/verbs/0"], Hits(found, "MQ9701"));
        Assert.Equal(["order-lifecycle.json:/rights/0/verbs/0"], Hits(found, "MQ9704"));
    }

    [Fact]
    public async Task MQ9604_judges_actors_from_the_object_in_actors_and_except_at_each_lifecycle_that_uses_them()
    {
        var found = await ValidateAsync(h =>
        {
            h.Write(RightFile, NamedRight($$""","except":["{{Submitter}}"]"""));
            Rights(h, $$"""[{"id":"{{R1}}","name":"A","access":"deny","verbs":["delete"],"except":["{{Submitter}}"]},{"id":"{{R2}}","right":"{{Named}}"}]""");
            Rights(h, $$"""
                [{"id":"01JRGT00000000000000000931","name":"A","access":"deny","verbs":["delete"],"actors":["{{Submitter}}"]},
                 {"id":"01JRGT00000000000000000932","right":"{{Named}}"}]
                """, file: CreditFile);
        });

        // Order holds submittedBy, Customer does not.
        Assert.Equal(["credit.json:/rights/0/actors/0", "credit.json:/rights/1/right"], Hits(found, "MQ9604"));
    }

    [Fact]
    public async Task MQ9106_counts_an_actor_a_right_names_as_used()
    {
        void Lonely(LoaderHarness h) => h.Write("model/actors/auditor.json", """{"kind":"actor","id":"01JACT00000000000000000902","name":"Auditor","type":"role"}""");

        Assert.Contains("auditor.json:", Hits(await ValidateAsync(Lonely), "MQ9106"));
        Assert.DoesNotContain("auditor.json:", Hits(await ValidateAsync(h =>
        {
            Lonely(h);
            h.Write(RightFile, NamedRight(""","except":["01JACT00000000000000000902"]"""));
        }), "MQ9106"));
        Assert.DoesNotContain("auditor.json:", Hits(await ValidateAsync(h =>
        {
            Lonely(h);
            Rights(h, """[{"id":"01JRGT00000000000000000911","name":"A","access":"deny","verbs":["delete"],"actors":["01JACT00000000000000000902"]}]""");
        }), "MQ9106"));
    }

    // ---- the right site ----

    [Fact]
    public async Task A_rights_condition_reads_the_object_the_actor_states_and_the_clock_but_no_context()
    {
        string Condition(string tree) => $$$"""[{"id":"{{{R1}}}","name":"A","access":"deny","verbs":["delete"],"condition":{{{tree}}} }]""";
        var reads = await ValidateAsync(h => Rights(h, Condition($$"""
            {"op":"and","args":[{"op":"eq","args":[{"path":["{{SubmittedBy}}"]},{"currentActor":true}]},{"inState":["{{Review}}"]},
              {"op":"le","args":[{"now":true},{"now":true}]},{"op":"isNotNull","args":[{"path":["{{CustomerEnd}}","01JATT00000000000000000051"]}]}]}
            """)));
        Assert.DoesNotContain(reads, d => d.Rule.StartsWith("MQ31", StringComparison.Ordinal));

        var context = await ValidateAsync(h => Rights(h, Condition($$"""{"op":"gt","args":[{"path":["{{ContextAmount}}"]},{"value":0}]}""")));
        Assert.Contains("never the context", Assert.Single(context, d => d.Rule == "MQ3102").Message, StringComparison.Ordinal);
        Assert.Equal(["order-lifecycle.json:/rights/0/condition/args/0"], Hits(context, "MQ3102"));

        var notBool = await ValidateAsync(h => Rights(h, Condition($$"""{"path":["{{SubmittedBy}}"]}""")));
        Assert.Contains("right's condition must be bool", Assert.Single(notBool, d => d.Rule == "MQ3103").Message, StringComparison.Ordinal);
        Assert.Equal(["order-lifecycle.json:/rights/0/condition"], Hits(await ValidateAsync(h => Rights(h, Condition("""{"text":"subject.submittedBy = 'x'"}"""))), "MQ3108"));
    }

    [Fact]
    public async Task Text_for_an_inline_rights_condition_is_saved_as_a_tree_in_the_lifecycles_scope()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var current = (await store.GetElementAsync(P, Ct))!;
        var node = JsonNode.Parse(current.Json.GetRawText())!.AsObject();
        node["rights"] = JsonNode.Parse($$"""[{"id":"{{R1}}","name":"A","access":"deny","verbs":["delete"],"condition":{"text":"subject.amount > 10 and current_actor() <> subject.submittedBy"} }]""");

        var saved = await store.SaveAsync(P, Encoding.UTF8.GetBytes(node.ToJsonString()), current.Hash, ChangeSource.Editor, Ct);

        Assert.True(saved.Outcome == SaveOutcome.Saved, Describe(saved.Diagnostics));
        var condition = store.Current!.Get<Process>(P)!.Rights[0].Condition!;
        Assert.Null(condition.Text);
        Assert.Equal([Amount], condition.Args![0].Args![0].Path);
    }

    // ---- named rights ----

    [Fact]
    public async Task A_named_right_with_no_use_keeps_its_text_and_has_MQ3108_alone()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var created = await store.CreateAsync(Encoding.UTF8.GetBytes(NamedRight(""","condition":{"text":"subject.nothing = (("}""")), ChangeSource.Editor, Ct);

        Assert.True(created.Outcome == SaveOutcome.Saved, Describe(created.Diagnostics));
        Assert.Equal("subject.nothing = ((", store.Current!.Get<Right>(Named)!.Condition!.Text);
        var found = await FindingsAsync(store);
        Assert.Equal(["secret-only-owner.json:/condition"], Hits(found, "MQ3108"));
        Assert.Contains("no lifecycle uses the right", found.Single(d => d.Rule == "MQ3108").Message, StringComparison.Ordinal);
        Assert.DoesNotContain(found, d => (d.FilePath ?? "").EndsWith(RightFile, StringComparison.Ordinal) && d.Rule != "MQ3108");
        // text-to-trees leaves it as it is.
        var converted = await store.ApplyBatchAsync(store.ParseBatch("""{ "operations": [ { "op": "text-to-trees" } ] }"""u8).Batch!, ChangeSource.Cli, Ct);
        Assert.Equal("subject.nothing = ((", store.Current!.Get<Right>(Named)!.Condition!.Text);
        Assert.DoesNotContain(converted.Items.SelectMany(i => i.Diagnostics), d => d.Rule == "MQ3101");
    }

    [Fact]
    public async Task A_named_rights_condition_is_parsed_in_the_scope_its_uses_share_and_typed_at_each_use()
    {
        var (h, store) = await OpenAsync(h =>
        {
            h.Write(RightFile, NamedRight());
            Rights(h, $$"""[{"id":"{{R1}}","right":"{{Named}}","states":["{{Draft}}"]}]""");
            Rights(h, $$"""[{"id":"01JRGT00000000000000000931","right":"{{Named}}"}]""", file: CreditFile);
        });
        using var _ = h;
        await using var __ = store;

        // Order and Customer both carry classified, so its attribute is in the scope both uses share; submittedBy is only Order's.
        async Task<SaveResult> SaveCondition(string text)
        {
            var current = (await store.GetElementAsync(Named, Ct))!;
            var node = JsonNode.Parse(current.Json.GetRawText())!.AsObject();
            node["condition"] = new JsonObject { ["text"] = text };
            return await store.SaveAsync(Named, Encoding.UTF8.GetBytes(node.ToJsonString()), current.Hash, ChangeSource.Editor, Ct);
        }

        var shared = await SaveCondition("subject.classification = 'secret'");
        Assert.True(shared.Outcome == SaveOutcome.Saved, Describe(shared.Diagnostics));
        Assert.Equal([Classification], store.Current!.Get<Right>(Named)!.Condition!.Args![0].Path);
        Assert.DoesNotContain(await FindingsAsync(store), d => d.Rule.StartsWith("MQ31", StringComparison.Ordinal));

        // Text that does not parse in that scope refuses the write, with the parser's finding.
        var onlyOrders = await SaveCondition("subject.submittedBy = 'x'");
        Assert.Equal(SaveOutcome.Invalid, onlyOrders.Outcome);
        Assert.Contains(onlyOrders.Diagnostics, d => d.Rule == "MQ3102" && d.Message.Contains("subject.submittedBy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_use_whose_subjects_lack_what_the_condition_reads_is_MQ3102_at_the_use()
    {
        var found = await ValidateAsync(h =>
        {
            h.Write(RightFile, NamedRight($$""","condition":{"op":"eq","args":[{"path":["{{SubmittedBy}}"]},{"currentActor":true}]}"""));
            Rights(h, $$"""[{"id":"{{R1}}","right":"{{Named}}"}]""");
            Rights(h, $$"""[{"id":"01JRGT00000000000000000931","right":"{{Named}}"}]""", file: CreditFile);
        });

        Assert.Equal(["credit.json:/rights/0/right"], Hits(found, "MQ3102"));
        Assert.StartsWith("Named right 'SecretOnlyOwner': ", found.Single(d => d.Rule == "MQ3102").Message, StringComparison.Ordinal);
        Assert.DoesNotContain(found, d => (d.FilePath ?? "").EndsWith(RightFile, StringComparison.Ordinal) && d.Rule.StartsWith("MQ3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Text_to_trees_converts_a_named_rights_text_once_a_lifecycle_uses_it()
    {
        var (h, store) = await OpenAsync(h =>
        {
            h.Write(RightFile, NamedRight(""","condition":{"text":"subject.classification = 'secret'"}"""));
            Rights(h, $$"""[{"id":"{{R1}}","right":"{{Named}}"}]""");
        });
        using var _ = h;
        await using var __ = store;
        Assert.Contains("convert it with text-to-trees", (await FindingsAsync(store)).Single(d => d.Rule == "MQ3108").Message, StringComparison.Ordinal);

        var converted = await store.ApplyBatchAsync(store.ParseBatch("""{ "operations": [ { "op": "text-to-trees" } ] }"""u8).Batch!, ChangeSource.Cli, Ct);

        Assert.True(converted.Outcome == SaveOutcome.Saved, Describe(converted.Items.SelectMany(i => i.Diagnostics)));
        Assert.Equal([Classification], store.Current!.Get<Right>(Named)!.Condition!.Args![0].Path);
        Assert.DoesNotContain(await FindingsAsync(store), d => d.Rule == "MQ3108");
    }

    // ---- preview ----

    [Fact]
    public async Task Preview_reads_a_rights_condition_in_its_lifecycle_and_a_named_right_in_its_uses_or_a_lifecycle_given()
    {
        var (h, store) = await OpenAsync(h => h.Write(RightFile, NamedRight($$""","condition":{"op":"eq","args":[{"path":["{{Classification}}"]},{"value":"secret"}]}""")));
        using var _ = h;
        await using var __ = store;
        Task<ExpressionPreview> Preview(ExpressionPreviewRequest request) => store.PreviewExpressionAsync(request, Ct);

        // At a lifecycle: text for an inline right reads the subject, the actor and states.
        var inline = await Preview(new ExpressionPreviewRequest { Element = P, Text = "subject.submittedBy = current_actor() and in_state(subject, OrderLifecycle.Draft)", Site = "right" });
        Assert.True(inline.Preview is not null, Describe(inline.Diagnostics));
        Assert.Equal(("bool", (string?)null), (inline.Preview!.Type, inline.Preview.Sql));

        // A named right no lifecycle uses has no scope until one is given.
        await Assert.ThrowsAsync<ArgumentException>(() => Preview(new ExpressionPreviewRequest { Element = Named }));
        var asIfUsed = await Preview(new ExpressionPreviewRequest { Element = Named, Lifecycle = "Credit" });
        Assert.Equal("subject.classification = 'secret'", asIfUsed.Preview!.Text);
        var text = await Preview(new ExpressionPreviewRequest { Element = Named, Text = "subject.submittedBy = 'x'", Site = "right", Lifecycle = P });
        Assert.True(text.Preview is not null, Describe(text.Diagnostics));
        var notThere = await Preview(new ExpressionPreviewRequest { Element = Named, Text = "subject.submittedBy = 'x'", Site = "right", Lifecycle = Credit });
        Assert.Null(notThere.Preview);
        await Assert.ThrowsAsync<ArgumentException>(() => Preview(new ExpressionPreviewRequest { Element = P, Text = "true", Site = "right", Lifecycle = P }));
    }

    [Fact]
    public async Task Preview_without_a_lifecycle_reads_a_named_right_in_the_scope_all_its_uses_share()
    {
        var (h, store) = await OpenAsync(h =>
        {
            h.Write(RightFile, NamedRight($$""","condition":{"op":"eq","args":[{"path":["{{Classification}}"]},{"value":"secret"}]}"""));
            Rights(h, $$"""[{"id":"{{R1}}","right":"{{Named}}"}]""");
            Rights(h, $$"""[{"id":"01JRGT00000000000000000931","right":"{{Named}}"}]""", file: CreditFile);
        });
        using var _ = h;
        await using var __ = store;

        var stored = await store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = Named }, Ct);
        var shared = await store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = Named, Text = "subject.classification <> 'open'", Site = "right" }, Ct);
        var onlyOrders = await store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = Named, Text = "subject.submittedBy = 'x'", Site = "right" }, Ct);
        // The use in Credit types the named right's condition against Customer.
        var atUse = await store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = "01JRGT00000000000000000931" }, Ct);

        Assert.Equal("subject.classification = 'secret'", stored.Preview!.Text);
        Assert.NotNull(shared.Preview);
        Assert.Null(onlyOrders.Preview);
        Assert.Equal("subject.classification = 'secret'", atUse.Preview!.Text);
    }

    // ---- scoped validation ----

    [Fact]
    public async Task A_change_to_a_named_right_its_actors_or_the_subjects_rechecks_the_lifecycles_that_use_it()
    {
        var (h, store) = await OpenAsync(h =>
        {
            h.Write(RightFile, NamedRight($$""","except":["{{Submitter}}"],"condition":{"op":"isNotNull","args":[{"path":["{{Classification}}"]}]}"""));
            Rights(h, $$"""[{"id":"{{R1}}","right":"{{Named}}"}]""", file: CreditFile);
        });
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        bool Rechecks(Element changed) => Maquettiste.Engine.Validation.ModelValidator.ProcessPeers(model, changed).Any(d => d.Element.Id == Credit);

        Assert.True(Rechecks(model.Get<Right>(Named)!));
        Assert.True(Rechecks(model.Get<Actor>(Submitter)!));
        Assert.True(Rechecks(model.Get<Entity>(Customer)!));
        Assert.Contains(Maquettiste.Engine.Validation.ModelValidator.ProcessPeers(model, model.Get<Right>(Named)!), d => d.Element.Id == Clerk);
        Assert.DoesNotContain(Maquettiste.Engine.Validation.ModelValidator.ProcessPeers(model, model.Get<Right>(Named)!), d => d.Element.Id == P);
    }

    // ---- MQ9305: verb steps ----

    [Fact]
    public async Task MQ9305_judges_a_verb_steps_fields_and_a_refusal_beside_accepted_true()
    {
        string Scenario(string step) => $$$"""
            {"kind":"scenario","id":"01JSCN00000000000000000901","name":"Verbs","process":"{{{P}}}",
             "objects":[{"id":"01JXBJ00000000000000000901","name":"order","entity":"{{{Order}}}","links":[{"end":"{{{CustomerEnd}}}","object":"01JXBJ00000000000000000902"}]},
                        {"id":"01JXBJ00000000000000000902","name":"customer","entity":"{{{Customer}}}"},
                        {"id":"01JXBJ00000000000000000903","name":"other","entity":"{{{Customer}}}"}],
             "subject":"01JXBJ00000000000000000901",
             "steps":[{"id":"01JSTP00000000000000000901",{{{step}}}}]}
            """;
        async Task<string?> Field(string step)
        {
            var found = await ValidateAsync(h => h.Write("model/scenarios/order-lifecycle/verbs.json", Scenario(step)));
            return found.Where(d => d.Rule == "MQ9305").Select(d => d.JsonPointer).FirstOrDefault(p => p is not null && p.StartsWith("/steps/0/", StringComparison.Ordinal));
        }

        Assert.Equal("/steps/0/verb", await Field("\"input\":\"verb\",\"actor\":\"" + Clerk + "\""));
        Assert.Equal("/steps/0/verb", await Field("\"input\":\"verb\",\"verb\":\"promote\""));
        Assert.Equal("/steps/0/verb", await Field("\"input\":\"verb\",\"verb\":\"freeze\""));
        Assert.Equal("/steps/0/verb", await Field("\"input\":\"verb\",\"verb\":\"escalate\""));
        Assert.Equal("/steps/0/values", await Field($$"""  "input":"verb","verb":"read","values":{"{{SubmittedBy}}":"ann"} """));
        Assert.Equal("/steps/0/values", await Field($$"""  "input":"verb","verb":"modify","values":{"01JATT00000000000000000002":"Draft"} """));
        Assert.Equal("/steps/0/values", await Field($$"""  "input":"verb","verb":"modify","values":{"{{Amount}}":"lots"} """));
        Assert.Equal("/steps/0/values", await Field($$"""  "input":"verb","verb":"modify","end":"{{CustomerEnd}}","object":"01JXBJ00000000000000000902","values":{"{{SubmittedBy}}":"ann"} """));
        Assert.Equal("/steps/0/end", await Field($$"""  "input":"verb","verb":"delete","end":"{{CustomerEnd}}","object":"01JXBJ00000000000000000902" """));
        Assert.Equal("/steps/0/end", await Field($$"""  "input":"verb","verb":"disconnect","end":"{{OrdersEnd}}","object":"01JXBJ00000000000000000902" """));
        Assert.Equal("/steps/0/object", await Field($$"""  "input":"verb","verb":"disconnect","end":"{{CustomerEnd}}","object":"01JXBJ00000000000000000903" """));
        Assert.Equal("/steps/0/expect/refusal", await Field("\"event\":\"01JPRX00000000000000000001\",\"expect\":{\"refusal\":\"right\"}"));

        // Well-formed steps pass the field checks.
        Assert.Null(await Field($$"""  "input":"verb","verb":"modify","values":{"{{SubmittedBy}}":"ann","{{Amount}}":12.5} """));
        Assert.Null(await Field($$"""  "input":"verb","verb":"connect","end":"{{CustomerEnd}}","object":"01JXBJ00000000000000000903" """));
        Assert.Null(await Field($$"""  "input":"verb","verb":"disconnect","end":"{{CustomerEnd}}","object":"01JXBJ00000000000000000902" """));
        Assert.Null(await Field("\"event\":\"01JPRX00000000000000000003\",\"expect\":{\"accepted\":false,\"refusal\":\"no-transition\"}"));
    }
}
