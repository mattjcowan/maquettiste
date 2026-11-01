using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>
/// Seed input by name (next-phase-1-design.md section 5.3): a seed body's columns, key and row cells given by name are stored as ids
/// and positional cells, through a create, a save and a batch; what cannot be resolved is MQ1002 and nothing is written.
/// </summary>
public sealed class SeedInputTests
{
    private const string Main = "01J92P0V1QRN2181XM2ZWE02W4";
    private const string Invoice = "01J92P0V0FJ23CGSNKM7P1W5V7";
    private const string InvoiceNumber = "01J92P0V0R3VSP7D5238DTNZX1";
    private const string InvoiceIssuedOn = "01J92P0V0SNXS6PZ42VDK42HP9";
    private const string Places = "01J92P0V1ACKN3G6TK3NJDTM82";
    private const string PlacesCustomer = "01J92P0V1EHF7PB28CZJG9C5SN";
    private const string PlacesInvoices = "01J92P0V1F439P4NH5F6HV5QM1";
    private const string Currency = "01K7SDN0000000000000000001";
    private const string CurrencyCode = "01K7SDN0000000000000000002";
    private const string CurrencyLabel = "01K7SDN0000000000000000003";
    private const string CurrencyLabelKey = "01K7SDN0000000000000000004";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<SaveResult> CreateAsync(BillingStore s, string json) =>
        await s.Store.CreateAsync(Encoding.UTF8.GetBytes(JsonNode.Parse(json)!.ToJsonString()), ChangeSource.Editor, Ct);

    private static void AssertSaved(SaveResult result) =>
        Assert.True(result.Outcome == SaveOutcome.Saved, result.Outcome + ": " + string.Join("; ", result.Diagnostics.Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}")));

    private static async Task<BillingStore> WithCurrencyAsync()
    {
        var s = await BillingStore.OpenAsync();
        AssertSaved(await CreateAsync(s, $$"""
            {"kind":"table","id":"{{Currency}}","name":"currency","database":"{{Main}}","columns":[
              {"id":"{{CurrencyCode}}","name":"code","type":"string","length":3,"nullable":false},
              {"id":"{{CurrencyLabel}}","name":"label","type":"string","length":40,"nullable":false}],
             "primaryKey":{"columns":["{{CurrencyCode}}"]},
             "uniques":[{"id":"{{CurrencyLabelKey}}","name":"uq_currency_label","columns":["{{CurrencyLabel}}"]}]}
            """));
        return s;
    }

    private static Seed SeedOf(BillingStore s, string id) => (Seed)s.Model.GetDocument(id)!.Element;

    private static List<string> Cells(SeedRow row) => [.. row.Values.Select(v => v.ValueKind == JsonValueKind.Null ? "null" : v.ToString())];

    private static ModelBatch Parse(BillingStore s, string json)
    {
        var parsed = s.Store.ParseBatch(Encoding.UTF8.GetBytes(json));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}")));
        return parsed.Batch;
    }

