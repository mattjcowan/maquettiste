using System.Collections.Immutable;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>Table overlays merged over synthesized tables, designed and imported tables, views and sequences (SPEC Section 9).</summary>
public sealed class PhysicalFileTests
{
    [Fact]
    public void Overlay_renames_tables_and_columns_sets_native_types_and_adds_indexes_and_columns()
    {
        var b = new ModelBuilder(seed: 60);
        var customer = b.Entity("Customer").Key("id", "uuid").Attr("name", "string").Attr("email", "string");
        var db = b.Database("main", Dialect.PostgreSql);
        var schema = db.Schema("crm");
        var nameKey = customer.AttrId("name");
        var extraId = b.NewId();
        b.Add(new Table
        {
            Id = b.NewId(), Database = db.Id, Schema = schema, Origin = TableOrigin.Synthesized, Entity = customer.Id, Name = "client",
            Comment = "Clients.",
            Columns =
            [
                new Column { Id = b.NewId(), Attribute = nameKey, Name = "full_name", NativeType = "citext", Nullable = false, Comment = "Name." },
                new Column { Id = extraId, Name = "legacy_code", Type = "string", Length = 8 },
            ],
            Indexes = [new TableIndex { Id = b.NewId(), Columns = [new IndexColumn { Column = nameKey, Descending = true }], Unique = true, Method = IndexMethod.Btree }],
            Checks = [new CheckConstraint { Id = b.NewId(), Expression = ImmutableDictionary<string, string>.Empty.Add("*", "length(full_name) > 0") }],
        });
        var model = ResolutionKit.Resolve(b);
        var table = model.Db("main").Table("client");
        Assert.Equal("crm", table.Schema);
        Assert.Equal("Clients.", table.Comment);
        Assert.Equal(["id", "full_name", "email", "legacy_code"], table.Columns.Names());
        var name = table.Column("full_name");
        Assert.Equal(nameKey, name.Key);
        Assert.Equal("citext", name.NativeType);
        Assert.False(name.Nullable);
        Assert.Equal("Name.", name.Comment);
        Assert.Equal(extraId, table.Column("legacy_code").Key);
        Assert.Equal("varchar(8)", table.Column("legacy_code").NativeType);
        var index = Assert.Single(table.Indexes);
        Assert.Equal("ix_client_full_name", index.Name);
        Assert.True(index.Unique && index.Columns[0].Descending);
        Assert.Equal("btree", index.Method);
        Assert.Equal("ck_client_1", Assert.Single(table.Checks).Name);
        Assert.Equal(["crm"], model.Db("main").Schemas.Select(s => s.Name).Where(s => s == "crm"));
        Assert.Same(table, model.Db("main").Schemas.Single(s => s.Name == "crm").Tables.Single());
    }

    [Fact]
    public void Overlays_target_junction_child_and_lookup_tables()
    {
        var b = new ModelBuilder(seed: 61);
        var status = b.Enum("Status").Member("On").Member("Off");
        var user = b.Entity("User").Key("id", "uuid").Attr("state", status).Attr("aliases", "string", a => a.Collection());
        var team = b.Entity("Team").Key("id", "uuid");
        var rel = b.Relation("joins", user, team);
        var db = b.Database("main", Dialect.PostgreSql);
        b.Settings(s => s with { Conventions = new Conventions { EnumStorage = StorageKind.Lookup } });
        b.Add(new Table { Id = b.NewId(), Database = db.Id, Origin = TableOrigin.Synthesized, Relation = rel.Id, Name = "memberships" });
        b.Add(new Table { Id = b.NewId(), Database = db.Id, Origin = TableOrigin.Synthesized, Entity = user.Id, Attribute = user.AttrId("aliases"), Name = "user_alias" });
        b.Add(new Table { Id = b.NewId(), Database = db.Id, Origin = TableOrigin.Synthesized, Enum = status.Id, Name = "status_codes" });
        var names = ResolutionKit.Resolve(b).Db("main").Tables.Select(t => t.Name).ToList();
        Assert.Equal(["memberships", "status_codes", "teams", "user_alias", "users"], names);
    }

