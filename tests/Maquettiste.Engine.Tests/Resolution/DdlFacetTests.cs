using System.Collections.Immutable;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// The DDL facets of designed tables: Unicode and fixed-length columns (the type map's variant entries), named default constraints,
/// deferrable foreign keys and column checks, with their rules MQ4056 (a feature the dialect lacks) and MQ4057 (a facet the type
/// lacks).
/// </summary>
public sealed class DdlFacetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<Dialect, string, bool, bool?, string> Variants => new()
    {
        { Dialect.SqlServer, "string", false, null, "nvarchar(40)" },
        { Dialect.SqlServer, "string", false, true, "nvarchar(40)" },
        { Dialect.SqlServer, "string", false, false, "varchar(40)" },
        { Dialect.SqlServer, "string", true, null, "nchar(40)" },
        { Dialect.SqlServer, "string", true, false, "char(40)" },
        { Dialect.SqlServer, "text", false, false, "varchar(max)" },
        { Dialect.SqlServer, "binary", true, null, "binary(40)" },
        { Dialect.PostgreSql, "string", true, null, "char(40)" },
        { Dialect.PostgreSql, "string", false, true, "varchar(40)" },
        { Dialect.PostgreSql, "binary", true, null, "bytea" },
        { Dialect.MySql, "string", true, null, "char(40)" },
        { Dialect.MySql, "string", false, true, "varchar(40) character set utf8mb4" },
        { Dialect.MySql, "string", true, true, "char(40) character set utf8mb4" },
        { Dialect.MySql, "text", false, true, "longtext character set utf8mb4" },
        { Dialect.MySql, "binary", true, null, "binary(40)" },
        { Dialect.Sqlite, "string", true, false, "text" },
        { Dialect.Sqlite, "binary", true, null, "blob" },
        { Dialect.Oracle, "string", false, true, "nvarchar2(40)" },
        { Dialect.Oracle, "string", true, null, "char(40 char)" },
        { Dialect.Oracle, "string", true, true, "nchar(40)" },
        { Dialect.Oracle, "text", false, true, "nclob" },
        { Dialect.Oracle, "binary", true, null, "raw(40)" },
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public void Unicode_and_fixed_length_columns_take_the_type_maps_variant_entries(Dialect dialect, string type, bool fixedLength, bool? unicode, string native)
    {
        var b = new ModelBuilder(seed: 70);
        var db = b.Database("db", dialect);
        b.Table("things", db).Column("value", type, length: 40)
            .Edit(t => t with { Columns = [t.Columns[0] with { FixedLength = fixedLength, Unicode = unicode }] });

        var column = ResolutionKit.Resolve(b).Db("db").Table("things").Column("value");

        Assert.Equal(native, column.NativeType);
        Assert.Equal(fixedLength, column.FixedLength);
        Assert.Equal(unicode, column.Unicode);
    }

    [Fact]
    public void A_project_type_map_overrides_a_variant_entry_and_a_missing_variant_falls_back_to_the_keyword()
    {
        var b = new ModelBuilder(seed: 71);
        b.Settings(s => s with
        {
            TypeMaps = ImmutableDictionary<string, IReadOnlyDictionary<string, string>>.Empty
                .Add("postgresql", new Dictionary<string, string> { ["string:fixed"] = "bpchar({length})" }),
        });
        var db = b.Database("db", Dialect.PostgreSql);
        b.Table("things", db).Column("code", "string", length: 3).Column("name", "string", length: 9)
            .Edit(t => t with { Columns = [t.Columns[0] with { FixedLength = true }, t.Columns[1] with { Unicode = false }] });

        var table = ResolutionKit.Resolve(b).Db("db").Table("things");

        Assert.Equal("bpchar(3)", table.Column("code").NativeType);
        Assert.Equal("varchar(9)", table.Column("name").NativeType);
    }

    [Fact]
    public void Default_names_deferral_and_column_checks_resolve()
    {
        var b = new ModelBuilder(seed: 72);
        var db = b.Database("db", Dialect.PostgreSql);
        var parent = b.Table("parents", db).Column("id", "int64", nullable: false).PrimaryKey("id");
        var child = b.Table("children", db).Column("id", "int64", nullable: false).Column("parent_id", "int64").Column("rank", "int32")
            .PrimaryKey("id").ForeignKey(parent, "parent_id");
        child.Edit(t => t with
        {
            Columns = [t.Columns[0], t.Columns[1], t.Columns[2] with { Default = System.Text.Json.JsonSerializer.SerializeToElement(1), DefaultName = "rank_default" }],
            ForeignKeys = [t.ForeignKeys[0] with { Deferrable = Deferrability.InitiallyDeferred }],
            Checks = [new CheckConstraint { Id = b.NewId(), Name = "ck_rank", Column = t.Columns[2].Id, Expression = new Dictionary<string, string> { ["*"] = "rank > 0" } }],
        });

        var table = ResolutionKit.Resolve(b).Db("db").Table("children");

        Assert.Equal("rank_default", table.Column("rank").DefaultName);
        Assert.Null(table.Column("id").DefaultName);
        Assert.Equal("initially-deferred", Assert.Single(table.ForeignKeys).Deferrable);
        var check = Assert.Single(table.Checks);
        Assert.Same(table.Column("rank"), check.Column);
    }

    private static async Task<List<Diagnostic>> Findings(ModelSnapshot model, params string[] rules) =>
        [.. (await ValidationFixture.Validator().ValidateAsync(model, ValidationScope.All, null, Ct)).Diagnostics.Where(d => rules.Contains(d.Rule))];

    [Fact]
    public async Task Unicode_and_fixed_length_on_a_type_without_them_are_errors()
    {
        var b = new ModelBuilder(seed: 73);
        var db = b.Database("db", Dialect.SqlServer);
        b.Table("things", db).Column("n", "int32").Column("d", "decimal").Column("s", "string").Column("t", "text").Column("x", "binary")
            .Edit(t => t with
            {
                Columns =
                [
                    t.Columns[0] with { Unicode = true },
                    t.Columns[1] with { FixedLength = true },
                    t.Columns[2] with { Unicode = false, FixedLength = true },
                    t.Columns[3] with { Unicode = false },
                    t.Columns[4] with { FixedLength = true, Length = 16 },
                ],
            });

        var findings = await Findings(b.Build(), "MQ4057");

        Assert.Equal(["/columns/0/unicode", "/columns/1/fixedLength"], findings.Select(d => d.JsonPointer));
        Assert.All(findings, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public async Task Features_the_dialect_lacks_are_warnings()
    {
        var b = new ModelBuilder(seed: 74);
        var mysql = b.Database("my", Dialect.MySql);
        var parent = b.Table("parents", mysql).Column("id", "int64", nullable: false).PrimaryKey("id");
        b.Table("children", mysql).Column("id", "int64", nullable: false).Column("parent_id", "int64").PrimaryKey("id").ForeignKey(parent, "parent_id")
            .Index(false, "parent_id")
            .Edit(t => t with
            {
                PrimaryKey = t.PrimaryKey! with { Clustered = true },
                ForeignKeys = [t.ForeignKeys[0] with { Deferrable = Deferrability.InitiallyImmediate }],
                Indexes = [t.Indexes[0] with { Include = [t.Columns[0].Id], Where = "parent_id > 0", Method = IndexMethod.Gin }],
            });
        var pg = b.Database("pg", Dialect.PostgreSql);
        b.Table("fine", pg).Column("id", "int64", nullable: false).Column("v", "int32").PrimaryKey("id").Index(false, "v")
            .Edit(t => t with { Indexes = [t.Indexes[0] with { Include = [t.Columns[0].Id], Where = "v > 0", Method = IndexMethod.Gin }] });

        var findings = await Findings(b.Build(), "MQ4056");

        Assert.Equal(["/primaryKey/clustered", "/foreignKeys/0/deferrable", "/indexes/0/include", "/indexes/0/where", "/indexes/0/method"],
            findings.Select(d => d.JsonPointer));
        Assert.All(findings, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
    }

    [Fact]
    public async Task A_column_check_naming_a_missing_column_is_reported()
    {
        var b = new ModelBuilder(seed: 75);
        var db = b.Database("db", Dialect.PostgreSql);
        b.Table("things", db).Column("id", "int64", nullable: false).PrimaryKey("id")
            .Edit(t => t with { Checks = [new CheckConstraint { Id = b.NewId(), Name = "ck", Column = "nope", Expression = new Dictionary<string, string> { ["*"] = "true" } }] });

        var finding = Assert.Single(await Findings(b.Build(), "MQ4008"));
        Assert.Equal("/checks/0/column", finding.JsonPointer);
    }
}
