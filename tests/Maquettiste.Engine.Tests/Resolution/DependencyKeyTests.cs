using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Dependency keys (engine-design.md sections 7 and 11, D38): every resolved object derived from an entity or relation lists
/// <c>r:&lt;id&gt;</c>, so a mapping, overlay or relation created after a unit was rendered changes that unit's input hash.
/// </summary>
public sealed class DependencyKeyTests
{
    private sealed record Fixture(ModelBuilder B, EntityBuilder Customer, EntityBuilder Invoice, RelationBuilder Places, EnumBuilder Status,
        RelationBuilder Tags, DatabaseBuilder Db);

    private static Fixture Create()
    {
        var b = new ModelBuilder(seed: 70);
        var status = b.Enum("Status").Member("Open").Member("Closed");
        var customer = b.Entity("Customer").Key("id", "uuid").Attr("name", "string");
        var invoice = b.Entity("Invoice").Key("id", "uuid").Attr("status", status);
        var places = b.Relation("places", customer, invoice, fromMax: MaxCardinality.One, fromRole: "customer", toRole: "invoice",
            fromNavigation: "customer", toNavigation: "invoices");
        var tags = b.Relation("tags", invoice, customer, fromRole: "tagged", toRole: "tagger").Attr("at", "date");
        var db = b.Database("main", Dialect.PostgreSql);
        b.Settings(s => s with { Conventions = new Conventions { EnumStorage = StorageKind.String } });
        return new Fixture(b, customer, invoice, places, status, tags, db);
    }

    [Fact]
    public void Entities_relations_and_navigations_list_referrer_keys_of_what_they_derive_from()
    {
        var f = Create();
        var mapping = f.B.Mapping(f.Db, f.Invoice);
        var model = ResolutionKit.Resolve(f.B);
        var invoice = model.Entity("Invoice");
        Assert.Contains($"e:{f.Invoice.Id}", invoice.Dependencies);
        Assert.Contains($"r:{f.Invoice.Id}", invoice.Dependencies);
        Assert.Contains($"e:{mapping.Id}", invoice.Dependencies);
        Assert.Contains($"e:{f.Db.Id}", invoice.Dependencies);
        Assert.Contains("s:conventions", invoice.Dependencies);

        var places = model.Relation("places");
        Assert.Contains($"r:{f.Places.Id}", places.Dependencies);
        Assert.Contains($"r:{f.Customer.Id}", places.Dependencies);
        Assert.Contains($"r:{f.Invoice.Id}", places.Dependencies);

        var navigation = model.Entity("Customer").Navigations.Single(n => n.Name == "invoices");
        Assert.Contains($"r:{f.Places.Id}", navigation.Dependencies);
        Assert.Contains($"r:{f.Customer.Id}", navigation.Dependencies);
        Assert.Contains($"r:{f.Invoice.Id}", navigation.Dependencies);
        Assert.Equal(invoice.Dependencies.Order(StringComparer.Ordinal), invoice.Dependencies);
        Assert.Equal(invoice.Dependencies.Distinct(StringComparer.Ordinal).Count(), invoice.Dependencies.Count);
    }

    [Fact]
    public void Synthesized_tables_and_their_columns_list_entity_relation_and_enum_referrer_keys()
    {
        var f = Create();
        var model = ResolutionKit.Resolve(f.B);
        var db = model.Db("main");

        var invoices = db.Table("invoices");
        Assert.Contains($"r:{f.Invoice.Id}", invoices.Dependencies);
        Assert.Contains($"r:{f.Places.Id}", invoices.Dependencies);     // it holds the relation's foreign key
        Assert.Contains($"r:{f.Customer.Id}", invoices.Dependencies);   // the key type comes from the principal
        Assert.Contains("s:conventions", invoices.Dependencies);
        Assert.Contains("s:typeMaps", invoices.Dependencies);
        Assert.Contains("s:inflection", invoices.Dependencies);
        Assert.All(invoices.Columns, c => Assert.Same(invoices.Dependencies, c.Dependencies));
        Assert.Equal(invoices.Dependencies, invoices.Columns.MembershipKeys);

        var junction = db.Tables.Single(t => t.IsJunction);
        Assert.Contains($"r:{f.Tags.Id}", junction.Dependencies);
        Assert.Contains($"r:{f.Invoice.Id}", junction.Dependencies);
        Assert.Contains($"r:{f.Customer.Id}", junction.Dependencies);
    }

