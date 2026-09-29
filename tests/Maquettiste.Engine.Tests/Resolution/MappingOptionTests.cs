using System.Collections.Immutable;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>One test per mapping option (SPEC Section 10 "What maps to what"; engine-design.md sections 7.4 and 7.5).</summary>
public sealed class MappingOptionTests
{
    private static (ModelBuilder B, DatabaseBuilder Db) Start(int seed)
    {
        var b = new ModelBuilder(seed);
        return (b, b.Database("main", Dialect.PostgreSql));
    }

    [Fact]
    public void Enum_storage_int_uses_member_values_else_ordinals()
    {
        var (b, _) = Start(20);
        var status = b.Enum("Status").Member("Open", 10).Member("Closed");
        b.Entity("Ticket").Key("id", "uuid").Attr("status", status, a => a.Default("Closed"));
        var column = ResolutionKit.Resolve(b).Db("main").Table("tickets").Column("status");
        Assert.Equal("int32", column.Type);
        Assert.Equal("integer", column.NativeType);
        Assert.Equal(1L, column.Default); // "Closed" has no value: its ordinal
    }

    [Fact]
    public void Enum_storage_string_uses_codes_else_names_with_the_longest_length()
    {
        var (b, db) = Start(21);
        var status = b.Enum("Status").Member("Open", 1, "O").Member("Cancelled");
        var ticket = b.Entity("Ticket").Key("id", "uuid").Attr("status", status, a => a.Default("Open"));
        b.Mapping(db, ticket).Storage("status", StorageKind.String);
        var column = ResolutionKit.Resolve(b).Db("main").Table("tickets").Column("status");
        Assert.Equal("string", column.Type);
        Assert.Equal(9, column.Length);
        Assert.Equal("varchar(9)", column.NativeType);
        Assert.Equal("O", column.Default);
    }

    [Fact]
    public void Value_object_embedded_gives_prefixed_columns_recursively_and_honours_the_prefix()
    {
        var (b, db) = Start(23);
        var geo = b.ValueObject("Geo").Attr("lat", "double", a => a.Required()).Attr("lng", "double", a => a.Required());
        var address = b.ValueObject("Address").Attr("city", "string", a => a.Length(80)).Attr("location", geo, a => a.Required());
        var site = b.Entity("Site").Key("id", "uuid").Attr("address", address, a => a.Required()).Attr("mailing", address);
        b.Add(new Mapping
        {
            Id = b.NewId(), Name = "site", Database = db.Id, Entity = site.Id,
            Attributes = [new AttributeMapping { Attribute = site.AttrId("mailing"), Prefix = "post" }],
        });
        var table = ResolutionKit.Resolve(b).Db("main").Table("sites");
        Assert.Equal(["id", "address_city", "address_location_lat", "address_location_lng", "post_city", "post_location_lat", "post_location_lng"],
            table.Columns.Names());
        Assert.False(table.Column("address_location_lat").Nullable);
        Assert.True(table.Column("post_location_lat").Nullable);
        Assert.Equal(site.AttrId("address") + "." + address.AttrId("location") + "." + geo.AttrId("lat"), table.Column("address_location_lat").Key);
    }

    [Fact]
    public void Value_object_table_storage_is_a_child_table_keyed_by_the_owner()
    {
        var (b, db) = Start(24);
        var address = b.ValueObject("Address").Attr("city", "string");
        var site = b.Entity("Site").Key("id", "uuid").Attr("address", address);
        b.Mapping(db, site).Storage("address", StorageKind.Table);
        var model = ResolutionKit.Resolve(b);
        var child = model.Db("main").Table("sites_address");
        Assert.Equal(site.Id + "." + site.AttrId("address") + "@" + db.Id, child.Key);
        Assert.Equal(["site_id", "city"], child.Columns.Names());
        Assert.Equal(["site_id"], child.PrimaryKey!.Columns.Names());
        Assert.Equal("cascade", Assert.Single(child.ForeignKeys).OnDelete);
        Assert.DoesNotContain(model.Db("main").Table("sites").Columns, c => c.Name.Contains("city", StringComparison.Ordinal));
    }

    [Fact]
    public void Value_object_json_storage_is_one_json_column()
    {
        var (b, db) = Start(25);
        var address = b.ValueObject("Address").Attr("city", "string");
        var site = b.Entity("Site").Key("id", "uuid").Attr("address", address);
        b.Mapping(db, site).Storage("address", StorageKind.Json);
        var column = ResolutionKit.Resolve(b).Db("main").Table("sites").Column("address");
        Assert.Equal("json", column.Type);
        Assert.Equal("jsonb", column.NativeType);
    }

    [Fact]
    public void Collection_defaults_to_a_child_table_with_position_and_can_be_json()
    {
        var (b, db) = Start(26);
        var phone = b.ValueObject("Phone").Attr("number", "string", a => a.Length(20).Required());
        var person = b.Entity("Person").Key("id", "uuid").Attr("phones", phone, a => a.Collection()).Attr("nicknames", "string", a => a.Collection());
        b.Mapping(db, person).Storage("nicknames", StorageKind.Json);
        var model = ResolutionKit.Resolve(b);
        var child = model.Db("main").Table("people_phones");
        Assert.Equal(["person_id", "position", "number"], child.Columns.Names());
        Assert.Equal(["person_id", "position"], child.PrimaryKey!.Columns.Names());
        Assert.Equal("json", model.Db("main").Table("people").Column("nicknames").Type);
        Assert.Contains(model.Entity("Person").Mappings["main"].Columns, m => ReferenceEquals(m.Column, child.Column("number")));
    }

