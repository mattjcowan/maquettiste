using System.Collections.Immutable;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Edge cases of physical resolution: foreign keys that cannot be resolved, bindings the design leaves to the resolver, unused
/// overlays, what a relation's foreign key depends on, explicit plural names, stored derived attributes and list memberships.
/// </summary>
public sealed class ResolutionEdgeTests
{
    private static Table DesignedTable(ModelBuilder b, DatabaseBuilder db, string name, bool withPrimaryKey, params (string Name, string Type)[] columns)
    {
        var cols = columns.Select(c => new Column { Id = b.NewId(), Name = c.Name, Type = c.Type, Nullable = false }).ToImmutableArray();
        return new Table
        {
            Id = b.NewId(), Name = name, Database = db.Id, Columns = cols,
            PrimaryKey = withPrimaryKey ? new PrimaryKey { Columns = [cols[0].Id] } : null,
        };
    }

    [Fact]
    public void A_relation_foreign_key_to_a_table_without_a_primary_key_is_reported_and_the_mapping_holds_none()
    {
        var b = new ModelBuilder(seed: 90);
        var customer = b.Entity("Customer").Key("id", "int64");
        var order = b.Entity("Order").Key("id", "int64");
        var rel = b.Relation("places", customer, order, fromMax: MaxCardinality.One, fromRole: "customer", toRole: "order");
        var db = b.Database("main", Dialect.PostgreSql);
        var staging = DesignedTable(b, db, "staging_customers", false, ("id", "int64"));
        b.Add(staging);
        b.Add(new Mapping { Id = b.NewId(), Name = "c", Database = db.Id, Entity = customer.Id, Table = staging.Id });
        var model = ResolutionKit.Resolve(b);

        var orders = model.Db("main").Table("orders");
        Assert.Contains(orders.Columns, c => c.Name == "customer_id");
        Assert.Empty(orders.ForeignKeys);
        var mapping = model.Relation("places").Mappings["main"];
        Assert.Equal("foreign-key", mapping.Shape);
        Assert.Null(mapping.ForeignKey);
        var diagnostic = Assert.Single(model.Diagnostics, d => d.Rule == "MQ4008");
        Assert.Equal(rel.Id, diagnostic.ElementId);
        Assert.Contains("'staging_customers' has no primary key", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bound_dependent_with_a_synthesized_principal_and_no_named_foreign_key_is_reported()
    {
        var b = new ModelBuilder(seed: 91);
        var customer = b.Entity("Customer").Key("id", "int64");
        var order = b.Entity("Order").Key("id", "int64");
        var rel = b.Relation("places", customer, order, fromMax: MaxCardinality.One, fromRole: "customer", toRole: "order");
        var db = b.Database("main", Dialect.PostgreSql);
        var legacy = DesignedTable(b, db, "legacy_orders", true, ("id", "int64"), ("cust", "int64"));
        b.Add(legacy);
        b.Add(new Mapping { Id = b.NewId(), Name = "o", Database = db.Id, Entity = order.Id, Table = legacy.Id });
        var model = ResolutionKit.Resolve(b);

        Assert.Null(model.Relation("places").Mappings["main"].ForeignKey);
        var diagnostic = Assert.Single(model.Diagnostics, d => d.Rule == "MQ4011");
        Assert.Equal(rel.Id, diagnostic.ElementId);
        Assert.Contains("'legacy_orders'", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_designed_foreign_key_to_a_missing_table_is_reported()
    {
        var b = new ModelBuilder(seed: 92);
        var db = b.Database("main", Dialect.PostgreSql);
        var table = DesignedTable(b, db, "audit", true, ("id", "int64"), ("ref", "int64"));
        table = table with { ForeignKeys = [new ForeignKey { Id = b.NewId(), Name = "fk_audit_ref", Columns = [table.Columns[1].Id], ReferencesTable = b.NewId() }] };
        b.Add(table);
        var model = ResolutionKit.Resolve(b);
        Assert.Empty(model.Db("main").Table("audit").ForeignKeys);
        var diagnostic = Assert.Single(model.Diagnostics);
        Assert.Equal(("MQ4008", table.Id), (diagnostic.Rule, diagnostic.ElementId));
        Assert.StartsWith("Foreign key 'fk_audit_ref' in table 'audit'", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_relation_bound_to_a_designed_foreign_key_depends_on_the_designed_table()
    {
        var b = new ModelBuilder(seed: 93);
        var customer = b.Entity("Customer").Key("id", "int64");
        var order = b.Entity("Order").Key("id", "int64");
        var rel = b.Relation("places", customer, order, fromMax: MaxCardinality.One, fromRole: "customer", toRole: "order");
        var db = b.Database("main", Dialect.PostgreSql);
        var customers = DesignedTable(b, db, "legacy_customers", true, ("cid", "int64"));
        var orders = DesignedTable(b, db, "legacy_orders", true, ("oid", "int64"), ("cust", "int64"));
        var fkId = b.NewId();
        orders = orders with { ForeignKeys = [new ForeignKey { Id = fkId, Name = "fk_legacy", Columns = [orders.Columns[1].Id], ReferencesTable = customers.Id }] };
        b.Add(customers).Add(orders);
        b.Add(new Mapping { Id = b.NewId(), Name = "c", Database = db.Id, Entity = customer.Id, Table = customers.Id });
        b.Add(new Mapping { Id = b.NewId(), Name = "o", Database = db.Id, Entity = order.Id, Table = orders.Id });
        b.Add(new Mapping { Id = b.NewId(), Name = "r", Database = db.Id, Relation = rel.Id, ForeignKey = fkId });
        var model = ResolutionKit.Resolve(b);
        var relation = model.Relation("places");
        Assert.Equal("fk_legacy", relation.Mappings["main"].ForeignKey!.Name);
        Assert.Contains($"e:{orders.Id}", relation.Dependencies);
        Assert.Empty(model.Diagnostics);
    }

    [Fact]
    public void A_synthesized_foreign_key_name_makes_the_relation_depend_on_inflection_and_the_host_overlay()
    {
        var b = new ModelBuilder(seed: 94);
        var customer = b.Entity("Customer").Key("id", "int64");
        var order = b.Entity("Order").Key("id", "int64");
        b.Relation("places", customer, order, fromMax: MaxCardinality.One, fromRole: "customer", toRole: "order");
        var db = b.Database("main", Dialect.PostgreSql);
        var overlay = new Table { Id = b.NewId(), Database = db.Id, Origin = TableOrigin.Synthesized, Entity = order.Id, Name = "purchase_orders" };
        b.Add(overlay);
        var relation = ResolutionKit.Resolve(b).Relation("places");
        Assert.Equal("fk_purchase_orders_customer_id", relation.Mappings["main"].ForeignKey!.Name);
        Assert.Contains("s:inflection", relation.Dependencies);
        Assert.Contains($"e:{overlay.Id}", relation.Dependencies);
    }

    [Fact]
    public void An_overlay_on_a_tph_derived_entity_is_reported_as_unused()
    {
        var b = new ModelBuilder(seed: 95);
        var animal = b.Entity("Animal").Key("id", "uuid");
        var dog = b.Entity("Dog").Base(animal).Attr("bark", "string");
        var db = b.Database("main", Dialect.PostgreSql);
        var overlay = new Table
        {
            Id = b.NewId(), Database = db.Id, Origin = TableOrigin.Synthesized, Entity = dog.Id,
            Columns = [new Column { Id = b.NewId(), Attribute = dog.AttrId("bark"), Name = "woof" }],
        };
        b.Add(overlay);
        var model = ResolutionKit.Resolve(b);
        Assert.Contains(model.Db("main").Table("animals").Columns, c => c.Name == "bark");
        var diagnostic = Assert.Single(model.Diagnostics);
        Assert.Equal(("MQ4009", overlay.Id), (diagnostic.Rule, diagnostic.ElementId));
        Assert.Contains("TPH-derived", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_plural_name_names_the_table()
    {
        var b = new ModelBuilder(seed: 96);
        var idAttr = b.NewId();
        var tagsAttr = b.NewId();
        b.Add(new Entity
        {
            Id = b.NewId(), Name = "Person", PluralName = "Persons", Key = new EntityKey { Attributes = [idAttr] },
            Attributes =
            [
                new ModelAttribute { Id = idAttr, Name = "id", Type = new TypeRef { Builtin = "uuid" } },
                new ModelAttribute { Id = tagsAttr, Name = "tags", Type = new TypeRef { Builtin = "string" }, Collection = true },
            ],
        });
        b.Database("main", Dialect.PostgreSql);
        var model = ResolutionKit.Resolve(b);
        Assert.Equal("Persons", model.Entity("Person").PluralName);
        Assert.Equal(["persons", "persons_tags"], model.Db("main").Tables.Select(t => t.Name));
    }

    [Fact]
    public void A_whole_name_inflection_override_reaches_table_names()
    {
        var b = new ModelBuilder(seed: 97);
        b.Entity("SalesPerson").Key("id", "uuid");
        b.Database("main", Dialect.PostgreSql);
        b.Settings(s => s with { Inflection = new InflectionSettings { Plurals = ImmutableDictionary<string, string>.Empty.Add("SalesPerson", "SalesTeam") } });
        // Overrides are matched and stored lowercase, so the plural keeps no inner word boundary (was "sales_people").
        Assert.Equal("salesteam", ResolutionKit.Resolve(b).Db("main").Tables.Single().Name);
    }

    [Fact]
    public void A_stored_derived_attribute_has_a_column_without_a_mapping()
    {
        var b = new ModelBuilder(seed: 98);
        b.Entity("Person").Key("id", "uuid").Attr("fullName", "string", a => a.Derived("first + last"))
            .Attr("age", "int32", a => a.Derived("now - born", stored: true));
        b.Database("main", Dialect.PostgreSql);
        Assert.Equal(["id", "age"], ResolutionKit.Resolve(b).Db("main").Table("people").Columns.Names());
    }

    [Fact]
    public void A_mapped_derived_attribute_has_a_column_even_when_not_marked_stored()
    {
        var b = new ModelBuilder(seed: 99);
        var person = b.Entity("Person").Key("id", "uuid").Attr("fullName", "string", a => a.Derived("first + last"));
        var db = b.Database("main", Dialect.PostgreSql);
        b.Add(new Mapping { Id = b.NewId(), Name = "p", Database = db.Id, Entity = person.Id, Attributes = [new AttributeMapping { Attribute = person.AttrId("fullName") }] });
        Assert.Equal(["id", "full_name"], ResolutionKit.Resolve(b).Db("main").Table("people").Columns.Names());
    }

    [Fact]
    public void Entity_and_relation_lists_depend_on_what_decides_promotion()
    {
        var b = new ModelBuilder(seed: 100);
        var user = b.Entity("User").Key("id", "uuid");
        var team = b.Entity("Team").Key("id", "uuid");
        var rel = b.Relation("joins", user, team).Attr("at", "date");
        var db = b.Database("main", Dialect.PostgreSql);
        var userMapping = b.Mapping(db, user);
        b.Mapping(db, rel).Shape(RelationShape.Promoted);
        var model = ResolutionKit.Resolve(b);
        Assert.Contains(model.Entities, e => e.IsPromoted);
        Assert.Contains($"e:{userMapping.Id}", model.Entities.MembershipKeys);
        Assert.Contains($"e:{db.Id}", model.Entities.MembershipKeys);
        Assert.Contains($"e:{userMapping.Id}", model.Relations.MembershipKeys);
        Assert.Contains($"e:{userMapping.Id}", model.Entity("User").Relations.MembershipKeys);

        userMapping.Ignore();
        Assert.DoesNotContain(ResolutionKit.Resolve(b).Entities, e => e.IsPromoted);
    }
}
