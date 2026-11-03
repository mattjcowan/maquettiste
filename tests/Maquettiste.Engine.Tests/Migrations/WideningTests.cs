using Maquettiste.Engine.Migrations;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Migrations;

/// <summary>The widening table (next-phase-1-design.md section 3.2, scope 6.2) and its copy in the schema changes guide.</summary>
public sealed class WideningTests
{
    [Theory]
    // Widenings, one or more per row of the table.
    [InlineData("varchar(10)", "varchar(20)", TypeChange.Widening)]
    [InlineData("character varying(10)", "VARCHAR(20)", TypeChange.Widening)]
    [InlineData("varchar(10)", "varchar", TypeChange.Widening)]
    [InlineData("varchar(10)", "text", TypeChange.Widening)]
    [InlineData("char(2)", "character(3)", TypeChange.Widening)]
    [InlineData("smallint", "integer", TypeChange.Widening)]
    [InlineData("int2", "int8", TypeChange.Widening)]
    [InlineData("integer", "bigint", TypeChange.Widening)]
    [InlineData("int4", "bigint", TypeChange.Widening)]
    [InlineData("numeric(10, 2)", "numeric(12, 2)", TypeChange.Widening)]
    [InlineData("numeric(10,2)", "numeric(11,3)", TypeChange.Widening)]
    [InlineData("decimal(8)", "numeric(10, 2)", TypeChange.Widening)]
    [InlineData("numeric(10, 2)", "numeric", TypeChange.Widening)]
    [InlineData("timestamp(3)", "timestamp(6)", TypeChange.Widening)]
    [InlineData("timestamp(3) without time zone", "timestamp", TypeChange.Widening)]
    [InlineData("timestamptz(0)", "timestamp with time zone", TypeChange.Widening)]
    [InlineData("time(2)", "time(4)", TypeChange.Widening)]
    [InlineData("timetz(2)", "time(4) with time zone", TypeChange.Widening)]
    [InlineData("interval(0)", "interval(3)", TypeChange.Widening)]
    [InlineData("varchar(10)[]", "varchar(20)[]", TypeChange.Widening)]
    [InlineData("integer[][]", "bigint[][]", TypeChange.Widening)]
    [InlineData("integer ARRAY", "bigint[]", TypeChange.Widening)]
    // The same type once aliases, case, spaces and defaults are folded.
    [InlineData("varchar(20)", "character varying (20)", TypeChange.Same)]
    [InlineData("timestamp", "timestamp(6) without time zone", TypeChange.Same)]
    [InlineData("numeric(10)", "numeric(10, 0)", TypeChange.Same)]
    [InlineData("uuid", "UUID", TypeChange.Same)]
    [InlineData("integer ARRAY[4]", "int4[]", TypeChange.Same)]
    // Narrowings: the reverse is a widening.
    [InlineData("text", "varchar(20)", TypeChange.Narrowing)]
    [InlineData("varchar(32)", "varchar(20)", TypeChange.Narrowing)]
    [InlineData("bigint", "integer", TypeChange.Narrowing)]
    [InlineData("numeric(12, 2)", "numeric(10, 2)", TypeChange.Narrowing)]
    [InlineData("numeric", "numeric(10, 2)", TypeChange.Narrowing)]
    [InlineData("timestamp(6)", "timestamp(3)", TypeChange.Narrowing)]
    [InlineData("bigint[]", "integer[]", TypeChange.Narrowing)]
    // Not widenings either way: conversions.
    [InlineData("real", "double precision", TypeChange.Conversion)]
    [InlineData("float4", "float8", TypeChange.Conversion)]
    [InlineData("timestamp", "timestamptz", TypeChange.Conversion)]
    [InlineData("timestamp with time zone", "timestamp", TypeChange.Conversion)]
    [InlineData("char(10)", "varchar(10)", TypeChange.Conversion)]
    [InlineData("integer", "numeric(12, 0)", TypeChange.Conversion)]
    [InlineData("smallint", "numeric", TypeChange.Conversion)]
    [InlineData("jsonb", "json", TypeChange.Conversion)]
    [InlineData("numeric(10, 2)", "numeric(10, 4)", TypeChange.Conversion)]
    [InlineData("integer[]", "bigint[][]", TypeChange.Conversion)]
    [InlineData("varchar", "text", TypeChange.Conversion)]
    [InlineData("billing.amount", "billing.price", TypeChange.Conversion)]
    public void Postgres_widening_table_matches_the_scope(string from, string to, TypeChange expected)
    {
        Assert.Equal(expected, Widening.Classify(Dialect.PostgreSql, from, to));
    }

