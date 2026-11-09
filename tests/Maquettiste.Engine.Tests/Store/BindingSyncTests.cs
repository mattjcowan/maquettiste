using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Editor;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>
/// The binding operations (next-phase-2-design.md section 3.3, D33) over a billing copy with a ledger table bound to an entity:
/// <c>map-by-name</c> maps the attributes a binding leaves unmapped to the free columns of the same loose name (fields name a table file's
/// columns by id and a view's by name) and records provenance; <c>resync-binding</c> renames the attributes that followed their column,
/// retypes, maps by name, adds attributes for added columns, removes the fields of removed columns and records provenance again; both
/// refuse with MQ4089 and never resolve the whole model.
/// </summary>
public sealed class BindingSyncTests
{
    private const string Main = EditorRepo.MainDatabaseId;
    private const string BillingSchema = "01J92P0V1RC04SKQ5353EAKHG2";
    private const string Money = "01J92P0V0304M76KA84NE4D1TE";
    private const string MoneyAmount = "01J92P0V08P9BVJAPQ793XYNTQ";
    private const string MoneyCurrency = "01J92P0V093A7BE6Q8AS477H7D";
    private const string InvoiceStatus = "01J92P0V05WQDH2GZG539D9VG1";
    private const string OutstandingView = "01J92P0V1YZ4YP352KD1A50FXS";
    private const string RevenueMonth = "01K6BND0000000000000000030";

    private const string Ledger = "01K7SYNC0000000000000000T0";
    private const string EntryIdColumn = "01K7SYNC0000000000000000C1";
    private const string PostedOnColumn = "01K7SYNC0000000000000000C2";
    private const string AmountDueColumn = "01K7SYNC0000000000000000C3";
    private const string MemoColumn = "01K7SYNC0000000000000000C4";
    private const string StatusColumn = "01K7SYNC0000000000000000C5";

    private const string LedgerEntry = "01K7SYNC0000000000000000E0";
    private const string EntryIdAttribute = "01K7SYNC0000000000000000A1";
    private const string PostedOnAttribute = "01K7SYNC0000000000000000A2";
    private const string AmountDueAttribute = "01K7SYNC0000000000000000A3";
    private const string MemoAttribute = "01K7SYNC0000000000000000A4";
    private const string StatusAttribute = "01K7SYNC0000000000000000A5";
    private const string LedgerBinding = "01K7SYNC0000000000000000B0";

    private static CancellationToken Ct => EditorRepo.Ct;

    // ---- setup ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Table <c>ledger_entries</c> (entry_id, Posted_On, amount_due, memo, status typed by InvoiceStatus) and entity <c>LedgerEntry</c>
    /// with the attributes <paramref name="attributes"/> (JSON items) bound to it with <paramref name="fields"/> (JSON items).
    /// </summary>
    private static async Task LedgerAsync(EditorRepo r, string attributes, string fields = "", string extraColumns = "")
    {
        await CreateAsync(r, $$"""
            { "kind": "table", "id": "{{Ledger}}", "name": "ledger_entries", "database": "{{Main}}", "schema": "{{BillingSchema}}",
              "columns": [
                { "id": "{{EntryIdColumn}}", "name": "entry_id", "type": "uuid", "nullable": false },
                { "id": "{{PostedOnColumn}}", "name": "Posted_On", "type": "date", "nullable": false },
                { "id": "{{AmountDueColumn}}", "name": "amount_due", "type": "decimal", "precision": 12, "scale": 2, "nullable": false },
                { "id": "{{MemoColumn}}", "name": "memo", "type": "text" },
                { "id": "{{StatusColumn}}", "name": "status", "type": "string", "length": 1, "typeRef": { "ref": "{{InvoiceStatus}}" } }
                {{extraColumns}}
              ],
              "primaryKey": { "columns": ["{{EntryIdColumn}}"] } }
            """);
        await CreateAsync(r, $$"""
            { "kind": "entity", "id": "{{LedgerEntry}}", "name": "LedgerEntry", "package": "{{EditorRepo.BillingPackageId}}",
              "key": { "attributes": ["{{EntryIdAttribute}}"] },
              "attributes": [ {{attributes}} ],
              "bindings": [ { "id": "{{LedgerBinding}}", "database": "{{Main}}", "source": "{{Ledger}}", "fields": [ {{fields}} ] } ] }
            """);
    }