    [Fact]
    public void A_mapping_overlay_or_relation_created_later_changes_the_referrer_hash_the_resolved_objects_list()
    {
        var f = Create();
        var before = f.B.Build();
        var resolved = ResolutionKit.Resolve(before);
        var key = $"r:{f.Invoice.Id}";
        Assert.Contains(key, resolved.Entity("Invoice").Dependencies);
        Assert.Contains(key, resolved.Db("main").Table("invoices").Dependencies);
        var hashBefore = before.ReferrersHash(f.Invoice.Id);

        var withMapping = Create();
        withMapping.B.Mapping(withMapping.Db, withMapping.Invoice).Storage("status", StorageKind.String);
        Assert.NotEqual(hashBefore, withMapping.B.Build().ReferrersHash(f.Invoice.Id));

        var withOverlay = Create();
        withOverlay.B.Table("", withOverlay.Db).OverlayFor(withOverlay.Invoice).Overlay(withOverlay.Invoice.AttrId("status"), name: "state");
        Assert.NotEqual(hashBefore, withOverlay.B.Build().ReferrersHash(f.Invoice.Id));

        var withRelation = Create();
        withRelation.B.Relation("follows", withRelation.Invoice, withRelation.Invoice);
        Assert.NotEqual(hashBefore, withRelation.B.Build().ReferrersHash(f.Invoice.Id));
    }

    [Fact]
    public void Promoted_entities_and_their_tables_list_the_relation_referrer_key()
    {
        var f = Create();
        f.B.Add(new Mapping { Id = f.B.NewId(), Name = "tags", Database = f.Db.Id, Relation = f.Tags.Id, Shape = RelationShape.Promoted });
        var model = ResolutionKit.Resolve(f.B);
        var promoted = model.Entities.Single(e => e.IsPromoted);
        Assert.Contains($"r:{f.Tags.Id}", promoted.Dependencies);
        Assert.Contains($"r:{f.Tags.Id}", promoted.Mappings["main"].Table.Dependencies);
    }

    [Fact]
    public void Lists_carry_membership_keys()
    {
        var f = Create();
        var model = ResolutionKit.Resolve(f.B);
        Assert.Contains("k:entity", model.Entities.MembershipKeys);
        Assert.Contains("k:relation", model.Entities.MembershipKeys);
        Assert.Contains("k:mapping", model.Entities.MembershipKeys);
        Assert.Equal(["k:enum"], model.Enums.MembershipKeys);
        Assert.Equal(["k:database"], model.Databases.MembershipKeys);
        var tables = model.Db("main").Tables.MembershipKeys;
        foreach (var key in new[] { "k:entity", "k:relation", "k:enum", "k:table", "k:mapping", "s:conventions", $"e:{f.Invoice.Id}", $"r:{f.Db.Id}" })
            Assert.Contains(key, tables);
        Assert.Contains($"r:{f.Customer.Id}", model.Entity("Customer").Navigations.MembershipKeys);
        Assert.Equal([$"r:{f.Customer.Id}"], model.Entity("Customer").Derived.MembershipKeys);
    }

    [Fact]
    public void Referrer_keys_are_present_for_every_synthesized_object_kind()
    {
        var f = Create();
        var model = ResolutionKit.Resolve(f.B);
        foreach (var table in model.Db("main").Tables)
        {
            var sources = new List<string>();
            if (table.Entity is { } e)
                sources.Add(e.Id);
            if (table.Relation is { } r)
                sources.Add(r.Id);
            Assert.NotEmpty(sources);
            Assert.All(sources, id => Assert.Contains("r:" + id, table.Dependencies));
        }

        Assert.All(model.Entities, e => Assert.Contains("r:" + e.Id, e.Dependencies));
        Assert.All(model.Relations, r => Assert.Contains("r:" + r.Id, r.Dependencies));
        Assert.All(model.Entities.SelectMany(e => e.Navigations), n => Assert.Contains("r:" + n.Relation.Id, n.Dependencies));
        Assert.IsType<RDatabase>(model.Find(f.Db.Id));
    }
}