    [Fact]
    public void Attribute_ignore_and_derived_attributes_are_not_stored()
    {
        var (b, db) = Start(27);
        var person = b.Entity("Person").Key("id", "uuid").Attr("secret", "string").Attr("fullName", "string", a => a.Derived("first + last"))
            .Attr("age", "int32", a => a.Derived("now - born"));
        b.Add(new Mapping
        {
            Id = b.NewId(), Name = "person", Database = db.Id, Entity = person.Id,
            Attributes = [new AttributeMapping { Attribute = person.AttrId("secret"), Ignore = true }, new AttributeMapping { Attribute = person.AttrId("age") }],
        });
        Assert.Equal(["id", "age"], ResolutionKit.Resolve(b).Db("main").Table("people").Columns.Names());
    }

    [Fact]
    public void Mapping_ignore_leaves_the_entity_and_its_relations_out_of_the_database()
    {
        var (b, db) = Start(28);
        var a = b.Entity("Alpha").Key("id", "uuid");
        var c = b.Entity("Beta").Key("id", "uuid");
        b.Relation("links", a, c, fromMax: MaxCardinality.One);
        b.Mapping(db, c).Ignore();
        var model = ResolutionKit.Resolve(b);
        Assert.Equal(["alphas"], model.Db("main").Tables.Select(t => t.Name));
        Assert.Empty(model.Entity("Beta").Mappings);
        Assert.Empty(model.Relation("links").Mappings);
    }

    [Fact]
    public void Database_packages_scope_the_entities_it_holds()
    {
        var b = new ModelBuilder(29);
        var sales = b.Package("Sales");
        var orders = b.Package("Orders", sales);
        b.Entity("Order", orders).Key("id", "uuid");
        b.Entity("Log").Key("id", "uuid");
        b.Database("main", Dialect.PostgreSql).Packages(sales);
        Assert.Equal(["orders"], ResolutionKit.Resolve(b).Db("main").Tables.Select(t => t.Name));
    }

    [Fact]
    public void Mapping_table_binds_an_entity_to_a_designed_table_by_name_and_by_column_id()
    {
        var (b, db) = Start(30);
        var customer = b.Entity("Customer").Key("id", "int64").Attr("name", "string").Attr("email", "string");
        var legacy = b.Table("legacy_customer", db).Column("id", "int64", nullable: false).Column("NAME", "string").Column("mail", "string").PrimaryKey("id");
        b.Add(new Mapping
        {
            Id = b.NewId(), Name = "customer", Database = db.Id, Entity = customer.Id, Table = legacy.Id,
            Attributes = [new AttributeMapping { Attribute = customer.AttrId("email"), Column = legacy.ColumnId("mail") }],
        });
        var model = ResolutionKit.Resolve(b);
        var table = Assert.Single(model.Db("main").Tables);
        Assert.Equal("designed", table.Origin);
        Assert.Equal(legacy.Id, table.Key);
        var mapping = model.Entity("Customer").Mappings["main"];
        Assert.Same(table, mapping.Table);
        Assert.Equal(["id", "NAME", "mail"], mapping.Columns.Select(c => c.Column.Name));
        Assert.Same(model.Entity("Customer"), table.Entity);
    }

    [Fact]
    public void Mapping_foreign_key_binds_a_relation_to_an_existing_foreign_key()
    {
        var (b, db) = Start(31);
        var customer = b.Entity("Customer").Key("id", "int64");
        var order = b.Entity("Order").Key("id", "int64");
        var rel = b.Relation("places", customer, order, fromMax: MaxCardinality.One, fromRole: "customer", toRole: "order", toNavigation: "orders");
        var (customers, cid) = Designed(b, db, "legacy_customers", ("cid", "int64"));
        var fkId = b.NewId();
        var (orders, _) = Designed(b, db, "legacy_orders", ("oid", "int64"), ("cust", "int64"));
        orders = orders with
        {
            ForeignKeys = [new ForeignKey { Id = fkId, Name = "fk_legacy", Columns = [orders.Columns[1].Id], ReferencesTable = customers.Id }],
        };
        b.Add(customers).Add(orders);
        b.Add(new Mapping { Id = b.NewId(), Name = "c", Database = db.Id, Entity = customer.Id, Table = customers.Id });
        b.Add(new Mapping { Id = b.NewId(), Name = "o", Database = db.Id, Entity = order.Id, Table = orders.Id });
        b.Add(new Mapping { Id = b.NewId(), Name = "r", Database = db.Id, Relation = rel.Id, ForeignKey = fkId });
        var model = ResolutionKit.Resolve(b);
        var fk = model.Relation("places").Mappings["main"].ForeignKey!;
        Assert.Equal("fk_legacy", fk.Name);
        Assert.Same(model.Relation("places"), fk.Relation);
        Assert.Equal("order", fk.End!.Role);
        Assert.Equal(["cust"], fk.Columns.Names());
        Assert.Equal(["cid"], fk.ReferencedColumns.Names());
        var join = model.Entity("Customer").Mappings["main"].Joins["orders"];
        Assert.Equal("legacy_customers", join.Steps[0].FromTable.Name);
        Assert.Equal("legacy_orders", join.Steps[0].ToTable.Name);
        _ = cid;
    }

