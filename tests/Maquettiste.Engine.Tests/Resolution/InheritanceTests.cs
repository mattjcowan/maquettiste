using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>TPH, TPT and TPC (engine-design.md section 7.5; SPEC Section 10 "Inheritance").</summary>
public sealed class InheritanceTests
{
    private static ModelBuilder Hierarchy(InheritanceStrategy? strategy, out EntityBuilder party)
    {
        var b = new ModelBuilder(seed: 40);
        party = b.Entity("Party").Abstract().Key("id", "uuid").Attr("name", "string", a => a.Required());
        var person = b.Entity("Person").Base(party).Attr("born", "date", a => a.Required());
        b.Entity("Employee").Base(person).Attr("badge", "string", a => a.Length(10).Required());
        b.Entity("Company").Base(party).Attr("vat", "string", a => a.Length(20));
        var order = b.Entity("Order").Key("id", "uuid");
        b.Relation("places", party, order, fromMax: MaxCardinality.One, fromRole: "party", toRole: "order", toNavigation: "orders");
        var db = b.Database("main", Dialect.PostgreSql);
        if (strategy is { } s)
            b.Mapping(db, party).Inheritance(s);
        return b;
    }

    [Fact]
    public void Tph_is_the_default_one_root_table_with_every_attribute_and_a_discriminator()
    {
        var model = ResolutionKit.Resolve(Hierarchy(null, out _));
        var db = model.Db("main");
        Assert.Equal(["orders", "parties"], db.Tables.Select(t => t.Name));
        var parties = db.Table("parties");
        Assert.Equal(["id", "name", "vat", "born", "badge", "discriminator"], parties.Columns.Names());
        Assert.False(parties.Column("name").Nullable);
        Assert.True(parties.Column("born").Nullable);   // derived attributes are nullable in the shared table
        Assert.True(parties.Column("badge").Nullable);
        var discriminator = parties.Column("discriminator");
        Assert.True(discriminator.IsDiscriminator);
        Assert.Equal(64, discriminator.Length);
        Assert.False(discriminator.Nullable);
        foreach (var name in new[] { "Party", "Person", "Employee", "Company" })
        {
            var mapping = model.Entity(name).Mappings["main"];
            Assert.Equal("tph", mapping.Inheritance);
            Assert.Same(parties, mapping.Table);
            Assert.Same(discriminator, mapping.DiscriminatorColumn);
            Assert.Equal(name, mapping.DiscriminatorValue);
        }

        Assert.Equal(["id", "name", "born", "badge"], model.Entity("Employee").Mappings["main"].Columns.Select(c => c.Column.Name));
        Assert.Equal("parties", Assert.Single(db.Table("orders").ForeignKeys).ReferencedTable.Name);
    }

    [Fact]
    public void Tpt_gives_each_entity_its_own_attributes_with_a_key_that_references_the_base()
    {
        var model = ResolutionKit.Resolve(Hierarchy(InheritanceStrategy.Tpt, out _));
        var db = model.Db("main");
        Assert.Equal(["companies", "employees", "orders", "parties", "people"], db.Tables.Select(t => t.Name));
        Assert.Equal(["id", "name"], db.Table("parties").Columns.Names());
        Assert.Equal(["id", "born"], db.Table("people").Columns.Names());
        Assert.Equal(["id", "badge"], db.Table("employees").Columns.Names());
        var fk = Assert.Single(db.Table("employees").ForeignKeys);
        Assert.Equal("people", fk.ReferencedTable.Name);
        Assert.Equal("cascade", fk.OnDelete);
        Assert.True(db.Table("employees").Column("id").IsPrimaryKey);
        Assert.False(db.Table("people").Column("born").Nullable);
        var mapping = model.Entity("Employee").Mappings["main"];
        Assert.Equal("tpt", mapping.Inheritance);
        Assert.Null(mapping.DiscriminatorColumn);
        Assert.Equal(["parties.id", "parties.name", "people.born", "employees.badge"],
            mapping.Columns.Select(c => c.Column.Table.Name + "." + c.Column.Name).Where(n => !n.EndsWith(".id", StringComparison.Ordinal) || n.StartsWith("parties", StringComparison.Ordinal)));
    }

    [Fact]
    public void Tpc_gives_concrete_entities_every_inherited_attribute_and_abstract_ones_no_table()
    {
        var model = ResolutionKit.Resolve(Hierarchy(InheritanceStrategy.Tpc, out _));
        var db = model.Db("main");
        Assert.Equal(["companies", "employees", "orders", "people"], db.Tables.Select(t => t.Name));
        Assert.Equal(["id", "name", "born", "badge"], db.Table("employees").Columns.Names());
        Assert.Equal(["id", "name", "vat"], db.Table("companies").Columns.Names());
        Assert.Empty(model.Entity("Party").Mappings);
        Assert.Equal("tpc", model.Entity("Company").Mappings["main"].Inheritance);
        // A foreign key cannot reference the abstract root's rows (they live in several tables): columns without a constraint.
        Assert.Empty(db.Table("orders").ForeignKeys);
        Assert.Contains(db.Table("orders").Columns, c => c.Name == "party_id");
    }

    [Fact]
    public void Tpc_puts_an_inherited_foreign_key_into_every_concrete_table()
    {
        var b = Hierarchy(InheritanceStrategy.Tpc, out var party);
        var region = b.Entity("Region").Key("id", "int32");
        b.Relation("is in", party, region, toMax: MaxCardinality.One, fromRole: "party", toRole: "region", toNavigation: "region");
        var model = ResolutionKit.Resolve(b);
        var db = model.Db("main");
        foreach (var table in new[] { "people", "employees", "companies" })
        {
            var fk = Assert.Single(db.Table(table).ForeignKeys);
            Assert.Equal(["region_id"], fk.Columns.Names());
            Assert.Equal("regions", fk.ReferencedTable.Name);
        }

        Assert.Equal("employees", model.Entity("Employee").Mappings["main"].Joins["region"].Steps[0].FromTable.Name);
        Assert.Equal("companies", model.Entity("Company").Mappings["main"].Joins["region"].Steps[0].FromTable.Name);

        // The abstract entity has no table, so its navigation has no single path; each concrete entity's mapping holds its own.
        Assert.Empty(model.Entity("Party").Mappings);
        Assert.Empty(model.Entity("Party").Navigations.Single(n => n.Name == "region").Joins);
    }

    [Fact]
    public void Conventions_choose_the_strategy_when_the_root_mapping_does_not()
    {
        var b = Hierarchy(null, out _);
        b.Settings(s => s with { Databases = s.Databases.ToDictionary().Append(new("main", new Conventions { Inheritance = InheritanceStrategy.Tpt })).ToDictionary() });
        Assert.Equal("tpt", ResolutionKit.Resolve(b).Entity("Person").Mappings["main"].Inheritance);
    }

    [Fact]
    public void Entities_outside_a_hierarchy_have_no_strategy()
    {
        var model = ResolutionKit.Resolve(Hierarchy(null, out _));
        Assert.Null(model.Entity("Order").Mappings["main"].Inheritance);
    }
}
