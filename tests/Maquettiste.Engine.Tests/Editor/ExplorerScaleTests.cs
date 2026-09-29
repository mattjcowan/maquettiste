using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>E5 to E5e (explorer-redesign.md section 4.1): index rows, batched reads, the index tag and table summaries.</summary>
public sealed class ExplorerScaleTests
{
    private const string OverviewDiagramId = "01J92P0V2164SDBW687ZV6E1MV";
    private const string InvoiceInMainMappingId = "01J92P0V200XW93JQYSRCT2SE3";
    private const string InvoiceNotesAttributeId = "01J92P0V0WKRGKH7YBKA2V30NC";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Index_rows_carry_the_E5_members_of_their_kind_only()
    {
        await using var repo = EditorRepo.Create(packs: false);
        var snapshot = await repo.Store.GetSnapshotAsync(EditorRepo.Ct);
        var index = await repo.Store.GetIndexAsync(EditorRepo.Ct);

        var places = index.Single(s => s.Id == EditorRepo.PlacesRelationId);
        var relation = snapshot.Get<Relation>(EditorRepo.PlacesRelationId)!;
        Assert.Equal(relation.Ends.Select(e => new RelationEndSummary(e.Entity, e.Role)), places.Ends!);
        Assert.Null(places.Database);
        Assert.Null(places.MemberCount);

        var diagram = index.Single(s => s.Id == OverviewDiagramId);
        Assert.Equal(snapshot.Get<Diagram>(OverviewDiagramId)!.Members.Count, diagram.MemberCount);
        Assert.True(diagram.MemberCount > 0);

        var overlay = index.Single(s => s.Id == EditorRepo.OverlayTableId);
        Assert.Equal(EditorRepo.MainDatabaseId, overlay.Database);
        Assert.Equal(EditorRepo.InvoiceId, overlay.Entity);
        var mapping = index.Single(s => s.Id == InvoiceInMainMappingId);
        Assert.Equal(EditorRepo.MainDatabaseId, mapping.Database);
        Assert.Equal(EditorRepo.InvoiceId, mapping.Entity);
        foreach (var summary in index.Where(s => s.Kind is "view" or "sequence"))
            Assert.Equal(EditorRepo.MainDatabaseId, summary.Database);

        var invoice = index.Single(s => s.Id == EditorRepo.InvoiceId);
        Assert.Null(invoice.Ends);
        Assert.Null(invoice.Database);
        Assert.Null(invoice.Entity);
        Assert.Null(invoice.MemberCount);
        Assert.Equal(snapshot.Get<Entity>(EditorRepo.InvoiceId)!.DisplayName, invoice.DisplayName);
    }

    [Fact]
    public async Task Null_E5_members_are_left_out_of_the_json_and_set_ones_are_written()
    {
        await using var repo = EditorRepo.Create(packs: false);
        var index = await repo.Store.GetIndexAsync(EditorRepo.Ct);

        var entity = JsonSerializer.SerializeToNode(index.Single(s => s.Id == EditorRepo.CustomerId), Web)!.AsObject();
        var relation = JsonSerializer.SerializeToNode(index.Single(s => s.Id == EditorRepo.PlacesRelationId), Web)!.AsObject();

        foreach (var name in new[] { "database", "entity", "memberCount", "ends" })
            Assert.False(entity.ContainsKey(name), name);
        Assert.True(entity.ContainsKey("package")); // the E4 and older members stay, null or not
        Assert.Equal(2, relation["ends"]!.AsArray().Count);
        Assert.NotNull(relation["ends"]![0]!["entity"]);
        Assert.NotNull(relation["ends"]![0]!["role"]);
    }

