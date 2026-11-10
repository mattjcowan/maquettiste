using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Revisions;

/// <summary>
/// The shared restart case (<c>tests/fixtures/revisions/restart-case.json</c>, revisions-design.md section 17), which the editor's mock reads
/// too: the engine gives exactly the expected traces, so the two copies can't drift.
/// </summary>
public sealed class RevisionRestartCaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What both copies agree on in one trace: acceptance, states, the object's values, the revision, and each microstep.</summary>
    internal static JsonObject Project(StepTrace trace)
    {
        var node = new JsonObject
        {
            ["index"] = trace.Index,
            ["accepted"] = trace.Accepted,
            ["configuration"] = new JsonArray([.. trace.Configuration.Select(s => (JsonNode?)JsonValue.Create(s))]),
            ["microsteps"] = new JsonArray([.. trace.Microsteps.Select(m => (JsonNode?)new JsonObject
            {
                ["transitions"] = new JsonArray([.. m.Transitions.Select(s => (JsonNode?)JsonValue.Create(s))]),
                ["exited"] = new JsonArray([.. m.Exited.Select(s => (JsonNode?)JsonValue.Create(s))]),
                ["entered"] = new JsonArray([.. m.Entered.Select(s => (JsonNode?)JsonValue.Create(s))]),
                ["actions"] = new JsonArray([.. m.Actions.Select(a => (JsonNode?)JsonValue.Create(a.Action))]),
            })]),
            ["final"] = trace.Final,
        };
        if (trace.Refusal is { } refusal)
            node["refusal"] = refusal;
        if (trace.ValuesChanged is { Count: > 0 } values)
            node["values"] = JsonSerializer.SerializeToNode(values.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value));
        if (trace.Revision is { } revision)
            node["revision"] = new JsonObject { ["from"] = revision.From, ["to"] = revision.To, ["level"] = revision.Level };
        return node;
    }

    [Fact]
    public async Task The_engine_gives_exactly_the_shared_case_traces()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Fixtures.Path("revisions", "restart-case.json")))!.AsObject();
        using var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        h.Write("model/entities/drawing.json", fixture["entity"]!.ToJsonString());
        h.Write("model/processes/drawing-life.json", fixture["process"]!.ToJsonString());
        await using var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var report = await store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(report.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Rule != "MQ1008");

        var scenario = fixture["scenario"]!;
        var call = await store.SimulateProcessAsync(fixture["process"]!["id"]!.GetValue<string>(), new SimulateRequest
        {
            Steps = [.. scenario["steps"]!.AsArray().Select(s => JsonSerializer.SerializeToElement(s))],
            Objects = [.. scenario["objects"]!.AsArray().Select(o => JsonSerializer.SerializeToElement(o))],
            Subject = scenario["subject"]!.GetValue<string>(),
        }, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));

        var actual = new JsonArray([.. call.Value!.Trace.Select(t => (JsonNode?)Project(t))]);
        Assert.Equal(fixture["expected"]!.ToJsonString(), actual.ToJsonString());
    }
}
