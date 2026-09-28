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
    public async Task A_second_mapping_for_one_target_is_seen_from_either_file_MQ4004()
    {
        var b = new ModelBuilder();
        var db = b.Database("main", Dialect.PostgreSql);
        var e = b.Entity("Customer").Key("id", "uuid");
        var zzz = b.Mapping(db, e).Rename("zzz");
        var aaa = b.Mapping(db, e).Rename("aaa");

        await AssertEveryParticipantSees(b.Build(), "MQ4004", zzz.Id, aaa.Id);
    }

    [Fact]
    public async Task A_second_overlay_for_one_synthesized_table_is_seen_from_either_file_MQ4004()
    {
        var b = new ModelBuilder();
        var db = b.Database("main", Dialect.PostgreSql);
        var e = b.Entity("Customer").Key("id", "uuid");
        var first = b.Table("customers_a", db).OverlayFor(e);
        var second = b.Table("customers_b", db).OverlayFor(e);

        await AssertEveryParticipantSees(b.Build(), "MQ4004", first.Id, second.Id);
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
    public async Task Binding_the_second_end_through_a_new_mapping_reports_the_relation_MQ4011()
    {
        var b = new ModelBuilder();
        var db = b.Database("main", Dialect.PostgreSql);
        var customer = b.Entity("Customer").Key("id", "uuid");
        var order = b.Entity("Order").Key("id", "uuid");
        var places = b.Relation("places", customer, order, fromMax: MaxCardinality.One, toMax: MaxCardinality.Many);
        var customers = b.Table("customers", db).Column("id", "uuid").PrimaryKey("id");
        var orders = b.Table("orders", db).Column("id", "uuid").PrimaryKey("id");
        var first = b.Mapping(db, customer).Table(customers);
        var second = b.Mapping(db, order).Table(orders);
        var model = b.Build();

        Assert.Equal(model.GetDocument(places.Id)!.Path, Assert.Single((await Validate(model)).Diagnostics, d => d.Rule == "MQ4011").FilePath);
        await AssertEveryParticipantSees(model, "MQ4011", first.Id, second.Id);
    }

    [Fact]
    public async Task A_relation_mapping_file_is_revalidated_when_an_entity_mapping_changes_MQ4011()
    {
        var b = new ModelBuilder();
        var db = b.Database("main", Dialect.PostgreSql);
        var customer = b.Entity("Customer").Key("id", "uuid");
        var order = b.Entity("Order").Key("id", "uuid");
        var places = b.Relation("places", customer, order, fromMax: MaxCardinality.One, toMax: MaxCardinality.Many);
        var customers = b.Table("customers", db).Column("id", "uuid").PrimaryKey("id");
        var orders = b.Table("orders", db).Column("id", "uuid").PrimaryKey("id");
        b.Mapping(db, customer).Table(customers);
        var second = b.Mapping(db, order).Table(orders);
        var relationMapping = b.Mapping(db, places);
        var model = b.Build();

        Assert.Equal(model.GetDocument(relationMapping.Id)!.Path, Assert.Single((await Validate(model)).Diagnostics, d => d.Rule == "MQ4011").FilePath);
        await AssertEveryParticipantSees(model, "MQ4011", second.Id);
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
}
