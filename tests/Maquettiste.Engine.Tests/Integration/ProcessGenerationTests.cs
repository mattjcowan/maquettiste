using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Generation over the gate 3 fixture with the real renderer: the <c>each process</c>, <c>each actor</c> and <c>each scenario</c>
/// scopes, and incremental runs driven by the keys templates record (phase-3-design.md section 4.3: a scenario's unit re-renders when
/// its process changes; editing one scenario re-renders only its unit).
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ProcessGenerationTests
{
    private const string Pack = "processes";
    private const string Sales = "01JQPRC0000000000000000001";
    private const string Purchase = "01JQPRC0000000000000000002";
    private const string HoldAndRelease = "01JQSCN0000000000000000004";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<string> Rendered(GenerationPlan plan) =>
        [.. plan.Units.Where(u => !u.Skipped).Select(u => u.Key).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Processes_actors_and_scenarios_render_through_their_scopes()
    {
        await using var repo = new ProcessRepo();
        var result = await repo.ApplyAsync();

        Assert.Equal(2 + 8 + 14, result.UnitsRendered);
        var sales = repo.Repo.ReadFile("gen/processes/sales-order-lifecycle.txt");
        Assert.StartsWith("SalesOrderLifecycle (lifecycle) of SalesOrder\nstate Draft atomic = Draft\n", sales, StringComparison.Ordinal);
        Assert.Contains("state Fulfilment.Resume history\n", sales, StringComparison.Ordinal);
        Assert.Contains("Fulfilment.Processing.Payment.AwaitingPayment -> Fulfilment.Processing.Payment.PaymentOverdue: after 30d (2592000000 ms)", sales, StringComparison.Ordinal);
        Assert.Contains("CreditCheck -> CreditReview: always [exceedsCreditLimit]", sales, StringComparison.Ordinal);
        Assert.Contains("gate creditApproval 2 of 2: instance:string process:id gate:id transition:id sequence:int64 signer:string actor:id meaning:id reason:text at:datetimeoffset outcome:string",
            sales, StringComparison.Ordinal);
        Assert.Contains("Approval -> (internal): after 5d / sendReminder (432000000 ms)", repo.Repo.ReadFile("gen/processes/purchase-approval.txt"), StringComparison.Ordinal);
        Assert.Equal("CreditManager (role)\nraises approveCredit\nraises rejectCredit\nsigns creditApproval", repo.Repo.ReadFile("gen/actors/credit-manager.txt").TrimEnd('\n'));
        Assert.Contains("2 event approveCredit by FinanceDirector -> CreditReview (refused)",
            repo.Repo.ReadFile("gen/scenarios/sales-order-lifecycle/repeat-signer-refused.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editing_a_scenario_re_renders_only_its_unit_and_editing_a_process_its_scenarios_too()
    {
        await using var repo = new ProcessRepo();
        await repo.ApplyAsync();
        Assert.Empty(Rendered(await repo.PlanAsync()));

        await repo.EditAsync(HoldAndRelease, n => n["displayName"] = "Hold, then release");
        Assert.Equal([Pack + "/scenario:" + HoldAndRelease], Rendered(await repo.PlanAsync()));
        await repo.ApplyAsync();

        // The process's own unit, each of its eight scenarios, and the actors whose events it declares (their referrers changed).
        await repo.EditAsync(Sales, n => n["description"] = "From draft to completion.");
        var rendered = Rendered(await repo.PlanAsync());
        var model = await repo.ResolveAsync();
        var expected = new List<string> { Pack + "/process:" + Sales };
        expected.AddRange(model.Scenarios.Where(s => s.Process.Id == Sales).Select(s => Pack + "/scenario:" + s.Id));
        expected.AddRange(model.Actors.Where(a => a.Name is "CreditManager" or "FinanceDirector").Select(a => Pack + "/actor:" + a.Id));
        Assert.Equal(expected.Order(StringComparer.Ordinal), rendered);
        Assert.DoesNotContain(Pack + "/process:" + Purchase, rendered);
        await repo.ApplyAsync();
        await repo.AssertIncrementalEqualsForcedAsync();
    }

    [Fact]
    public async Task Two_cold_runs_are_byte_identical()
    {
        await using var a = new ProcessRepo();
        await using var b = new ProcessRepo();
        await a.ApplyAsync();
        await b.ApplyAsync();
        FullRunTests.AssertSameBytes(a.Outputs(), b.Outputs());
    }

    /// <summary>The gate 3 fixture in a temporary repo with the <c>processes</c> fixture pack writing under <c>gen/</c>.</summary>
    private sealed class ProcessRepo : IAsyncDisposable
    {
        public ProcessRepo()
        {
            Repo = new TempRepo();
            E2ERepo.CopyTree(Fixtures.Path("models", "processes"), Repo.RepoRoot);
            E2ERepo.CopyTree(Fixtures.Path("integration", "packs", Pack), Path.Combine(Repo.ModelRoot, "templates", Pack));
            var settingsPath = Path.Combine(Repo.ModelRoot, "maquettiste.json");
            var node = JsonNode.Parse(File.ReadAllBytes(settingsPath))!.AsObject();
            node["outputs"] = new JsonObject { ["allow"] = new JsonArray(new JsonObject { ["path"] = "gen" }) };
            node["packs"] = new JsonObject { [Pack] = new JsonObject { ["output"] = "gen" } };
            File.WriteAllBytes(settingsPath, TestServices.Json.Write(node, "maquettiste.json", "maquettiste.json"));
            Store = new ModelStore(Repo.Options);
            Service = new GenerationService(Store, Repo.Options);
        }

        public TempRepo Repo { get; }

        private ModelStore Store { get; }

        private GenerationService Service { get; }

        public async Task<Engine.Resolution.ResolvedModel> ResolveAsync() => Tests.Resolution.ResolutionKit.Resolve(await Store.GetSnapshotAsync(Ct));

        public async Task<GenerationResult> ApplyAsync(bool force = false)
        {
            var result = await Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Jobs = 2, Force = force }, null, Ct);
            E2ERepo.AssertOutcome(RunOutcome.Succeeded, result);
            return result;
        }

        public async Task<GenerationPlan> PlanAsync()
        {
            var result = await Service.PlanAsync(new GenerationRequest { Jobs = 2 }, null, Ct);
            Assert.Equal(RunOutcome.Succeeded, result.Outcome);
            return result.Plan!;
        }

        public async Task EditAsync(string id, Action<JsonObject> edit)
        {
            var document = await Store.GetElementAsync(id, Ct);
            Assert.NotNull(document);
            var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
            edit(node);
            var result = await Store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
            Assert.True(result.Outcome == SaveOutcome.Saved, result.Outcome + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        }

        public SortedDictionary<string, byte[]> Outputs()
        {
            var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var path in Repo.ListFiles().Where(p => p.StartsWith("gen/", StringComparison.Ordinal)))
                files[path] = File.ReadAllBytes(Repo.PathOf(path));
            return files;
        }

        public async Task AssertIncrementalEqualsForcedAsync()
        {
            var tree = Outputs();
            var forced = await ApplyAsync(force: true);
            Assert.All(forced.Changes, c => Assert.True(c.Kind == FileChangeKind.Kept, "forced run changed " + c.Kind + " " + c.Path));
            FullRunTests.AssertSameBytes(tree, Outputs());
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            Repo.Dispose();
        }
    }
}
