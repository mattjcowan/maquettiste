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
    public async Task Operation_kinds_resolve_and_only_member_stays_reserved()
    {
        // Phase 2 (next-phase-2-design.md section 4.1): operation is an element kind, operation-member, outcome and error are
        // sub-element kinds; member (the members of custom elements, section 4.2) is one too, so nothing dangles for want of a kind.
        var b = new ModelBuilder(36);
        string send = b.NewId(), recipient = b.NewId(), applied = b.NewId(), alreadySent = b.NewId(), part = b.NewId();
        var invoice = b.Entity("Invoice").Key("id", "uuid");
        b.Add(new Operation
        {
            Id = send, Name = "send", Subject = invoice.Id, Effect = OperationEffect.Change,
            Input = [new OperationMember { Id = recipient, Name = "recipient", Type = new TypeRef { Builtin = "string" } }],
            Outcomes = [new OperationOutcome { Id = applied, Name = "applied" }],
            Errors = [new OperationError { Id = alreadySent, Name = "already_sent" }],
        });
        var order = b.Entity("Order").Key("id", "uuid")
            .Property("handler", send).Property("result", applied).Property("failure", alreadySent).Property("field", recipient)
            .Property("part", part).Property("wrong", alreadySent);
        var extension = PropertySchemaSetTests.Extension("""
            { "handler": { "type": "string", "x-ref": { "kind": "operation" } },
              "result": { "type": "string", "x-ref": { "kind": "outcome" } },
              "failure": { "type": "string", "x-ref": { "kind": "error" } },
              "field": { "type": "string", "x-ref": { "kind": "operation-member" } },
              "part": { "type": "string", "x-ref": { "kind": ["operation-member", "member"] } },
              "wrong": { "type": "string", "x-ref": { "kind": "outcome" } } }
            """);

        var found = await Findings(b, extension);

        Assert.Equal([("MQ2001", "/properties/part"), ("MQ2002", "/properties/wrong")], found.Select(d => (d.Rule, d.JsonPointer!)));
        Assert.All(found, d => Assert.Equal(order.Id, d.ElementId));
        Assert.DoesNotContain("arrives in a later phase", found[0].Message, StringComparison.Ordinal);
        Assert.False(XRef.IsReserved("operation"));
        Assert.False(XRef.IsReserved("outcome"));
        Assert.False(XRef.IsReserved("error"));
        Assert.False(XRef.IsReserved("member"));
        Assert.False(XRef.IsReserved("custom:setting"));
        Assert.True(XRef.IsSubElementKind("member"));
        Assert.True(XRef.IsSubElementKind("operation-member"));
        Assert.False(XRef.IsSubElementKind("operation"));

        // An of beside the element kind operation is MQ2009, as for every element kind.
        var problems = new List<string>();
        using var scoped = JsonDocument.Parse("""{ "kind": "operation", "of": "$element" }""");
        Assert.Null(XRef.Parse(scoped.RootElement, "/x-ref", true, (_, message) => problems.Add(message)));
        Assert.Equal(["x-ref of fits sub-element kinds only, and 'operation' is not one."], problems);
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
        var bareExcluded = Audited("bare_excluded", main).Property("sortBy", created).Edit(t => t with { Excludes = [created] });
        var plain = b.Table("plain", main).Column("id", "uuid", nullable: false).PrimaryKey("id").Property("sortBy", created);
        var extension = PropertySchemaSetTests.Extension("""
            { "sortBy": { "type": "string", "x-ref": { "kind": "column", "of": "$element" } },
              "sameBase": { "type": "string", "x-ref": { "kind": "column", "sameDatabase": true } } }
            """, "sorting", ["table"]);

        var found = await Findings(b, extension);

        Assert.Equal(
            new[]
            {
                (bare.Id, "/properties/sortBy"),
                (bareExcluded.Id, "/properties/sortBy"),
                (excluding.Id, "/properties/sortBy"),
                (other.Id, "/properties/sameBase"),
                (other.Id, "/properties/sortBy"),
                (overlay.Id, "/properties/sortBy"),
                (plain.Id, "/properties/sortBy"),
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
        // A bare id of a member the table does not inherit is not told to write a keyed id, which would be out of scope as well.
        Assert.Contains("a member of stereotype 'audited' that table bare_excluded does not inherit (it excludes the member)",
            Message(bareExcluded.Id, "/properties/sortBy"), StringComparison.Ordinal);
        Assert.Contains("a member of stereotype 'audited' that table plain does not inherit", Message(plain.Id, "/properties/sortBy"), StringComparison.Ordinal);
        Assert.All(new[] { Message(bareExcluded.Id, "/properties/sortBy"), Message(plain.Id, "/properties/sortBy") },
            m => Assert.DoesNotContain("write '", m, StringComparison.Ordinal));
        Assert.DoesNotContain("excludes", Message(plain.Id, "/properties/sortBy"), StringComparison.Ordinal);
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

    // ---- custom element content (next-phase-2-design.md section 4.2) ----

    [Fact]
    public async Task Content_values_are_checked_like_properties()
    {
        var b = new ModelBuilder(51);
        var customer = b.Entity("Customer").Key("id", "uuid");
        string a = b.NewId(), c = b.NewId(), d = b.NewId(), ghost = b.NewId();
        b.Add(CustomKinds.CustomFixture.Element(a, "by_name", "setting", """{ "code": "net_30" }"""));
        b.Add(CustomKinds.CustomFixture.Element(c, "dangling", "setting", $$"""{ "code": "{{ghost}}" }"""));
        b.Add(CustomKinds.CustomFixture.Element(d, "wrong_kind", "setting", $$"""{ "code": "{{customer.Id}}" }"""));
        var model = CustomKinds.CustomFixture.Snapshot(b, [CustomKinds.CustomFixture.KindDocument(CustomKinds.CustomFixture.SettingKind)]);

        var found = await CustomKinds.CustomFixture.FindingsAsync(model, Rules);

        Assert.Equal(
            [("MQ2010", a, "/content/code"), ("MQ2001", c, "/content/code"), ("MQ2002", d, "/content/code")],
            found.Select(f => (f.Rule, f.ElementId!, f.JsonPointer!)).OrderBy(f => f.Item1 == "MQ2010" ? 0 : f.Item1 == "MQ2001" ? 1 : 2));
        Assert.Contains("(custom-kind:setting)", found.Single(f => f.Rule == "MQ2010").Message, StringComparison.Ordinal);
        Assert.Contains("expects a row", found.Single(f => f.Rule == "MQ2002").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_member_of_another_custom_element_is_MQ2011()
    {
        var b = new ModelBuilder(52);
        string first = b.NewId(), second = b.NewId(), mine = b.NewId(), theirs = b.NewId(), line = b.NewId(), reply = b.NewId();
        b.Add(CustomKinds.CustomFixture.Element(first, "invoice_layout", "layout", $$"""
            { "sections": [ { "id": "{{mine}}", "name": "header", "lines": [ { "id": "{{line}}", "name": "number" } ] } ], "pick": "{{line}}" }
            """));
        b.Add(CustomKinds.CustomFixture.Element(second, "credit_layout", "layout", $$"""{ "sections": [ { "id": "{{theirs}}", "name": "header" } ], "pick": "{{mine}}" }"""));
        b.Add(CustomKinds.CustomFixture.Element(reply, "late_reply", "scoped", $$"""{ "part": "{{theirs}}", "fine": "{{mine}}" }"""));
        var scoped = CustomKinds.CustomFixture.KindDocument($$"""
            { "type": "scoped", "properties": {
                "part": { "type": "string", "x-ref": { "kind": "member", "of": "{{first}}" } },
                "fine": { "type": "string", "x-ref": { "kind": "member", "of": "{{first}}" } } } }
            """);
        var model = CustomKinds.CustomFixture.Snapshot(b, [CustomKinds.CustomFixture.KindDocument(CustomKinds.CustomFixture.LayoutKind), scoped]);

        var found = await CustomKinds.CustomFixture.FindingsAsync(model, Rules);

        Assert.Equal([("MQ2011", second, "/content/pick"), ("MQ2011", reply, "/content/part")], found.Select(f => (f.Rule, f.ElementId!, f.JsonPointer!)).Order());
        Assert.Contains("of \"$element\" takes a member of custom credit_layout", found.Single(f => f.ElementId == second).Message, StringComparison.Ordinal);
        Assert.Contains("not of custom invoice_layout", found.Single(f => f.ElementId == reply).Message, StringComparison.Ordinal);

        // With kind member, an of that names no custom element is malformed (MQ2009 on the custom kind file).
        var customer = b.Entity("Customer").Key("id", "uuid");
        var wrongOf = CustomKinds.CustomFixture.KindDocument($$"""{ "type": "scoped", "properties": { "part": { "type": "string", "x-ref": { "kind": "member", "of": "{{customer.Id}}" } } } }""");
        var malformed = await CustomKinds.CustomFixture.FindingsAsync(CustomKinds.CustomFixture.Snapshot(b, [CustomKinds.CustomFixture.KindDocument(CustomKinds.CustomFixture.LayoutKind), wrongOf]), "MQ2009");
        var problem = Assert.Single(malformed);
        Assert.Equal((".maquettiste/extensions/kinds/scoped.json", "/properties/part/x-ref/of"), (problem.FilePath, problem.JsonPointer));
    }

    [Fact]
    public async Task A_custom_element_of_another_type_is_MQ2002()
    {
        var b = new ModelBuilder(53);
        string dueDays = b.NewId(), live = b.NewId();
        b.Add(CustomKinds.CustomFixture.Element(dueDays, "due_days", "setting", """{ "value": 30 }"""));
        b.Add(CustomKinds.CustomFixture.Element(live, "billing_live", "flag", """{ "on": true }"""));
        var order = b.Entity("Order").Key("id", "uuid").Property("terms", dueDays).Property("switch", live).Property("anything", live);
        var extension = PropertySchemaSetTests.Extension("""
            { "terms": { "type": "string", "x-ref": { "kind": "custom:setting" } },
              "switch": { "type": "string", "x-ref": { "kind": "custom:setting" } },
              "anything": { "type": "string", "x-ref": { "kind": "custom" } } }
            """);
        var model = CustomKinds.CustomFixture.Snapshot(b, [CustomKinds.CustomFixture.KindDocument(CustomKinds.CustomFixture.SettingKind), CustomKinds.CustomFixture.KindDocument(CustomKinds.CustomFixture.FlagKind)], extension);

        var found = await CustomKinds.CustomFixture.FindingsAsync(model, Rules);

        var finding = Assert.Single(found);
        Assert.Equal(("MQ2002", order.Id, "/properties/switch"), (finding.Rule, finding.ElementId!, finding.JsonPointer!));
        Assert.Contains("a custom:flag; its schema (extension:links) expects a custom:setting", finding.Message, StringComparison.Ordinal);
        Assert.Contains(model.ReferencesTo(dueDays), r => r.FromElementId == order.Id && r.Field == "terms");
    }

    [Fact]
    public async Task A_value_under_an_undeclared_custom_kind_is_MQ2002()
    {
        var b = new ModelBuilder(54);
        var dueDays = b.NewId();
        b.Add(CustomKinds.CustomFixture.Element(dueDays, "due_days", "setting", """{ "value": 30 }"""));
        var order = b.Entity("Order").Key("id", "uuid").Property("widget", dueDays);
        var extension = PropertySchemaSetTests.Extension("""{ "widget": { "type": "string", "x-ref": { "kind": "custom:widget" } } }""");
        var model = CustomKinds.CustomFixture.Snapshot(b, [CustomKinds.CustomFixture.KindDocument(CustomKinds.CustomFixture.SettingKind)], extension);

        var found = await CustomKinds.CustomFixture.FindingsAsync(model, Rules);

        var finding = Assert.Single(found);
        Assert.Equal(("MQ2002", order.Id, "/properties/widget"), (finding.Rule, finding.ElementId!, finding.JsonPointer!));
        Assert.Empty(model.PropertySchemas.Diagnostics);
    }
}