    [Fact]
    public void Designed_and_imported_tables_pass_through_with_foreign_keys_to_synthesized_tables()
    {
        var b = new ModelBuilder(seed: 62);
        var customer = b.Entity("Customer").Key("id", "int64");
        var db = b.Database("main", Dialect.SqlServer);
        var id = b.NewId();
        var customerRef = b.NewId();
        var total = b.NewId();
        b.Add(new Table
        {
            Id = b.NewId(), Name = "sales_report", Database = db.Id, Origin = TableOrigin.Imported,
            Source = new SourceInfo { Format = "database", Name = "dbo.sales_report" },
            Columns =
            [
                new Column { Id = id, Name = "id", Type = "int64", Nullable = false, Generated = ColumnGeneration.Identity },
                new Column { Id = customerRef, Name = "customer", Type = "int64" },
                new Column { Id = total, Name = "total", Type = "decimal", Precision = 12, Scale = 2, DefaultSql = ImmutableDictionary<string, string>.Empty.Add("sqlserver", "0").Add("*", "0.0") },
            ],
            PrimaryKey = new PrimaryKey { Name = "pk_report", Columns = [id] },
            ForeignKeys = [new ForeignKey { Id = b.NewId(), Columns = [customerRef], ReferencesTable = customer.Id + "@" + db.Id, OnDelete = ReferentialAction.SetNull }],
            Uniques = [new UniqueConstraint { Id = b.NewId(), Columns = [customerRef, total] }],
        });
        var table = ResolutionKit.Resolve(b).Db("main").Table("sales_report");
        Assert.Equal("imported", table.Origin);
        Assert.Equal("dbo", table.Schema);
        Assert.Equal("pk_report", table.PrimaryKey!.Name);
        Assert.True(table.Column("id").Identity);
        Assert.True(table.Column("customer").Nullable);
        Assert.Equal("0", table.Column("total").DefaultSql);
        Assert.Equal("decimal(12,2)", table.Column("total").NativeType);
        var fk = Assert.Single(table.ForeignKeys);
        Assert.Equal("customers", fk.ReferencedTable.Name);
        Assert.Equal(["id"], fk.ReferencedColumns.Names());
        Assert.Equal("set-null", fk.OnDelete);
        Assert.Equal("fk_sales_report_customer", fk.Name);
        Assert.Equal("uq_sales_report_customer_total", Assert.Single(table.Uniques).Name);
    }

    [Fact]
    public void Views_take_the_body_for_the_dialect_and_sequences_pass_through()
    {
        var b = new ModelBuilder(seed: 63);
        var db = b.Database("main", Dialect.PostgreSql);
        b.Add(new View
        {
            Id = b.NewId(), Name = "active_customers", Database = db.Id,
            Body = ImmutableDictionary<string, string>.Empty.Add("*", "select 1").Add("postgresql", "select 2"),
            Columns = [new ViewColumn { Name = "n", Type = "int32", Nullable = false }],
        });
        b.Add(new Sequence { Id = b.NewId(), Name = "invoice_numbers", Database = db.Id, Type = "int32", Start = 1000, Increment = 5 });
        var rdb = ResolutionKit.Resolve(b).Db("main");
        var view = Assert.Single(rdb.Views);
        Assert.Equal("select 2", view.Body);
        Assert.Equal("public", view.Schema);
        Assert.Equal("integer", view.Columns.Single().NativeType);
        var sequence = Assert.Single(rdb.Sequences);
        Assert.Equal((1000L, 5L, "integer"), (sequence.Start, sequence.Increment, sequence.NativeType));
    }

    [Fact]
    public void Sequence_strategy_synthesizes_a_sequence_unless_the_key_overlay_names_one()
    {
        var b = new ModelBuilder(seed: 64);
        var order = b.Entity("Order").Key("id", "int64", IdentityStrategy.Sequence);
        var item = b.Entity("Item").Key("id", "int64", IdentityStrategy.Sequence);
        var db = b.Database("main", Dialect.Oracle);
        var named = b.NewId();
        b.Add(new Sequence { Id = named, Name = "item_ids", Database = db.Id });
        b.Add(new Table
        {
            Id = b.NewId(), Database = db.Id, Origin = TableOrigin.Synthesized, Entity = item.Id,
            Columns = [new Column { Id = b.NewId(), Attribute = item.AttrId("id"), Generated = ColumnGeneration.Sequence, Sequence = named }],
        });
        var model = ResolutionKit.Resolve(b);
        var rdb = model.Db("main");
        Assert.Equal(["item_ids", "orders_seq"], rdb.Sequences.Select(s => s.Name));
        var synthesized = rdb.Sequences.Single(s => s.Name == "orders_seq");
        Assert.Equal(order.Id + ".sequence@" + db.Id, synthesized.Id);
        Assert.Same(synthesized, rdb.Table("orders").Column("id").Sequence);
        Assert.Same(synthesized, model.Entity("Order").Key!.Sequences["main"]);
        Assert.Equal("item_ids", rdb.Table("items").Column("id").Sequence!.Name);
        Assert.Equal("item_ids", model.Entity("Item").Key!.Sequences["main"].Name);
    }

    [Fact]
    public void Overlay_primary_key_name_and_defaults_apply()
    {
        var b = new ModelBuilder(seed: 65);
        var thing = b.Entity("Thing").Key("id", "uuid").Attr("count", "int32", a => a.Default(0));
        var db = b.Database("main", Dialect.PostgreSql);
        b.Add(new Table
        {
            Id = b.NewId(), Database = db.Id, Origin = TableOrigin.Synthesized, Entity = thing.Id,
            PrimaryKey = new PrimaryKey { Name = "things_pkey", Columns = [] },
            Columns = [new Column { Id = b.NewId(), Attribute = thing.AttrId("id"), DefaultSql = ImmutableDictionary<string, string>.Empty.Add("postgresql", "uuidv7()") }],
        });
        var table = ResolutionKit.Resolve(b).Db("main").Table("things");
        Assert.Equal("things_pkey", table.PrimaryKey!.Name);
        Assert.Equal("uuidv7()", table.Column("id").DefaultSql);
        Assert.Equal(0L, table.Column("count").Default);
    }
}
