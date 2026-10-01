using System.Collections.Immutable;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>Dialect type maps (engine-design.md section 7.6; SPEC Section 9 "Dialect maps").</summary>
public sealed class DialectTests
{
    public static TheoryData<Dialect, string, string> Expected => new()
    {
        { Dialect.PostgreSql, "decimal", "numeric(18,2)" },
        { Dialect.PostgreSql, "string", "varchar(255)" },
        { Dialect.PostgreSql, "datetimeoffset", "timestamptz(6)" },
        { Dialect.PostgreSql, "json", "jsonb" },
        { Dialect.PostgreSql, "bool", "boolean" },
        { Dialect.SqlServer, "decimal", "decimal(18,2)" },
        { Dialect.SqlServer, "string", "nvarchar(255)" },
        { Dialect.SqlServer, "uuid", "uniqueidentifier" },
        { Dialect.SqlServer, "datetime", "datetime2(6)" },
        { Dialect.SqlServer, "bool", "bit" },
        { Dialect.MySql, "decimal", "decimal(18,2)" },
        { Dialect.MySql, "string", "varchar(255)" },
        { Dialect.MySql, "bool", "tinyint(1)" },
        { Dialect.MySql, "uuid", "char(36)" },
        { Dialect.Sqlite, "decimal", "numeric" },
        { Dialect.Sqlite, "string", "text" },
        { Dialect.Sqlite, "int64", "integer" },
        { Dialect.Oracle, "decimal", "number(18,2)" },
        { Dialect.Oracle, "string", "varchar2(255 char)" },
        { Dialect.Oracle, "int32", "number(10)" },
        { Dialect.Oracle, "datetimeoffset", "timestamp(6) with time zone" },
    };

    [Theory]
    [MemberData(nameof(Expected))]
    public void Default_maps_render_native_types_with_convention_facets(Dialect dialect, string keyword, string native)
    {
        var b = new ModelBuilder(seed: 50);
        b.Entity("Thing").Key("id", "int64").Attr("value", keyword);
        b.Database("db", dialect);
        Assert.Equal(native, ResolutionKit.Resolve(b).Db("db").Table("things").Column("value").NativeType);
    }