    [Fact]
    public async Task Seed_columns_given_by_name_are_stored_as_ids()
    {
        await using var s = await BillingStore.OpenAsync();

        // An entity's attributes and the role of its to-one end; a relation's end roles.
        var invoices = await CreateAsync(s, $$"""
            {"kind":"seed","name":"demoInvoices","target":"{{Invoice}}","columns":["number","issuedOn","customer"],"rows":[]}
            """);
        AssertSaved(invoices);
        Assert.Equal([InvoiceNumber, InvoiceIssuedOn, PlacesCustomer], SeedOf(s, invoices.Id!).Columns);
        var links = await CreateAsync(s, $$"""
            {"kind":"seed","name":"demoPlaces","target":"{{Places}}","columns":["invoices","customer"],"rows":[]}
            """);
        AssertSaved(links);
        Assert.Equal([PlacesInvoices, PlacesCustomer], SeedOf(s, links.Id!).Columns);

        // A save takes names too, and ids stay as they are.
        var document = s.Model.GetDocument(invoices.Id!)!;
        var saved = await s.Store.SaveAsync(invoices.Id!, BillingStore.Edit(document, n => n["columns"] = new JsonArray(InvoiceNumber, "customer")),
            document.Hash, ChangeSource.Editor, Ct);
        AssertSaved(saved);
        Assert.Equal([InvoiceNumber, PlacesCustomer], SeedOf(s, invoices.Id!).Columns);
        Assert.DoesNotContain("\"customer\"", File.ReadAllText(s.Harness.Repo.PathOf(s.Model.GetDocument(invoices.Id!)!.Path)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_table_column_named_code_is_stored_as_its_id()
    {
        await using var s = await WithCurrencyAsync();
        var result = await CreateAsync(s, $$"""
            {"kind":"seed","name":"currencies","target":"{{Currency}}","columns":["code","label"],"rows":[{"values":["EUR","Euro"]}]}
            """);
        AssertSaved(result);
        Assert.Equal([CurrencyCode, CurrencyLabel], SeedOf(s, result.Id!).Columns);
    }

    [Fact]
    public async Task Row_values_keyed_by_column_name_are_stored_in_column_order()
    {
        await using var s = await WithCurrencyAsync();
        var result = await CreateAsync(s, $$"""
            {"kind":"seed","name":"currencies","target":"{{Currency}}","columns":["code","label"],
             "rows":[{"values":{"label":"Euro","code":"EUR"} },{"values":{"code":"USD"} },{"values":["GBP","Pound sterling"]}]}
            """);
        AssertSaved(result);
        var seed = SeedOf(s, result.Id!);
        Assert.Equal([["EUR", "Euro"], ["USD"], ["GBP", "Pound sterling"]], seed.Rows.Select(Cells));
        Assert.All(seed.Rows, r => Assert.True(IdFormat.IsValid(r.Id)));

        // On a save the keys match the columns as the body writes them: ids here.
        var document = s.Model.GetDocument(result.Id!)!;
        var saved = await s.Store.SaveAsync(result.Id!, BillingStore.Edit(document, n => n["rows"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "01K7SDN0000000000000000030", ["values"] = new JsonObject { [CurrencyLabel] = "Swiss franc", [CurrencyCode] = "CHF" },
        })), document.Hash, ChangeSource.Editor, Ct);
        AssertSaved(saved);
        Assert.Equal(["CHF", "Swiss franc"], Cells(SeedOf(s, result.Id!).Rows[^1]));
    }

    [Fact]
    public async Task A_values_key_outside_columns_is_MQ1002_and_writes_nothing()
    {
        await using var s = await WithCurrencyAsync();
        var before = s.Files();
        var result = await CreateAsync(s, $$"""
            {"kind":"seed","name":"currencies","target":"{{Currency}}","columns":["code","label"],
             "rows":[{"values":{"code":"EUR","label":"Euro"} },{"values":{"code":"USD","symbol":"$"} }]}
            """);
        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(("MQ1002", "/rows/1/values/symbol"), (error.Rule, error.JsonPointer));
        Assert.Contains("'symbol' is not one of the seed's columns", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, s.Files());
    }

    [Fact]
    public async Task A_seed_key_given_by_constraint_name_is_stored_as_its_id()
    {
        await using var s = await WithCurrencyAsync();
        var result = await CreateAsync(s, $$"""
            {"kind":"seed","name":"currencies","target":"{{Currency}}","columns":["code","label"],"key":"uq_currency_label",
             "rows":[{"values":{"code":"EUR","label":"Euro"} }]}
            """);
        AssertSaved(result);
        Assert.Equal(CurrencyLabelKey, SeedOf(s, result.Id!).Key);

        // A key names a unique constraint of a table seed's table; a constraint name that finds none is MQ1002.
        var unknown = await CreateAsync(s, $$"""
            {"kind":"seed","name":"more","target":"{{Currency}}","columns":["code"],"key":"uq_currency_code","rows":[]}
            """);
        Assert.Equal(("MQ1002", "/key"), (Assert.Single(unknown.Diagnostics).Rule, unknown.Diagnostics[0].JsonPointer));
    }

    [Fact]
    public async Task An_unknown_or_ambiguous_column_name_is_MQ1002_and_writes_nothing()
    {
        await using var s = await WithCurrencyAsync();
        var before = s.Files();
        var unknown = await CreateAsync(s, $$"""
            {"kind":"seed","name":"currencies","target":"{{Currency}}","columns":["code","symbol"],"rows":[]}
            """);
        Assert.Equal(SaveOutcome.Invalid, unknown.Outcome);
        var error = Assert.Single(unknown.Diagnostics);
        Assert.Equal(("MQ1002", "/columns/1"), (error.Rule, error.JsonPointer));
        Assert.Contains("'symbol' names no column of table 'currency' (" + Currency + ")", error.Message, StringComparison.Ordinal);
        // A to-many end is no column of an entity's seed: its links belong in a seed of the relation.
        var toMany = await CreateAsync(s, $$"""
            {"kind":"seed","name":"demoCustomers","target":"01J92P0V0ETQKXXP951CMMNHH3","columns":["name","invoices"],"rows":[]}
            """);
        Assert.Equal(("MQ1002", "/columns/1"), (Assert.Single(toMany.Diagnostics).Rule, toMany.Diagnostics[0].JsonPointer));
        Assert.Equal(before, s.Files());

        // An attribute named like the role of a to-one end: the name finds both, and the message lists each id.
        var invoice = s.Model.GetDocument(Invoice)!;
        AssertSaved(await s.Store.SaveAsync(Invoice, BillingStore.Edit(invoice, n => n["attributes"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "01K7SDN0000000000000000040", ["name"] = "customer", ["type"] = "string",
        })), invoice.Hash, ChangeSource.Editor, Ct));
        before = s.Files();
        var ambiguous = await CreateAsync(s, $$"""
            {"kind":"seed","name":"demoInvoices","target":"{{Invoice}}","columns":["number","customer"],"rows":[]}
            """);
        Assert.Equal(SaveOutcome.Invalid, ambiguous.Outcome);
        error = Assert.Single(ambiguous.Diagnostics);
        Assert.Equal(("MQ1002", "/columns/1"), (error.Rule, error.JsonPointer));
        Assert.Contains("01K7SDN0000000000000000040", error.Message, StringComparison.Ordinal);
        Assert.Contains(PlacesCustomer, error.Message, StringComparison.Ordinal);
        Assert.Equal(before, s.Files());
    }

    [Fact]
    public async Task A_batch_creates_a_table_and_its_seed_by_column_names()
    {
        await using var s = await BillingStore.OpenAsync();
        var result = await s.Store.ApplyBatchAsync(Parse(s, """
            { "operations": [
              { "op": "create", "as": "$cur", "element": { "kind": "table", "name": "currency", "database": "@database:main",
                  "columns": [ { "name": "code", "type": "string", "length": 3, "nullable": false }, { "name": "label", "type": "string", "length": 40 } ],
                  "primaryKey": { "columns": [ "@column:billing.currency.code" ] } } },
              { "op": "create", "as": "$seed", "element": { "kind": "seed", "name": "currencies", "target": "$cur", "columns": [ "code", "label" ],
                  "rows": [ { "values": { "code": "EUR", "label": "Euro" } }, { "values": [ "USD", "US dollar" ] } ] } }
            ] }
            """), ChangeSource.Cli, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}")));
        var table = (Table)s.Model.GetDocument(result.Ids.Names["$cur"])!.Element;
        var seed = SeedOf(s, result.Ids.Names["$seed"]);
        Assert.Equal(table.Id, seed.Target);
        Assert.Equal(table.Columns.Select(c => c.Id), seed.Columns);
        Assert.Equal([["EUR", "Euro"], ["USD", "US dollar"]], seed.Rows.Select(Cells));

        // A name that finds nothing fails its operation under /operations/<n>/element, and the batch writes nothing.
        var before = s.Files();
        var failed = await s.Store.ApplyBatchAsync(Parse(s, $$"""
            { "operations": [
              { "op": "create", "element": { "kind": "seed", "name": "more", "target": "{{table.Id}}", "columns": [ "code", "rate" ], "rows": [] } }
            ] }
            """), ChangeSource.Cli, Ct);
        Assert.Equal(SaveOutcome.Invalid, failed.Outcome);
        Assert.Equal(("MQ1002", "/operations/0/element/columns/1"), (failed.Items[0].Diagnostics.Single().Rule, failed.Items[0].Diagnostics[0].JsonPointer));
        Assert.Equal(before, s.Files());
    }

    [Fact]
    public async Task Qualified_names_resolve_before_bare_names()
    {
        await using var s = await BillingStore.OpenAsync();
        // One column as 5.1's qualified name, one bare; the row's values object is keyed by both as the body writes them.
        var result = await s.Store.ApplyBatchAsync(Parse(s, """
            { "operations": [
              { "op": "create", "as": "$cur", "element": { "kind": "table", "name": "currency", "database": "@database:main",
                  "columns": [ { "name": "code", "type": "string", "length": 3, "nullable": false }, { "name": "label", "type": "string", "length": 40 } ],
                  "primaryKey": { "columns": [ "@column:billing.currency.code" ] } } },
              { "op": "create", "as": "$seed", "element": { "kind": "seed", "name": "currencies", "target": "$cur",
                  "columns": [ "@column:billing.currency.code", "label" ],
                  "rows": [ { "values": { "label": "Euro", "@column:billing.currency.code": "EUR" } } ] } }
            ] }
            """), ChangeSource.Cli, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}")));
        var table = (Table)s.Model.GetDocument(result.Ids.Names["$cur"])!.Element;
        var seed = SeedOf(s, result.Ids.Names["$seed"]);
        Assert.Equal(table.Columns.Select(c => c.Id), seed.Columns);
        Assert.Equal(["EUR", "Euro"], Cells(seed.Rows.Single()));
        Assert.Equal(table.Columns[0].Id, result.Ids.Names["@column:billing.currency.code"]);
    }
}