    [Fact]
    public void Mapping_junction_table_and_ends_bind_a_relation_to_a_designed_junction()
    {
        var (b, db) = Start(32);
        var user = b.Entity("User").Key("id", "uuid");
        var team = b.Entity("Team").Key("id", "uuid");
        var rel = b.Relation("is member of", user, team, fromRole: "member", toRole: "team", toNavigation: "teams");
        var (members, _) = Designed(b, db, "memberships", ("uid", "uuid"), ("tid", "uuid"));
        var userFk = b.NewId();
        var teamFk = b.NewId();
        members = members with
        {
            ForeignKeys =
            [
                new ForeignKey { Id = userFk, Columns = [members.Columns[0].Id], ReferencesTable = user.Id + "@" + db.Id },
                new ForeignKey { Id = teamFk, Columns = [members.Columns[1].Id], ReferencesTable = team.Id + "@" + db.Id },
            ],
        };
        b.Add(members);
        b.Add(new Mapping
        {
            Id = b.NewId(), Name = "m", Database = db.Id, Relation = rel.Id, Shape = RelationShape.Junction, JunctionTable = members.Id,
            Ends = [new RelationEndMapping { End = rel.EndIds[0], ForeignKey = userFk }, new RelationEndMapping { End = rel.EndIds[1], ForeignKey = teamFk }],
        });
        var model = ResolutionKit.Resolve(b);
        var junction = model.Relation("is member of").Mappings["main"].JunctionTable!;
        Assert.Equal("memberships", junction.Name);
        Assert.True(junction.IsJunction);
        Assert.Equal(["fk_memberships_uid", "fk_memberships_tid"], junction.ForeignKeys.Select(f => f.Name));
        Assert.Equal(["member", "team"], junction.ForeignKeys.Select(f => f.End!.Role));
        Assert.DoesNotContain(model.Db("main").Tables, t => t.Name == "user_team");
        var join = model.Entity("User").Mappings["main"].Joins["teams"];
        Assert.Equal(["users", "memberships"], join.Steps.Select(s => s.FromTable.Name));
    }

    [Fact]
    public void Mapping_shape_overrides_the_default_and_promoted_name_names_the_entity()
    {
        var (b, db) = Start(33);
        var user = b.Entity("User").Key("id", "uuid");
        var team = b.Entity("Team").Key("id", "uuid");
        var one = b.Relation("likes", user, team, fromRole: "fan", toRole: "team");
        var two = b.Relation("coaches", team, user, fromMax: MaxCardinality.One, fromRole: "team", toRole: "coach").Attr("since", "date");
        b.Add(new Mapping { Id = b.NewId(), Name = "one", Database = db.Id, Relation = one.Id, Shape = RelationShape.Promoted, PromotedName = "Fandom" });
        b.Add(new Mapping { Id = b.NewId(), Name = "two", Database = db.Id, Relation = two.Id, Shape = RelationShape.ForeignKey });
        var model = ResolutionKit.Resolve(b);
        Assert.True(model.Entity("Fandom").IsPromoted);
        Assert.Contains(model.Db("main").Tables, t => t.Name == "fandoms");
        var users = model.Db("main").Table("users");
        Assert.Equal(["id", "team_id", "since"], users.Columns.Names());
        Assert.True(users.Column("since").Nullable);
        Assert.Equal("foreign-key", model.Relation("coaches").Mappings["main"].Shape);
    }

    [Fact]
    public void Discriminator_value_comes_from_the_mapping_else_the_entity_name()
    {
        var (b, db) = Start(34);
        var animal = b.Entity("Animal").Abstract().Key("id", "uuid");
        var dog = b.Entity("Dog").Base(animal);
        b.Entity("Cat").Base(animal);
        b.Mapping(db, dog).Discriminator("D");
        var model = ResolutionKit.Resolve(b);
        Assert.Equal("D", model.Entity("Dog").Mappings["main"].DiscriminatorValue);
        Assert.Equal("Cat", model.Entity("Cat").Mappings["main"].DiscriminatorValue);
    }

    private static (Table Table, string FirstColumn) Designed(ModelBuilder b, DatabaseBuilder db, string name, params (string Name, string Type)[] columns)
    {
        var cols = columns.Select(c => new Column { Id = b.NewId(), Name = c.Name, Type = c.Type, Nullable = false }).ToImmutableArray();
        var table = new Table { Id = b.NewId(), Name = name, Database = db.Id, Columns = cols, PrimaryKey = new PrimaryKey { Columns = [cols[0].Id] } };
        return (table, cols[0].Id);
    }
}
