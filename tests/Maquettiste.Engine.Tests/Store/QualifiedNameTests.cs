using System.Text;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>Qualified names and the forms each kind registers (next-phase-1-design.md section 5.1).</summary>
public sealed class QualifiedNameTests
{
    private const string Billing = "01J92P0V01KDRN8GX5PGYCNKSX";
    private const string Catalog = "01J92P0V025DRFTXKMS240G2NG";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What a name finds in a snapshot: the candidates' ids, ordinal by longest form.</summary>
    private static IReadOnlyList<NameEntry> Find(ModelSnapshot model, string text)
    {
        Assert.True(QualifiedName.TryParse(text, out var name, out var error), error);
        return model.Names(name.Kind).Find(name.Path);
    }

    private static string Resolve(ModelSnapshot model, string text) => Assert.Single(Find(model, text)).Id;

    private static async Task<string> CreateAsync(BillingStore s, string json)
    {
        var result = await s.Store.CreateAsync(Encoding.UTF8.GetBytes(json), ChangeSource.Cli, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        return result.Id!;
    }

    [Fact]
    public void Parses_and_canonicalizes_quoted_segments()
    {
        Assert.True(QualifiedName.TryParse("@entity:\"Billing\".Invoice", out var plain, out _));
        Assert.True(QualifiedName.TryParse("@column:\"my.schema\".\"a \"\"b\"\" c\".x", out var quoted, out _));
        Assert.True(QualifiedName.TryParse("@table:main.billing.notes", out var table, out _));

        Assert.Equal("entity", plain.Kind);
        Assert.Equal(["Billing", "Invoice"], plain.Segments.AsEnumerable());
        Assert.Equal("@entity:Billing.Invoice", plain.Text);
        Assert.Equal(["my.schema", "a \"b\" c", "x"], quoted.Segments.AsEnumerable());
        Assert.Equal("@column:\"my.schema\".\"a \"\"b\"\" c\".x", quoted.Text);
        Assert.Equal("main.billing.notes", table.Path);
        Assert.Equal(QualifiedName.Create("entity", ["Billing", "Invoice"]), plain);
        Assert.Equal("\"Billing overview\"", QualifiedName.FormatSegment("Billing overview"));

        foreach (var malformed in (string[])["entity:Invoice", "@Entity:Invoice", "@entity", "@entity:", "@entity:a..b", "@entity:a.", "@entity:.a",
            "@entity:\"unclosed", "@entity:\"\"", "@entity:\"a\"b", "@entity:a b", "@entity:a\"b", "@unknown:x", "@migration:x", "@transition:x",
            "@tag-vocabulary:x", "$pm"])
        {
            Assert.False(QualifiedName.TryParse(malformed, out _, out var error), malformed);
            Assert.False(string.IsNullOrEmpty(error));
        }

        Assert.True(QualifiedName.IsPlaceholder("$pm"));
        Assert.True(QualifiedName.IsPlaceholder("$_type-2"));
        Assert.False(QualifiedName.IsPlaceholder("$2pm"));
        Assert.False(QualifiedName.IsPlaceholder("$"));
        Assert.False(QualifiedName.IsPlaceholder("$a.b"));
        Assert.True(QualifiedName.IsName("@entity:Invoice"));
        Assert.True(QualifiedName.IsName("$x"));
        Assert.False(QualifiedName.IsName("01J92P0V01KDRN8GX5PGYCNKSX"));
    }

    public static TheoryData<string, string> BillingNames() => new()
    {
        { "@package:Billing", Billing },
        { "@package:Catalog", Catalog },
        { "@package:Billing.Catalog", Catalog },
        { "@entity:Invoice", "01J92P0V0FJ23CGSNKM7P1W5V7" },
        { "@entity:Billing.Invoice", "01J92P0V0FJ23CGSNKM7P1W5V7" },
        { "@entity:\"Billing\".Invoice", "01J92P0V0FJ23CGSNKM7P1W5V7" },
        { "@entity:Product", "01J92P0V0JR8BE8253SKT29ZG7" },
        { "@entity:Catalog.Product", "01J92P0V0JR8BE8253SKT29ZG7" },
        { "@entity:Billing.Catalog.Product", "01J92P0V0JR8BE8253SKT29ZG7" },
        { "@value-object:Money", "01J92P0V0304M76KA84NE4D1TE" },
        { "@scalar-type:Billing.EmailAddress", "01J92P0V04TDYE2C73WMNXVDBV" },
        { "@enum:InvoiceStatus", "01J92P0V05WQDH2GZG539D9VG1" },
        { "@relation:settles", "01J92P0V1DKHD5Q02DV6S5M53D" },
        { "@relation:\"refers to\"", "01J92P0V1CC98R0RRCAV73SGF4" },
        { "@diagram:Billing.\"Billing overview\"", "01J92P0V2164SDBW687ZV6E1MV" },
        { "@database:main", "01J92P0V1QRN2181XM2ZWE02W4" },
        { "@mapping:\"Invoice in main\"", "01J92P0V200XW93JQYSRCT2SE3" },
        { "@stereotype:audited", "01J92P0V25BX5TMQ8N9F6AJE61" },
        { "@table:notes", "01K6BND0000000000000000001" },
        { "@table:billing.notes", "01K6BND0000000000000000001" },
        { "@table:main.billing.notes", "01K6BND0000000000000000001" },
        { "@view:billing.outstanding_invoices", "01J92P0V1YZ4YP352KD1A50FXS" },
        { "@sequence:main.billing.invoice_number_seq", "01J92P0V1S972MSFDJ8G6V6MWT" },
        { "@query:InvoicesByCustomer", "01K6QRY0000000000000000001" },
        { "@query:main.RevenueByMonth", "01K6QRY0000000000000000002" },
        { "@schema:billing", "01J92P0V1RC04SKQ5353EAKHG2" },
        { "@schema:main.billing", "01J92P0V1RC04SKQ5353EAKHG2" },
        { "@column:billing.notes.body", "01K6BND0000000000000000005" },
        { "@column:notes.entity_type", "01K6BND0000000000000000003" },
        { "@column:main.billing.notes.entity_id", "01K6BND0000000000000000004" },
        { "@attribute:Invoice.total", "01J92P0V0T6EA0XM025XQX8GBW" },
        { "@attribute:Billing.Invoice.total", "01J92P0V0T6EA0XM025XQX8GBW" },
        { "@attribute:Money.amount", "01J92P0V08P9BVJAPQ793XYNTQ" },
        { "@attribute:settles.allocated", "01J92P0V1PA2P9XH6BP1AFNR96" },
        { "@attribute:audited.createdAt", "01J92P0V26XYZRDQ8FJ6KZXT6S" },
        { "@enum-member:InvoiceStatus.Draft", "01J92P0V0A895FZKKJSFWJC6GV" },
        { "@end:settles.payments", "01J92P0V1M776PCP4JKV5J31SY" },
        { "@binding:CustomerNote.main", "01K6BND0000000000000000025" },
        { "@key:Product.sku", "01J92P0V160MQ5VBCRQV4WDHYW" },
        { "@key:Billing.Catalog.Product.sku", "01J92P0V160MQ5VBCRQV4WDHYW" },
        { "@category:Receivables", "01J92P0V06TYE8P8990AV35A8K" },
    };

    [Theory]
    [MemberData(nameof(BillingNames))]
    public async Task Billing_names_resolve_to_their_ids(string name, string id)
    {
        await using var s = await BillingStore.OpenAsync();

        Assert.Equal(id, Resolve(s.Model, name));
    }

    [Fact]
    public async Task Billing_names_that_are_not_forms_find_nothing()
    {
        await using var s = await BillingStore.OpenAsync();

        // Names compare as stored: no case folding, no resolved names, no bare attribute names, no unnamed overlay table.
        foreach (var name in (string[])["@entity:invoice", "@entity:Catalog.Invoice", "@attribute:total", "@table:invoice", "@package:Billing.Billing",
            "@database:Billing.main", "@table:main.notes", "@stereotype:Audited"])
        {
            Assert.Empty(Find(s.Model, name));
        }

        Assert.Same(s.Model.Names("table"), s.Model.Names("table"));
        Assert.Equal("Billing.Catalog.Product.sku", s.Model.Names("key").Get("01J92P0V160MQ5VBCRQV4WDHYW")!.Longest);
        Assert.Equal("main.billing.notes", s.Model.Names("table").Get("01K6BND0000000000000000001")!.Longest);
    }

    [Fact]
    public async Task Process_attribute_names_resolve()
    {
        await using var s = await BillingStore.OpenAsync();
        var process = await CreateAsync(s, $$"""
            {
              "kind": "process", "name": "InvoiceApproval", "package": "{{Billing}}",
              "context": [ { "name": "amount", "type": "decimal" } ],
              "events": [ { "id": "01K7B00000000000000000000E", "name": "submit", "payload": [ { "name": "note", "type": "string" } ] } ],
              "states": [
                { "id": "01K7B000000000000000000001", "name": "Draft" },
                { "id": "01K7B000000000000000000002", "name": "Review", "type": "compound", "initial": "01K7B000000000000000000003",
                  "states": [ { "id": "01K7B000000000000000000003", "name": "Pending" } ] }
              ],
              "initial": "01K7B000000000000000000001",
              "transitions": [
                { "source": "01K7B000000000000000000001", "event": "01K7B00000000000000000000E", "targets": ["01K7B000000000000000000002"],
                  "gate": { "name": "signOff", "signers": ["01K7B00000000000000000000A"], "meanings": [ { "name": "approved" } ],
                    "auditAttributes": [ { "name": "costCentre", "type": "string" } ] } }
              ]
            }
            """);
        var scenario = await CreateAsync(s, $$"""
            { "kind": "scenario", "name": "happy", "process": "{{process}}", "steps": [ { "event": "01K7B00000000000000000000E" } ] }
            """);
        var model = s.Model;
        var typed = model.Get<Process>(process)!;
        var gate = typed.Transitions[0].Gate!;

        Assert.Equal(process, Resolve(model, "@process:Billing.InvoiceApproval"));
        Assert.Equal(typed.Context[0].Id, Resolve(model, "@attribute:InvoiceApproval.amount"));
        Assert.Equal(typed.Context[0].Id, Resolve(model, "@attribute:Billing.InvoiceApproval.amount"));
        Assert.Equal(typed.Events[0].Payload[0].Id, Resolve(model, "@attribute:InvoiceApproval.submit.note"));
        Assert.Equal(gate.AuditAttributes[0].Id, Resolve(model, "@attribute:InvoiceApproval.signOff.costCentre"));
        Assert.Equal("01K7B00000000000000000000E", Resolve(model, "@event:InvoiceApproval.submit"));
        Assert.Equal(gate.Id, Resolve(model, "@gate:InvoiceApproval.signOff"));
        Assert.Equal(gate.Meanings[0].Id, Resolve(model, "@meaning:InvoiceApproval.signOff.approved"));
        Assert.Equal("01K7B000000000000000000003", Resolve(model, "@state:InvoiceApproval.Review.Pending"));
        Assert.Equal("01K7B000000000000000000001", Resolve(model, "@state:Billing.InvoiceApproval.Draft"));
        Assert.Empty(Find(model, "@state:InvoiceApproval.Pending"));
        Assert.Equal(scenario, Resolve(model, "@scenario:happy"));
        Assert.Equal(scenario, Resolve(model, "@scenario:Billing.InvoiceApproval.happy"));
    }

    [Fact]
    public async Task Rows_and_categories_take_their_longer_forms()
    {
        await using var s = await BillingStore.OpenAsync();
        var type = await CreateAsync(s, """
            { "kind": "reference-type", "name": "Currency", "code": { "length": 3 }, "label": { "length": 40 } }
            """);
        var live = await CreateAsync(s, $$"""
            { "kind": "seed", "name": "currencies", "target": "{{type}}", "columns": ["code", "label"],
              "rows": [ { "id": "01K7C000000000000000000001", "values": ["EUR", "Euro"] }, { "id": "01K7C000000000000000000002", "values": ["USD", "US dollar"] } ] }
            """);
        await CreateAsync(s, $$"""
            { "kind": "seed", "name": "testCurrencies", "target": "{{type}}", "columns": ["label", "code"],
              "rows": [ { "id": "01K7C000000000000000000003", "values": ["Euro", "EUR"] }, { "id": "01K7C000000000000000000004", "values": ["Numeric", 978] } ] }
            """);
        var tree = await CreateAsync(s, $$"""
            { "kind": "category-tree", "name": "billing-categories", "package": "{{Billing}}",
              "categories": [ { "id": "01K7C00000000000000000000A", "name": "Receivables" }, { "id": "01K7C00000000000000000000B", "name": "Overdue", "parent": "01K7C00000000000000000000A" } ] }
            """);
        var model = s.Model;

        var both = Find(model, "@row:Currency.EUR");
        Assert.Equal(["01K7C000000000000000000001", "01K7C000000000000000000003"], both.Select(e => e.Id));
        Assert.Equal(["currencies.EUR", "testCurrencies.EUR"], both.Select(e => e.Longest));
        Assert.Equal("01K7C000000000000000000001", Resolve(model, "@row:currencies.EUR"));
        Assert.Equal("01K7C000000000000000000003", Resolve(model, "@row:testCurrencies.EUR"));
        Assert.Equal("01K7C000000000000000000002", Resolve(model, "@row:Currency.USD"));
        Assert.Equal("01K7C000000000000000000004", Resolve(model, "@row:testCurrencies.978"));
        Assert.Equal(live, model.Names("row").Get("01K7C000000000000000000002")!.ElementId);

        var receivables = Find(model, "@category:Receivables");
        Assert.Equal(["01J92P0V06TYE8P8990AV35A8K", "01K7C00000000000000000000A"], receivables.Select(e => e.Id).Order(StringComparer.Ordinal));
        Assert.Equal("01K7C00000000000000000000A", Resolve(model, "@category:Billing.Receivables"));
        Assert.Equal("01K7C00000000000000000000B", Resolve(model, "@category:Receivables.Overdue"));
        Assert.Equal("01K7C00000000000000000000B", Resolve(model, "@category:Billing.Receivables.Overdue"));
        Assert.Equal("Billing.Receivables.Overdue", model.Names("category").Get("01K7C00000000000000000000B")!.Longest);
        Assert.Equal(tree, model.Names("category").Get("01K7C00000000000000000000B")!.ElementId);
    }

    [Fact]
    public void Every_kind_and_index_kind_has_forms_or_is_listed_as_id_only()
    {
        var kinds = KindInfo.All.Select(k => k.Name).Concat(ModelIndexer.SubElementKinds).ToList();

        Assert.All(kinds, k => Assert.True(NameIndex.NamedKinds.Contains(k) ^ NameIndex.IdOnlyKinds.Contains(k), k));
        Assert.Contains("migration", (IReadOnlySet<string>)NameIndex.IdOnlyKinds);
        Assert.Equal(["category-tree", "migration", "step", "tag-vocabulary", "transition"], NameIndex.IdOnlyKinds.Order(StringComparer.Ordinal));
        Assert.All(NameIndex.NamedKinds, k => Assert.True(QualifiedName.TryParse("@" + k + ":x", out _, out _), k));
        Assert.All(NameIndex.IdOnlyKinds, k => Assert.False(QualifiedName.TryParse("@" + k + ":x", out _, out _), k));
        Assert.Equal(kinds.Order(StringComparer.Ordinal), NameIndex.NamedKinds.Concat(NameIndex.IdOnlyKinds).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Packaged_kinds_take_their_package_form()
    {
        await using var s = await BillingStore.OpenAsync();
        var type = await CreateAsync(s, $$"""
            { "kind": "reference-type", "name": "Currency", "package": "{{Billing}}", "code": { "length": 3 }, "label": { "length": 40 } }
            """);
        var seed = await CreateAsync(s, $$"""
            { "kind": "seed", "name": "currencies", "package": "{{Billing}}", "target": "{{type}}", "columns": ["code", "label"],
              "rows": [ { "id": "01K7D000000000000000000001", "values": ["EUR", "Euro"] } ] }
            """);
        var database = await CreateAsync(s, $$"""
            { "kind": "database", "name": "reporting", "package": "{{Catalog}}", "dialect": "postgresql" }
            """);
        var model = s.Model;

        Assert.Equal(type, Resolve(model, "@reference-type:Currency"));
        Assert.Equal(type, Resolve(model, "@reference-type:Billing.Currency"));
        Assert.Equal(seed, Resolve(model, "@seed:currencies"));
        Assert.Equal(seed, Resolve(model, "@seed:Billing.currencies"));
        Assert.Equal(database, Resolve(model, "@database:reporting"));
        Assert.Equal(database, Resolve(model, "@database:Catalog.reporting"));
        Assert.Equal(database, Resolve(model, "@database:Billing.Catalog.reporting"));
        Assert.Equal("01K7D000000000000000000001", Resolve(model, "@row:Billing.currencies.EUR"));
        Assert.Equal("01K7D000000000000000000001", Resolve(model, "@row:Billing.Currency.EUR"));
        Assert.Equal(model.Get<ReferenceType>(type)!.Code.Id, Resolve(model, "@reference-field:Billing.Currency.code"));
        Assert.Equal("Billing.currencies", model.Names("seed").Get(seed)!.Longest);
        Assert.Empty(Find(model, "@database:Catalog.main"));
    }
}
