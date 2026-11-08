using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// Trigger points' expressions and rules (trigger-points-design.md decisions 12 to 15 and section 6): the two sites and <c>old.</c>, which only
/// they read; a session that evaluates a when, a condition and a set with the values before the verb; MQ8101 to MQ8107 and the new cases of
/// MQ3001, MQ3102, MQ3104, MQ9701 and MQ9803; and no new finding for a model without trigger points.
/// </summary>
public sealed class TriggerRuleTests
{
    private const string P = "01JPRC00000000000000000001"; // OrderLifecycle, bound to Order.status
    private const string Other = "01JPRC00000000000000000007"; // a second lifecycle of Order
    private const string Flow = "01JPRC00000000000000000008"; // an orchestration
    private const string Order = "01JENT00000000000000000001";
    private const string Package = "01JPKG00000000000000000001";
    private const string OrderId = "01JATT00000000000000000001";
    private const string Status = "01JATT00000000000000000002";
    private const string Amount = "01JATT00000000000000000936";
    private const string Total = "01JATT00000000000000000937"; // derived
    private const string Draft = "01JSTA00000000000000000001";
    private const string Submit = "01JTRN00000000000000000001";
    private const string ProcessFile = "model/processes/order-lifecycle.json";
    private const string OtherFile = "model/processes/order-archiving.json";
    private const string FlowFile = "model/processes/flow.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Tp(int n) => "01JTGP000000000000000000" + n.ToString("00", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(string points, string? otherPoints = null, string? flowPoints = null,
        Action<JsonObject>? more = null)
    {
        var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        h.Write("model/entities/order.json", $$"""
            {"kind":"entity","id":"{{Order}}","name":"Order","package":"{{Package}}","lifecycle":"{{P}}",
             "key":{"attributes":["{{OrderId}}"],"strategy":"uuid-v7"},
             "attributes":[{"id":"{{OrderId}}","name":"id","type":"uuid","required":true},
               {"id":"{{Status}}","name":"status","type":{"ref":"01JENM00000000000000000001"},"required":true,"default":"Draft"},
               {"id":"{{Amount}}","name":"amount","type":"decimal","precision":12,"scale":2},
               {"id":"{{Total}}","name":"total","type":"decimal","precision":12,"scale":2,"derived":{"value":{"path":["{{Amount}}"]} } }]}
            """);
        var process = JsonNode.Parse(h.Read(ProcessFile))!.AsObject();
        if (points.Length > 0)
            process["triggerPoints"] = JsonNode.Parse(points);
        more?.Invoke(process);
        h.Write(ProcessFile, process.ToJsonString());
        if (otherPoints is not null)
            h.Write(OtherFile, $$"""
                {"kind":"process","id":"{{Other}}","name":"OrderArchiving","package":"{{Package}}","use":"lifecycle",
                 "subjects":[{"id":"01JPRX00000000000000000701","entity":"{{Order}}"}],
                 "states":[{"id":"01JSTA00000000000000000701","name":"Kept"}],"triggerPoints":{{otherPoints}}}
                """);
        if (flowPoints is not null)
            h.Write(FlowFile, $$"""
                {"kind":"process","id":"{{Flow}}","name":"Flow","package":"{{Package}}","use":"orchestration",
                 "states":[{"id":"01JSTA00000000000000000801","name":"Start"}],"triggerPoints":{{flowPoints}}}
                """);
        var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        return (h, store);
    }

    private static async Task<List<Diagnostic>> ValidateAsync(string points, string? otherPoints = null, string? flowPoints = null)
    {
        var (h, store) = await OpenAsync(points, otherPoints, flowPoints);
        using var _ = h;
        await using var __ = store;
        return [.. (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics];
    }

    private static List<string> Hits(IEnumerable<Diagnostic> diagnostics, string rule) =>
        [.. diagnostics.Where(d => d.Rule == rule).Select(d => Path.GetFileName(d.FilePath) + ":" + d.JsonPointer).Order(StringComparer.Ordinal)];

    private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(d => d.Rule + " " + Path.GetFileName(d.FilePath) + d.JsonPointer + " " + d.Message));

    private static string Text(string text) => JsonSerializer.Serialize(new { text });

    // ---- no change ----

    [Fact]
    public async Task A_model_without_trigger_points_has_no_trigger_point_findings()
    {
        var before = await ValidateAsync("");
        Assert.DoesNotContain(before, d => d.Rule.StartsWith("MQ81", StringComparison.Ordinal));
        var empty = await ValidateAsync("[]");
        Assert.Equal(Describe(before), Describe(empty));
    }

    // ---- expressions ----

    [Fact]
    public async Task Old_reads_the_values_before_the_verb_only_at_a_trigger_points_sites()
    {
        var (h, store) = await OpenAsync($$"""
            [{"id":"{{Tp(1)}}","name":"PriceDrop","type":"check","severity":"warning","verbs":["modify"],"attributes":["{{Amount}}"],
              "condition":{{Text("coalesce(old.amount, 0) = 0 or subject.amount >= old.amount * 0.9")}} },
             {"id":"{{Tp(2)}}","name":"KeepAmount","type":"action","timing":"before","verbs":["modify"],"when":{{Text("old.amount is not null")}},
              "sets":[{"id":"01JTGS00000000000000000001","attribute":"{{Amount}}","value":{{Text("coalesce(subject.amount, old.amount)")}} }]}]
            """);
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var process = Assert.IsType<Process>(model.GetDocument(P)!.Element);
        var found = (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics;
        Assert.DoesNotContain(found, d => d.Severity == DiagnosticSeverity.Error && !RuleCatalog.IsReplayFinding(d.Rule));

        // Text with old. types at the trigger sites (it stays text in a file, MQ3108), and nothing else is reported there.
        Assert.Equal(["order-lifecycle.json:/triggerPoints/0/condition", "order-lifecycle.json:/triggerPoints/1/sets/0/value", "order-lifecycle.json:/triggerPoints/1/when"],
            Hits(found, "MQ3108"));
        Assert.DoesNotContain(found, d => d.Rule is "MQ3101" or "MQ3102" or "MQ3103" or "MQ3104");
    }

    [Fact]
    public async Task Old_is_refused_elsewhere_and_the_context_is_never_read_at_a_trigger_site()
    {
        var (h, store) = await OpenAsync($$"""
            [{"id":"{{Tp(1)}}","name":"ReadsContext","type":"check","verbs":["modify"],"condition":{"path":["01JATT00000000000000000012"]} }]
            """, more: process =>
        {
            process["guards"]![0]!["condition"] = JsonNode.Parse($$"""{"op":"isNull","args":[{"path":["{{Amount}}"],"old":true}]}""");
            process["guards"]![0]!.AsObject().Remove("expression");
        });
        using var _ = h;
        await using var __ = store;
        var found = (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics;
        Assert.Equal(["order-lifecycle.json:/guards/0/condition/args/0"], Hits(found, "MQ3104"));
        Assert.Equal(["order-lifecycle.json:/triggerPoints/0/condition"], Hits(found, "MQ3102"));
    }

    [Fact]
    public async Task A_payload_is_read_only_by_a_point_whose_verbs_make_moves()
    {
        var found = await ValidateAsync($$"""
            [{"id":"{{Tp(1)}}","name":"ReadsPayload","type":"check","verbs":["modify"],"condition":{"path":["01JATT00000000000000000021"]} }]
            """);
        Assert.Equal(["order-lifecycle.json:/triggerPoints/0/condition"], Hits(found, "MQ3102"));
    }

    [Fact]
    public async Task A_payload_some_runs_do_not_carry_is_worded_for_the_trigger_point()
    {
        // submit and approve both name promote; only submit carries the payload attribute.
        var (h, store) = await OpenAsync($$"""
            [{"id":"{{Tp(1)}}","name":"ReadsPayload","type":"check","verbs":["promote"],
              "condition":{"op":"isNotNull","args":[{"path":["01JATT00000000000000000021"]}]} },
             {"id":"{{Tp(2)}}","name":"SetsFromPayload","type":"action","timing":"before","verbs":["promote"],
              "sets":[{"id":"01JTGS00000000000000000021","attribute":"{{Amount}}","value":{"case":[{"when":{"op":"isNotNull","args":[{"path":["01JATT00000000000000000021"]} ]},"then":{"value":1} } ],"else":{"value":0} } }]}]
            """, more: process =>
        {
            process["events"]![0]!["verb"] = "promote";
            process["events"]![1]!["verb"] = "promote";
        });
        using var _ = h;
        await using var __ = store;
        var found = (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics;
        var texts = found.Where(d => d.Rule == "MQ3102").Select(d => d.JsonPointer + " " + d.Message).ToList();
        var all = Describe(found.Where(d => (d.FilePath ?? "").EndsWith("order-lifecycle.json", StringComparison.Ordinal)));
        Assert.False(texts.Count == 0 || texts.Any(t => t.Contains("guard", StringComparison.Ordinal)), all);
        Assert.True(texts.Any(t => t.Contains(" The trigger point reads the payload attribute", StringComparison.Ordinal)), all);
        Assert.True(texts.Any(t => t.Contains(" The set's value reads the payload attribute", StringComparison.Ordinal) && t.Contains("its trigger point", StringComparison.Ordinal)), all);
    }

    [Fact]
    public async Task A_replace_clash_across_lifecycles_shows_when_either_lifecycle_alone_is_validated()
    {
        var (h, store) = await OpenAsync($$"""[{"id":"{{Tp(1)}}","name":"ArchiveOne","type":"replace","verbs":["delete"]}]""",
            otherPoints: $$"""[{"id":"{{Tp(21)}}","name":"Elsewhere","type":"replace","verbs":["delete"]}]""");
        using var _ = h;
        await using var __ = store;
        foreach (var id in new[] { P, Other })
        {
            var found = (await store.ValidateAsync(new ValidationScope([id], IncludeReferrers: false), Ct)).Diagnostics;
            Assert.Equal(["order-lifecycle.json:/triggerPoints/0"], Hits(found, "MQ8104"));
        }
    }

    // ---- rules ----

    [Fact]
    public async Task MQ8101_to_MQ8107_report_what_does_not_fit()
    {
        var found = await ValidateAsync($$"""
            [{"id":"{{Tp(1)}}","name":"NoTiming","type":"action","verbs":["modify"]},
             {"id":"{{Tp(2)}}","name":"WrongKeys","type":"replace","timing":"before","severity":"warning","verbs":["delete"],"condition":{"value":true} },
             {"id":"{{Tp(3)}}","name":"CheckSets","type":"check","verbs":["modify"],"sets":[{"id":"01JTGS00000000000000000003","attribute":"{{Amount}}","value":{"null":true} }]},
             {"id":"{{Tp(4)}}","name":"OutsideSets","type":"action","timing":"before","verbs":["create"],"outside":true,
              "sets":[{"id":"01JTGS00000000000000000004","attribute":"{{Amount}}","value":{"null":true} }]},
             {"id":"{{Tp(5)}}","name":"StampOnDelete","type":"action","timing":"with-change","verbs":["delete"],
              "sets":[{"id":"01JTGS00000000000000000005","attribute":"{{Amount}}","value":{"null":true} }]},
             {"id":"{{Tp(6)}}","name":"Places","type":"check","verbs":["modify"],"states":["{{Submit}}"],"targets":["{{Draft}}"],"transitions":["{{Draft}}"],
              "attributes":["01JATT00000000000000000999"],"ends":["01JEND00000000000000000001"],"entities":["01JENT00000000000000000999"]},
             {"id":"{{Tp(7)}}","name":"ReplaceModify","type":"replace","verbs":["modify"]},
             {"id":"{{Tp(8)}}","name":"Late","type":"action","timing":"after-commit","verbs":["modify"],"sets":[{"id":"01JTGS00000000000000000008","attribute":"{{Amount}}","value":{"null":true} }]},
             {"id":"{{Tp(9)}}","name":"Remote","type":"action","timing":"with-change","verbs":["modify"],"outside":true},
             {"id":"{{Tp(10)}}","name":"Never","type":"check","verbs":["promote","override"]},
             {"id":"{{Tp(11)}}","name":"ArchiveOne","type":"replace","verbs":["delete"]},
             {"id":"{{Tp(12)}}","name":"ArchiveTwo","type":"replace","verbs":["delete"],"sets":[{"id":"01JTGS00000000000000000012","attribute":"{{Total}}","value":{"null":true} },
               {"id":"01JTGS00000000000000000013","attribute":"{{Total}}","value":{"null":true} }]},
             {"id":"{{Tp(13)}}","name":"isLarge","type":"check","verbs":["nonsense"]},
             {"id":"{{Tp(14)}}","name":"NoTiming","type":"check","verbs":["read"]}]
            """, otherPoints: $$"""[{"id":"{{Tp(21)}}","name":"Elsewhere","type":"replace","verbs":["delete"]}]""",
            flowPoints: $$"""[{"id":"{{Tp(31)}}","name":"OnFlow","type":"check","verbs":["modify"]}]""");

        Assert.Equal(
            ["flow.json:/triggerPoints/0", "order-lifecycle.json:/triggerPoints/0", "order-lifecycle.json:/triggerPoints/1/condition",
             "order-lifecycle.json:/triggerPoints/1/severity", "order-lifecycle.json:/triggerPoints/1/timing", "order-lifecycle.json:/triggerPoints/2/sets",
             "order-lifecycle.json:/triggerPoints/3/outside"],
            Hits(found, "MQ8101"));
        Assert.Equal(
            ["order-lifecycle.json:/triggerPoints/5/attributes", "order-lifecycle.json:/triggerPoints/5/attributes/0",
             "order-lifecycle.json:/triggerPoints/5/ends/0", "order-lifecycle.json:/triggerPoints/5/entities/0", "order-lifecycle.json:/triggerPoints/5/states/0",
             "order-lifecycle.json:/triggerPoints/5/targets", "order-lifecycle.json:/triggerPoints/5/transitions", "order-lifecycle.json:/triggerPoints/5/transitions/0"],
            Hits(found, "MQ8102"));
        Assert.Equal(["order-lifecycle.json:/triggerPoints/6/verbs/0"], Hits(found, "MQ8103"));
        // A clash across lifecycles is reported once, on the lifecycle that sorts later (OrderLifecycle after OrderArchiving).
        Assert.Equal(["order-lifecycle.json:/triggerPoints/1", "order-lifecycle.json:/triggerPoints/10", "order-lifecycle.json:/triggerPoints/11"],
            Hits(found, "MQ8104"));
        Assert.Equal(["order-lifecycle.json:/triggerPoints/7/sets"], Hits(found, "MQ8105"));
        Assert.Equal(["order-lifecycle.json:/triggerPoints/8/timing"], Hits(found, "MQ8106"));
        Assert.Equal(["order-lifecycle.json:/triggerPoints/9/verbs"], Hits(found, "MQ8107"));
        Assert.Equal(["order-lifecycle.json:/triggerPoints/12/name", "order-lifecycle.json:/triggerPoints/13/name"], Hits(found, "MQ3001"));
        Assert.Equal(["order-lifecycle.json:/triggerPoints/12/verbs/0"], Hits(found, "MQ9701"));
        Assert.Equal(["order-lifecycle.json:/triggerPoints/11/sets/1/attribute"], Hits(found, "MQ9803"));
        Assert.Equal(["order-lifecycle.json:/triggerPoints/11/sets/0/attribute"], Hits(found, "MQ9802"));
        // A with-change set on delete is fine where a replace on delete covers the entity (here, three do).
        Assert.DoesNotContain("order-lifecycle.json:/triggerPoints/4/sets", Hits(found, "MQ8101"));
    }
}
