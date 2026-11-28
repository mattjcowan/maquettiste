using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// The five mismatches between an incremental and a whole-model validation that differential fuzzing found (2026-11-28 review), each as a
/// plain test: after the edit, the store's report must equal a fresh whole-model validation (<see cref="IncrementalValidationCheck"/>).
/// </summary>
public sealed class IncrementalValidationReproTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TempRepo Repo(string model)
    {
        var repo = new TempRepo();
        IncrementalValidationCheck.CopyModel(repo.ModelRoot, Fixtures.Path("models", model, ".maquettiste"));
        return repo;
    }

    private static void Write(TempRepo repo, string modelPath, string json)
    {
        var path = Path.Combine(repo.ModelRoot, modelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    /// <summary>A seed whose rows script makes one row per audited entity and throws on the name Boom.</summary>
    internal static void ScriptedSeed(TempRepo repo)
    {
        Write(repo, "model/reference-types/audited-record-type.json", """
            {"kind":"reference-type","id":"01K8SCR0000000000000000001","name":"AuditedRecordType",
             "code":{"id":"01K8SCR0000000000000000002","length":32},"label":{"id":"01K8SCR0000000000000000003","length":64}}
            """);
        Write(repo, "model/seeds/audited-record-type/audited-records.json", """
            {"kind":"seed","id":"01K8SCR0000000000000000010","name":"AuditedRecords","target":"01K8SCR0000000000000000001","columns":["code","label"],
             "rowsFrom":{"script":"seeds/audited-records.js"}}
            """);
        Write(repo, "extensions/seeds/audited-records.js", """
            maquettiste.rows((model, seed) => model.withStereotype("audited")
              .filter(d => d.kind === "entity")
              .map(d => { if (d.name === "Boom") throw new Error("boom"); return ({ code: d.name.toLowerCase(), label: d.name }); }));
            """);
    }

    [Fact]
    public async Task A_rows_script_reads_the_model_so_any_edit_judges_it_again()
    {
        using var repo = Repo("billing");
        ScriptedSeed(repo);
        await using var check = new IncrementalValidationCheck(repo.Options);
        await check.CheckAsync("load", false, Ct);
        await check.EditAsync(Integration.E2ERepo.InvoiceId, n => n["name"] = "Boom", Ct);
        await check.CheckAsync("audited Invoice renamed Boom", false, Ct);
        Assert.Contains((await check.Store.WholeValidationAsync(check.Snapshot, false, Ct)).Report.Diagnostics, d => d.Rule == "MQ7127");
    }

    private const string P = "01K6TSP00000000000000000";

    private static string N(int n) => P + n.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Database edge (sqlite) with customers (an identity id) &lt;- invoices &lt;- invoice_lines and a seed of each, the lines keyed by a
    /// key that holds a value the database fills two foreign keys away (MQ7125 on seed invoiceLines).
    /// </summary>
    internal static void SeedChain(TempRepo repo)
    {
        Write(repo, "model/databases/edge/database.json", $$"""{"kind":"database","id":"{{N(99)}}","name":"edge","dialect":"sqlite","byConvention":"none"}""");
        Write(repo, "model/databases/edge/tables/customers.json", $$"""
            {"kind":"table","id":"{{N(1)}}","name":"customers","database":"{{N(99)}}","columns":[
              {"id":"{{N(2)}}","name":"id","type":"int64","nullable":false,"generated":"identity"},
              {"id":"{{N(3)}}","name":"name","type":"string","nullable":false},
              {"id":"{{N(4)}}","name":"email","type":"string","length":254,"nullable":false}],
             "primaryKey":{"columns":["{{N(2)}}"]},"uniques":[{"id":"{{N(5)}}","name":"uq_customers_email","columns":["{{N(4)}}"]}]}
            """);
        Write(repo, "model/databases/edge/tables/invoices.json", $$"""
            {"kind":"table","id":"{{N(10)}}","name":"invoices","database":"{{N(99)}}","columns":[
              {"id":"{{N(11)}}","name":"id","type":"uuid","nullable":false,"stableId":true},
              {"id":"{{N(12)}}","name":"number","type":"string","length":32,"nullable":false},
              {"id":"{{N(13)}}","name":"issued_on","type":"date"},
              {"id":"{{N(14)}}","name":"customer_id","type":"int64","nullable":false}],
             "primaryKey":{"columns":["{{N(11)}}"]},
             "uniques":[{"id":"{{N(15)}}","name":"uq_invoices_number","columns":["{{N(12)}}"]},
                        {"id":"{{N(18)}}","name":"uq_invoices_customer_number","columns":["{{N(14)}}","{{N(12)}}"]}],
             "foreignKeys":[{"id":"{{N(16)}}","name":"fk_invoices_customer","columns":["{{N(14)}}"],"referencesTable":"{{N(1)}}"}]}
            """);
        Write(repo, "model/databases/edge/tables/invoice-lines.json", $$"""
            {"kind":"table","id":"{{N(30)}}","name":"invoice_lines","database":"{{N(99)}}","columns":[
              {"id":"{{N(31)}}","name":"id","type":"uuid","nullable":false,"stableId":true},
              {"id":"{{N(32)}}","name":"customer_id","type":"int64","nullable":false},
              {"id":"{{N(33)}}","name":"invoice_number","type":"string","length":32,"nullable":false}],
             "primaryKey":{"columns":["{{N(31)}}"]},"uniques":[{"id":"{{N(34)}}","name":"uq_invoice_lines_invoice","columns":["{{N(32)}}","{{N(33)}}"]}],
             "foreignKeys":[{"id":"{{N(35)}}","name":"fk_invoice_lines_invoice","columns":["{{N(32)}}","{{N(33)}}"],
               "referencesTable":"{{N(10)}}","referencesColumns":["{{N(14)}}","{{N(12)}}"]}]}
            """);
        Write(repo, "model/seeds/customers/customers.json", $$"""
            {"kind":"seed","id":"{{N(6)}}","name":"customers","target":"{{N(1)}}","key":"{{N(5)}}","columns":["{{N(3)}}","{{N(4)}}"],
             "rows":[{"id":"{{N(7)}}","values":["Ada","ada@example.com"]},{"id":"{{N(8)}}","values":["Grace","grace@example.com"]}]}
            """);
        Write(repo, "model/seeds/invoices/invoices.json", $$"""
            {"kind":"seed","id":"{{N(17)}}","name":"invoices","target":"{{N(10)}}","key":"{{N(15)}}","columns":["{{N(12)}}","{{N(13)}}","{{N(16)}}"],
             "rows":[{"id":"{{N(20)}}","values":["INV-1","2026-01-05","ada@example.com"]}]}
            """);
        Write(repo, $"model/seeds/{N(36)}/{N(36)}.json", $$"""
            {"kind":"seed","id":"{{N(36)}}","name":"invoiceLines","target":"{{N(30)}}","key":"{{N(34)}}","columns":["{{N(35)}}"],
             "rows":[{"id":"{{N(37)}}","values":["INV-1"]}]}
            """);
    }

    [Fact]
    public async Task A_table_seed_edit_reaches_the_seeds_two_foreign_keys_away()
    {
        using var repo = Repo("billing");
        SeedChain(repo);
        await using var check = new IncrementalValidationCheck(repo.Options);
        await check.CheckAsync("load", false, Ct);
        Assert.Contains((await check.Store.WholeValidationAsync(check.Snapshot, false, Ct)).Report.Diagnostics, d => d.Rule == "MQ7125" && d.ElementId == N(36));
        var path = check.Snapshot.GetDocument(N(6))!.Path;
        var bytes = await check.DeleteAsync(path, Ct);
        await check.CheckAsync("customers seed deleted", null, Ct);
        await check.WriteAsync(path, bytes, Ct);
        await check.CheckAsync("customers seed created again", null, Ct);
        await check.EditAsync(N(6), n => n.Remove("target"), Ct);
        await check.CheckAsync("customers seed made invalid", null, Ct);
    }

    /// <summary>process-basics with entity Invoice naming OrderLifecycle (whose subject is Order) as its lifecycle (MQ9201 on Invoice).</summary>
    internal static void BorrowedLifecycle(TempRepo repo) => Write(repo, "model/entities/invoice.json", """
        {"kind":"entity","id":"01JENT00000000000000000002","name":"Invoice","package":"01JPKG00000000000000000001","lifecycle":"01JPRC00000000000000000001",
         "key":{"attributes":["01JATT00000000000000000091"]},
         "attributes":[{"id":"01JATT00000000000000000091","name":"id","type":"uuid"},{"id":"01JATT00000000000000000092","name":"number","type":"string","length":20}]}
        """);

    private static string OrderId(IncrementalValidationCheck check) => check.Snapshot.All<Entity>().Single(e => e.Name == "Order").Id;

    [Fact]
    public async Task An_id_that_changes_kind_validates_the_whole_model()
    {
        using var repo = Repo("process-basics");
        BorrowedLifecycle(repo);
        await using var check = new IncrementalValidationCheck(repo.Options);
        await check.CheckAsync("load", false, Ct);
        var order = OrderId(check);
        await check.EditAsync(order, n =>
        {
            var name = n["name"]!.DeepClone();
            var package = n["package"]?.DeepClone();
            n.Clear();
            n["kind"] = "enum";
            n["id"] = order;
            n["name"] = name;
            if (package is not null)
                n["package"] = package;
            n["members"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["id"] = "01KZZZZZZZZZZZZZZZZZZZZZ71", ["name"] = "One" });
        }, Ct);
        await check.CheckAsync("entity Order rewritten as an enum", false, Ct);
    }

    /// <summary>Reference type PaymentTerm whose former code net30 was replaced by the row of code net-30, an Invoice attribute typed by it and a seed of Invoice that still writes net30.</summary>
    internal static void FormerCode(TempRepo repo)
    {
        Write(repo, "model/reference-types/payment-term.json", """
            {"kind":"reference-type","id":"01HK1540DD0000000000000002","name":"PaymentTerm","code":{"id":"01HK1540DD000000000000000A","length":8},
             "label":{"id":"01HK1540DD000000000000000B","length":40},"formerCodes":[{"code":"net30","replacedBy":"01HK1540DD0000000000000003"}]}
            """);
        Write(repo, "model/seeds/payment-terms.json", """
            {"kind":"seed","id":"01HK1540DD000000000000000C","name":"paymentTerms","target":"01HK1540DD0000000000000002","columns":["code","label"],
             "rows":[{"id":"01HK1540DD0000000000000003","values":["net-30","Net 30"]},{"id":"01HK1540DD0000000000000004","values":["net60","Net 60"]}]}
            """);
        Write(repo, "model/entities/invoice.json", """
            {"kind":"entity","id":"01HK1540DD000000000000000D","name":"Invoice","key":{"attributes":["01HK1540DD000000000000000E"],"strategy":"uuid-v7"},
             "attributes":[{"id":"01HK1540DD000000000000000E","name":"id","type":"uuid","required":true},
                           {"id":"01HK1540DD000000000000000F","name":"paymentTerm","type":{"ref":"01HK1540DD0000000000000002"}}]}
            """);
        Write(repo, "model/seeds/demo-invoices.json", """
            {"kind":"seed","id":"01HK1540DD000000000000000G","name":"demoInvoices","target":"01HK1540DD000000000000000D","columns":["01HK1540DD000000000000000F"],
             "rows":[{"id":"01HK1540DD000000000000000H","values":["net30"]}]}
            """);
    }

    [Fact]
    public async Task The_code_of_a_replacing_row_reaches_the_seeds_that_write_the_former_code()
    {
        using var repo = new TempRepo();
        Write(repo, "maquettiste.json", """{"formatVersion":1}""");
        FormerCode(repo);
        await using var check = new IncrementalValidationCheck(repo.Options);
        await check.CheckAsync("load", false, Ct);
        await check.EditAsync("01HK1540DD000000000000000C", n => n["rows"]![0]!["values"]![0] = "net-31", Ct);
        await check.CheckAsync("replacing row's code changed", true, Ct);
        Assert.Contains((await check.Store.WholeValidationAsync(check.Snapshot, false, Ct)).Report.Diagnostics,
            d => d.Rule == "MQ7019" && d.Message.Contains("'net-31'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_entity_rename_reaches_the_entities_that_name_its_lifecycle()
    {
        using var repo = Repo("process-basics");
        BorrowedLifecycle(repo);
        await using var check = new IncrementalValidationCheck(repo.Options);
        await check.CheckAsync("load", false, Ct);
        await check.EditAsync(OrderId(check), n => n["name"] = "OrderX", Ct);
        await check.CheckAsync("entity Order renamed", true, Ct);
        Assert.Contains((await check.Store.WholeValidationAsync(check.Snapshot, false, Ct)).Report.Diagnostics,
            d => d.Rule == "MQ9201" && d.Message.Contains("'OrderX'", StringComparison.Ordinal));
    }
}
