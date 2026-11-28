using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// Scoped validation (what <c>ModelStore</c> runs on a save) sees every cross-file conflict the element takes part in, even when the
/// saved file is the ordinally first participant and the diagnostic lands on the other file.
/// </summary>
public sealed class ScopedValidationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<ValidationReport> Validate(ModelSnapshot model, ValidationScope? scope = null) =>
        ValidationFixture.Validator().ValidateAsync(model, scope ?? ValidationScope.All, null, Ct);

    /// <summary>
    /// Asserts that the whole model has diagnostics of a rule, and that validating any one of the participants alone (with and
    /// without referrers) reports every one of them.
    /// </summary>
    private static async Task AssertEveryParticipantSees(ModelSnapshot model, string rule, params string[] participantIds)
    {
        var whole = (await Validate(model)).Diagnostics.Where(d => d.Rule == rule).ToList();
        Assert.NotEmpty(whole);
        foreach (var id in participantIds)
        {
            foreach (var includeReferrers in new[] { true, false })
            {
                var scoped = await Validate(model, new ValidationScope([id], includeReferrers));
                foreach (var expected in whole)
                    Assert.Contains(expected, scoped.Diagnostics);
            }
        }
    }

    [Fact]
    public async Task A_duplicate_name_is_seen_from_the_ordinally_first_file_MQ3001()
    {
        var b = new ModelBuilder();
        var original = b.Entity("Customer").Key("id", "uuid");
        var duplicate = b.Entity("Customer").Key("id", "uuid");
        var model = b.Build();

        // The duplicate's file (customer-<n>.json) sorts before customer.json, so the diagnostic lands on the untouched file.
        Assert.True(string.CompareOrdinal(model.GetDocument(duplicate.Id)!.Path, model.GetDocument(original.Id)!.Path) < 0);
        Assert.Equal(model.GetDocument(original.Id)!.Path, Assert.Single((await Validate(model)).Diagnostics, d => d.Rule == "MQ3001").FilePath);
        await AssertEveryParticipantSees(model, "MQ3001", original.Id, duplicate.Id);
    }

    [Fact]
    public async Task A_duplicate_table_name_is_seen_from_either_file_MQ4002()
    {
        var b = new ModelBuilder();
        var db = b.Database("main", Dialect.PostgreSql);
        var first = b.Table("orders", db).Column("id", "int64").PrimaryKey("id");
        var second = b.Table("ORDERS", db).Column("id", "int64").PrimaryKey("id");

        await AssertEveryParticipantSees(b.Build(), "MQ4002", first.Id, second.Id);
    }

    [Fact]
    public async Task A_composition_child_with_two_owners_is_seen_from_either_relation_MQ3016()
    {
        var b = new ModelBuilder();
        var order = b.Entity("Order").Key("id", "uuid");
        var quote = b.Entity("Quote").Key("id", "uuid");
        var line = b.Entity("Line").Key("id", "uuid");
        var first = b.Relation("contains", order, line, fromMax: MaxCardinality.One).Kind(RelationKind.Composition);
        var second = b.Relation("also contains", quote, line, fromMax: MaxCardinality.One).Kind(RelationKind.Composition);

        await AssertEveryParticipantSees(b.Build(), "MQ3016", first.Id, second.Id);
    }

    [Fact]
    public async Task Two_relations_generating_one_navigation_are_seen_from_either_relation_MQ3009()
    {
        var b = new ModelBuilder();
        var customer = b.Entity("Customer").Key("id", "uuid");
        var order = b.Entity("Order").Key("id", "uuid");
        var quote = b.Entity("Quote").Key("id", "uuid");
        var first = b.Relation("places", customer, order, fromMax: MaxCardinality.One, toNavigation: "items");
        var second = b.Relation("requests", customer, quote, fromMax: MaxCardinality.One, toNavigation: "items");

        await AssertEveryParticipantSees(b.Build(), "MQ3009", first.Id, second.Id);
    }

    [Fact]
    public async Task An_attribute_on_a_derived_entity_is_checked_against_base_navigations_MQ3009()
    {
        var b = new ModelBuilder();
        var root = b.Entity("Root").Key("id", "uuid");
        var derived = b.Entity("Derived").Base(root).Attr("orders", "string");
        var order = b.Entity("Order").Key("id", "uuid");
        var has = b.Relation("has", root, order, fromMax: MaxCardinality.One, toNavigation: "orders");

        await AssertEveryParticipantSees(b.Build(), "MQ3009", derived.Id, has.Id);
    }

    [Fact]
    public async Task An_attribute_added_to_a_root_is_checked_on_every_descendant_MQ3007()
    {
        var b = new ModelBuilder();
        var root = b.Entity("Root").Key("id", "uuid").Attr("code", "string");
        var middle = b.Entity("Middle").Base(root);
        b.Entity("Leaf").Base(middle).Attr("code", "string");   // the leaf does not reference Root directly

        await AssertEveryParticipantSees(b.Build(), "MQ3007", root.Id);
    }

    [Fact]
    public async Task A_value_object_or_a_stereotype_save_rejudges_the_columns_bound_through_its_attributes()
    {
        // 0.28.0: a binding maps total.currency (a member of the value object Money) and region (an attribute the stereotype Regional adds)
        // to columns whose defaults are no stored codes of their enum: the value object's and the stereotype's saves bring the table, as
        // the entity's, the enum's and the table's do.
        var b = new ModelBuilder(seed: 3304);
        var db = b.Database("main", Dialect.PostgreSql);
        var currency = b.Enum("Currency").Member("Euro", 0, "EUR").Member("Dollar", 1, "USD");
        var money = b.ValueObject("Money").Attr("amount", "decimal").Attr("currency", currency);
        const string regional = "01K9X3S0000000000000000040";
        b.Add(new Stereotype
        {
            Id = regional, Key = "regional", Name = "Regional", AppliesTo = ["entity"],
            Attributes = [new ModelAttribute { Id = "01K9X3S0000000000000000041", Name = "region", Type = new TypeRef { Ref = currency.Id } }],
        });
        var invoice = b.Entity("Invoice").Key("id", "uuid").Attr("total", money).Stereotype("regional");
        var table = b.Table("invoices", db).Column("id", "uuid", nullable: false).Column("currency", "string", length: 3).Column("region", "string", length: 3)
            .PrimaryKey("id")
            .Edit(t => t with { Columns = [t.Columns[0], t.Columns[1] with { Default = Json("GBP") }, t.Columns[2] with { Default = Json("CHF") }] });
        invoice.Bind(new EntityBinding
        {
            Id = "", Database = db.Id, Source = table.Id,
            Fields =
            [
                new BindingField { Attribute = invoice.AttrId("id"), Column = "id" },
                new BindingField { Attribute = invoice.AttrId("total") + "." + money.AttrId("currency"), Column = "currency" },
                new BindingField { Attribute = "01K9X3S0000000000000000041", Column = "region" },
            ],
        });
        var model = b.Build();

        Assert.Equal(2, (await Validate(model)).Diagnostics.Count(d => d.Rule == "MQ7014"));
        await AssertEveryParticipantSees(model, "MQ7014", money.Id, regional, invoice.Id, currency.Id, table.Id);
    }

    [Fact]
    public async Task Removing_a_code_from_a_seed_rejudges_a_typed_column_default()
    {
        // A column typed by a reference type defaults to the code I; no reference reaches the seed's row from the default
        // (next-phase-2-design.md section 3.2), so the seed's save brings the table as a peer.
        static ModelSnapshot Build(bool withIssued)
        {
            var b = new ModelBuilder(seed: 3301);
            var db = b.Database("main", Dialect.PostgreSql);
            var state = new ReferenceType
            {
                Id = "01K9X3S0000000000000000001", Name = "InvoiceState",
                Code = new ReferenceCode { Id = "01K9X3S0000000000000000002", Length = 1 },
                Label = new ReferenceLabel { Id = "01K9X3S0000000000000000003", Length = 40 },
            };
            b.Add(state);
            List<SeedRow> rows = [new SeedRow { Id = "01K9X3S0000000000000000011", Values = [Json("D"), Json("Draft")] }];
            if (withIssued)
                rows.Add(new SeedRow { Id = "01K9X3S0000000000000000012", Values = [Json("I"), Json("Issued")] });
            b.Add(new Seed { Id = "01K9X3S0000000000000000010", Name = "invoiceStates", Target = state.Id, Columns = ["code", "label"], Rows = rows });
            b.Table("invoice_events", db).Column("id", "uuid", nullable: false).Column("state", "string", nullable: false, length: 1).PrimaryKey("id")
                .Edit(t => t with { Columns = [t.Columns[0], t.Columns[1] with { Default = Json("I") }] })
                .BindTyped("InvoiceEvent", ("state", state.Id));
            return b.Build();
        }

        Assert.DoesNotContain((await Validate(Build(withIssued: true))).Diagnostics, d => d.Rule == "MQ7014");
        var model = Build(withIssued: false);
        var finding = Assert.Single((await Validate(model)).Diagnostics, d => d.Rule == "MQ7014");
        Assert.Equal("/columns/1/default", finding.JsonPointer);
        await AssertEveryParticipantSees(model, "MQ7014", "01K9X3S0000000000000000010", "01K9X3S0000000000000000001");
    }

    [Fact]
    public async Task Removing_an_enum_member_rejudges_the_table_seed_cells()
    {
        // A table seed's cells hold an enum's stored values; the seed names its table, not the enum, so the enum's save brings it.
        static (ModelSnapshot Model, string EnumId, string SeedId) Build(bool withVoid)
        {
            var b = new ModelBuilder(seed: 3302);
            var db = b.Database("main", Dialect.PostgreSql);
            var status = b.Enum("Status").Member("Draft", 0).Member("Issued", 1);
            if (withVoid)
                status.Member("Void", 2);
            var events = b.Table("invoice_events", db).Column("id", "int32", nullable: false).Column("status", "int32", nullable: false).PrimaryKey("id");
            events.BindTyped("InvoiceEvent", ("status", status.Id));
            var seed = new Seed
            {
                Id = "01K9X3S0000000000000000020", Name = "events", Target = events.Id, Columns = [events.ColumnId("id"), events.ColumnId("status")],
                Rows = [new SeedRow { Id = "01K9X3S0000000000000000021", Values = [Json(1), Json(2)] }],
            };
            b.Add(seed);
            return (b.Build(), status.Id, seed.Id);
        }

        Assert.DoesNotContain((await Validate(Build(withVoid: true).Model)).Diagnostics, d => d.Rule == "MQ7014");
        var (model, enumId, seedId) = Build(withVoid: false);
        var finding = Assert.Single((await Validate(model)).Diagnostics, d => d.Rule == "MQ7014");
        Assert.Equal(("/rows/0/values/1", model.GetDocument(seedId)!.Path), (finding.JsonPointer, finding.FilePath));
        await AssertEveryParticipantSees(model, "MQ7014", enumId, seedId);
    }

    [Fact]
    public async Task Removing_an_enum_member_rejudges_a_table_that_gets_the_typed_column_through_an_implied_stereotype()
    {
        // The table lists lifecycle, which implies stateful, whose column a binding maps to an attribute of the enum; the enum's save brings
        // the table seed.
        static (ModelSnapshot Model, string EnumId, string SeedId) Build(bool withVoid)
        {
            const string statusColumn = "01K9X3S0000000000000000031";
            var b = new ModelBuilder(seed: 3303);
            var db = b.Database("main", Dialect.PostgreSql);
            var status = b.Enum("Status").Member("Draft", 0).Member("Issued", 1);
            if (withVoid)
                status.Member("Void", 2);
            b.Add(new Stereotype
            {
                Id = b.NewId(), Key = "stateful", Name = "Stateful", AppliesTo = ["table"],
                Columns = [new Column { Id = statusColumn, Name = "status", Type = "int32", Nullable = false }],
            });
            b.Stereotype("lifecycle").AppliesTo("table").Implies("stateful");
            var events = b.Table("invoice_events", db).Column("id", "int32", nullable: false).PrimaryKey("id").Stereotype("lifecycle");
            events.BindTyped("InvoiceEvent", ("status", status.Id));
            var seed = new Seed
            {
                Id = "01K9X3S0000000000000000030", Name = "events", Target = events.Id, Columns = [events.ColumnId("id"), events.Id + "." + statusColumn],
                Rows = [new SeedRow { Id = "01K9X3S0000000000000000032", Values = [Json(1), Json(2)] }],
            };
            b.Add(seed);
            return (b.Build(), status.Id, seed.Id);
        }

        Assert.DoesNotContain((await Validate(Build(withVoid: true).Model)).Diagnostics, d => d.Rule == "MQ7014");
        var (model, enumId, seedId) = Build(withVoid: false);
        var finding = Assert.Single((await Validate(model)).Diagnostics, d => d.Rule == "MQ7014");
        Assert.Equal(("/rows/0/values/1", model.GetDocument(seedId)!.Path), (finding.JsonPointer, finding.FilePath));
        await AssertEveryParticipantSees(model, "MQ7014", enumId, seedId);
    }

    private static System.Text.Json.JsonElement Json(object value) => System.Text.Json.JsonSerializer.SerializeToElement(value);
}
