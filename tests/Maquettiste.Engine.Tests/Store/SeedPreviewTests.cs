using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Editor;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>
/// One seed's rows, a page at a time (next-phase-2-design.md section 3.4, D44; MCP <c>preview_seed_rows</c>): as the seed writes them or
/// its script returns them, with ids and keys in seed terms, or in a database's terms with stable ids, the values foreign-key entries
/// fill and their refs, running only the scripts of the seed and of the seeds its entries name.
/// </summary>
public sealed class SeedPreviewTests
{
    internal const string P = "01K9SPV00000000000000000";
    private const string Sales = P + "99";
    internal const string Customers = P + "01", CustomerId = P + "02", CustomerName = P + "03", CustomerEmail = P + "04", ByEmail = P + "05";
    internal const string CustomerSeed = P + "06";
    internal const string Invoices = P + "10", InvoiceId = P + "11", InvoiceNumber = P + "12", InvoiceCustomer = P + "14", ByNumber = P + "15";
    internal const string InvoiceToCustomer = P + "16", InvoiceSeed = P + "17";
    private const string Regions = P + "40", RegionCode = P + "41", RegionName = P + "42", RegionSeed = P + "43";
    private const string Offices = P + "50", OfficeCode = P + "51", OfficeRegion = P + "52", OfficeToRegion = P + "53", OfficeSeed = P + "54";
    private const string RecordType = P + "60", RecordSeed = P + "63";

    private static CancellationToken Ct => EditorRepo.Ct;

    /// <summary>
    /// A billing copy whose main database gains schema sales with customers (a stable id, keyed by email) and invoices (a stable id, keyed
    /// by number, naming its customer by email through fk_invoices_customer), each with a seed; written before the store loads.
    /// </summary>
    internal static EditorRepo NaturalKeyRepo()
    {
        var r = EditorRepo.Create(packs: false);
        const string database = ".maquettiste/model/databases/main/database.json";
        var node = JsonNode.Parse(r.Repo.ReadFile(database))!.AsObject();
        node["schemas"]!.AsArray().Add(new JsonObject { ["id"] = Sales, ["name"] = "sales" });
        r.Repo.WriteFile(database, node.ToJsonString());
        r.Repo.WriteFile(".maquettiste/model/databases/main/tables/customers.json", $$"""
            {"kind":"table","id":"{{Customers}}","name":"customers","database":"{{EditorRepo.MainDatabaseId}}","schema":"{{Sales}}","columns":[
              {"id":"{{CustomerId}}","name":"id","type":"uuid","nullable":false,"stableId":true},
              {"id":"{{CustomerName}}","name":"name","type":"string","nullable":false},
              {"id":"{{CustomerEmail}}","name":"email","type":"string","length":254,"nullable":false}],
             "primaryKey":{"columns":["{{CustomerId}}"]},"uniques":[{"id":"{{ByEmail}}","name":"uq_customers_email","columns":["{{CustomerEmail}}"]}]}
            """);
        r.Repo.WriteFile(".maquettiste/model/databases/main/tables/invoices.json", $$"""
            {"kind":"table","id":"{{Invoices}}","name":"invoices","database":"{{EditorRepo.MainDatabaseId}}","schema":"{{Sales}}","columns":[
              {"id":"{{InvoiceId}}","name":"id","type":"uuid","nullable":false,"stableId":true},
              {"id":"{{InvoiceNumber}}","name":"number","type":"string","length":32,"nullable":false},
              {"id":"{{InvoiceCustomer}}","name":"customer_id","type":"uuid","nullable":false}],
             "primaryKey":{"columns":["{{InvoiceId}}"]},"uniques":[{"id":"{{ByNumber}}","name":"uq_invoices_number","columns":["{{InvoiceNumber}}"]}],
             "foreignKeys":[{"id":"{{InvoiceToCustomer}}","name":"fk_invoices_customer","columns":["{{InvoiceCustomer}}"],"referencesTable":"{{Customers}}"}]}
            """);
        r.Repo.WriteFile(".maquettiste/model/seeds/customers.json", $$"""
            {"kind":"seed","id":"{{CustomerSeed}}","name":"customers","target":"{{Customers}}","key":"{{ByEmail}}","columns":["{{CustomerName}}","{{CustomerEmail}}"],
             "rows":[{"id":"{{P}}07","values":["Ada","ada@example.com"]},{"id":"{{P}}08","values":["Grace","grace@example.com"]},
                     {"id":"{{P}}09","values":["Edsger","edsger@example.com"]}]}
            """);
        r.Repo.WriteFile(".maquettiste/model/seeds/invoices.json", $$"""
            {"kind":"seed","id":"{{InvoiceSeed}}","name":"invoices","target":"{{Invoices}}","key":"{{ByNumber}}","columns":["{{InvoiceNumber}}","{{InvoiceToCustomer}}"],
             "rows":[{"id":"{{P}}20","values":["INV-1","ada@example.com"]},{"id":"{{P}}21","values":["INV-2","grace@example.com"]}]}
            """);
        return r;
    }

