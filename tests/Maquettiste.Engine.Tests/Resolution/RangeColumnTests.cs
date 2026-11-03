using System.Collections.Immutable;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Range and multirange columns (next-phase-1-design.md section 3.4): column-only keywords with a subtype, resolved to PostgreSQL's
/// built-in range types without facet defaults, stored as the dialect's text type elsewhere (MQ4056), a missing or misplaced subtype
/// MQ4057 and facets MQ3013, and values (defaults, seed cells) taken as the text form.
/// </summary>
public sealed class RangeColumnTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<Diagnostic>> Findings(ModelSnapshot model, params string[] rules) =>
        [.. (await ValidationFixture.Validator().ValidateAsync(model, ValidationScope.All, null, Ct)).Diagnostics.Where(d => rules.Contains(d.Rule))];

    /// <summary>Sets the subtype (and any other change) of the named columns.</summary>
    private static Table With(Table table, params (string Name, Func<Column, Column> Change)[] changes) => table with
    {
        Columns = [.. table.Columns.Select(c => changes.FirstOrDefault(x => x.Name == c.Name) is { Change: { } change } ? change(c) : c)],
    };

    [Fact]
    public async Task Range_and_multirange_resolve_to_postgresql_builtin_types_without_facet_defaults()
    {
        var b = new ModelBuilder(seed: 250);
        var db = b.Database("db", Dialect.PostgreSql);
        b.Table("bookings", db).Column("id", "int64", nullable: false).PrimaryKey("id")
            .Column("seats", "range").Column("ids", "multirange").Column("amounts", "range").Column("days", "multirange")
            .Column("local_during", "range").Column("during", "range")
            .Edit(t => With(t,
                ("seats", c => c with { Subtype = "int32" }), ("ids", c => c with { Subtype = "int64" }), ("amounts", c => c with { Subtype = "decimal" }),
                ("days", c => c with { Subtype = "date" }), ("local_during", c => c with { Subtype = "datetime" }),
                ("during", c => c with { Subtype = "datetimeoffset", Default = JsonDocument.Parse("\"[2026-01-01,2027-01-01)\"").RootElement })));

        Assert.Empty(await Findings(b.Build(), "MQ3013", "MQ3019", "MQ4056", "MQ4057"));
        var resolved = ResolutionKit.Resolve(b).Db("db");
        var table = resolved.Table("bookings");
        Assert.Equal(
            [
                ("seats", "range", "int32", "int4range"), ("ids", "multirange", "int64", "int8multirange"), ("amounts", "range", "decimal", "numrange"),
                ("days", "multirange", "date", "datemultirange"), ("local_during", "range", "datetime", "tsrange"),
                ("during", "range", "datetimeoffset", "tstzrange"),
            ],
            table.Columns.Skip(1).Select(c => (c.Name, c.Type, c.Subtype!, c.NativeType)));
        Assert.All(table.Columns.Skip(1), c => Assert.Equal((null, null, null), (c.Length, c.Precision, c.Scale)));
        Assert.Null(table.Column("id").Subtype);

        var snapshot = SnapshotCapture.Capture(resolved, 1, ct: Ct).Tables.Single();
        var during = snapshot.Columns.Single(c => c.Name == "during");
        Assert.Equal(("range", "datetimeoffset", (int?)null, "tstzrange"), (during.Type, during.Subtype, during.Length, during.NativeType));
        Assert.Null(snapshot.Columns.Single(c => c.Name == "id").Subtype);
    }

    [Fact]
    public async Task A_missing_or_misplaced_subtype_is_MQ4057_and_facets_are_MQ3013()
    {
        var b = new ModelBuilder(seed: 251);
        var db = b.Database("db", Dialect.PostgreSql);
        var json = (string text) => JsonDocument.Parse(text).RootElement;
        var table = b.Table("slots", db).Column("id", "int64", nullable: false).PrimaryKey("id")
            .Column("bare", "range").Column("code", "string", length: 10).Column("sized", "range", length: 20).Column("exact", "multirange")
            .Column("text_default", "range").Column("number_default", "range")
            .Edit(t => With(t,
                ("code", c => c with { Subtype = "int32" }), ("sized", c => c with { Subtype = "int32" }),
                ("exact", c => c with { Subtype = "decimal", Precision = 10, Scale = 2 }),
                ("text_default", c => c with { Subtype = "int32", Default = json("\"[1,10)\"") }),
                ("number_default", c => c with { Subtype = "int32", Default = json("5") })));
        var built = table.Build() as Table;
        b.Add(new Seed
        {
            Id = "01JB2Q0M8X4T5V6W7Y8Z9A0R01", Name = "slots", Target = table.Id,
            Columns = [built!.Columns[0].Id, built.Columns[5].Id],
            Rows =
            [
                new SeedRow { Id = "01JB2Q0M8X4T5V6W7Y8Z9A0R02", Values = [json("1"), json("\"[1,5)\"")] },
                new SeedRow { Id = "01JB2Q0M8X4T5V6W7Y8Z9A0R03", Values = [json("2"), json("7")] },
            ],
        });

        var findings = await Findings(b.Build(), "MQ3013", "MQ3019", "MQ4057", "MQ7109");
        Assert.Equal(
            [
                ("MQ3013", "/columns/3/length"), ("MQ3013", "/columns/4/precision"), ("MQ3013", "/columns/4/scale"), ("MQ3019", "/columns/6/default"),
                ("MQ4057", "/columns/1/type"), ("MQ4057", "/columns/2/subtype"),
            ],
            findings.Where(d => d.Rule != "MQ7109").Select(d => (d.Rule, d.JsonPointer!)).Order());
        Assert.Contains("is a range column without a subtype", findings.Single(d => d.JsonPointer == "/columns/1/type").Message, StringComparison.Ordinal);
        Assert.Contains("sets subtype, which only range and multirange columns have", findings.Single(d => d.JsonPointer == "/columns/2/subtype").Message,
            StringComparison.Ordinal);
        Assert.Contains("expected a string (the range's text form)", findings.Single(d => d.Rule == "MQ3019").Message, StringComparison.Ordinal);
        var seed = Assert.Single(findings, d => d.Rule == "MQ7109");
        Assert.Contains("01JB2Q0M8X4T5V6W7Y8Z9A0R03", seed.Message, StringComparison.Ordinal);
        Assert.Contains("the range's text form", seed.Message, StringComparison.Ordinal);

        // Resolution goes on: a range column without a subtype is stored as text, and a misplaced subtype is dropped.
        var resolved = ResolutionKit.Resolve(b).Db("db").Table("slots");
        Assert.Equal(("text", (string?)null), (resolved.Column("bare").NativeType, resolved.Column("bare").Subtype));
        Assert.Equal(("varchar(10)", (string?)null), (resolved.Column("code").NativeType, resolved.Column("code").Subtype));
    }

    [Fact]
    public async Task Range_outside_postgresql_is_text_and_MQ4056()
    {
        foreach (var (dialect, text) in new[] { (Dialect.SqlServer, "nvarchar(max)"), (Dialect.MySql, "longtext"), (Dialect.Sqlite, "text"), (Dialect.Oracle, "clob") })
        {
            var b = new ModelBuilder(seed: 252);
            b.Settings(s => s with
            {
                TypeMaps = ImmutableDictionary<string, IReadOnlyDictionary<string, string>>.Empty
                    .Add(Engine.Resolution.DialectTypeMaps.Name(dialect), new Dictionary<string, string> { ["range:int64"] = "varchar(100)" }),
            });
            var db = b.Database("db", dialect);
            b.Table("slots", db).Column("id", "int64", nullable: false).PrimaryKey("id")
                .Column("seats", "range").Column("days", "multirange").Column("own", "range", nativeType: "varchar(200)").Column("mapped", "range")
                .Edit(t => With(t,
                    ("seats", c => c with { Subtype = "int32" }), ("days", c => c with { Subtype = "date" }), ("own", c => c with { Subtype = "int32" }),
                    ("mapped", c => c with { Subtype = "int64" })));

            var findings = await Findings(b.Build(), "MQ4056");
            Assert.Equal(["/columns/1/type", "/columns/2/type"], findings.Select(d => d.JsonPointer!).Order(StringComparer.Ordinal));
            Assert.Contains("is a range column, which " + Engine.Resolution.DialectTypeMaps.Name(dialect), findings[0].Message, StringComparison.Ordinal);
            Assert.Contains("stores it as the dialect's text type", findings[0].Message, StringComparison.Ordinal);

            var table = ResolutionKit.Resolve(b).Db("db").Table("slots");
            Assert.Equal([text, text, "varchar(200)", "varchar(100)"], table.Columns.Skip(1).Select(c => c.NativeType));
            Assert.Equal(["int32", "date", "int32", "int64"], table.Columns.Skip(1).Select(c => c.Subtype));
        }
    }
}