    [Fact]
    public void Every_dialect_maps_every_builtin_keyword()
    {
        foreach (var dialect in Enum.GetValues<Dialect>())
        {
            var map = DialectTypeMaps.Default(dialect);
            Assert.Equal(BuiltinTypes.All.Order(StringComparer.Ordinal), map.Keys.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Attribute_facets_win_over_convention_defaults()
    {
        var b = new ModelBuilder(seed: 51);
        b.Entity("Thing").Key("id", "int64").Attr("price", "decimal", a => a.Precision(10).Scale(3)).Attr("code", "string", a => a.Length(12))
            .Attr("at", "datetime", a => a.Precision(3));
        b.Database("db", Dialect.PostgreSql);
        b.Settings(s => s with { Conventions = new Conventions { DefaultStringLength = 100, DecimalPrecision = 20, DatetimePrecision = 0 } });
        var table = ResolutionKit.Resolve(b).Db("db").Table("things");
        Assert.Equal("numeric(10,3)", table.Column("price").NativeType);
        Assert.Equal("varchar(12)", table.Column("code").NativeType);
        Assert.Equal("timestamp(3)", table.Column("at").NativeType);
    }

    [Fact]
    public void Scalar_type_facets_apply_through_the_attribute()
    {
        var b = new ModelBuilder(seed: 52);
        var email = b.ScalarType("Email", "string").Length(254);
        var amount = b.ScalarType("Amount", "decimal").Decimal(12, 4);
        b.Entity("Thing").Key("id", "int64").Attr("email", email).Attr("amount", amount);
        b.Database("db", Dialect.SqlServer);
        var model = ResolutionKit.Resolve(b);
        var table = model.Db("db").Table("things");
        Assert.Equal("nvarchar(254)", table.Column("email").NativeType);
        Assert.Equal("decimal(12,4)", table.Column("amount").NativeType);
        Assert.Equal(254, model.Entity("Thing").Attributes.Single(a => a.Name == "email").Length);
        Assert.Equal("string", model.Entity("Thing").Attributes.Single(a => a.Name == "email").Type.Builtin);
    }

    [Fact]
    public void Project_type_maps_override_single_entries_and_overlay_native_types_win()
    {
        var b = new ModelBuilder(seed: 53);
        var thing = b.Entity("Thing").Key("id", "int64").Attr("name", "string").Attr("payload", "json").Attr("flag", "bool");
        var db = b.Database("db", Dialect.PostgreSql);
        b.Table("", db).OverlayFor(thing).Overlay(thing.AttrId("flag"), nativeType: "bit(1)");
        b.Settings(s => s with
        {
            TypeMaps = ImmutableDictionary<string, IReadOnlyDictionary<string, string>>.Empty
                .Add("postgresql", ImmutableDictionary<string, string>.Empty.Add("string", "text").Add("json", "json")),
        });
        var table = ResolutionKit.Resolve(b).Db("db").Table("things");
        Assert.Equal("text", table.Column("name").NativeType);
        Assert.Equal("json", table.Column("payload").NativeType);
        Assert.Equal("bigint", table.Column("id").NativeType);   // untouched entries keep the default map
        Assert.Equal("bit(1)", table.Column("flag").NativeType);
        Assert.Contains("s:typeMaps", table.Column("name").Dependencies);
    }

    [Fact]
    public void A_custom_type_native_type_wins_in_entity_child_and_junction_tables()
    {
        var b = new ModelBuilder(seed: 55);
        var lsn = b.ScalarType("Lsn", "binary").Length(8).NativeType("postgresql", "pg_lsn").NativeType("sqlserver", "binary({length})");
        var ledger = b.Entity("Ledger").Attr("position", lsn, a => a.Required()).CompositeKey("position");
        var entry = b.Entity("Entry").Key("id", "int64").Attr("at", lsn).Attr("history", lsn, a => a.Collection()).Attr("raw", "binary");
        b.Relation("records", ledger, entry, fromMax: MaxCardinality.One, toMax: MaxCardinality.Many, fromRole: "ledger");
        b.Relation("audits", ledger, entry, fromRole: "auditor", toRole: "audited");
        b.Database("pg", Dialect.PostgreSql);
        b.Database("ss", Dialect.SqlServer);
        b.Database("my", Dialect.MySql);
        // The project's type map for the base does not reach the custom type's own native type.
        b.Settings(s => s with
        {
            TypeMaps = ImmutableDictionary<string, IReadOnlyDictionary<string, string>>.Empty
                .Add("postgresql", ImmutableDictionary<string, string>.Empty.Add("binary", "bytea")),
        });
        var model = ResolutionKit.Resolve(b);

        Assert.Equal(ImmutableSortedDictionary<string, string>.Empty.Add("postgresql", "pg_lsn").Add("sqlserver", "binary({length})"),
            model.ScalarTypes.Single().NativeTypes);
        foreach (var (db, native, plain) in new[] { ("pg", "pg_lsn", "bytea"), ("ss", "binary(8)", "varbinary(max)"), ("my", "longblob", "longblob") })
        {
            var tables = model.Db(db).Tables;
            string Native(Func<RTable, bool> table, Func<RColumn, bool> column) => tables.Single(table).Columns.Single(column).NativeType;
            Assert.Equal(native, Native(t => t.Name == "ledgers", c => c.Name == "position"));
            Assert.Equal(native, Native(t => t.Name == "entries", c => c.Name == "at"));
            Assert.Equal(plain, Native(t => t.Name == "entries", c => c.Name == "raw"));
            Assert.Equal(native, Native(t => t.Name == "entries", c => c.IsForeignKey));
            Assert.Equal(native, Native(t => t.Attribute?.Name == "history", c => c.AttributePath is not null));
            Assert.Equal(native, Native(t => t.IsJunction, c => c.IsForeignKey && c.Name.StartsWith("auditor", StringComparison.Ordinal)));
            Assert.Equal("int64", tables.Single(t => t.IsJunction).Columns.Single(c => c.Name.StartsWith("audited", StringComparison.Ordinal)).Type);
        }

        Assert.Contains(model.Db("pg").Tables.Single(t => t.Name == "entries").Dependencies, d => d.Contains(lsn.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void An_overlay_native_type_wins_over_the_custom_type()
    {
        var b = new ModelBuilder(seed: 56);
        var lsn = b.ScalarType("Lsn", "binary").Length(8).NativeType("postgresql", "pg_lsn");
        var thing = b.Entity("Thing").Key("id", "int64").Attr("at", lsn).Attr("seen", lsn);
        var db = b.Database("db", Dialect.PostgreSql);
        b.Table("", db).OverlayFor(thing).Overlay(thing.AttrId("seen"), nativeType: "bytea");
        var table = ResolutionKit.Resolve(b).Db("db").Table("things");
        Assert.Equal("pg_lsn", table.Column("at").NativeType);
        Assert.Equal("bytea", table.Column("seen").NativeType);
    }

    [Fact]
    public void Database_defaults_follow_the_dialect()
    {
        var b = new ModelBuilder(seed: 54);
        foreach (var dialect in Enum.GetValues<Dialect>())
            b.Database(ResolutionValues.Kebab(dialect), dialect);
        var model = ResolutionKit.Resolve(b);
        Assert.Equal(("public", 63), (model.Db("postgresql").DefaultSchema, model.Db("postgresql").MaxIdentifierLength));
        Assert.Equal(("dbo", 128), (model.Db("sqlserver").DefaultSchema, model.Db("sqlserver").MaxIdentifierLength));
        Assert.Equal((null, 64), (model.Db("mysql").DefaultSchema, model.Db("mysql").MaxIdentifierLength));
        Assert.Equal((null, (int?)null), (model.Db("sqlite").DefaultSchema, model.Db("sqlite").MaxIdentifierLength));
        Assert.Equal((null, 128), (model.Db("oracle").DefaultSchema, model.Db("oracle").MaxIdentifierLength));
        Assert.Equal("reserved", model.Db("oracle").Quoting);
    }
}