    [Fact]
    public async Task Pages_hold_one_seeds_rows_with_ids_and_keys()
    {
        await using var r = NaturalKeyRepo();

        // As written: values by the seed's column names in column order, keys in seed terms, no refs.
        var first = (await r.Store.PreviewSeedRowsAsync(CustomerSeed, null, null, 2, Ct))!;
        Assert.Equal((CustomerSeed, (string?)null, (string?)null, 3), (first.Seed, first.Script, first.Database, first.Total));
        Assert.Empty(first.Diagnostics);
        Assert.Equal([P + "07", P + "08"], first.Rows.Select(x => x.Id));
        Assert.Equal(["ada@example.com"], first.Rows[0].Key);
        Assert.Equal(["name", "email"], first.Rows[0].Values.Keys);
        Assert.Equal(["Ada", "ada@example.com"], first.Rows[0].Values.Values);
        Assert.Empty(first.Rows[0].Refs);
        Assert.NotNull(first.Next);
        var second = (await r.Store.PreviewSeedRowsAsync(CustomerSeed, null, first.Next, 2, Ct))!;
        Assert.Equal([P + "09"], second.Rows.Select(x => x.Id));
        Assert.Null(second.Next);
        var invoices = (await r.Store.PreviewSeedRowsAsync(InvoiceSeed, null, null, 100, Ct))!;
        Assert.Equal(["INV-1"], invoices.Rows[0].Key);
        Assert.Equal("ada@example.com", invoices.Rows[0].Values["fk_invoices_customer"]); // the natural key as written

        // In database terms (by name or id): the stable ids, the value the natural key finds, and the ref it names.
        var main = (await r.Store.PreviewSeedRowsAsync(InvoiceSeed, "main", null, 100, Ct))!;
        Assert.True(main.Diagnostics.Count == 0, string.Join("; ", main.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Equal((EditorRepo.MainDatabaseId, 2), (main.Database, main.Total));
        var ada = main.Rows[0];
        Assert.Equal(P + "20", ada.Id);
        Assert.Equal(["INV-1"], ada.Key); // the row key in database terms: the seeds' key, uq_invoices_number
        Assert.Equal(StableIds.Of(Invoices, ["INV-1"]), ada.Values["id"]);
        Assert.Equal(StableIds.Of(Customers, ["ada@example.com"]), ada.Values["customer_id"]);
        Assert.Equal("INV-1", ada.Values["number"]);
        var link = Assert.Single(ada.Refs);
        Assert.Equal(("fk_invoices_customer", "sales.customers", true), (link.ForeignKey, link.Table, link.Found));
        Assert.Equal(["ada@example.com"], link.Key);
        Assert.Empty(link.Unknown);
        Assert.Equal(main.Rows.Select(x => x.Id), (await r.Store.PreviewSeedRowsAsync(InvoiceSeed, EditorRepo.MainDatabaseId, null, 100, Ct))!.Rows.Select(x => x.Id));

        // An unknown seed is null; an unknown database, cursor or limit is refused.
        Assert.Null(await r.Store.PreviewSeedRowsAsync(P + "98", null, null, 100, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => r.Store.PreviewSeedRowsAsync(InvoiceSeed, "nowhere", null, 100, Ct));
        await Assert.ThrowsAsync<FormatException>(() => r.Store.PreviewSeedRowsAsync(InvoiceSeed, null, "not-a-cursor", 100, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => r.Store.PreviewSeedRowsAsync(InvoiceSeed, null, null, 1001, Ct));
    }

    [Fact]
    public async Task Database_terms_run_only_the_scripts_of_the_seed_and_the_seeds_it_names()
    {
        await using var r = EditorRepo.Create(packs: false);
        // Regions take their rows from a script; offices name their region by code; an unrelated reference type has a scripted seed too.
        r.Repo.WriteFile(".maquettiste/model/databases/main/tables/regions.json", $$"""
            {"kind":"table","id":"{{Regions}}","name":"regions","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{RegionCode}}","name":"code","type":"string","length":8,"nullable":false},
              {"id":"{{RegionName}}","name":"name","type":"string","length":40}],
             "primaryKey":{"columns":["{{RegionCode}}"]} }
            """);
        r.Repo.WriteFile(".maquettiste/model/databases/main/tables/offices.json", $$"""
            {"kind":"table","id":"{{Offices}}","name":"offices","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{OfficeCode}}","name":"code","type":"string","length":8,"nullable":false},
              {"id":"{{OfficeRegion}}","name":"region_code","type":"string","length":8,"nullable":false}],
             "primaryKey":{"columns":["{{OfficeCode}}"]},
             "foreignKeys":[{"id":"{{OfficeToRegion}}","name":"fk_offices_region","columns":["{{OfficeRegion}}"],"referencesTable":"{{Regions}}"}]}
            """);
        r.Repo.WriteFile(".maquettiste/extensions/seeds/regions.js",
            "maquettiste.rows(() => [{ code: \"EU\", name: \"Europe\" }, { code: \"NA\", name: \"North America\" }]);\n");
        r.Repo.WriteFile(".maquettiste/model/seeds/regions.json", $$"""
            {"kind":"seed","id":"{{RegionSeed}}","name":"regions","target":"{{Regions}}","columns":["{{RegionCode}}","{{RegionName}}"],
             "rowsFrom":{"script":"seeds/regions.js"} }
            """);
        r.Repo.WriteFile(".maquettiste/model/seeds/offices.json", $$"""
            {"kind":"seed","id":"{{OfficeSeed}}","name":"offices","target":"{{Offices}}","columns":["{{OfficeCode}}","{{OfficeToRegion}}"],
             "rows":[{"id":"{{P}}55","values":["PAR","EU"]},{"id":"{{P}}56","values":["NYC","NA"]}]}
            """);
        r.Repo.WriteFile(".maquettiste/model/reference-types/record-type.json", $$"""
            {"kind":"reference-type","id":"{{RecordType}}","name":"RecordType","code":{"id":"{{P}}61","length":32},"label":{"id":"{{P}}62","length":64} }
            """);
        r.Repo.WriteFile(".maquettiste/extensions/seeds/records.js", "maquettiste.rows(() => [{ code: \"one\", label: \"One\" }]);\n");
        r.Repo.WriteFile(".maquettiste/model/seeds/records.json", $$"""
            {"kind":"seed","id":"{{RecordSeed}}","name":"records","target":"{{RecordType}}","columns":["code","label"],"rowsFrom":{"script":"seeds/records.js"} }
            """);
        var cache = r.Store.Services.SeedRows;

        var offices = (await r.Store.PreviewSeedRowsAsync(OfficeSeed, "main", null, 100, Ct))!;

        Assert.True(offices.Diagnostics.Count == 0, string.Join("; ", offices.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Equal([("PAR", "EU", true), ("NYC", "NA", true)],
            offices.Rows.Select(x => ((string)x.Values["code"]!, (string)x.Values["region_code"]!, x.Refs.Single().Found)));
        // Only the regions script ran: the records seed is read by nothing the preview reads.
        Assert.Equal(1, cache.Runs);
        var resolved = (await r.Store.ResolvedAsync(await r.Store.GetSnapshotAsync(Ct), Ct)).Model!;
        Assert.False(Assert.IsType<Engine.Resolution.RSeed>(resolved.Find(RecordSeed)).RowsBuilt);

        // The regions as their script returns them reuse that run; their ids derive from the seed and the key.
        var regions = (await r.Store.PreviewSeedRowsAsync(RegionSeed, null, null, 100, Ct))!;
        Assert.Equal(("seeds/regions.js", 2), (regions.Script, regions.Total));
        Assert.Equal([StableIds.RowId(RegionSeed, ["EU"]), StableIds.RowId(RegionSeed, ["NA"])], regions.Rows.Select(x => x.Id));
        Assert.Equal(["EU", "NA"], regions.Rows.Select(x => (string)Assert.Single(x.Key)!));
        Assert.Equal(1, cache.Runs);

        // A script that fails gives no rows and its MQ7127, for the seed and for a seed whose entries name its rows.
        r.Repo.WriteFile(".maquettiste/extensions/seeds/regions.js", "maquettiste.rows(() => { throw new Error('not ready'); });\n");
        await r.Store.RescanAsync(false, Ct);
        var failed = (await r.Store.PreviewSeedRowsAsync(RegionSeed, null, null, 100, Ct))!;
        Assert.Empty(failed.Rows);
        Assert.Equal("MQ7127", Assert.Single(failed.Diagnostics, d => d.Severity == DiagnosticSeverity.Error).Rule);
        var named = (await r.Store.PreviewSeedRowsAsync(OfficeSeed, "main", null, 100, Ct))!;
        Assert.Empty(named.Rows);
        Assert.Contains(named.Diagnostics, d => d.Rule == "MQ7127");
    }
}