    private const string EntryId = $$"""{ "id": "{{EntryIdAttribute}}", "name": "entryId", "type": "uuid", "required": true }""";
    private const string PostedOn = $$"""{ "id": "{{PostedOnAttribute}}", "name": "postedOn", "type": "date", "required": true }""";
    private const string AmountDue = $$"""{ "id": "{{AmountDueAttribute}}", "name": "amountDue", "type": "decimal", "precision": 12, "scale": 2, "required": true }""";
    private const string Memo = $$"""{ "id": "{{MemoAttribute}}", "name": "memo", "type": "text" }""";
    private const string Status = $$"""{ "id": "{{StatusAttribute}}", "name": "status", "type": { "ref": "{{InvoiceStatus}}" } }""";

    /// <summary>The ledger with every column mapped by name, so the binding records the provenance of each.</summary>
    private static async Task MappedLedgerAsync(EditorRepo r)
    {
        await LedgerAsync(r, string.Join(",", EntryId, PostedOn, AmountDue, Memo, Status));
        AssertSaved(await ApplyAsync(r, MapByName(LedgerEntry)));
    }

    private static async Task CreateAsync(EditorRepo r, string json)
    {
        var result = await r.Store.CreateAsync(Encoding.UTF8.GetBytes(JsonNode.Parse(json)!.ToJsonString()), ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
    }

    /// <summary>Saves an element with a change, through the store.</summary>
    private static async Task SaveAsync(EditorRepo r, string id, Action<JsonObject> change)
    {
        var document = (await r.Store.GetElementAsync(id, Ct))!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        change(node);
        var saved = await r.Store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
    }

    /// <summary>Rewrites the ledger table on disk behind the store's back (a change the store's rescan picks up).</summary>
    private static async Task EditLedgerOnDiskAsync(EditorRepo r, Action<JsonArray> columns)
    {
        var path = r.Store.Current!.GetDocument(Ledger)!.Path;
        var node = JsonNode.Parse(File.ReadAllText(r.Repo.PathOf(path)))!.AsObject();
        columns((JsonArray)node["columns"]!);
        File.WriteAllText(r.Repo.PathOf(path), node.ToJsonString(), new UTF8Encoding(false));
        await r.Store.GetSnapshotAsync(Ct);
    }

    private static JsonObject Column(JsonObject table, string id) =>
        ((JsonArray)table["columns"]!).OfType<JsonObject>().Single(c => c["id"]!.GetValue<string>() == id);

    private static string MapByName(params string[] entities) =>
        $$"""{ "op": "map-by-name", "database": "{{Main}}", "entities": [{{string.Join(",", entities.Select(e => "\"" + e + "\""))}}] }""";

    private static string Resync(string entity, string? columns = null, bool keepNames = false) =>
        $$"""{ "op": "resync-binding", "database": "{{Main}}", "entities": ["{{entity}}"]{{(columns is null ? "" : ", \"columns\": " + columns)}}{{(keepNames ? ", \"keepNames\": true" : "")}} }""";

    private static async Task<BatchResult> ApplyAsync(EditorRepo r, string operation)
    {
        await r.Store.GetSnapshotAsync(Ct);
        var parsed = r.Store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + operation + "]}"));
        Assert.Empty(parsed.Diagnostics);
        return await r.Store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);
    }

    private static void AssertSaved(BatchResult result) =>
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));

    private static Diagnostic Refused(BatchResult result)
    {
        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        var refusal = Assert.Single(result.Items.SelectMany(i => i.Diagnostics), d => d.Rule == "MQ4089");
        Assert.Equal("/operations/0", refusal.JsonPointer);
        return refusal;
    }

    private static Entity Entity(EditorRepo r, string id) => r.Store.Current!.Get<Entity>(id)!;

    private static EntityBinding Binding(EditorRepo r, string id) => Entity(r, id).Bindings.Single(b => b.Database == Main);

    private static ModelAttribute Attribute(EditorRepo r, string entity, string id) => Entity(r, entity).Attributes.Single(a => a.Id == id);

    private static List<(string Attribute, string Column)> Fields(EditorRepo r, string id) => [.. Binding(r, id).Fields.Select(f => (f.Attribute, f.Column))];

    /// <summary>The provenance the binding should record for the ledger table as it is now.</summary>
    private static Dictionary<string, string> Shapes(EditorRepo r)
    {
        var model = r.Store.Current!;
        return BindingDrift.ColumnsOf(model, model.Get<Table>(Ledger)!).ToDictionary(c => c.Id, c => c.Shape, StringComparer.Ordinal);
    }

    private static async Task<List<Diagnostic>> DriftAsync(EditorRepo r, string entity) =>
        [.. (await r.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics.Where(d => d.ElementId == entity && d.Rule is "MQ4047" or "MQ4087" or "MQ4088")];

    // ---- map-by-name ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Map_by_name_matches_case_and_separators_and_writes_ids()
    {
        await using var r = EditorRepo.Create(packs: false);
        await LedgerAsync(r, string.Join(",", EntryId, PostedOn, AmountDue,
            """{ "id": "01K7SYNC0000000000000000A9", "name": "notes", "type": "text" }"""));
        var attributesBefore = Entity(r, LedgerEntry).Attributes;

        AssertSaved(await ApplyAsync(r, MapByName(LedgerEntry)));

        // postedOn reads Posted_On and amountDue amount_due; memo and status stay free (no attribute has their names); no attribute is written.
        Assert.Equal([(EntryIdAttribute, EntryIdColumn), (PostedOnAttribute, PostedOnColumn), (AmountDueAttribute, AmountDueColumn)], Fields(r, LedgerEntry));
        Assert.Equal(attributesBefore, Entity(r, LedgerEntry).Attributes);
        // The binding had no provenance: it records every column of the table.
        Assert.Equal(Shapes(r), Binding(r, LedgerEntry).Provenance!.Columns);
        Assert.Contains(await DriftAsync(r, LedgerEntry), d => d.Rule == "MQ4047" && d.Message.Contains("'memo'", StringComparison.Ordinal));

        // Nothing left to match: refused, nothing written.
        var files = r.Files();
        var refusal = Refused(await ApplyAsync(r, MapByName(LedgerEntry)));
        Assert.Contains("nothing to map", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(files, r.Files());
    }

    [Fact]
    public async Task Map_by_name_matches_members_and_navigation_keys()
    {
        await using var r = EditorRepo.Create(packs: false);
        const string Balance = "01K7SYNC0000000000000000A6";
        const string CustomerEnd = "01K7SYNC0000000000000000R1", InvoiceEnd = "01K7SYNC0000000000000000R3";
        await LedgerAsync(r, string.Join(",", EntryId, $$"""{ "id": "{{Balance}}", "name": "balance", "type": { "ref": "{{Money}}" } }"""),
            extraColumns: """
                , { "id": "01K7SYNC0000000000000000C6", "name": "balance_amount", "type": "decimal", "precision": 18, "scale": 2 }
                , { "id": "01K7SYNC0000000000000000C7", "name": "BALANCE_CURRENCY", "type": "string", "length": 3 }
                , { "id": "01K7SYNC0000000000000000C8", "name": "customer_id", "type": "uuid" }
                , { "id": "01K7SYNC0000000000000000C9", "name": "invoice", "type": "uuid" }
                """);
        foreach (var (relation, end, other, entity, navigation) in new[]
        {
            ("01K7SYNC0000000000000000R0", CustomerEnd, "01K7SYNC0000000000000000R2", EditorRepo.CustomerId, "customer"),
            ("01K7SYNC0000000000000000R4", InvoiceEnd, "01K7SYNC0000000000000000R5", EditorRepo.InvoiceId, "invoice"),
        })
        {
            await CreateAsync(r, $$"""
                { "kind": "relation", "id": "{{relation}}", "name": "ledger_{{navigation}}", "package": "{{EditorRepo.BillingPackageId}}",
                  "ends": [ { "id": "{{end}}", "entity": "{{entity}}", "role": "{{navigation}}", "navigation": "{{navigation}}", "max": 1 },
                            { "id": "{{other}}", "entity": "{{LedgerEntry}}", "role": "ledgerEntries" } ] }
                """);
        }

        AssertSaved(await ApplyAsync(r, MapByName(LedgerEntry)));

        // A member as attributeMember, a navigation key as navigation plus Id (customer_id) or without it (invoice).
        Assert.Equal(
            [
                (EntryIdAttribute, EntryIdColumn), (Balance + "." + MoneyAmount, "01K7SYNC0000000000000000C6"), (Balance + "." + MoneyCurrency, "01K7SYNC0000000000000000C7"),
                (CustomerEnd, "01K7SYNC0000000000000000C8"), (InvoiceEnd, "01K7SYNC0000000000000000C9"),
            ],
            Fields(r, LedgerEntry));
    }

    [Fact]
    public async Task Map_by_name_maps_the_field_forms_of_an_end_with_several_types_that_the_entity_holds()
    {
        // Several-types-design.md decisions 46 and section 19.11 (part 2 lifts part 1's decision 21): the field candidates of a holding entity
        // include an end with several types, as <endId> (its id column), <endId>.type and <endId>.<entityId>, and on way 1 the one-type end each
        // covered type holds. AttachedTo attaches an entry to one customer or invoice; Filed files entries and invoices under one customer.
        await using var r = EditorRepo.Create(packs: false);
        const string AttachedEnd = "01K7SYNC0000000000000000R7", FiledCustomer = "01K7SYNC0000000000000000R9";
        await LedgerAsync(r, EntryId, extraColumns: """
            , { "id": "01K7SYNC0000000000000000D1", "name": "attached_to_id", "type": "uuid" }
            , { "id": "01K7SYNC0000000000000000D2", "name": "filed_customer_id", "type": "uuid" }
            , { "id": "01K7SYNC0000000000000000D4", "name": "attached_to_type", "type": "text" }
            """);
        await CreateAsync(r, $$"""
            { "kind": "relation", "id": "01K7SYNC0000000000000000R6", "name": "AttachedTo", "package": "{{EditorRepo.BillingPackageId}}",
              "ends": [ { "id": "01K7SYNC0000000000000000R8", "entity": "{{LedgerEntry}}", "role": "entries" },
                        { "id": "{{AttachedEnd}}", "types": [ { "entity": "{{EditorRepo.CustomerId}}" }, { "entity": "{{EditorRepo.InvoiceId}}" } ], "role": "attachedTo", "navigation": "attachedTo", "max": 1 } ] }
            """);
        await CreateAsync(r, $$"""
            { "kind": "relation", "id": "01K7SYNC0000000000000000RA", "name": "Filed", "package": "{{EditorRepo.BillingPackageId}}",
              "ends": [ { "id": "01K7SYNC0000000000000000RB", "types": [ { "entity": "{{LedgerEntry}}" }, { "entity": "{{EditorRepo.InvoiceId}}" } ], "role": "filed" },
                        { "id": "{{FiledCustomer}}", "entity": "{{EditorRepo.CustomerId}}", "role": "filedCustomer", "navigation": "filedCustomer", "max": 1 } ] }
            """);

        AssertSaved(await ApplyAsync(r, MapByName(LedgerEntry)));
        Assert.Equal(
            [
                (EntryIdAttribute, EntryIdColumn), (AttachedEnd, "01K7SYNC0000000000000000D1"), (FiledCustomer, "01K7SYNC0000000000000000D2"),
                (AttachedEnd + ".type", "01K7SYNC0000000000000000D4"),
            ],
            Fields(r, LedgerEntry));
    }

    [Fact]
    public async Task Map_by_name_on_a_view_writes_names_and_no_provenance()
    {
        await using var r = EditorRepo.Create(packs: false);
        const string Open = "01K7SYNC0000000000000000E1";
        await CreateAsync(r, $$"""
            { "kind": "entity", "id": "{{Open}}", "name": "OpenInvoice", "package": "{{EditorRepo.BillingPackageId}}",
              "key": { "attributes": ["01K7SYNC0000000000000000D1"] },
              "attributes": [ { "id": "01K7SYNC0000000000000000D1", "name": "id", "type": "uuid", "required": true },
                              { "id": "01K7SYNC0000000000000000D2", "name": "number", "type": "string" },
                              { "id": "01K7SYNC0000000000000000D3", "name": "issuedOn", "type": "date" } ],
              "bindings": [ { "id": "01K7SYNC0000000000000000D9", "database": "{{Main}}", "source": "{{OutstandingView}}" } ] }
            """);

        AssertSaved(await ApplyAsync(r, MapByName(Open)));

        Assert.Equal([("01K7SYNC0000000000000000D1", "id"), ("01K7SYNC0000000000000000D2", "number"), ("01K7SYNC0000000000000000D3", "issued_on")], Fields(r, Open));
        Assert.Null(Binding(r, Open).Provenance);
    }

    [Fact]
    public async Task Map_by_name_on_a_query_is_refused()
    {
        await using var r = EditorRepo.Create(packs: false);
        var files = r.Files();

        var refusal = Refused(await ApplyAsync(r, MapByName(RevenueMonth)));
        Assert.Contains("reads query", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(files, r.Files());

        // The preview answers the same refusal, and an entity with no binding to the database is refused too.
        var plan = await r.Store.PlanMaterializeAsync(new MaterializeRequest("map-by-name", Main, [RevenueMonth]), Ct);
        Assert.False(plan.Valid);
        Assert.Equal("MQ4089", Assert.Single(plan.Diagnostics).Rule);
        Assert.Contains("has no binding to database 'main'", Refused(await ApplyAsync(r, MapByName(EditorRepo.InvoiceId))).Message, StringComparison.Ordinal);
    }

    // ---- resync-binding ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Resync_renames_attributes_that_followed_their_column_and_keeps_ids()
    {
        await using var r = EditorRepo.Create(packs: false);
        await MappedLedgerAsync(r);
        await SaveAsync(r, Ledger, t => Column(t, AmountDueColumn)["name"] = "amount_owed");
        Assert.Contains(await DriftAsync(r, LedgerEntry), d => d.Rule == "MQ4087" && d.Message.Contains("'amount_owed' renamed (attribute 'amountDue')", StringComparison.Ordinal));

        var plan = await r.Store.PlanMaterializeAsync(new MaterializeRequest("resync-binding", Main, [LedgerEntry]), Ct);
        Assert.True(plan.Valid, string.Join("; ", plan.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Equal([LedgerEntry], plan.Updates.Select(u => u.Id));
        Assert.Contains(plan.Notes, n => n.Contains("is renamed 'amountOwed'", StringComparison.Ordinal));

        AssertSaved(await ApplyAsync(r, Resync(LedgerEntry)));

        var renamed = Attribute(r, LedgerEntry, AmountDueAttribute);
        Assert.Equal("amountOwed", renamed.Name);
        Assert.Contains((AmountDueAttribute, AmountDueColumn), Fields(r, LedgerEntry));
        Assert.Equal(Shapes(r), Binding(r, LedgerEntry).Provenance!.Columns);
        Assert.DoesNotContain(await DriftAsync(r, LedgerEntry), d => d.Rule == "MQ4087");
    }

    [Fact]
    public async Task Resync_keeps_a_name_that_never_followed_its_column()
    {
        await using var r = EditorRepo.Create(packs: false);
        // owed reads amount_due by hand; a re-sync from scratch records the provenance of every column.
        await LedgerAsync(r, string.Join(",", EntryId, $$"""{ "id": "{{AmountDueAttribute}}", "name": "owed", "type": "decimal", "precision": 12, "scale": 2, "required": true }"""),
            $$"""{ "attribute": "{{EntryIdAttribute}}", "column": "{{EntryIdColumn}}" }, { "attribute": "{{AmountDueAttribute}}", "column": "{{AmountDueColumn}}" }""");
        AssertSaved(await ApplyAsync(r, Resync(LedgerEntry)));
        Assert.Equal(Shapes(r), Binding(r, LedgerEntry).Provenance!.Columns);

        await SaveAsync(r, Ledger, t => Column(t, AmountDueColumn)["name"] = "amount_owed");
        AssertSaved(await ApplyAsync(r, Resync(LedgerEntry)));

        Assert.Equal("owed", Attribute(r, LedgerEntry, AmountDueAttribute).Name);
        Assert.Equal(Shapes(r)[AmountDueColumn], Binding(r, LedgerEntry).Provenance!.Columns[AmountDueColumn]);
        Assert.DoesNotContain(await DriftAsync(r, LedgerEntry), d => d.Rule == "MQ4087");
    }

    [Fact]
    public async Task Resync_with_keep_names_records_the_new_name()
    {
        await using var r = EditorRepo.Create(packs: false);
        await MappedLedgerAsync(r);
        var before = Binding(r, LedgerEntry).Provenance!.Columns[AmountDueColumn];
        await SaveAsync(r, Ledger, t => Column(t, AmountDueColumn)["name"] = "amount_owed");

        AssertSaved(await ApplyAsync(r, Resync(LedgerEntry, keepNames: true)));

        Assert.Equal("amountDue", Attribute(r, LedgerEntry, AmountDueAttribute).Name);
        var after = Binding(r, LedgerEntry).Provenance!.Columns[AmountDueColumn];
        Assert.Equal((ColumnShapeHash.NamePart("amount_owed"), ColumnShapeHash.TypePartOf(before)), (ColumnShapeHash.NamePartOf(after), ColumnShapeHash.TypePartOf(after)));
        Assert.DoesNotContain(await DriftAsync(r, LedgerEntry), d => d.Rule == "MQ4087");
    }

    [Fact]
    public async Task Resync_retype_keeps_a_ref_type_that_still_fits()
    {
        await using var r = EditorRepo.Create(packs: false);
        const string Flag = "01K7SYNC0000000000000000A7", FlagColumn = "01K7SYNC0000000000000000CA";
        await LedgerAsync(r, string.Join(",", EntryId, PostedOn, AmountDue, Memo, Status,
                $$"""{ "id": "{{Flag}}", "name": "flag", "type": { "ref": "{{InvoiceStatus}}" } }"""),
            extraColumns: $$""", { "id": "{{FlagColumn}}", "name": "flag", "type": "string", "length": 1 }""");
        AssertSaved(await ApplyAsync(r, MapByName(LedgerEntry)));
        await SaveAsync(r, Ledger, t =>
        {
            // status widens and becomes NOT NULL: InvoiceStatus's codes still fit; flag becomes a bool, which cannot hold them; amount_due widens.
            Column(t, StatusColumn)["length"] = 4;
            Column(t, StatusColumn)["nullable"] = false;
            Column(t, FlagColumn)["type"] = "bool";
            Column(t, FlagColumn).Remove("length");
            Column(t, AmountDueColumn)["precision"] = 14;
        });
        Assert.Contains(await DriftAsync(r, LedgerEntry), d => d.Rule == "MQ4087" && d.Message.Contains("'status' retyped", StringComparison.Ordinal));

        AssertSaved(await ApplyAsync(r, Resync(LedgerEntry)));

        var status = Attribute(r, LedgerEntry, StatusAttribute);
        Assert.Equal((InvoiceStatus, (int?)null, true), (status.Type.Ref, status.Length, status.Required));
        var flag = Attribute(r, LedgerEntry, Flag);
        Assert.Equal(("bool", false), (flag.Type.Builtin, flag.Required));
        var amount = Attribute(r, LedgerEntry, AmountDueAttribute);
        Assert.Equal(("decimal", 14, 2, true), (amount.Type.Builtin, amount.Precision, amount.Scale, amount.Required));
        Assert.Equal("amountDue", amount.Name);
        Assert.DoesNotContain(await DriftAsync(r, LedgerEntry), d => d.Rule == "MQ4087");
    }

    [Fact]
    public async Task Resync_adds_attributes_for_added_columns()
    {
        await using var r = EditorRepo.Create(packs: false);
        await MappedLedgerAsync(r);
        await SaveAsync(r, Ledger, t =>
        {
            var columns = (JsonArray)t["columns"]!;
            columns.Add(JsonNode.Parse("""{ "id": "01K7SYNC0000000000000000CB", "name": "due_on", "type": "date" }"""));
            columns.Add(JsonNode.Parse("""{ "id": "01K7SYNC0000000000000000CC", "name": "region", "type": "string", "length": 8, "nullable": false, "default": "eu" }"""));
        });
        // due_on is MQ4047's; region, which the database fills, is accounted for, yet both are added columns.
        var drift = await DriftAsync(r, LedgerEntry);
        Assert.Contains(drift, d => d.Rule == "MQ4047" && d.Message.Contains("'due_on'", StringComparison.Ordinal) && !d.Message.Contains("'region'", StringComparison.Ordinal));

        AssertSaved(await ApplyAsync(r, Resync(LedgerEntry)));

        var entity = Entity(r, LedgerEntry);
        var dueOn = entity.Attributes.Single(a => a.Name == "dueOn");
        var region = entity.Attributes.Single(a => a.Name == "region");
        Assert.Equal(("date", false), (dueOn.Type.Builtin, dueOn.Required));
        Assert.Equal(("string", 8, true), (region.Type.Builtin, region.Length, region.Required));
        Assert.Contains((dueOn.Id, "01K7SYNC0000000000000000CB"), Fields(r, LedgerEntry));
        Assert.Contains((region.Id, "01K7SYNC0000000000000000CC"), Fields(r, LedgerEntry));
        Assert.Equal(Shapes(r), Binding(r, LedgerEntry).Provenance!.Columns);
        Assert.Empty(await DriftAsync(r, LedgerEntry));
    }

    [Fact]
    public async Task Resync_removes_fields_of_removed_columns_and_keeps_attributes()
    {
        await using var r = EditorRepo.Create(packs: false);
        await MappedLedgerAsync(r);
        Assert.Contains((MemoAttribute, MemoColumn), Fields(r, LedgerEntry));
        await EditLedgerOnDiskAsync(r, columns => columns.Remove(columns.OfType<JsonObject>().Single(c => c["id"]!.GetValue<string>() == MemoColumn)));

        var result = await ApplyAsync(r, Resync(LedgerEntry));

        AssertSaved(result);
        Assert.DoesNotContain(Fields(r, LedgerEntry), f => f.Column == MemoColumn);
        Assert.Equal("memo", Attribute(r, LedgerEntry, MemoAttribute).Name);
        Assert.False(Binding(r, LedgerEntry).Provenance!.Columns.ContainsKey(MemoColumn));
        Assert.Equal(Shapes(r), Binding(r, LedgerEntry).Provenance!.Columns);
    }

    [Fact]
    public async Task Resync_after_a_column_is_dropped_and_re_added_moves_its_field_and_adds_no_attribute()
    {
        // A column dropped and re-added under its name has a new id: removed and added at once. The field of the removed column goes
        // before map by name, so the attribute it freed follows the new column, and no second attribute ('memo2') is added.
        const string NewMemoColumn = "01K7SYNC0000000000000000CD";
        await using var r = EditorRepo.Create(packs: false);
        await MappedLedgerAsync(r);
        var attributes = Entity(r, LedgerEntry).Attributes.Select(a => (a.Id, a.Name)).ToList();
        await EditLedgerOnDiskAsync(r, columns =>
        {
            columns.Remove(columns.OfType<JsonObject>().Single(c => c["id"]!.GetValue<string>() == MemoColumn));
            columns.Add(JsonNode.Parse($$"""{ "id": "{{NewMemoColumn}}", "name": "memo", "type": "text" }"""));
        });

        AssertSaved(await ApplyAsync(r, Resync(LedgerEntry)));

        Assert.Equal(attributes, Entity(r, LedgerEntry).Attributes.Select(a => (a.Id, a.Name)));
        var fields = Fields(r, LedgerEntry);
        Assert.Equal(NewMemoColumn, Assert.Single(fields, f => f.Attribute == MemoAttribute).Column);
        Assert.DoesNotContain(fields, f => f.Column == MemoColumn);
        Assert.Equal(Shapes(r), Binding(r, LedgerEntry).Provenance!.Columns);
        Assert.Empty(await DriftAsync(r, LedgerEntry));
    }

    [Fact]
    public async Task Resync_of_a_named_column_dropped_and_re_added_moves_its_field_and_adds_no_attribute()
    {
        // The same with the re-sync limited to the new column by name: the gone column was recorded under that name, so its field and
        // its provenance entry go, and the attribute follows the new column.
        const string NewMemoColumn = "01K7SYNC0000000000000000CD";
        await using var r = EditorRepo.Create(packs: false);
        await MappedLedgerAsync(r);
        var attributes = Entity(r, LedgerEntry).Attributes.Select(a => (a.Id, a.Name)).ToList();
        await EditLedgerOnDiskAsync(r, columns =>
        {
            columns.Remove(columns.OfType<JsonObject>().Single(c => c["id"]!.GetValue<string>() == MemoColumn));
            columns.Add(JsonNode.Parse($$"""{ "id": "{{NewMemoColumn}}", "name": "memo", "type": "text" }"""));
        });

        AssertSaved(await ApplyAsync(r, Resync(LedgerEntry, """["memo"]""")));

        Assert.Equal(attributes, Entity(r, LedgerEntry).Attributes.Select(a => (a.Id, a.Name)));
        var fields = Fields(r, LedgerEntry);
        Assert.Equal(NewMemoColumn, Assert.Single(fields, f => f.Attribute == MemoAttribute).Column);
        Assert.DoesNotContain(fields, f => f.Column == MemoColumn);
        Assert.Equal(Shapes(r), Binding(r, LedgerEntry).Provenance!.Columns);
        Assert.Empty(await DriftAsync(r, LedgerEntry));
    }

    [Fact]
    public async Task Resync_with_columns_keeps_other_entries()
    {
        await using var r = EditorRepo.Create(packs: false);
        await MappedLedgerAsync(r);
        var before = Binding(r, LedgerEntry).Provenance!.Columns;
        await SaveAsync(r, Ledger, t =>
        {
            Column(t, AmountDueColumn)["name"] = "amount_owed";
            Column(t, MemoColumn)["name"] = "memo_text";
        });

        // One column, by its physical name: only its attribute and its entry follow; memo keeps drifting.
        AssertSaved(await ApplyAsync(r, Resync(LedgerEntry, """["amount_owed"]""")));

        Assert.Equal("amountOwed", Attribute(r, LedgerEntry, AmountDueAttribute).Name);
        Assert.Equal("memo", Attribute(r, LedgerEntry, MemoAttribute).Name);
        var after = Binding(r, LedgerEntry).Provenance!.Columns;
        Assert.Equal(Shapes(r)[AmountDueColumn], after[AmountDueColumn]);
        Assert.Equal(before[MemoColumn], after[MemoColumn]);
        Assert.Contains(await DriftAsync(r, LedgerEntry), d => d.Rule == "MQ4087" && d.Message.Contains("'memo_text'", StringComparison.Ordinal)
            && !d.Message.Contains("amount_owed", StringComparison.Ordinal));

        // By key it works the same; a column the table lacks, or columns for two entities, are refused.
        AssertSaved(await ApplyAsync(r, Resync(LedgerEntry, $$"""["{{MemoColumn}}"]""")));
        Assert.Equal("memoText", Attribute(r, LedgerEntry, MemoAttribute).Name);
        Assert.Contains("is not a column of table 'ledger_entries'", Refused(await ApplyAsync(r, Resync(LedgerEntry, """["nope"]"""))).Message, StringComparison.Ordinal);
        Assert.Contains("one entity at a time", Refused(await ApplyAsync(r,
            $$"""{ "op": "resync-binding", "database": "{{Main}}", "entities": ["{{LedgerEntry}}", "01K6BND0000000000000000010"], "columns": ["memo_text"] }""")).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resync_notes_inherited_attributes()
    {
        await using var r = EditorRepo.Create(packs: false);
        // A stereotype gives LedgerEntry the attribute memo; the binding maps it like an own one.
        await CreateAsync(r, """
            { "kind": "stereotype", "id": "01K7SYNC0000000000000000S0", "key": "annotated", "name": "Annotated", "appliesTo": ["entity"],
              "attributes": [ { "id": "01K7SYNC0000000000000000S1", "name": "memo", "type": "text" } ] }
            """);
        await LedgerAsync(r, string.Join(",", EntryId, PostedOn, AmountDue, Status));
        await SaveAsync(r, LedgerEntry, e => e["stereotypes"] = new JsonArray("annotated"));
        AssertSaved(await ApplyAsync(r, MapByName(LedgerEntry)));
        Assert.Contains(("01K7SYNC0000000000000000S1", MemoColumn), Fields(r, LedgerEntry));
        await SaveAsync(r, Ledger, t => Column(t, MemoColumn)["name"] = "memo_text");

        var plan = await r.Store.PlanMaterializeAsync(new MaterializeRequest("resync-binding", Main, [LedgerEntry]), Ct);

        Assert.True(plan.Valid, string.Join("; ", plan.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Contains(plan.Notes, n => n.Contains("'memo'", StringComparison.Ordinal) && n.Contains("not an attribute of the entity's own", StringComparison.Ordinal));
        AssertSaved(await ApplyAsync(r, Resync(LedgerEntry)));
        Assert.Equal("memo", r.Store.Current!.Get<Stereotype>("01K7SYNC0000000000000000S0")!.Attributes.Single().Name);
        Assert.DoesNotContain(await DriftAsync(r, LedgerEntry), d => d.Rule == "MQ4087");
    }

    [Fact]
    public async Task Resync_of_bindings_in_step_is_refused()
    {
        await using var r = EditorRepo.Create(packs: false);
        await MappedLedgerAsync(r);
        var files = r.Files();

        var refusal = Refused(await ApplyAsync(r, Resync(LedgerEntry)));

        Assert.Contains("is in step", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(files, r.Files());
        // A source that is not a table file is refused as well.
        Assert.Contains("not a designed or imported table file", Refused(await ApplyAsync(r, Resync(RevenueMonth))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Binding_operations_and_their_plan_never_resolve_the_whole_model()
    {
        await using var r = EditorRepo.Create(packs: false);
        await LedgerAsync(r, string.Join(",", EntryId, PostedOn, AmountDue, Memo, Status));
        var services = EngineServices.Create(r.Repo.Options);
        var resolver = new CountingResolver(services.Resolver);
        await using var store = new ModelStore(r.Repo.Options, services with { Resolver = resolver });

        var plan = await store.PlanMaterializeAsync(new MaterializeRequest("map-by-name", Main, [LedgerEntry]), Ct);
        Assert.True(plan.Valid, string.Join("; ", plan.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + MapByName(LedgerEntry) + "]}"));
        var preview = await store.PreviewBatchAsync(parsed.Batch!, Ct);
        Assert.Equal(SaveOutcome.Saved, preview.Outcome);
        AssertSaved(await store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct));

        var document = (await store.GetElementAsync(Ledger, Ct))!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        Column(node, AmountDueColumn)["name"] = "amount_owed";
        Assert.Equal(SaveOutcome.Saved, (await store.SaveAsync(Ledger, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct)).Outcome);
        Assert.True((await store.PlanMaterializeAsync(new MaterializeRequest("resync-binding", Main, [LedgerEntry]), Ct)).Valid);
        var resync = store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + Resync(LedgerEntry) + "]}"));
        AssertSaved(await store.ApplyBatchAsync(resync.Batch!, ChangeSource.Editor, Ct));

        Assert.Equal(0, resolver.WholeModel);
        Assert.True(resolver.Databases > 0);
    }

    /// <summary>Counts whole-model resolves apart from resolutions of some databases (the scope fact recording and binding plans use).</summary>
    private sealed class CountingResolver(IModelResolver inner) : IModelResolver, IDatabaseResolver
    {
        public int WholeModel;
        public int Databases;

        public Task<ResolvedModel> ResolveAsync(ModelSnapshot model, IProgress<ProgressUpdate>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref WholeModel);
            return inner.ResolveAsync(model, progress, ct);
        }

        public Task<(IReadOnlyList<RDatabase?> Databases, IReadOnlyList<Diagnostic> Diagnostics)> ResolveDatabasesAsync(ModelSnapshot model,
            IReadOnlyList<string> databaseIds, bool freeze, CancellationToken ct)
        {
            Interlocked.Increment(ref Databases);
            return ((IDatabaseResolver)inner).ResolveDatabasesAsync(model, databaseIds, freeze, ct);
        }
    }
}
