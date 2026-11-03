using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Tests.Rendering;

namespace Maquettiste.Engine.Tests.Generation;

/// <summary>Incremental runs, plans and applies of packs that read each other's exports (next-phase-2-design.md section 5.3).</summary>
public sealed class ExportRunTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<GenerationPlan> PlanAsync(GenerationFixture f, IReadOnlyList<string>? packs = null, IReadOnlyList<string>? units = null,
        IReadOnlyList<string>? elements = null) =>
        f.Service.PlanAsync(new GenerationRequest { Mode = GenerationMode.DryRun, Packs = packs, Units = units, Elements = elements }, null, Ct)
            .ContinueWith(t => t.Result.Plan!, Ct, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    [Fact]
    public async Task A_consumer_is_skipped_when_only_the_exporting_template_body_changes()
    {
        await using var f = await ExportPacks.CreateAsync();
        Assert.Equal(4, (await f.RunAsync()).UnitsRendered);
        f.WritePackFile("records", "record.scriban", "public class {{ exports.class_name }}");

        var second = await f.RunAsync();

        Assert.True(second.Outcome == RunOutcome.Succeeded, ExportPacks.Problems(second));
        Assert.Equal((2, 2), (second.UnitsRendered, second.UnitsSkipped));
        Assert.Equal("public class CustomerRecord", f.Outputs()["out/records/CustomerRecord.cs"]);
    }

    [Fact]
    public async Task A_consumer_renders_when_an_export_value_changes()
    {
        await using var f = await ExportPacks.CreateAsync();
        await f.RunAsync();
        LibraryPacks.Pack(f, "records", ExportPacks.RecordUnit.Replace("}}Record\"", "}}Row\"", StringComparison.Ordinal));

        var second = await f.RunAsync();

        Assert.Equal(4, second.UnitsRendered);
        Assert.StartsWith("class CustomerService uses CustomerRow:", f.Outputs()["out/services/CustomerService.cs"], StringComparison.Ordinal);
        Assert.False(f.Outputs().ContainsKey("out/records/CustomerRecord.cs"));
    }

    [Fact]
    public async Task A_skipped_exporting_unit_serves_its_stored_record()
    {
        await using var f = await ExportPacks.CreateAsync();
        await f.RunAsync();
        f.WritePackFile("services", "service.scriban", "// v2\n" + ExportPacks.ServiceTemplate);

        var plan = await PlanAsync(f);

        var record = plan.Units.First(u => u.Key.StartsWith("records/record:", StringComparison.Ordinal));
        Assert.True(record.Skipped);
        Assert.Equal("unchanged", record.Reason);
        Assert.EndsWith("Record", record.Exports!.Values.GetProperty("class_name").GetString(), StringComparison.Ordinal);
        Assert.All(plan.Units.Where(u => u.Key.StartsWith("services/", StringComparison.Ordinal)), u => Assert.False(u.Skipped));
        Assert.Equal(RunOutcome.Succeeded, (await f.Service.ApplyAsync(plan.Id, null, Ct)).Outcome);
        Assert.StartsWith("// v2\nclass CustomerService uses CustomerRecord", f.Outputs()["out/services/CustomerService.cs"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_units_reading_each_others_exports_in_one_pack_skip_on_a_second_run()
    {
        await using var f = await ExportPacks.CreateAsync(ExportPacks.RecordUnit + """
            , { "id": "row", "template": "row.scriban", "for": "each entity", "output": "out/records/{{ exports.row }}.cs", "exports": { "row": "{{ entity.name }}Row" } }
            """);
        f.WritePackFile("records", "record.scriban", """class {{ exports.class_name }} : {{ (entity | exported "records").row }}""");
        f.WritePackFile("records", "row.scriban", """class {{ exports.row }} of {{ (entity | exported "records").class_name }}""");

        var first = await f.RunAsync();
        Assert.True(first.Outcome == RunOutcome.Succeeded, ExportPacks.Problems(first));
        Assert.Equal("class CustomerRecord : CustomerRow", f.Outputs()["out/records/CustomerRecord.cs"]);
        Assert.Equal("class CustomerRow of CustomerRecord", f.Outputs()["out/records/CustomerRow.cs"]);

        Assert.Equal(0, (await f.RunAsync()).UnitsRendered);
    }

    [Fact]
    public async Task Forced_and_check_runs_evaluate_every_record()
    {
        await using var f = await ExportPacks.CreateAsync();
        await f.RunAsync();
        var before = f.Outputs();

        var forced = await f.RunAsync(force: true);
        var check = await f.RunAsync(GenerationMode.Check);

        Assert.Equal(4, forced.UnitsRendered);
        Assert.Equal(before, f.Outputs());
        Assert.True(check.Outcome == RunOutcome.Succeeded, ExportPacks.Problems(check));
        Assert.Equal(4, check.UnitsRendered);
    }

    [Fact]
    public async Task Generating_the_consumer_alone_writes_nothing_for_the_used_pack()
    {
        await using var f = await ExportPacks.CreateAsync();

        var result = await f.RunAsync(packs: ["services"]);

        Assert.True(result.Outcome == RunOutcome.Succeeded, ExportPacks.Problems(result));
        Assert.Equal(["out/services/CustomerService.cs", "out/services/ProductService.cs"], f.Outputs().Keys);
        Assert.Equal("", f.CommittedManifest("records"));
        var plan = await PlanAsync(f, packs: ["services"]);
        Assert.Equal(2, plan.Exports.Count);
        Assert.All(plan.Exports, e => Assert.StartsWith("records/record:", e.Key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Output_is_identical_with_one_and_eight_jobs()
    {
        var outputs = new List<SortedDictionary<string, string>>();
        foreach (var jobs in new[] { 1, 8 })
        {
            await using var f = await ExportPacks.CreateAsync();
            var result = await f.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Jobs = jobs }, null, Ct);
            Assert.True(result.Outcome == RunOutcome.Succeeded, ExportPacks.Problems(result));
            outputs.Add(f.Outputs());
        }

        Assert.Equal(outputs[0], outputs[1]);
    }

    [Fact]
    public async Task A_scoped_run_evaluates_only_the_records_its_units_read()
    {
        await using var f = await ExportPacks.CreateAsync();
        var full = await PlanAsync(f);
        var customer = full.Units.First(u => u.Key.StartsWith("services/service:", StringComparison.Ordinal)).ElementId!;

        var plan = await PlanAsync(f, units: ["services/service"], elements: [customer]);

        Assert.Equal(["services/service:" + customer], plan.Units.Select(u => u.Key));
        Assert.Equal(["records/record:" + customer], plan.Exports.Select(e => e.Key));
        Assert.Equal(RunOutcome.Succeeded, (await f.Service.ApplyAsync(plan.Id, null, Ct)).Outcome);
        Assert.Equal(["out/services/CustomerService.cs"], f.Outputs().Keys);
    }

    [Fact]
    public async Task The_plan_records_exports_of_rendered_skipped_and_export_only_units()
    {
        await using var f = await ExportPacks.CreateAsync();
        var plan = await PlanAsync(f);

        Assert.All(plan.Units.Where(u => u.Key.StartsWith("records/", StringComparison.Ordinal)), u => Assert.NotNull(u.Exports));
        Assert.All(plan.Units.Where(u => u.Key.StartsWith("services/", StringComparison.Ordinal)), u => Assert.Null(u.Exports));
        var record = plan.Units.First(u => u.Key.StartsWith("records/record:", StringComparison.Ordinal));
        Assert.Contains("px:records/" + record.ElementId, plan.Units.Single(u => u.Key == "services/service:" + record.ElementId).ReadKeys);
        Assert.Empty(plan.Exports);
        Assert.Equal(2, (await PlanAsync(f, packs: ["services"])).Exports.Count);
    }

    [Fact]
    public async Task Apply_seeds_exports_from_the_plan_without_evaluating()
    {
        await using var f = await ExportPacks.CreateAsync();
        var plan = await PlanAsync(f, packs: ["services"]);
        Assert.Equal(2, plan.Exports.Count);
        var applied = await f.Service.ApplyAsync(plan.Id, null, Ct);

        Assert.Equal(RunOutcome.Succeeded, applied.Outcome);
        Assert.StartsWith("class CustomerService uses CustomerRecord", f.Outputs()["out/services/CustomerService.cs"], StringComparison.Ordinal);
        Assert.Equal("", f.CommittedManifest("records"));
    }

    [Fact]
    public async Task A_disabled_used_pack_exports_though_its_output_is_outside_every_root()
    {
        // records is disabled, so packs.records.output is "", and its unit's output is under no allowed root: an export-only unit
        // writes nothing, so neither MQ6019 nor the copy checks apply to it, and services reads its exports.
        await using var f = await ExportPacks.CreateAsync(ExportPacks.RecordUnit.Replace("\"out/records/{{ exports.class_name }}.cs\"",
            "\"Models/{{ entity.name }}.cs\"", StringComparison.Ordinal));
        await f.WriteModelAsync(b => Models.Shop(b), s => s with
        {
            Packs = new Dictionary<string, Maquettiste.Engine.Model.PackSettings>(StringComparer.Ordinal) { ["records"] = new() { Enabled = false } },
        });

        var result = await f.RunAsync();

        Assert.True(result.Outcome == RunOutcome.Succeeded, ExportPacks.Problems(result));
        Assert.DoesNotContain(result.Diagnostics, d => d.Rule == "MQ6019");
        Assert.Equal(["out/services/CustomerService.cs", "out/services/ProductService.cs"], f.Outputs().Keys);
        Assert.StartsWith("class CustomerService uses CustomerRecord: Id Name", f.Outputs()["out/services/CustomerService.cs"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_changed_export_renders_again_only_the_units_that_read_it()
    {
        await using var f = await ExportPacks.CreateAsync(extraServiceUnits: """
            , { "id": "readme", "template": "readme.scriban", "for": "each entity", "output": "out/services/{{ entity.name }}.txt" }
            """);
        f.WritePackFile("services", "readme.scriban", "{{ entity.name }}");
        Assert.Equal(6, (await f.RunAsync()).UnitsRendered);

        // The exporting unit's input changes for both entities, its value for Customer only.
        LibraryPacks.Pack(f, "records", ExportPacks.RecordUnit.Replace("\"{{ pascal entity.name }}Record\"",
            "\"{{ if entity.name == 'Customer' }}Client{{ else }}{{ pascal entity.name }}{{ end }}Record\"", StringComparison.Ordinal));
        var plan = await PlanAsync(f);

        var customer = plan.Units.Single(u => u.Key.StartsWith("records/record:", StringComparison.Ordinal) && u.Exports!.Values.GetProperty("class_name").GetString() == "ClientRecord").ElementId;
        Assert.Equal(["records/record:", "records/record:", "services/service:" + customer],
            plan.Units.Where(u => !u.Skipped).Select(u => u.Key.StartsWith("records/", StringComparison.Ordinal) ? u.Key[..(u.Key.IndexOf(':', StringComparison.Ordinal) + 1)] : u.Key)
                .Order(StringComparer.Ordinal));
        Assert.Contains(plan.Units.Single(u => u.Key == "services/service:" + customer).Causes, c => c.Kind == "exports");
        Assert.Equal(RunOutcome.Succeeded, (await f.Service.ApplyAsync(plan.Id, null, Ct)).Outcome);
        Assert.StartsWith("class CustomerService uses ClientRecord:", f.Outputs()["out/services/CustomerService.cs"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_is_stale_when_an_export_only_units_inputs_changed()
    {
        await using var f = await ExportPacks.CreateAsync();
        var plan = await PlanAsync(f, packs: ["services"]);
        LibraryPacks.Pack(f, "records", ExportPacks.RecordUnit.Replace("}}Record\"", "}}Row\"", StringComparison.Ordinal));

        var applied = await f.Service.ApplyAsync(plan.Id, null, Ct);

        Assert.Equal(RunOutcome.Stale, applied.Outcome);
        Assert.Contains(applied.StaleUnits, k => k.StartsWith("records/record:", StringComparison.Ordinal));
        Assert.Empty(f.Outputs());
    }
}
