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
        var db = b.Database("db", dialect);
        b.Table("things", db).Column("id", "int64", nullable: false).Column("value", keyword).PrimaryKey("id");
        Assert.Equal(native, ResolutionKit.Resolve(b).Db("db").Table("things").Column("value").NativeType);
    }

    [Fact]
    public void Every_dialect_maps_every_builtin_keyword()
    {
        foreach (var dialect in Enum.GetValues<Dialect>())
        {
            var map = DialectTypeMaps.Default(dialect);
            Assert.Equal(BuiltinTypes.All.Order(StringComparer.Ordinal), map.Keys.Where(k => !k.Contains(':', StringComparison.Ordinal)).Order(StringComparer.Ordinal));
            // Variant entries: fixed-length strings and binaries, Unicode and single-byte strings and texts; and the range and
            // multirange entries by subtype (DialectTypeMaps.RangeKey, section 3.4).
            static bool Range(string k) => k.StartsWith("range:", StringComparison.Ordinal) || k.StartsWith("multirange:", StringComparison.Ordinal);
            Assert.All(map.Keys.Where(k => k.Contains(':', StringComparison.Ordinal) && !Range(k)), k => Assert.Contains(k, (string[])
                ["string:fixed", "string:unicode", "string:ansi", "string:fixed:unicode", "string:fixed:ansi", "text:unicode", "text:ansi", "binary:fixed"]));
            Assert.Contains("string:fixed", map.Keys);
            Assert.Contains("binary:fixed", map.Keys);
        }
    }

    [Fact]
    public void Column_facets_win_over_convention_defaults()
    {
        var b = new ModelBuilder(seed: 51);
        var db = b.Database("db", Dialect.PostgreSql);
        b.Table("things", db).Column("id", "int64", nullable: false).Column("price", "decimal").Column("code", "string", length: 12).Column("at", "datetime")
            .Column("name", "string").Column("amount", "decimal").Column("seen", "datetime").PrimaryKey("id")
            .Edit(t => t with
            {
                Columns = [.. t.Columns.Select(c => c.Name switch
                {
                    "price" => c with { Precision = 10, Scale = 3 },
                    "at" => c with { Precision = 3 },
                    _ => c,
                })],
            });
        b.Settings(s => s with { Conventions = new Conventions { DefaultStringLength = 100, DecimalPrecision = 20, DatetimePrecision = 0 } });
        var table = ResolutionKit.Resolve(b).Db("db").Table("things");
        Assert.Equal("numeric(10,3)", table.Column("price").NativeType);
        Assert.Equal("varchar(12)", table.Column("code").NativeType);
        Assert.Equal("timestamp(3)", table.Column("at").NativeType);
        Assert.Equal("varchar(100)", table.Column("name").NativeType);
        Assert.Equal("numeric(20,2)", table.Column("amount").NativeType);
        Assert.Equal("timestamp(0)", table.Column("seen").NativeType);
    }

    [Fact]
    public void Scalar_type_facets_apply_to_the_attribute()
    {
        var b = new ModelBuilder(seed: 52);
        var email = b.ScalarType("Email", "string").Length(254);
        var amount = b.ScalarType("Amount", "decimal").Decimal(12, 4);
        b.Entity("Thing").Key("id", "int64").Attr("email", email).Attr("amount", amount);
        var model = ResolutionKit.Resolve(b);
        Assert.Equal((12, 4), (model.Entity("Thing").Attributes.Single(a => a.Name == "amount").Precision, model.Entity("Thing").Attributes.Single(a => a.Name == "amount").Scale));
        Assert.Equal(254, model.Entity("Thing").Attributes.Single(a => a.Name == "email").Length);
        Assert.Equal("string", model.Entity("Thing").Attributes.Single(a => a.Name == "email").Type.Builtin);
    }

    [Fact]
    public void Project_type_maps_override_single_entries_and_a_column_native_type_wins()
    {
        var b = new ModelBuilder(seed: 53);
        var db = b.Database("db", Dialect.PostgreSql);
        b.Table("things", db).Column("id", "int64", nullable: false).Column("name", "string").Column("payload", "json").Column("flag", "bool", nativeType: "bit(1)")
            .PrimaryKey("id");
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
