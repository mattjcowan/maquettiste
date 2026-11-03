using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Tests.Editor;
using Maquettiste.Engine.Tests.Integration;
using Maquettiste.Engine.Tests.Rendering;

namespace Maquettiste.Engine.Tests.Generation;

/// <summary>
/// The packs of a dry run's impact (next-phase-2-design.md section 6.1, decisions 3 to 5), over a billing copy with the two example
/// packs: the units whose stored read keys the change touches, with their outputs; the units of deleted elements; and the units a
/// created element enters, without outputs, filtered when the unit has a where filter, dropped by a skip hint; and the units a
/// changed element enters once a where filter or a skip hint lets it in.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class OutputImpactTests
{
    private const string CustomerId = EditorRepo.CustomerId;
    private const string BillingPackage = EditorRepo.BillingPackageId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task GenerateAsync(EditorRepo repo)
    {
        var result = await repo.Service.RunAsync(new GenerationRequest { Jobs = 2 }, null, Ct);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, result);
    }

    private static async Task<string> UpdateAsync(ModelStore store, string id, Action<JsonObject> change)
    {
        var document = (await store.GetElementAsync(id, Ct))!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        change(node);
        return $$"""{ "op": "update", "id": "{{id}}", "expectedHash": "{{document.Hash}}", "element": {{node.ToJsonString()}} }""";
    }

    private static string Create(string name, string extra = "") => $$"""
        { "op": "create", "element": { "kind": "entity", "name": "{{name}}", "package": "{{BillingPackage}}", "key": { "attributes": [ "${{name}}Id" ] },
          "attributes": [ { "as": "${{name}}Id", "name": "id", "type": "uuid", "required": true } ]{{extra}} } }
        """;

    private static async Task<BatchPreview> DryRunAsync(ModelStore store, GenerationService service, params string[] operations)
    {
        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes($$"""{ "operations": [ {{string.Join(", ", operations)}} ] }"""));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}")));
        var preview = await service.PreviewBatchAsync(parsed.Batch, Ct);
        Assert.True(preview.Outcome == SaveOutcome.Saved,
            preview.Outcome + ": " + string.Join("; ", preview.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        Assert.NotNull(preview.Impact?.Packs);
        return preview;
    }

    private static Task<BatchPreview> DryRunAsync(EditorRepo repo, params string[] operations) => DryRunAsync(repo.Store, repo.Service, operations);

    private static async Task ApplyAsync(EditorRepo repo, params string[] operations)
    {
        var applied = await repo.Store.ApplyBatchAsync(repo.Store.ParseBatch(Encoding.UTF8.GetBytes(
            $$"""{ "operations": [ {{string.Join(", ", operations)}} ] }""")).Batch!, ChangeSource.Cli, Ct);
        Assert.True(applied.Outcome == SaveOutcome.Saved,
            applied.Outcome + ": " + string.Join("; ", applied.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
    }

    private static async Task<IReadOnlyList<string>> RenderedAsync(EditorRepo repo) =>
        [.. (await repo.Service.PlanAsync(new GenerationRequest(), null, Ct)).Plan!.Units.Where(u => !u.Skipped).Select(u => u.Key)];

    private static ImpactPack Pack(BatchPreview preview, string name) => preview.Impact!.Packs!.Single(p => p.Pack == name);

    private static string Minted(BatchPreview preview) => preview.Ids.Minted.Single(m => m.Pointer.Length == 0).Id;

    [Fact]
    public async Task Units_whose_stored_keys_hold_a_touched_key_are_listed_with_their_outputs()
    {
        await using var repo = EditorRepo.Create();
        await GenerateAsync(repo);
        var states = await repo.Service.Services.UnitState.LoadAsync("csharp-dapper", Ct);

        var preview = await DryRunAsync(repo, await UpdateAsync(repo.Store, CustomerId, e => e["description"] = "Who buys."));

        var unit = Pack(preview, "csharp-dapper").Units.Single(u => u.Key == "csharp-dapper/entity:" + CustomerId);
        Assert.Equal("inputs", unit.Change);
        Assert.Contains("e:" + CustomerId, unit.Keys);
        Assert.True(unit.Keys.Count <= 5);
        Assert.Equal([.. unit.Keys.Order(StringComparer.Ordinal)], unit.Keys);
        Assert.Equal(states[unit.Key].Outputs.Select(o => o.Path).Order(StringComparer.Ordinal), unit.Outputs);
        Assert.NotEmpty(unit.Outputs);
        Assert.Equal("known", Pack(preview, "sql-ddl").State);
        Assert.DoesNotContain(Pack(preview, "csharp-dapper").Units, u => u.Key == "csharp-dapper/entity:" + EditorRepo.PaymentId);
    }

    [Fact]
    public async Task Deleting_an_element_lists_its_units_as_deleted()
    {
        await using var repo = EditorRepo.Create();
        repo.EditSettingsOnDisk(E2ERepo.RecordOff);
        var applied = await repo.Store.ApplyBatchAsync(repo.Store.ParseBatch(Encoding.UTF8.GetBytes(
            """{ "operations": [ """ + Create("Refund") + " ] }")).Batch!, ChangeSource.Cli, Ct);
        Assert.Equal(SaveOutcome.Saved, applied.Outcome);
        var refund = applied.Ids.Minted.Single(m => m.Pointer.Length == 0).Id;
        await GenerateAsync(repo);
        var document = (await repo.Store.GetElementAsync(refund, Ct))!;

        var preview = await DryRunAsync(repo, $$"""{ "op": "delete", "id": "{{refund}}", "expectedHash": "{{document.Hash}}" }""");

        var entity = Pack(preview, "csharp-dapper").Units.Single(u => u.Key == "csharp-dapper/entity:" + refund);
        Assert.Equal("deleted", entity.Change);
        Assert.NotEmpty(entity.Outputs);
        var table = Pack(preview, "sql-ddl").Units.Single(u => u.Key == "sql-ddl/table:" + refund + "@" + EditorRepo.MainDatabaseId);
        Assert.Equal("deleted", table.Change);
    }

    [Fact]
    public async Task A_created_entity_lists_the_each_entity_units_as_added_without_outputs()
    {
        await using var repo = EditorRepo.Create();
        await GenerateAsync(repo);

        var preview = await DryRunAsync(repo, Create("Refund"));
        var refund = Minted(preview);

        var entity = Pack(preview, "csharp-dapper").Units.Single(u => u.Key == "csharp-dapper/entity:" + refund);
        Assert.Equal(("added", false), (entity.Change, entity.Filtered));
        Assert.Empty(entity.Keys);
        Assert.Empty(entity.Outputs);
        // The table only resolution makes is not listed; plan finds it.
        Assert.DoesNotContain(Pack(preview, "sql-ddl").Units, u => u.Key.Contains(refund, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_unit_with_a_where_filter_is_added_as_filtered()
    {
        await using var repo = EditorRepo.Create();
        await GenerateAsync(repo);

        var preview = await DryRunAsync(repo, Create("Refund"));

        var repository = Pack(preview, "csharp-dapper").Units.Single(u => u.Key == "csharp-dapper/repository:" + Minted(preview));
        Assert.Equal(("added", true), (repository.Change, repository.Filtered));
        Assert.Contains("\"filtered\":true", JsonSerializer.Serialize(repository, EngineJson.Options), StringComparison.Ordinal);
        Assert.DoesNotContain("filtered", JsonSerializer.Serialize(Pack(preview, "csharp-dapper").Units.First(u => !u.Filtered), EngineJson.Options),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_unit_with_a_script_where_is_added_as_filtered_too()
    {
        await using var repo = EditorRepo.Create();
        await GenerateAsync(repo);
        var packFile = repo.Repo.PathOf(".maquettiste/templates/csharp-dapper/pack.json");
        var pack = JsonNode.Parse(File.ReadAllText(packFile))!.AsObject();
        pack["units"]!.AsArray().Single(u => (string?)u!["id"] == "entity")!["where"] = new JsonObject { ["script"] = "isPublic" };
        File.WriteAllText(packFile, pack.ToJsonString());

        var preview = await DryRunAsync(repo, Create("Refund"));

        Assert.Equal("known", Pack(preview, "csharp-dapper").State);
        var entity = Pack(preview, "csharp-dapper").Units.Single(u => u.Key == "csharp-dapper/entity:" + Minted(preview));
        Assert.True(entity.Filtered);
    }

    [Fact]
    public async Task A_skip_hint_drops_an_added_unit()
    {
        await using var repo = EditorRepo.Create();
        await GenerateAsync(repo);

        var preview = await DryRunAsync(repo, Create("Refund", """, "generation": { "csharp-dapper": { "skip": true } }"""));
        var refund = Minted(preview);

        Assert.DoesNotContain(Pack(preview, "csharp-dapper").Units, u => u.Key.EndsWith(":" + refund, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pack_without_unit_states_is_state_none()
    {
        await using var repo = EditorRepo.Create();

        var preview = await DryRunAsync(repo, await UpdateAsync(repo.Store, CustomerId, e => e["description"] = "Who buys."));

        Assert.Equal([("csharp-dapper", "none"), ("sql-ddl", "none")], preview.Impact!.Packs!.Select(p => (p.Pack, p.State)));
        Assert.All(preview.Impact.Packs!, p => Assert.Equal((0, 0), (p.Units.Count, p.UnitsTotal)));
    }

    [Fact]
    public async Task Units_are_capped_per_pack_with_a_total()
    {
        await using var repo = EditorRepo.Create();
        await GenerateAsync(repo);

        var preview = await DryRunAsync(repo, [.. Enumerable.Range(0, 120).Select(i => Create("Refund" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)))]);

        var pack = Pack(preview, "csharp-dapper");
        Assert.Equal(ImpactPack.MaxUnits, pack.Units.Count);
        Assert.True(pack.UnitsTotal >= 240, pack.UnitsTotal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal([.. pack.Units.Select(u => u.Key).Order(StringComparer.Ordinal)], pack.Units.Select(u => u.Key));
    }

    [Fact]
    public async Task Two_dry_runs_answer_the_same_bytes()
    {
        await using var repo = EditorRepo.Create();
        await GenerateAsync(repo);
        var update = await UpdateAsync(repo.Store, CustomerId, e => e["attributes"]![1]!["length"] = 150);
        var described = await UpdateAsync(repo.Store, EditorRepo.InvoiceId, e => e["description"] = "A bill.");

        var first = JsonSerializer.Serialize(await DryRunAsync(repo, update, described), EngineJson.Options);
        var second = JsonSerializer.Serialize(await DryRunAsync(repo, update, described), EngineJson.Options);

        Assert.Equal(first, second);
        Assert.Contains("\"packs\"", first, StringComparison.Ordinal);
        Assert.Contains("\"referrers\"", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_exports_key_is_touched_through_the_stored_export_keys()
    {
        await using var f = await ExportPacks.CreateAsync();
        Assert.Equal(RunOutcome.Succeeded, (await f.RunAsync()).Outcome);
        var model = f.Store.Current!;
        var customer = model.All<Entity>().Single(e => e.Name == "Customer").Id;
        var product = model.All<Entity>().Single(e => e.Name == "Product").Id;

        var preview = await DryRunAsync(f.Store, f.Service, await UpdateAsync(f.Store, customer, e => e["description"] = "Who buys."));

        var services = Pack(preview, "services");
        Assert.Contains("px:records/" + customer, services.Units.Single(u => u.Key == "services/service:" + customer).Keys);
        Assert.DoesNotContain(services.Units, u => u.Key == "services/service:" + product);
        Assert.Contains(Pack(preview, "records").Units, u => u.Key == "records/record:" + customer);
    }

    [Fact]
    public async Task A_where_filter_that_lets_a_changed_element_in_lists_its_unit_as_added()
    {
        await using var repo = EditorRepo.Create();
        var packFile = repo.Repo.PathOf(".maquettiste/templates/csharp-dapper/pack.json");
        var pack = JsonNode.Parse(File.ReadAllText(packFile))!.AsObject();
        pack["units"]!.AsArray().Single(u => (string?)u!["id"] == "entity")!["where"] = new JsonObject { ["tags"] = new JsonArray("pii") };
        File.WriteAllText(packFile, pack.ToJsonString());
        await GenerateAsync(repo);
        var key = "csharp-dapper/entity:" + EditorRepo.InvoiceId;
        Assert.DoesNotContain(key, (await repo.Service.Services.UnitState.LoadAsync("csharp-dapper", Ct)).Keys);
        var tagged = await UpdateAsync(repo.Store, EditorRepo.InvoiceId, e => e["tags"] = new JsonArray("billing", "pii"));

        var preview = await DryRunAsync(repo, tagged);

        var unit = Pack(preview, "csharp-dapper").Units.Single(u => u.Key == key);
        Assert.Equal(("added", true), (unit.Change, unit.Filtered));
        Assert.Empty(unit.Outputs);
        await ApplyAsync(repo, tagged);
        Assert.Contains(key, await RenderedAsync(repo));
    }

    [Fact]
    public async Task Losing_a_skip_hint_lists_the_units_it_dropped_as_added()
    {
        await using var repo = EditorRepo.Create();
        await ApplyAsync(repo, await UpdateAsync(repo.Store, EditorRepo.InvoiceId,
            e => e["generation"] = new JsonObject { ["csharp-dapper"] = new JsonObject { ["skip"] = true } }));
        await GenerateAsync(repo);
        var invoice = ":" + EditorRepo.InvoiceId;
        Assert.DoesNotContain((await repo.Service.Services.UnitState.LoadAsync("csharp-dapper", Ct)).Keys, k => k.EndsWith(invoice, StringComparison.Ordinal));
        var unskipped = await UpdateAsync(repo.Store, EditorRepo.InvoiceId, e => e.Remove("generation"));

        var preview = await DryRunAsync(repo, unskipped);

        var units = Pack(preview, "csharp-dapper").Units.Where(u => u.Key.EndsWith(invoice, StringComparison.Ordinal)).ToList();
        Assert.Equal([("csharp-dapper/entity" + invoice, "added", true), ("csharp-dapper/repository" + invoice, "added", true)],
            units.Select(u => (u.Key, u.Change, u.Filtered)));
        await ApplyAsync(repo, unskipped);
        var rendered = await RenderedAsync(repo);
        Assert.All(units, u => Assert.Contains(u.Key, rendered));
    }

    [Fact]
    public void A_unit_whose_element_id_both_models_hold_is_not_deleted_when_a_part_of_it_goes()
    {
        // An inherited copy <table>.<member> both models hold, whose member alone goes; and the parts of an id neither holds.
        HashSet<string> before = ["refunds.amount", "refunds", "amount", "main"], after = ["refunds.amount", "refunds", "main"];

        Assert.False(OutputImpact.ElementDeleted("sql-ddl/column:refunds.amount", before.Contains, after.Contains));
        Assert.False(OutputImpact.ElementDeleted("sql-ddl/column:refunds.amount@main", before.Contains, after.Contains));
        Assert.True(OutputImpact.ElementDeleted("sql-ddl/column:credits.amount@main", before.Contains, after.Contains));
        Assert.True(OutputImpact.ElementDeleted("sql-ddl/table:amount@main", before.Contains, after.Contains));
    }

    [Fact]
    public async Task A_created_table_is_added_under_the_key_plan_then_stores()
    {
        const string Refunds = "01K9RFND000000000000000001";
        const string RefundsId = "01K9RFND000000000000000002";
        await using var repo = EditorRepo.Create();
        repo.EditSettingsOnDisk(E2ERepo.RecordOff);
        await GenerateAsync(repo);
        var create = $$"""
            { "op": "create", "element": { "kind": "table", "id": "{{Refunds}}", "name": "refunds", "database": "{{EditorRepo.MainDatabaseId}}",
              "columns": [ { "id": "{{RefundsId}}", "name": "id", "type": "uuid", "nullable": false } ], "primaryKey": { "columns": [ "{{RefundsId}}" ] } } }
            """;

        var preview = await DryRunAsync(repo, create);

        var unit = Pack(preview, "sql-ddl").Units.Single(u => u.Key.Contains(Refunds, StringComparison.Ordinal));
        Assert.Equal(("added", false), (unit.Change, unit.Filtered));
        await ApplyAsync(repo, create);
        await GenerateAsync(repo);
        var stored = (await repo.Service.Services.UnitState.LoadAsync("sql-ddl", Ct)).Keys.Where(k => k.Contains(Refunds, StringComparison.Ordinal));
        Assert.Equal([unit.Key], stored);
    }
}
