using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// MQ4059: a foreign key references the referenced table's primary key or one of its unique keys (a unique constraint, or a unique
/// index without a filter, which Oracle does not take), in any order; and MQ4008 points at the column a key names that the table no
/// longer has.
/// </summary>
public sealed class ForeignKeyTargetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<Diagnostic>> Findings(ModelSnapshot model, params string[] rules) =>
        [.. (await ValidationFixture.Validator().ValidateAsync(model, ValidationScope.All, null, Ct)).Diagnostics.Where(d => rules.Contains(d.Rule))];

    [Theory]
    [InlineData(Dialect.PostgreSql)]
    [InlineData(Dialect.Oracle)]
    public async Task A_foreign_key_to_columns_that_are_not_a_key_is_an_error(Dialect dialect)
    {
        var b = new ModelBuilder(seed: 91);
        var db = b.Database("db", dialect);
        var parent = b.Table("parents", db).Column("id", "int64", nullable: false).Column("code", "string", length: 8).Column("region", "string", length: 8)
            .Column("name", "string", length: 40).Column("tag", "string", length: 8).PrimaryKey("id").Index(true, "tag");
        parent.Edit(t => t with { Uniques = [new UniqueConstraint { Id = b.NewId(), Columns = [t.Columns[2].Id, t.Columns[1].Id] }] });
        var child = b.Table("children", db).Column("id", "int64", nullable: false).Column("parent_id", "int64").Column("parent_code", "string", length: 8)
            .Column("parent_region", "string", length: 8).Column("parent_name", "string", length: 40).Column("parent_tag", "string", length: 8).PrimaryKey("id");
        string P(string name) => parent.ColumnId(name);
        string C(string name) => child.ColumnId(name);
        child.Edit(t => t with
        {
            ForeignKeys =
            [
                new ForeignKey { Id = b.NewId(), Name = "fk_pk", Columns = [C("parent_id")], ReferencesTable = parent.Id },
                new ForeignKey { Id = b.NewId(), Name = "fk_pk_named", Columns = [C("parent_id")], ReferencesTable = parent.Id, ReferencesColumns = [P("id")] },
                new ForeignKey { Id = b.NewId(), Name = "fk_unique_any_order", Columns = [C("parent_code"), C("parent_region")], ReferencesTable = parent.Id, ReferencesColumns = [P("code"), P("region")] },
                new ForeignKey { Id = b.NewId(), Name = "fk_not_a_key", Columns = [C("parent_name")], ReferencesTable = parent.Id, ReferencesColumns = [P("name")] },
                new ForeignKey { Id = b.NewId(), Name = "fk_part_of_a_key", Columns = [C("parent_code")], ReferencesTable = parent.Id, ReferencesColumns = [P("code")] },
                new ForeignKey { Id = b.NewId(), Name = "fk_unique_index", Columns = [C("parent_tag")], ReferencesTable = parent.Id, ReferencesColumns = [P("tag")] },
            ],
        });

        var findings = await Findings(b.Build(), "MQ4059");

        var expected = dialect == Dialect.Oracle ? new[] { 3, 4, 5 } : [3, 4];
        Assert.Equal(expected.Select(i => $"/foreignKeys/{i}/referencesColumns"), findings.Select(d => d.JsonPointer));
        Assert.All(findings, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Contains("Foreign key 'fk_not_a_key' references (name) of table 'parents'", findings[0].Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A key names each referenced column once: (a1, a2) to (id, id) has the primary key's columns as a set, and is still refused. MySQL
    /// also needs an index that starts with the referenced columns in the key's order: the unique (region, code) referenced as (code,
    /// region) is refused there, and taken once an index starts with (code, region).
    /// </summary>
    [Theory]
    [InlineData(Dialect.PostgreSql)]
    [InlineData(Dialect.MySql)]
    public async Task A_key_naming_a_referenced_column_twice_or_out_of_order_on_mysql_is_an_error(Dialect dialect)
    {
        var b = new ModelBuilder(seed: 93);
        var db = b.Database("db", dialect);
        var parent = b.Table("parents", db).Column("id", "int64", nullable: false).Column("code", "string", length: 8).Column("region", "string", length: 8)
            .PrimaryKey("id");
        parent.Edit(t => t with { Uniques = [new UniqueConstraint { Id = b.NewId(), Columns = [t.Columns[2].Id, t.Columns[1].Id] }] });
        var child = b.Table("children", db).Column("id", "int64", nullable: false).Column("a1", "int64").Column("a2", "int64")
            .Column("parent_code", "string", length: 8).Column("parent_region", "string", length: 8).PrimaryKey("id");
        string P(string name) => parent.ColumnId(name);
        string C(string name) => child.ColumnId(name);
        child.Edit(t => t with
        {
            ForeignKeys =
            [
                new ForeignKey { Id = b.NewId(), Name = "fk_twice", Columns = [C("a1"), C("a2")], ReferencesTable = parent.Id, ReferencesColumns = [P("id"), P("id")] },
                new ForeignKey { Id = b.NewId(), Name = "fk_unique_in_order", Columns = [C("parent_region"), C("parent_code")], ReferencesTable = parent.Id, ReferencesColumns = [P("region"), P("code")] },
                new ForeignKey { Id = b.NewId(), Name = "fk_unique_other_order", Columns = [C("parent_code"), C("parent_region")], ReferencesTable = parent.Id, ReferencesColumns = [P("code"), P("region")] },
            ],
        });

        var findings = await Findings(b.Build(), "MQ4059");

        var expected = dialect == Dialect.MySql ? new[] { 0, 2 } : [0];
        Assert.Equal(expected.Select(i => $"/foreignKeys/{i}/referencesColumns"), findings.Select(d => d.JsonPointer));
        Assert.Contains("naming column 'id' more than once", findings[0].Message, StringComparison.Ordinal);
        if (dialect == Dialect.MySql)
        {
            Assert.Contains("in an order no index of it starts with", findings[1].Message, StringComparison.Ordinal);
            parent.Index(false, "code", "region", "id");
            Assert.Equal(["/foreignKeys/0/referencesColumns"], (await Findings(b.Build(), "MQ4059")).Select(d => d.JsonPointer));
        }
    }

    [Fact]
    public async Task A_key_naming_a_column_the_table_no_longer_has_points_at_it()
    {
        var b = new ModelBuilder(seed: 92);
        var db = b.Database("db", Dialect.PostgreSql);
        var parent = b.Table("parents", db).Column("id", "int64", nullable: false).PrimaryKey("id");
        b.Table("children", db).Column("id", "int64", nullable: false).Column("parent_id", "int64").PrimaryKey("id").ForeignKey(parent, "parent_id")
            .Edit(t => t with { Columns = [t.Columns[0]] });

        var finding = Assert.Single(await Findings(b.Build(), "MQ4008"));

        Assert.Equal("/foreignKeys/0/columns/0", finding.JsonPointer);
        Assert.Contains("Foreign key", finding.Message, StringComparison.Ordinal);
    }
}
