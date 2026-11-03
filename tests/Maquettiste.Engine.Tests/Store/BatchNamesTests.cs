using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>Batches by name: the resolver turns qualified names and placeholders into ids (next-phase-1-design.md section 5.1).</summary>
public sealed class BatchNamesTests
{
    private const string BillingPackage = "01J92P0V01KDRN8GX5PGYCNKSX";
    private const string CatalogPackage = "01J92P0V025DRFTXKMS240G2NG";
    private const string Main = "01J92P0V1QRN2181XM2ZWE02W4";
    private const string BillingSchema = "01J92P0V1RC04SKQ5353EAKHG2";
    private const string Notes = "01K6BND0000000000000000001";
    private const string EntityType = "01K6BND0000000000000000003";
    private const string EntityId = "01K6BND0000000000000000004";
    private const string Product = "01J92P0V0JR8BE8253SKT29ZG7";
    private const string Customer = "01J92P0V0ETQKXXP951CMMNHH3";
    private const string Invoice = "01J92P0V0FJ23CGSNKM7P1W5V7";
    private const string InvoiceNumber = "01J92P0V0R3VSP7D5238DTNZX1";
    private const string InvoicesByCustomer = "01K6QRY0000000000000000001";

    /// <summary>The acceptance batch: a table, a reference type bound to it and its seed, by name and placeholder only.</summary>
    private const string PaymentMethods = """
        { "operations": [
          { "op": "create", "as": "$pm", "element": { "kind": "table", "name": "payment_method", "database": "@database:main", "schema": "@schema:billing",
              "columns": [ { "name": "code", "type": "string", "length": 16, "nullable": false }, { "name": "label", "type": "string", "length": 64, "nullable": false } ],
              "primaryKey": { "columns": [ "@column:billing.payment_method.code" ] } } },
          { "op": "create", "as": "$type", "element": { "kind": "reference-type", "name": "PaymentMethod", "code": { "length": 16 }, "label": { "length": 64 },
              "bindings": [ { "database": "@database:main", "table": "$pm", "fields": [ { "attribute": "code", "column": "code" }, { "attribute": "label", "column": "label" } ] } ] } },
          { "op": "create", "element": { "kind": "seed", "name": "PaymentMethods", "target": "$type", "columns": [ "code", "label" ],
              "rows": [ { "values": [ "card", "Card" ] }, { "values": [ "transfer", "Bank transfer" ] } ] } }
        ] }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ModelBatch Parse(BillingStore s, string json)
    {
        var parsed = s.Store.ParseBatch(Encoding.UTF8.GetBytes(json));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}")));
        return parsed.Batch;
    }

    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Explain(BatchResult result) =>
        string.Join("\n", result.Items.SelectMany(i => i.Diagnostics).Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}"));

    private static List<(string Rule, string? Pointer)> Failures(BatchResult result) =>
        [.. result.Items.SelectMany(i => i.Diagnostics).Select(d => (d.Rule, d.JsonPointer)).Order()];

    private static IEnumerable<string> Strings(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().SelectMany(p => Strings(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().SelectMany(Strings),
        JsonValueKind.String => [value.GetString()!],
        _ => [],
    };

    private static List<string> Errors(ValidationReport report) =>
        [.. report.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Rule} {d.ElementId} {d.JsonPointer}").Order(StringComparer.Ordinal)];

    private static string Edited(ElementDocument document, Action<JsonObject> change) => Encoding.UTF8.GetString(BillingStore.Edit(document, change));

    [Fact]
    public async Task An_agent_creates_a_table_its_columns_a_reference_type_and_a_seed_in_one_batch_without_writing_an_id()
    {
        await using var first = await BillingStore.OpenAsync(validate: true);
        await using var second = await BillingStore.OpenAsync(validate: true);
        var validator = EngineServices.Create(first.Harness.Options).Validator;
        var errorsBefore = Errors(await validator.ValidateAsync(first.Model, ValidationScope.All, null, Ct));

        var result = await first.Store.ApplyBatchAsync(Parse(first, PaymentMethods), ChangeSource.Cli, Ct);
        var again = await second.Store.ApplyBatchAsync(Parse(second, PaymentMethods), ChangeSource.Cli, Ct);

        Assert.True(result.Outcome == SaveOutcome.Saved, Explain(result));
        Assert.True(again.Outcome == SaveOutcome.Saved, Explain(again));
        var table = first.Model.Get<Table>(result.Items[0].Id!)!;
        var type = first.Model.Get<ReferenceType>(result.Items[1].Id!)!;
        var seed = first.Model.Get<Seed>(result.Items[2].Id!)!;
        Assert.Equal((Main, BillingSchema), (table.Database, table.Schema));
        Assert.Equal([table.Columns[0].Id], table.PrimaryKey!.Columns);
        var binding = Assert.Single(type.Bindings);
        Assert.Equal((Main, table.Id), (binding.Database, binding.Table));
        Assert.Equal(type.Id, seed.Target);
        foreach (var item in result.Items)
            Assert.DoesNotContain(Strings(item.Current!.Json), v => v.StartsWith('@') || v.StartsWith('$'));

        Assert.Equal(10, result.Ids.Minted.Count);
        Assert.Equal(
            [
                (0, "", "table", table.Id, "@table:main.billing.payment_method", "$pm"),
                (0, "/columns/0", "column", table.Columns[0].Id, "@column:main.billing.payment_method.code", null),
                (0, "/columns/1", "column", table.Columns[1].Id, "@column:main.billing.payment_method.label", null),
                (1, "", "reference-type", type.Id, "@reference-type:PaymentMethod", "$type"),
                (1, "/code", "reference-field", type.Code.Id, "@reference-field:PaymentMethod.code", null),
                (1, "/label", "reference-field", type.Label.Id, "@reference-field:PaymentMethod.label", null),
                (1, "/bindings/0", "binding", binding.Id, "@binding:PaymentMethod.main", null),
                (2, "", "seed", seed.Id, "@seed:PaymentMethods", null),
                (2, "/rows/0", "row", seed.Rows[0].Id, "@row:PaymentMethods.card", null),
                (2, "/rows/1", "row", seed.Rows[1].Id, "@row:PaymentMethods.transfer", (string?)null),
            ],
            result.Ids.Minted.Select(m => (m.Operation, m.Pointer, m.Kind, m.Id, m.Name, m.Placeholder)));
        Assert.Equal(
            [
                ("$pm", table.Id), ("$type", type.Id), ("@column:billing.payment_method.code", table.Columns[0].Id), ("@database:main", Main),
                ("@schema:billing", BillingSchema),
            ],
            result.Ids.Names.Select(kv => (kv.Key, kv.Value)));
        Assert.Equal(errorsBefore, Errors(await validator.ValidateAsync(first.Model, ValidationScope.All, null, Ct)));
        Assert.Equal(first.Files(), second.Files());
    }

    [Fact]
    public async Task A_batch_without_names_still_reports_the_longest_forms_of_the_ids_it_mints()
    {
        await using var s = await BillingStore.OpenAsync();
        const string Archive = "01K6BNM0000000000000000001";
        var notes = s.Model.GetDocument(Notes)!;
        var element = Edited(notes, n => n["columns"]!.AsArray().Add(JsonNode.Parse("""{ "name": "author", "type": "string", "length": 64 }""")));

        // Every reference is an id and nothing declares a placeholder, so pass 2 is skipped; the new table's form reads the database
        // created by the operation before it.
        var result = await s.Store.ApplyBatchAsync(Parse(s, $$"""
            { "operations": [
              { "op": "create", "element": { "kind": "database", "id": "{{Archive}}", "name": "archive", "dialect": "postgresql", "version": "16",
                  "defaultSchema": "archive", "schemas": [ { "name": "archive" } ], "byConvention": "none" } },
              { "op": "create", "element": { "kind": "table", "name": "payment_method", "database": "{{Archive}}",
                  "columns": [ { "name": "code", "type": "string", "length": 16, "nullable": false } ] } },
              { "op": "update", "id": "{{Notes}}", "expectedHash": "{{notes.Hash}}", "element": {{element}} }
            ] }
            """), ChangeSource.Cli, Ct);

        Assert.True(result.Outcome == SaveOutcome.Saved, Explain(result));
        var database = s.Model.Get<Database>(Archive)!;
        var table = s.Model.Get<Table>(result.Items[1].Id!)!;
        var author = s.Model.Get<Table>(Notes)!.Columns.Single(c => c.Name == "author");
        Assert.Empty(result.Ids.Names);
        Assert.Equal(
            [
                (0, "/schemas/0", "schema", database.Schemas[0].Id, "@schema:archive.archive"),
                (1, "", "table", table.Id, "@table:archive.archive.payment_method"),
                (1, "/columns/0", "column", table.Columns[0].Id, "@column:archive.archive.payment_method.code"),
                (2, "/columns/5", "column", author.Id, (string?)"@column:main.billing.notes.author"),
            ],
            result.Ids.Minted.Select(m => (m.Operation, m.Pointer, m.Kind, m.Id, m.Name)));
    }

    [Fact]
    public async Task Names_see_the_earlier_operations_and_the_operation_itself()
    {
        await using var s = await BillingStore.OpenAsync();

        var result = await s.Store.ApplyBatchAsync(Parse(s, """
            { "operations": [
              { "op": "create", "element": { "kind": "enum", "name": "RefundReason", "package": "@package:Billing", "members": [ { "name": "Damaged" }, { "name": "Late" } ] } },
              { "op": "create", "element": { "kind": "entity", "name": "Refund", "package": "@package:Billing",
                  "key": { "attributes": [ "@attribute:Refund.id" ], "strategy": "uuid-v7" },
                  "attributes": [ { "name": "id", "type": "uuid", "required": true }, { "name": "reason", "type": { "ref": "@enum:Billing.RefundReason" } } ] } }
            ] }
            """), ChangeSource.Cli, Ct);

        Assert.True(result.Outcome == SaveOutcome.Saved, Explain(result));
        var reason = s.Model.Get<EnumType>(result.Items[0].Id!)!;
        var refund = s.Model.Get<Entity>(result.Items[1].Id!)!;
        Assert.Equal(BillingPackage, reason.Package);
        Assert.Equal(BillingPackage, refund.Package);
        Assert.Equal([refund.Attributes[0].Id], refund.Key!.Attributes);
        Assert.Equal(reason.Id, refund.Attributes[1].Type.Ref);
        Assert.Equal(reason.Id, result.Ids.Names["@enum:Billing.RefundReason"]);
        Assert.Equal(refund.Attributes[0].Id, result.Ids.Names["@attribute:Refund.id"]);
    }

    [Fact]
    public async Task Update_and_delete_targets_resolve_on_the_model_as_loaded()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Model.GetDocument(Customer)!;
        var query = s.Model.GetDocument(InvoicesByCustomer)!;

        // Operation 0 adds a second Customer; the update's target still names the one the model had.
        var result = await s.Store.ApplyBatchAsync(Parse(s, $$"""
            { "operations": [
              { "op": "create", "element": { "kind": "entity", "name": "Customer", "package": "@package:Catalog", "attributes": [ { "name": "id", "type": "uuid" } ] } },
              { "op": "update", "id": "@entity:Customer", "expectedHash": "{{customer.Hash}}", "element": {{Edited(customer, c => c["displayName"] = "Client")}} },
              { "op": "delete", "id": "@query:InvoicesByCustomer", "expectedHash": "{{query.Hash}}" }
            ] }
            """), ChangeSource.Cli, Ct);

        Assert.True(result.Outcome == SaveOutcome.Saved, Explain(result));
        Assert.Equal([Customer, InvoicesByCustomer], result.Items.Skip(1).Select(i => i.Id));
        Assert.Equal("Client", s.Model.Get<Entity>(Customer)!.DisplayName);
        Assert.Equal(CatalogPackage, s.Model.Get<Entity>(result.Items[0].Id!)!.Package);
        Assert.Null(s.Model.GetDocument(InvoicesByCustomer));
        Assert.Equal(Customer, result.Ids.Names["@entity:Customer"]);
        Assert.Equal(InvoicesByCustomer, result.Ids.Names["@query:InvoicesByCustomer"]);
    }

    [Fact]
    public async Task A_name_inside_the_operations_own_element_sees_its_new_document()
    {
        await using var s = await BillingStore.OpenAsync();
        var notes = s.Model.GetDocument(Notes)!;
        var element = Edited(notes, n =>
        {
            // The two columns swap names and keep their ids.
            var columns = n["columns"]!.AsArray();
            columns[1]!["name"] = "entity_id";
            columns[2]!["name"] = "entity_type";
            n["indexes"]!.AsArray().Add(JsonNode.Parse("""{ "columns": [ { "column": "@column:billing.notes.entity_type" } ] }"""));
        });

        var result = await s.Store.ApplyBatchAsync(Parse(s, $$"""
            { "operations": [ { "op": "update", "id": "@table:billing.notes", "expectedHash": "{{notes.Hash}}", "element": {{element}} } ] }
            """), ChangeSource.Cli, Ct);

        Assert.True(result.Outcome == SaveOutcome.Saved, Explain(result));
        var table = s.Model.Get<Table>(Notes)!;
        Assert.Equal(("entity_id", "entity_type"), (table.Columns.Single(c => c.Id == EntityType).Name, table.Columns.Single(c => c.Id == EntityId).Name));
        Assert.Equal(2, table.Indexes.Count);
        Assert.Equal(EntityId, Assert.Single(table.Indexes[1].Columns).Column);
        Assert.Equal(EntityId, result.Ids.Names["@column:billing.notes.entity_type"]);
        Assert.Equal("/indexes/1", Assert.Single(result.Ids.Minted).Pointer);
    }

    [Fact]
    public async Task A_rename_earlier_in_the_batch_does_not_requalify_untouched_elements()
    {
        await using var s = await BillingStore.OpenAsync();
        var catalog = s.Model.GetDocument(CatalogPackage)!;
        var renamed = Edited(catalog, p => p["name"] = "Catalog2");
        string Batch(string entity) => $$"""
            { "operations": [
              { "op": "update", "id": "@package:Billing.Catalog", "expectedHash": "{{catalog.Hash}}", "element": {{renamed}} },
              { "op": "create", "element": { "kind": "mapping", "name": "product-in-main", "package": "@package:Billing.Catalog2", "entity": "{{entity}}",
                  "database": "@database:main" } }
            ] }
            """;
        var files = s.Files();

        var stale = await s.Store.ApplyBatchAsync(Parse(s, Batch("@entity:Catalog2.Product")), ChangeSource.Cli, Ct);

        Assert.Equal(SaveOutcome.Invalid, stale.Outcome);
        Assert.Equal([("MQ1012", "/operations/1/element/entity")], Failures(stale));
        Assert.Equal(files, s.Files());

        var result = await s.Store.ApplyBatchAsync(Parse(s, Batch("@entity:Catalog.Product")), ChangeSource.Cli, Ct);

        Assert.True(result.Outcome == SaveOutcome.Saved, Explain(result));
        var mapping = s.Model.Get<Mapping>(result.Items[1].Id!)!;
        Assert.Equal((Product, CatalogPackage, Main), (mapping.Entity, mapping.Package, mapping.Database));
        Assert.Equal("Catalog2", s.Model.Get<Package>(CatalogPackage)!.Name);
    }

    [Fact]
    public async Task Placeholders_reach_forward_so_two_new_tables_reference_each_other()
    {
        await using var s = await BillingStore.OpenAsync();

        var result = await s.Store.ApplyBatchAsync(Parse(s, """
            { "operations": [
              { "op": "create", "as": "$orders", "element": { "kind": "table", "name": "orders", "database": "@database:main", "schema": "@schema:billing",
                  "columns": [ { "as": "$orders_id", "name": "id", "type": "uuid", "nullable": false }, { "as": "$orders_shipment", "name": "last_shipment_id", "type": "uuid" } ],
                  "primaryKey": { "columns": [ "$orders_id" ] },
                  "foreignKeys": [ { "columns": [ "$orders_shipment" ], "referencesTable": "$shipments", "referencesColumns": [ "$shipments_id" ] } ] } },
              { "op": "create", "as": "$shipments", "element": { "kind": "table", "name": "shipments", "database": "@database:main", "schema": "@schema:billing",
                  "columns": [ { "as": "$shipments_id", "name": "id", "type": "uuid", "nullable": false }, { "as": "$shipments_order", "name": "order_id", "type": "uuid" } ],
                  "primaryKey": { "columns": [ "$shipments_id" ] },
                  "foreignKeys": [ { "columns": [ "$shipments_order" ], "referencesTable": "$orders", "referencesColumns": [ "$orders_id" ] } ] } }
            ] }
            """), ChangeSource.Cli, Ct);

        Assert.True(result.Outcome == SaveOutcome.Saved, Explain(result));
        var orders = s.Model.Get<Table>(result.Items[0].Id!)!;
        var shipments = s.Model.Get<Table>(result.Items[1].Id!)!;
        var toShipments = Assert.Single(orders.ForeignKeys);
        var toOrders = Assert.Single(shipments.ForeignKeys);
        Assert.Equal((orders.Columns[1].Id, shipments.Id, shipments.Columns[0].Id),
            (Assert.Single(toShipments.Columns), toShipments.ReferencesTable, Assert.Single(toShipments.ReferencesColumns)));
        Assert.Equal((shipments.Columns[1].Id, orders.Id, orders.Columns[0].Id),
            (Assert.Single(toOrders.Columns), toOrders.ReferencesTable, Assert.Single(toOrders.ReferencesColumns)));
        Assert.Equal(
            ["$orders", "$orders_id", "$orders_shipment", "$shipments", "$shipments_id", "$shipments_order", "@database:main", "@schema:billing"],
            result.Ids.Names.Keys);
        Assert.Equal(orders.Columns[1].Id, result.Ids.Names["$orders_shipment"]);
        Assert.Equal("$orders_shipment", result.Ids.Minted.Single(m => m.Id == orders.Columns[1].Id).Placeholder);
        Assert.DoesNotContain("\"as\"", result.Items[0].Current!.Json.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attributes_items_and_expected_hashes_keys_take_names()
    {
        await using var s = await BillingStore.OpenAsync();
        const string Composite = InvoiceNumber + ".01J92P0V0000000000000000AB";
        var batch = new ModelBatch(
        [
            new BatchOperation(BatchOp.MaterializeColumns, null, null, null, Database: "@database:main", Entities: ["@entity:Invoice"],
                Attributes: ["@attribute:Invoice.number", Composite], ExpectedHashes: new Dictionary<string, string> { ["@entity:Invoice"] = "a", [Notes] = "b" }),
        ]);

        var resolved = BatchNames.Resolve(s.Model, batch, [null], TestServices.Schemas, new SequentialIdGenerator());

        Assert.False(resolved.Failed, string.Join("; ", resolved.Diagnostics.SelectMany(d => d).Select(d => d.Message)));
        var o = Assert.Single(resolved.Batch.Operations);
        Assert.Equal(Main, o.Database);
        Assert.Equal([Invoice], o.Entities!);
        Assert.Equal([InvoiceNumber, Composite], o.Attributes!);
        Assert.Equal([(Invoice, "a"), (Notes, "b")], o.ExpectedHashes!.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)));
        Assert.Equal(
            ["@attribute:Invoice.number", "@database:main", "@entity:Invoice"],
            resolved.Ids.Names.Keys);

        // Two keys for one element: MQ1014, at the second key.
        var twice = BatchNames.Resolve(s.Model, new ModelBatch(
        [
            new BatchOperation(BatchOp.MaterializeColumns, null, null, null, Database: Main, Entities: [Invoice],
                ExpectedHashes: new Dictionary<string, string> { ["@entity:Invoice"] = "a", [Invoice] = "b" }),
        ]), [null], TestServices.Schemas, new SequentialIdGenerator());

        var duplicate = Assert.Single(twice.Diagnostics[0]);
        Assert.Equal(("MQ1014", "/operations/0/expectedHashes/@entity:Invoice"), (duplicate.Rule, duplicate.JsonPointer));
    }

    [Fact]
    public async Task An_unknown_name_is_MQ1012_and_nothing_is_written()
    {
        await using var s = await BillingStore.OpenAsync();
        var files = s.Files();
        s.Notifications.Clear();

        var result = await s.Store.ApplyBatchAsync(Parse(s, """
            { "operations": [
              { "op": "create", "element": { "kind": "table", "name": "refunds", "database": "@database:archive", "schema": "@schema:billing",
                  "columns": [ { "name": "id", "type": "uuid" } ] } },
              { "op": "create", "element": { "kind": "entity", "name": "Refund", "package": "$nowhere", "attributes": [ { "name": "id", "type": "uuid" } ] } },
              { "op": "create", "element": { "kind": "entity", "name": "Credit", "package": "@package:Billing", "attributes": [ { "name": "id", "type": "uuid" } ] } }
            ] }
            """), ChangeSource.Cli, Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        Assert.Equal([("MQ1012", "/operations/0/element/database"), ("MQ1012", "/operations/1/element/package")], Failures(result));
        Assert.Equal([SaveOutcome.Invalid, SaveOutcome.Invalid, SaveOutcome.Saved], result.Items.Select(i => i.Outcome));
        Assert.Null(result.Items[2].Hash);
        Assert.Same(BatchIds.Empty, result.Ids);
        Assert.Contains("no operation declares the placeholder $nowhere", result.Items[1].Diagnostics[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(files, s.Files());
        Assert.Empty(s.Notifications);
    }

    [Fact]
    public async Task An_ambiguous_name_is_MQ1013_listing_the_candidates()
    {
        await using var s = await BillingStore.OpenAsync();
        var other = await s.Store.CreateAsync(Encoding.UTF8.GetBytes($$"""
            { "kind": "entity", "name": "Product", "package": "{{BillingPackage}}", "attributes": [ { "name": "id", "type": "uuid" } ] }
            """), ChangeSource.Cli, Ct);
        Assert.Equal(SaveOutcome.Saved, other.Outcome);
        string Batch(string entity) => $$"""
            { "operations": [ { "op": "create", "element": { "kind": "mapping", "name": "product-in-main", "entity": "{{entity}}", "database": "@database:main" } } ] }
            """;

        var ambiguous = await s.Store.ApplyBatchAsync(Parse(s, Batch("@entity:Product")), ChangeSource.Cli, Ct);

        Assert.Equal(SaveOutcome.Invalid, ambiguous.Outcome);
        var diagnostic = Assert.Single(ambiguous.Items[0].Diagnostics);
        Assert.Equal(("MQ1013", "/operations/0/element/entity"), (diagnostic.Rule, diagnostic.JsonPointer));
        Assert.Contains($"@entity:Billing.Catalog.Product ({Product}), @entity:Billing.Product ({other.Id})", diagnostic.Message, StringComparison.Ordinal);

        var result = await s.Store.ApplyBatchAsync(Parse(s, Batch("@entity:Catalog.Product")), ChangeSource.Cli, Ct);

        Assert.True(result.Outcome == SaveOutcome.Saved, Explain(result));
        Assert.Equal(Product, s.Model.Get<Mapping>(result.Items[0].Id!)!.Entity);
    }

    [Fact]
    public async Task A_placeholder_declared_twice_or_misplaced_as_is_MQ1014()
    {
        await using var s = await BillingStore.OpenAsync();
        var query = s.Model.GetDocument(InvoicesByCustomer)!;
        var files = s.Files();

        var result = await s.Store.ApplyBatchAsync(new ModelBatch(
        [
            new BatchOperation(BatchOp.Create, null, null, Element("""{ "kind": "entity", "name": "Refund", "attributes": [ { "name": "id", "type": "uuid" } ] }"""), As: "$x"),
            new BatchOperation(BatchOp.Create, null, null, Element("""{ "kind": "entity", "name": "Credit", "attributes": [ { "name": "id", "type": "uuid" } ] }"""), As: "$x"),
            new BatchOperation(BatchOp.Delete, InvoicesByCustomer, query.Hash, null, As: "$q"),
            new BatchOperation(BatchOp.Create, null, null, Element($$"""
                { "kind": "table", "name": "refunds", "database": "{{Main}}", "schema": "{{BillingSchema}}", "columns": [ { "name": "id", "type": "uuid" } ],
                  "primaryKey": { "as": "$pk", "columns": [ "x" ] } }
                """)),
            new BatchOperation(BatchOp.AddSchema, Main, null, null, Name: "archive", As: "$s"),
            new BatchOperation(BatchOp.Create, null, null, Element("""{ "kind": "entity", "name": "Debit", "as": "$debit", "attributes": [ { "name": "id", "type": "uuid" } ] }""")),
        ]), ChangeSource.Cli, Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        Assert.Equal(
            [
                ("MQ1014", "/operations/1/as"), ("MQ1014", "/operations/2/as"), ("MQ1014", "/operations/3/element/primaryKey/as"), ("MQ1014", "/operations/4/as"),
                ("MQ1014", "/operations/5/element/as"),
            ],
            Failures(result));
        Assert.Equal(files, s.Files());
    }

    [Fact]
    public async Task A_malformed_name_a_named_create_id_or_a_placeholder_target_is_MQ1015()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Model.GetDocument(Customer)!;
        var files = s.Files();

        var result = await s.Store.ApplyBatchAsync(new ModelBatch(
        [
            new BatchOperation(BatchOp.Create, null, null, Element("""{ "kind": "mapping", "name": "m", "database": "@Database:main" }""")),
            new BatchOperation(BatchOp.Create, "@entity:Refund", null, Element("""{ "kind": "entity", "name": "Refund", "attributes": [ { "name": "id", "type": "uuid" } ] }""")),
            new BatchOperation(BatchOp.Update, "$customer", customer.Hash, Element(customer.Json.GetRawText())),
            new BatchOperation(BatchOp.MaterializeColumns, null, null, null, Database: Main, Entities: [Invoice], ExpectedHashes: new Dictionary<string, string> { ["$x"] = "a" }),
            new BatchOperation(BatchOp.Create, null, null, Element("""{ "kind": "entity", "name": "Credit", "package": "@migration:x", "attributes": [ { "name": "id", "type": "uuid" } ] }""")),
            new BatchOperation(BatchOp.Create, null, null, Element("""{ "kind": "entity", "name": "Debit", "package": "@entity:\"Billing", "attributes": [ { "name": "id", "type": "uuid" } ] }""")),
        ]), ChangeSource.Cli, Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        Assert.Equal(
            [
                ("MQ1015", "/operations/0/element/database"), ("MQ1015", "/operations/1/id"), ("MQ1015", "/operations/2/id"),
                ("MQ1015", "/operations/3/expectedHashes/$x"), ("MQ1015", "/operations/4/element/package"), ("MQ1015", "/operations/5/element/package"),
            ],
            Failures(result));
        Assert.Contains("has no names", result.Items[4].Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Contains("not closed", result.Items[5].Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Equal(files, s.Files());
    }

    [Fact]
    public async Task Text_and_seed_cells_that_look_like_names_are_left_alone()
    {
        await using var s = await BillingStore.OpenAsync();

        var result = await s.Store.ApplyBatchAsync(Parse(s, """
            { "operations": [
              { "op": "create", "as": "$type", "element": { "kind": "reference-type", "name": "Channel", "displayName": "$channel",
                  "description": "Pick @entity:Invoice or $pm; see @reference-type:Channel.", "code": { "length": 16 }, "label": { "length": 64 } } },
              { "op": "create", "element": { "kind": "seed", "name": "Channels", "target": "$type", "columns": [ "code", "label" ],
                  "rows": [ { "values": [ "web", "$pm" ] }, { "values": [ "desk", "@entity:Invoice" ] } ] } }
            ] }
            """), ChangeSource.Cli, Ct);

        Assert.True(result.Outcome == SaveOutcome.Saved, Explain(result));
        var type = s.Model.Get<ReferenceType>(result.Items[0].Id!)!;
        var seed = s.Model.Get<Seed>(result.Items[1].Id!)!;
        Assert.Equal("$channel", type.DisplayName);
        Assert.Contains("Pick @entity:Invoice or $pm; see @reference-type:Channel.", result.Items[0].Current!.Json.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(type.Id, seed.Target);
        Assert.Equal(["$pm", "@entity:Invoice"], seed.Rows.Select(r => r.Values[1].GetString()));
        Assert.Equal(["$type"], result.Ids.Names.Keys);
    }
}