    [Fact]
    public void Other_dialects_have_no_widening_table()
    {
        foreach (var dialect in Enum.GetValues<Dialect>().Where(d => d != Dialect.PostgreSql))
        {
            Assert.Empty(Widening.Rules(dialect));
            Assert.Equal(TypeChange.Conversion, Widening.Classify(dialect, "nvarchar(10)", "nvarchar(20)"));
            Assert.Equal(TypeChange.Conversion, Widening.Classify(dialect, "int", "bigint"));
            Assert.Equal(TypeChange.Same, Widening.Classify(dialect, "NVARCHAR(10)", "nvarchar( 10 )"));
        }
    }

    [Fact]
    public void Change_type_sides_classify_by_native_type_else_by_facets()
    {
        static MigrationState S(string? type = null, int? length = null, int? precision = null, int? scale = null, string? native = null) =>
            new() { Type = type, Length = length, Precision = precision, Scale = scale, NativeType = native };

        Assert.Equal(TypeChange.Narrowing, Widening.Classify(Dialect.PostgreSql, S("string", 32, native: "text"), S("string", 32, native: "varchar(20)")));
        Assert.Equal(TypeChange.Narrowing, Widening.Classify(Dialect.PostgreSql, S("string", 120), S("string", 80)));
        Assert.Equal(TypeChange.Widening, Widening.Classify(Dialect.PostgreSql, S("string", 80), S("string", 120)));
        Assert.Equal(TypeChange.Widening, Widening.Classify(Dialect.PostgreSql, S("decimal", precision: 10, scale: 2), S("decimal", precision: 12, scale: 2)));
        Assert.Equal(TypeChange.Same, Widening.Classify(Dialect.PostgreSql, S("int32"), S("int32")));
        Assert.Equal(TypeChange.Conversion, Widening.Classify(Dialect.PostgreSql, S("int32"), S("int64")));
        Assert.Equal(TypeChange.Conversion, Widening.Classify(Dialect.SqlServer, S("string", 80), S("string", 120)));

        // One side pins a native type: the other side's keyword and facets render through the type map before the two compare.
        Assert.Equal(TypeChange.Widening, Widening.Classify(Dialect.PostgreSql, S("string", 80), S("string", 80, native: "text")));
        Assert.Equal(TypeChange.Narrowing, Widening.Classify(Dialect.PostgreSql, S("string", native: "text"), S("string", 20)));
        Assert.Equal(TypeChange.Same, Widening.Classify(Dialect.PostgreSql, S("string", native: "varchar(20)"), S("string", 20)));
        Assert.Equal(TypeChange.Same, Widening.Classify(Dialect.PostgreSql, S("datetime"), S("datetime", native: "timestamp"))); // a missing time precision reads as 6
        Assert.Equal(TypeChange.Conversion, Widening.Classify(Dialect.PostgreSql, S("string"), S("string", native: "text"))); // no length to render
        Assert.Equal(TypeChange.Conversion, Widening.Classify(Dialect.SqlServer, S("string", native: "nvarchar(max)"), S("string", 20)));
    }

    [Fact]
    public void Widening_table_in_the_guide_matches_the_code()
    {
        var lines = File.ReadAllLines(Path.Combine(Fixtures.RepoRoot, "docs", "guides", "schema-changes.md"));
        var header = Array.IndexOf(lines, "| From | To | Widening when |");
        Assert.True(header >= 0, "docs/guides/schema-changes.md has no widening table.");
        var rows = lines.Skip(header + 2).TakeWhile(l => l.StartsWith('|')).ToList();

        var expected = Widening.Rules(Dialect.PostgreSql).Select(r => $"| `{r.From}` | `{r.To}` | {When(r.When)} |")
            .Append("| an array of any of these | the array of its widening, same dimensions | as its element type |")
            .Append("| a not-null column | the column made nullable | always |");
        Assert.Equal(expected, rows);

        static string When(WideningCondition condition) => condition switch
        {
            WideningCondition.Always => "always",
            WideningCondition.LongerLength => "`m > n`",
            WideningCondition.DigitsKept => "`s' >= s` and `p' - s' >= p - s`",
            _ => "`p' > p`",
        };
    }
}
