using System.Collections.Immutable;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// The validator's <c>x-ref</c> sites (next-phase-1-design.md section 4.1): a value an <c>x-ref</c> types is MQ2010 when it is a
/// name, MQ2001 when an id it holds does not exist, MQ2002 when the id is of another kind and MQ2011 when it is out of scope
/// (<c>of</c>, <c>sameDatabase</c>, a stereotype default).
/// </summary>
public sealed class XrefRuleTests
{
    private static readonly string[] Rules = ["MQ2001", "MQ2002", "MQ2009", "MQ2010", "MQ2011"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_name_is_MQ2010()
    {
        var b = new ModelBuilder(31);
        var customer = b.Entity("Customer").Key("id", "uuid");
        var order = b.Entity("Order").Key("id", "uuid")
            .Property("owner", "Customer")
            .Property("reviewers", new[] { customer.Id, "Billing team" })
            .Property("keyed", customer.Id + ".total");
        var extension = PropertySchemaSetTests.Extension("""
            { "owner": { "type": "string", "x-ref": { "kind": "entity" } },
              "keyed": { "type": "string", "x-ref": { "kind": "attribute" } },
              "reviewers": { "type": "array", "items": { "type": "string", "x-ref": { "kind": "entity" } } } }
            """);

        var found = await Findings(b, extension);

        Assert.Equal(
            [
                ("MQ2010", order.Id, "/properties/keyed"),
                ("MQ2010", order.Id, "/properties/owner"),
                ("MQ2010", order.Id, "/properties/reviewers/1"),
            ],
            found.Select(d => (d.Rule, d.ElementId!, d.JsonPointer!)));
        Assert.Contains("'owner' holds 'Customer', which is a name or other text, not an id", found[1].Message, StringComparison.Ordinal);
        Assert.Contains("(extension:links)", found[1].Message, StringComparison.Ordinal);
        Assert.Contains("run the batch operation names-to-ids ({\"op\":\"names-to-ids\"})", found[1].Message, StringComparison.Ordinal);
        Assert.Equal(".maquettiste/model/entities/order.json", found[1].FilePath);
        Assert.Null(RuleCatalog.Get("MQ2010").QuickFix);
    }

    [Fact]
    public async Task Dangling_is_MQ2001_and_wrong_kind_MQ2002()
    {
        var b = new ModelBuilder(32);
        var status = b.Enum("InvoiceStatus").Member("Draft", 0);
        var customer = b.Entity("Customer").Key("id", "uuid");
        var ghost = b.NewId();
        var audited = b.Stereotype("audited").DefaultProperty("approver", ghost);
        var order = b.Entity("Order").Key("id", "uuid").Stereotype("audited")
            .Property("owner", ghost)
            .Property("payer", status.Id)
            .Property("keyed", customer.Id + "." + ghost)
            .Property("sibling", customer.AttrId("id"));
        var extensions = new[]
        {
            PropertySchemaSetTests.Extension("""
                { "owner": { "type": "string", "x-ref": { "kind": "entity" } }, "payer": { "type": "string", "x-ref": { "kind": ["entity", "value-object"] } },
                  "keyed": { "type": "string", "x-ref": { "kind": "attribute" } }, "sibling": { "type": "string", "x-ref": { "kind": "attribute" } } }
                """, path: ".maquettiste/extensions/a-links.json"),
            // A second schema typing owner the same way: the shared finding is reported once.
            PropertySchemaSetTests.Extension("""{ "owner": { "type": "string", "x-ref": { "kind": "entity", "optional": true } } }""", "more",
                path: ".maquettiste/extensions/b-more.json"),
            PropertySchemaSetTests.Extension("""{ "approver": { "type": "string", "x-ref": { "kind": "actor" } } }""", "audit", [], ["audited"],
                ".maquettiste/extensions/c-audit.json"),
        };

        var found = await Findings(b, extensions);

        Assert.Equal(
            [
                ("MQ2001", audited.Id, "/defaultProperties/approver"),
                ("MQ2001", order.Id, "/properties/keyed"),
                ("MQ2001", order.Id, "/properties/owner"),
                ("MQ2002", order.Id, "/properties/payer"),
            ],
            found.Select(d => (d.Rule, d.ElementId!, d.JsonPointer!)).Order());
        Assert.Contains($"'keyed' references '{ghost}', which does not exist.", found.Single(d => d.JsonPointer == "/properties/keyed").Message, StringComparison.Ordinal);
        Assert.Contains("an enum; its schema (extension:links) expects an entity or value-object", found.Single(d => d.Rule == "MQ2002").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scopes_are_MQ2011()
    {
        var b = new ModelBuilder(33);
        var main = b.Database("main", Dialect.PostgreSql);
        var reporting = b.Database("reporting", Dialect.PostgreSql);
        var currency = b.Entity("Currency").Key("code", "string");
        var country = b.Entity("Country").Key("code", "string");
        var eur = b.NewId();
        var fr = b.NewId();
        b.Add(new Seed { Id = b.NewId(), Name = "currencies", Target = currency.Id, Columns = [currency.AttrId("code")], Rows = [Row(eur, "EUR")] });
        b.Add(new Seed { Id = b.NewId(), Name = "countries", Target = country.Id, Columns = [country.AttrId("code")], Rows = [Row(fr, "FR")] });
        var stamped = b.Stereotype("stamped").Attr("stampedOn", "date");
        var stampedOn = ((Stereotype)stamped.Build()).Attributes.Single().Id;
        var dated = b.Stereotype("dated").DefaultProperty("sortBy", currency.AttrId("code"));
        var document = b.Entity("Document").Key("id", "uuid").Attr("issuedOn", "date");
        var ledger = b.Table("ledger", main).Column("id", "int64", nullable: false).PrimaryKey("id");
        var archive = b.Table("ledger_archive", reporting).Column("id", "int64", nullable: false).PrimaryKey("id");
        var history = b.Table("ledger_history", main).Column("id", "int64", nullable: false).PrimaryKey("id")
            .Property("archive", archive.Id);                // a table of another database
        var copy = b.Table("ledger_copy", main).Column("id", "int64", nullable: false).PrimaryKey("id")
            .Property("archive", ledger.Id);
        var invoice = b.Entity("Invoice").Base(document).Stereotype("stamped").Stereotype("dated").Attr("total", "decimal")
            .Property("sortBy", currency.AttrId("code"))   // a member of another entity
            .Property("inherited", document.AttrId("issuedOn"))
            .Property("virtual", stampedOn)
            .Property("currency", fr)                       // a row of another list
            .Property("archive", ledger.Id);                // an entity has no single database
        invoice.Property("own", invoice.AttrId("total"));
        var extensions = new[]
        {
            PropertySchemaSetTests.Extension("""
                { "sortBy": { "type": "string", "x-ref": { "kind": "attribute", "of": "$element" } },
                  "inherited": { "type": "string", "x-ref": { "kind": "attribute", "of": "$element" } },
                  "virtual": { "type": "string", "x-ref": { "kind": "attribute", "of": "$element" } },
                  "own": { "type": "string", "x-ref": { "kind": "attribute", "of": "$element" } },
                  "currency": { "type": "string", "x-ref": { "kind": "row", "of": "CURRENCY" } } }
                """.Replace("CURRENCY", currency.Id, StringComparison.Ordinal), "listing", path: ".maquettiste/extensions/a-listing.json"),
            PropertySchemaSetTests.Extension("""{ "archive": { "type": "string", "x-ref": { "kind": "table", "sameDatabase": true } } }""", "archive",
                ["entity", "table"], path: ".maquettiste/extensions/b-archive.json"),
            PropertySchemaSetTests.Extension("""{ "sortBy": { "type": "string", "x-ref": { "kind": "attribute", "of": "$element" } } }""", "dated", [], ["dated"],
                ".maquettiste/extensions/c-dated.json"),
        };

        var found = await Findings(b, extensions);

        Assert.Equal(
            [
                (dated.Id, "/defaultProperties/sortBy"),
                (history.Id, "/properties/archive"),
                (invoice.Id, "/properties/archive"),
                (invoice.Id, "/properties/currency"),
                (invoice.Id, "/properties/sortBy"),
            ],
            found.Select(d => (d.ElementId!, d.JsonPointer!)).Order());
        Assert.All(found, d => Assert.Equal("MQ2011", d.Rule));
        Assert.DoesNotContain(found, d => d.ElementId == copy.Id);
        string Message(string id, string pointer) => found.Single(d => d.ElementId == id && d.JsonPointer == pointer).Message;
        Assert.Contains("a member of entity Currency; of \"$element\" takes a member of entity Invoice", Message(invoice.Id, "/properties/sortBy"), StringComparison.Ordinal);
        Assert.Contains("not a row of a seed of entity Currency", Message(invoice.Id, "/properties/currency"), StringComparison.Ordinal);
        Assert.Contains("entity Invoice has no single database", Message(invoice.Id, "/properties/archive"), StringComparison.Ordinal);
        Assert.Contains("in database reporting, not in database main (sameDatabase)", Message(history.Id, "/properties/archive"), StringComparison.Ordinal);
        Assert.Contains("a stereotype default cannot use of \"$element\"", Message(dated.Id, "/defaultProperties/sortBy"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_column_host_reaches_a_sibling_column()
    {
        var b = new ModelBuilder(34);
        var main = b.Database("main", Dialect.PostgreSql);
        var other = b.Table("rates", main).Column("id", "int64", nullable: false).Column("valid_from", "date").PrimaryKey("id");
        var periods = b.Table("periods", main).Column("id", "int64", nullable: false).Column("starts_on", "date").Column("ends_on", "date").Column("closed_on", "date")
            .PrimaryKey("id");
        periods.Edit(t => t with
        {
            Columns =
            [
                .. t.Columns.Select(c => c.Name switch
                {
                    "ends_on" => c with { Properties = Props(("pairedWith", periods.ColumnId("starts_on"))) },
                    "closed_on" => c with { Properties = Props(("pairedWith", other.ColumnId("valid_from"))) },
                    _ => c,
                }),
            ],
        });
        var extension = PropertySchemaSetTests.Extension("""{ "pairedWith": { "type": "string", "x-ref": { "kind": "column", "of": "$element" } } }""", "periods",
            ["column"]);

        var found = await Findings(b, extension);

        var d = Assert.Single(found);
        Assert.Equal(("MQ2011", periods.ColumnId("closed_on"), "/columns/3/properties/pairedWith"), (d.Rule, d.ElementId!, d.JsonPointer!));
        Assert.Contains("a member of table rates", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fixed_of_in_a_stereotype_default_is_clean()
    {
        var b = new ModelBuilder(35);
        var currency = b.Entity("Currency").Key("code", "string");
        var eur = b.NewId();
        b.Add(new Seed { Id = b.NewId(), Name = "currencies", Target = currency.Id, Columns = [currency.AttrId("code")], Rows = [Row(eur, "EUR")] });
        b.Stereotype("priced").DefaultProperty("currency", eur);
        b.Entity("Invoice").Key("id", "uuid").Stereotype("priced");
        var extension = PropertySchemaSetTests.Extension("""{ "currency": { "type": "string", "x-ref": { "kind": "row", "of": "CURRENCY" } } }"""
            .Replace("CURRENCY", currency.Id, StringComparison.Ordinal), "pricing", [], ["priced"]);

        Assert.Empty(await Findings(b, extension));
    }

    [Fact]
    public async Task Reserved_kinds_are_valid_and_dangle()
    {
        var b = new ModelBuilder(36);
        var operation = b.NewId();
        var rule = b.NewId();
        var run = b.NewId();
        var order = b.Entity("Order").Key("id", "uuid").Property("handler", operation).Property("policy", rule).Property("run", run);
        var extension = PropertySchemaSetTests.Extension("""
            { "handler": { "type": "string", "x-ref": { "kind": ["operation", "outcome", "error", "member"] } },
              "policy": { "type": "string", "x-ref": { "kind": ["custom", "custom:billing-rule"] } },
              "run": { "type": "string", "x-ref": { "kind": ["entity", "custom:billing-run"] } } }
            """);

        var found = await Findings(b, extension);

        Assert.Equal([("MQ2001", "/properties/handler"), ("MQ2001", "/properties/policy"), ("MQ2001", "/properties/run")],
            found.Select(d => (d.Rule, d.JsonPointer!)));
        Assert.All(found, d => Assert.Equal(order.Id, d.ElementId));
        Assert.Contains("(operation, outcome, error, member arrive in a later phase, so no value of those kinds exists yet.)", found[0].Message,
            StringComparison.Ordinal);
        Assert.Contains("(custom, custom:billing-rule arrive in a later phase, so no value of those kinds exists yet.)", found[1].Message,
            StringComparison.Ordinal);
        Assert.Contains("(custom:billing-run arrives in a later phase, so no value of that kind exists yet.)", found[2].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Element_scope_takes_an_inherited_column_by_keyed_id_only()
    {
        // A table reaches a column a stereotype gives it by its keyed id <tableId>.<memberId> (next-phase-2-design.md section 3.1).
        var b = new ModelBuilder(37);
        var main = b.Database("main", Dialect.PostgreSql);
        var reporting = b.Database("reporting", Dialect.PostgreSql);
        string created = b.NewId(), updated = b.NewId();
        b.Add(new Stereotype
        {
            Id = b.NewId(), Key = "audited", Name = "Audited", AppliesTo = ["entity", "table"],
            Columns = [new Column { Id = created, Name = "created_at", Type = "datetimeoffset" }, new Column { Id = updated, Name = "updated_at", Type = "datetimeoffset" }],
        });
        TableBuilder Audited(string name, DatabaseBuilder database) =>
            b.Table(name, database).Column("id", "uuid", nullable: false).PrimaryKey("id").Stereotype("audited");
        var archive = Audited("archive", reporting);
        var notes = Audited("notes", main);
        notes.Property("sortBy", notes.Id + "." + created).Property("sameBase", notes.Id + "." + updated);
        var bare = Audited("bare", main).Property("sortBy", created);
        var other = Audited("other", main).Property("sortBy", notes.Id + "." + created).Property("sameBase", archive.Id + "." + created);
        var excluding = Audited("excluding", main);
        excluding.Property("sortBy", excluding.Id + "." + updated).Edit(t => t with { Excludes = [updated] });
        var product = b.Entity("Product").Key("id", "uuid");
        var overlay = b.Table("products", main).OverlayFor(product).Stereotype("audited").Property("sortBy", created);
        var extension = PropertySchemaSetTests.Extension("""
            { "sortBy": { "type": "string", "x-ref": { "kind": "column", "of": "$element" } },
              "sameBase": { "type": "string", "x-ref": { "kind": "column", "sameDatabase": true } } }
            """, "sorting", ["table"]);

        var found = await Findings(b, extension);

        Assert.Equal(
            new[]
            {
                (bare.Id, "/properties/sortBy"),
                (excluding.Id, "/properties/sortBy"),
                (other.Id, "/properties/sameBase"),
                (other.Id, "/properties/sortBy"),
                (overlay.Id, "/properties/sortBy"),
            }.Order(),
            found.Select(d => (d.ElementId!, d.JsonPointer!)).Order());
        Assert.All(found, d => Assert.Equal("MQ2011", d.Rule));
        Assert.DoesNotContain(found, d => d.ElementId == notes.Id);
        string Message(string id, string pointer) => found.Single(d => d.ElementId == id && d.JsonPointer == pointer).Message;
        Assert.Contains($"a member of stereotype 'audited'; of \"$element\" takes a member a table inherits by its keyed id: write '{bare.Id}.{created}'",
            Message(bare.Id, "/properties/sortBy"), StringComparison.Ordinal);
        Assert.Contains("as table notes inherits it", Message(other.Id, "/properties/sortBy"), StringComparison.Ordinal);
        Assert.Contains("that table excluding does not inherit (it excludes the member)", Message(excluding.Id, "/properties/sortBy"), StringComparison.Ordinal);
        Assert.Contains("which is in database reporting, not in database main (sameDatabase)", Message(other.Id, "/properties/sameBase"), StringComparison.Ordinal);
        Assert.Contains("holds the synthesized table key, which an x-ref value cannot hold", Message(overlay.Id, "/properties/sortBy"), StringComparison.Ordinal);
    }

    private static SeedRow Row(string id, string code) => new() { Id = id, Values = [JsonSerializer.SerializeToElement(code)] };

    private static ImmutableDictionary<string, JsonElement> Props(params (string Key, string Value)[] values) =>
        values.ToImmutableDictionary(v => v.Key, v => JsonSerializer.SerializeToElement(v.Value), StringComparer.Ordinal);

    /// <summary>Validates the whole model with the extensions and returns its reference findings, sorted as reports sort them.</summary>
    private static async Task<IReadOnlyList<Diagnostic>> Findings(ModelBuilder b, params ExtensionDocument[] extensions)
    {
        var model = ModelSnapshot.Create(b.BuildDocuments(), new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "", extensions, [], 1);
        var report = await ValidationFixture.Validator().ValidateAsync(model, ValidationScope.All, null, Ct);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule is "MQ5001" or "MQ5004");
        return [.. report.Diagnostics.Where(d => Rules.Contains(d.Rule))];
    }
}