    [Fact]
    public void Entity_rows_carry_their_base_and_other_rows_leave_it_out()
    {
        var b = new Maquettiste.Testing.ModelBuilder(7);
        var party = b.Entity("Party").Abstract().Key("id", "uuid", IdentityStrategy.UuidV7);
        var person = b.Entity("Person").Base(party);
        var status = b.Enum("Status").Member("Draft", 0);
        var snapshot = ModelSnapshot.Create(b.BuildDocuments(), new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "", [], [], 1);
        var rows = snapshot.Summaries();

        Assert.Equal(party.Id, rows.Single(r => r.Id == person.Id).Base);
        Assert.Null(rows.Single(r => r.Id == party.Id).Base);
        Assert.Null(rows.Single(r => r.Id == status.Id).Base);
        var json = JsonSerializer.SerializeToNode(rows.Single(r => r.Id == person.Id), Web)!.AsObject();
        Assert.Equal(party.Id, (string?)json["base"]);
        Assert.False(JsonSerializer.SerializeToNode(rows.Single(r => r.Id == party.Id), Web)!.AsObject().ContainsKey("base"));
        Assert.NotEqual(rows.Single(r => r.Id == person.Id), rows.Single(r => r.Id == person.Id) with { Base = null });
    }

    [Fact]
    public void Summaries_compare_their_lists_by_items()
    {
        var a = new ElementSummary("A", "relation", "r", null, ["t"], "h", "p", null, ["s"], Ends: [new RelationEndSummary("E", "x")]);
        var b = new ElementSummary("A", "relation", "r", null, ["t"], "h", "p", null, ["s"], Ends: [new RelationEndSummary("E", "x")]);
        var c = b with { Ends = [new RelationEndSummary("E", "y")] };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, b with { MemberCount = 1 });
        Assert.NotEqual(a, b with { Tags = ["u"] });
    }

    [Fact]
    public async Task Reading_many_ids_returns_each_document_once_in_request_order_with_the_missing_ids()
    {
        await using var repo = EditorRepo.Create(packs: false);
        const string unknown = "01J92P0V0FJ23CGSNKM7P1W5V9";

        var result = await repo.Store.ReadElementsAsync(
            [EditorRepo.PaymentId, InvoiceNotesAttributeId, unknown, EditorRepo.InvoiceId, EditorRepo.PaymentId], EditorRepo.Ct);

        Assert.Equal([EditorRepo.PaymentId, EditorRepo.InvoiceId], result.Elements.Select(d => d.Element.Id));
        Assert.Equal([unknown], result.Missing);
        Assert.Same(await repo.Store.GetElementAsync(EditorRepo.InvoiceId, EditorRepo.Ct), result.Elements[1]);
    }

    [Fact]
    public async Task Reading_more_than_200_ids_is_refused()
    {
        await using var repo = EditorRepo.Create(packs: false);
        var ids = Enumerable.Repeat(EditorRepo.InvoiceId, ModelReads.MaxReadIds + 1).ToArray();

        await Assert.ThrowsAsync<ArgumentException>(() => repo.Store.ReadElementsAsync(ids, EditorRepo.Ct));
        var exactly = await repo.Store.ReadElementsAsync(ids[..ModelReads.MaxReadIds], EditorRepo.Ct);
        Assert.Single(exactly.Elements);
    }

    [Fact]
    public async Task The_index_tag_is_stable_across_stores_and_changes_with_any_row()
    {
        await using var one = EditorRepo.Create(packs: false);
        await using var two = EditorRepo.Create(packs: false);
        var first = ModelReads.IndexTag(await one.Store.GetIndexAsync(EditorRepo.Ct));

        Assert.Equal(first, ModelReads.IndexTag(await two.Store.GetIndexAsync(EditorRepo.Ct)));
        Assert.Matches("^[0-9a-f]{64}$", first);

        var payment = (await one.Store.GetElementAsync(EditorRepo.PaymentId, EditorRepo.Ct))!;
        var node = JsonNode.Parse(payment.Json.GetRawText())!.AsObject();
        node["name"] = "Settlement";
        var saved = await one.Store.SaveAsync(EditorRepo.PaymentId, Encoding.UTF8.GetBytes(node.ToJsonString()), payment.Hash, ChangeSource.Editor, EditorRepo.Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);

        Assert.NotEqual(first, ModelReads.IndexTag(await one.Store.GetIndexAsync(EditorRepo.Ct)));
    }

    [Fact]
    public async Task Table_summaries_match_the_view_without_columns()
    {
        await using var repo = EditorRepo.Create(packs: false);
        var tables = new DatabaseTables(repo.Service);

        var result = await tables.GetAsync(EditorRepo.MainDatabaseId, EditorRepo.Ct);
        var view = (await repo.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, EditorRepo.Ct)).View!;

        Assert.False(result.Partial);
        Assert.DoesNotContain(result.Diagnostics, Outcomes.IsInvalid);
        Assert.Equal(
            view.Tables.Select(t => new TableSummary(t.Key, t.Name, t.Schema, t.Origin, t.EntityId, t.RelationId, t.IsJunction, t.IsLookup, t.Columns.Count)),
            result.Tables);
        Assert.Contains(result.Tables, t => t.IsJunction);
    }

    [Fact]
    public async Task A_model_with_errors_returns_the_tables_that_resolve_and_says_it_is_partial()
    {
        await using var repo = EditorRepo.Create(packs: false);
        var tables = new DatabaseTables(repo.Service);
        var complete = await tables.GetAsync(EditorRepo.MainDatabaseId, EditorRepo.Ct);
        var payment = (await repo.Store.GetElementAsync(EditorRepo.PaymentId, EditorRepo.Ct))!;
        var node = JsonNode.Parse(payment.Json.GetRawText())!.AsObject();
        node.Remove("key");
        File.WriteAllText(repo.Repo.PathOf(payment.Path), node.ToJsonString(), new UTF8Encoding(false));
        await repo.Store.RescanAsync(false, EditorRepo.Ct); // what the host's watcher does

        var result = await tables.GetAsync(EditorRepo.MainDatabaseId, EditorRepo.Ct);

        Assert.True(result.Partial);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ3005" && d.ElementId == EditorRepo.PaymentId && d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.Tables, t => t.EntityId == EditorRepo.PaymentId);
        Assert.Contains(complete.Tables, t => t.EntityId == EditorRepo.PaymentId);
        Assert.Contains(result.Tables, t => t.EntityId == EditorRepo.CustomerId);
    }

    [Fact]
    public async Task A_broken_mapping_leaves_out_its_entitys_table()
    {
        await using var repo = EditorRepo.Create(packs: false);
        var tables = new DatabaseTables(repo.Service);
        var mapping = (await repo.Store.GetElementAsync(InvoiceInMainMappingId, EditorRepo.Ct))!;
        var node = JsonNode.Parse(mapping.Json.GetRawText())!.AsObject();
        node["attributes"] = new JsonArray(new JsonObject { ["attribute"] = "01J92P0V0FJ23CGSNKM7P1W5V9", ["storage"] = "string" });
        File.WriteAllText(repo.Repo.PathOf(mapping.Path), node.ToJsonString(), new UTF8Encoding(false));
        await repo.Store.RescanAsync(false, EditorRepo.Ct); // what the host's watcher does

        var result = await tables.GetAsync(EditorRepo.MainDatabaseId, EditorRepo.Ct);

        Assert.True(result.Partial);
        Assert.Contains(result.Diagnostics, d => d.ElementId == InvoiceInMainMappingId && Outcomes.IsInvalid(d));
        Assert.DoesNotContain(result.Tables, t => t.EntityId == EditorRepo.InvoiceId);
        Assert.Contains(result.Tables, t => t.EntityId == EditorRepo.CustomerId);
    }

    [Fact]
    public async Task A_database_is_resolved_once_per_snapshot()
    {
        await using var repo = EditorRepo.Create(packs: false);
        var tables = new DatabaseTables(repo.Service);

        var first = await tables.GetAsync(EditorRepo.MainDatabaseId, EditorRepo.Ct);
        var second = await tables.GetAsync(EditorRepo.MainDatabaseId, EditorRepo.Ct);
        var unknown = await tables.GetAsync("01J92P0V0FJ23CGSNKM7P1W5V9", EditorRepo.Ct);

        Assert.Same(first.Tables, second.Tables);
        Assert.Same(first.Diagnostics, second.Diagnostics);
        Assert.Empty(unknown.Tables);
        Assert.Contains(unknown.Diagnostics, d => d.Rule == "MQ6017");
    }

    [Fact]
    public async Task A_cancelled_caller_stops_waiting_without_failing_the_shared_resolve()
    {
        await using var repo = EditorRepo.Create(packs: false);
        var tables = new DatabaseTables(repo.Service);
        await repo.Store.LoadAsync(EditorRepo.Ct);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tables.GetAsync(EditorRepo.MainDatabaseId, cancelled.Token));
        var result = await tables.GetAsync(EditorRepo.MainDatabaseId, EditorRepo.Ct);

        Assert.NotEmpty(result.Tables);
    }

    [Fact]
    public async Task Table_detail_is_the_views_table_and_each_database_resolved_alone_matches_the_whole_model()
    {
        await using var repo = EditorRepo.Create(packs: false);
        var tables = new DatabaseTables(repo.Service);
        var snapshot = await repo.Store.GetSnapshotAsync(EditorRepo.Ct);

        foreach (var database in snapshot.All<Database>())
        {
            var view = (await repo.Service.GetDatabaseViewAsync(database.Id, EditorRepo.Ct)).View!;
            var summaries = await tables.GetAsync(database.Id, EditorRepo.Ct);
            Assert.Equal(view.Tables.Select(t => t.Key), summaries.Tables.Select(t => t.Key));
            foreach (var table in view.Tables)
            {
                var detail = await tables.GetTableAsync(database.Id, table.Key, EditorRepo.Ct);
                Assert.False(detail.Partial);
                Assert.Equal(JsonSerializer.Serialize(table, Web), JsonSerializer.Serialize(detail.Table, Web));
            }
        }

        Assert.Contains(snapshot.All<Database>(), d => d.Id == EditorRepo.MainDatabaseId);
        Assert.Null((await tables.GetTableAsync(EditorRepo.MainDatabaseId, "no-such-table", EditorRepo.Ct)).Table);
    }

    [Fact]
    public async Task Table_detail_of_a_table_whose_entity_has_errors_is_null_and_partial()
    {
        await using var repo = EditorRepo.Create(packs: false);
        var tables = new DatabaseTables(repo.Service);
        var key = (await tables.GetAsync(EditorRepo.MainDatabaseId, EditorRepo.Ct)).Tables.First(t => t.EntityId == EditorRepo.PaymentId).Key;
        var payment = (await repo.Store.GetElementAsync(EditorRepo.PaymentId, EditorRepo.Ct))!;
        var node = JsonNode.Parse(payment.Json.GetRawText())!.AsObject();
        node.Remove("key");
        File.WriteAllText(repo.Repo.PathOf(payment.Path), node.ToJsonString(), new UTF8Encoding(false));
        await repo.Store.RescanAsync(false, EditorRepo.Ct);

        var detail = await tables.GetTableAsync(EditorRepo.MainDatabaseId, key, EditorRepo.Ct);

        Assert.Null(detail.Table);
        Assert.True(detail.Partial);
        Assert.Contains(detail.Diagnostics, d => d.Rule == "MQ3005" && d.ElementId == EditorRepo.PaymentId);
    }

    [Fact]
    public async Task A_resolver_without_the_per_database_entry_point_falls_back_to_one_whole_model_resolve()
    {
        await using var repo = EditorRepo.Create(packs: false);
        var services = Maquettiste.Engine.Generation.EngineServices.Create(repo.Repo.Options);
        var counting = new CountingResolver(services.Resolver);
        var tables = new DatabaseTables(new GenerationService(repo.Store, repo.Repo.Options, services with { Resolver = counting }));
        var reference = new DatabaseTables(repo.Service);
        var snapshot = await repo.Store.GetSnapshotAsync(EditorRepo.Ct);

        foreach (var database in snapshot.All<Database>())
        {
            var result = await tables.GetAsync(database.Id, EditorRepo.Ct);
            Assert.Equal(
                JsonSerializer.Serialize((await reference.GetAsync(database.Id, EditorRepo.Ct)).Tables, Web),
                JsonSerializer.Serialize(result.Tables, Web));
        }

        var unknown = await tables.GetAsync("01J92P0V0FJ23CGSNKM7P1W5V9", EditorRepo.Ct);
        Assert.Contains(unknown.Diagnostics, d => d.Rule == "MQ6017");
        Assert.Equal(1, counting.Calls);
    }

    private sealed class CountingResolver(Maquettiste.Engine.Pipeline.IModelResolver inner) : Maquettiste.Engine.Pipeline.IModelResolver
    {
        public int Calls;

        public Task<Maquettiste.Engine.Resolution.ResolvedModel> ResolveAsync(ModelSnapshot model, IProgress<Maquettiste.Engine.Pipeline.ProgressUpdate>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return inner.ResolveAsync(model, progress, ct);
        }
    }
}
