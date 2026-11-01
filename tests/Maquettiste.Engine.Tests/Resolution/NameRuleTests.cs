using System.Collections.Immutable;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Name rules (next-phase-1-design.md section 3.5): MQ4068 for index and constraint names that collide in a namespace the
/// dialect keeps, compared after cutting to the identifier limit and without case, and MQ4001 on derived names measured in
/// the dialect's unit.
/// </summary>
public sealed class NameRuleTests
{
    private static ImmutableDictionary<string, string> Sql(string text) => ImmutableDictionary<string, string>.Empty.Add("*", text);

    private static List<Diagnostic> Findings(ModelBuilder b, string rule = "MQ4068") =>
        [.. ResolutionKit.Resolve(b).Diagnostics.Where(d => d.Rule == rule)];

    /// <summary>A database with tables <c>accounts</c> (id, email, owner_id) and <c>owners</c> (id), each keyed by id.</summary>
    private static (ModelBuilder Builder, DatabaseBuilder Db, TableBuilder Accounts, TableBuilder Owners) Model(Dialect dialect, int seed)
    {
        var b = new ModelBuilder(seed: seed);
        var db = b.Database("db", dialect);
        var owners = b.Table("owners", db).Column("id", "int64", nullable: false).PrimaryKey("id");
        var accounts = b.Table("accounts", db).Column("id", "int64", nullable: false).Column("email", "string", nullable: true)
            .Column("owner_id", "int64", nullable: true).PrimaryKey("id");
        return (b, db, accounts, owners);
    }

    [Fact]
    public void Two_file_indexes_on_the_same_columns_derive_one_name_and_report_MQ4068()
    {
        var b = new ModelBuilder(seed: 2901);
        var db = b.Database("db", Dialect.PostgreSql);
        var documents = b.Table("documents", db).Column("id", "int64", nullable: false).Column("tags", "string", nullable: true).PrimaryKey("id");
        documents.Edit(t => t with
        {
            Indexes =
            [
                new TableIndex { Id = b.NewId(), Columns = [new IndexColumn { Column = t.Columns[1].Id }] },
                new TableIndex { Id = b.NewId(), Columns = [new IndexColumn { Column = t.Columns[1].Id }], Method = IndexMethod.Gin },
            ],
        });

        var resolved = ResolutionKit.Resolve(b);
        Assert.Equal(["ix_documents_tags", "ix_documents_tags"], resolved.Db("db").Table("documents").Indexes.Select(i => i.Name));
        var finding = Assert.Single(resolved.Diagnostics, d => d.Rule == "MQ4068");
        Assert.Equal((documents.Id, "/indexes/1"), (finding.ElementId, finding.JsonPointer));
        Assert.Equal(DiagnosticSeverity.Error, finding.Severity);
        Assert.Equal(
            "Index 'ix_documents_tags' of table 'public.documents' in database 'db' has the name of index 'ix_documents_tags' of table "
            + "'public.documents': postgresql keeps index and key names in one namespace per schema, shared with its tables, views and "
            + "sequences; rename one of them.",
            finding.Message);

        documents.Edit(t => t with { Indexes = [t.Indexes[0], t.Indexes[1] with { Name = "ix_documents_tags_gin" }] });
        Assert.Empty(Findings(b));
    }

    [Fact]
    public void A_unique_key_and_an_index_share_the_postgresql_schema_namespace()
    {
        var (b, _, accounts, owners) = Model(Dialect.PostgreSql, 2902);
        accounts.Edit(t => t with { Uniques = [new UniqueConstraint { Id = b.NewId(), Name = "account_key", Columns = [t.Columns[1].Id] }] });
        owners.Edit(t => t with { Indexes = [new TableIndex { Id = b.NewId(), Name = "ACCOUNT_KEY", Columns = [new IndexColumn { Column = t.Columns[0].Id }] }] });

        var finding = Assert.Single(Findings(b));
        Assert.Equal((owners.Id, "/indexes/0/name"), (finding.ElementId, finding.JsonPointer));
        Assert.StartsWith("Index 'ACCOUNT_KEY' of table 'public.owners' in database 'db' has the name of unique key 'account_key' of table 'public.accounts'",
            finding.Message, StringComparison.Ordinal);

        // A check named like a unique key of its own table collides in the table's constraint namespace.
        accounts.Edit(t => t with { Checks = [new CheckConstraint { Id = b.NewId(), Name = "ACCOUNT_KEY", Expression = Sql("id > 0") }] });
        owners.Edit(t => t with { Indexes = [] });
        Assert.Single(Findings(b), d => d.JsonPointer == "/checks/0/name" && d.ElementId == accounts.Id);
    }

    [Fact]
    public void An_index_named_like_a_table_or_view_collides_on_postgresql()
    {
        var (b, db, accounts, _) = Model(Dialect.PostgreSql, 2903);
        b.Add(new View { Id = b.NewId(), Name = "open_accounts", Database = db.Id, Body = Sql("SELECT id FROM accounts") });
        b.Add(new Sequence { Id = b.NewId(), Name = "account_numbers", Database = db.Id, Type = "int64" });
        accounts.Edit(t => t with
        {
            Indexes =
            [
                new TableIndex { Id = b.NewId(), Name = "owners", Columns = [new IndexColumn { Column = t.Columns[2].Id }] },
                new TableIndex { Id = b.NewId(), Name = "open_accounts", Columns = [new IndexColumn { Column = t.Columns[1].Id }] },
                new TableIndex { Id = b.NewId(), Name = "account_numbers", Columns = [new IndexColumn { Column = t.Columns[0].Id }] },
            ],
        });

        var findings = Findings(b).OrderBy(d => d.JsonPointer, StringComparer.Ordinal).ToList();
        Assert.Equal(["/indexes/0/name", "/indexes/1/name", "/indexes/2/name"], findings.Select(d => d.JsonPointer));
        Assert.Contains("has the name of table 'public.owners'", findings[0].Message, StringComparison.Ordinal);
        Assert.Contains("has the name of view 'public.open_accounts'", findings[1].Message, StringComparison.Ordinal);
        Assert.Contains("has the name of sequence 'public.account_numbers'", findings[2].Message, StringComparison.Ordinal);

        // SQL Server keeps index names per table, so none of them collides there.
        var (other, otherDb, otherAccounts, _) = Model(Dialect.SqlServer, 2903);
        other.Add(new View { Id = other.NewId(), Name = "open_accounts", Database = otherDb.Id, Body = Sql("SELECT id FROM accounts") });
        otherAccounts.Edit(t => t with { Indexes = [new TableIndex { Id = other.NewId(), Name = "owners", Columns = [new IndexColumn { Column = t.Columns[2].Id }] }] });
        Assert.Empty(Findings(other));
    }

    [Fact]
    public void Foreign_key_names_may_repeat_across_tables_on_postgresql_but_not_on_mysql()
    {
        foreach (var dialect in new[] { Dialect.PostgreSql, Dialect.MySql })
        {
            var (b, db, accounts, owners) = Model(dialect, 2904);
            var notes = b.Table("notes", db).Column("id", "int64", nullable: false).Column("owner_id", "int64", nullable: true).PrimaryKey("id")
                .ForeignKey(owners, "owner_id").Edit(t => t with { ForeignKeys = [t.ForeignKeys[0] with { Name = "fk_owner" }] });
            accounts.ForeignKey(owners, "owner_id").Edit(t => t with { ForeignKeys = [t.ForeignKeys[0] with { Name = "fk_owner" }] });

            var findings = Findings(b);
            if (dialect == Dialect.PostgreSql)
            {
                Assert.Empty(findings);
                continue;
            }

            var finding = Assert.Single(findings);
            Assert.Equal((notes.Id, "/foreignKeys/0/name"), (finding.ElementId, finding.JsonPointer));
            Assert.Equal(
                "Foreign key 'fk_owner' of table 'notes' in database 'db' has the name of foreign key 'fk_owner' of table 'accounts': mysql keeps "
                + "foreign key names in one namespace per schema; rename one of them.",
                finding.Message);
        }
    }

    [Fact]
    public void Names_compare_after_cutting_to_the_byte_limit()
    {
        var (b, _, accounts, _) = Model(Dialect.PostgreSql, 2905);
        var stem = "ix_" + new string('a', 61);
        accounts.Edit(t => t with
        {
            Indexes =
            [
                new TableIndex { Id = b.NewId(), Name = stem + "_email", Columns = [new IndexColumn { Column = t.Columns[1].Id }] },
                new TableIndex { Id = b.NewId(), Name = stem + "_owner", Columns = [new IndexColumn { Column = t.Columns[2].Id }] },
            ],
        });
        b.Settings(s => s with { Validation = new ValidationSettings { Rules = ImmutableDictionary<string, string>.Empty.Add("MQ4001", "off") } });
        Assert.Equal((70, 70), ((stem + "_email").Length, (stem + "_owner").Length));

        var resolved = ResolutionKit.Resolve(b);
        Assert.DoesNotContain(resolved.Diagnostics, d => d.Rule == "MQ4001");
        var finding = Assert.Single(resolved.Diagnostics, d => d.Rule == "MQ4068");
        Assert.Equal("/indexes/1/name", finding.JsonPointer);
        Assert.EndsWith($"; it keeps only the first 63 bytes of a name, and both names start with '{stem[..63]}'; rename one of them.", finding.Message,
            StringComparison.Ordinal);

        // Multibyte characters count in bytes: the cut never splits one, so 62 bytes of 'é' stay (31 characters) and the names collide.
        var wide = new string('é', 31);
        accounts.Edit(t => t with { Indexes = [t.Indexes[0] with { Name = wide + "ab" }, t.Indexes[1] with { Name = wide + "ba" }] });
        Assert.Empty(Findings(b));
        accounts.Edit(t => t with { Indexes = [t.Indexes[0] with { Name = wide + "éa" }, t.Indexes[1] with { Name = wide + "éb" }] });
        Assert.Contains($"start with '{wide}'", Assert.Single(Findings(b)).Message, StringComparison.Ordinal);
    }

    /// <summary>The cases of <see cref="Each_dialect_checks_its_own_namespaces"/>.</summary>
    public enum Case
    {
        /// <summary>An index of accounts named like the unique key of owners.</summary>
        IndexLikeOtherTablesUnique,

        /// <summary>A check of accounts named like a foreign key of accounts.</summary>
        CheckLikeOwnForeignKey,

        /// <summary>A check of accounts named like a check of owners.</summary>
        CheckLikeOtherTablesCheck,

        /// <summary>A check of accounts named like a foreign key of owners.</summary>
        CheckLikeOtherTablesForeignKey,

        /// <summary>An index of accounts named like an index of owners.</summary>
        IndexLikeOtherTablesIndex,

        /// <summary>An index of accounts named like its own primary key.</summary>
        IndexLikeOwnPrimaryKey,

        /// <summary>An index of accounts named like its own unique key.</summary>
        IndexLikeOwnUnique,

        /// <summary>A check of accounts named like a routine.</summary>
        CheckLikeRoutine,

        /// <summary>A default constraint of accounts named like the primary key of owners.</summary>
        DefaultLikeOtherTablesPrimaryKey,

        /// <summary>An index of accounts named like the table owners.</summary>
        IndexLikeTable,

        /// <summary>The primary key of accounts named like its own index (one holder in two namespaces), and a check named like an index.</summary>
        KeySharesItsIndexName,
    }

    /// <summary>Rows: the dialect, the case, the namespace the message names (null: no finding) and the table that reports it.</summary>
    public static TheoryData<Dialect, Case, string?, string?> Namespaces => new()
    {
        { Dialect.PostgreSql, Case.IndexLikeOtherTablesUnique, "postgresql keeps index and key names in one namespace per schema", "owners" },
        { Dialect.PostgreSql, Case.CheckLikeOwnForeignKey, "postgresql keeps constraint names in one namespace per table", "accounts" },
        { Dialect.PostgreSql, Case.CheckLikeOtherTablesCheck, null, null },
        { Dialect.SqlServer, Case.CheckLikeRoutine, "sqlserver keeps constraint names in one namespace per schema, shared with its tables, views, sequences and routines", "accounts" },
        { Dialect.SqlServer, Case.CheckLikeOtherTablesCheck, "sqlserver keeps constraint names in one namespace per schema", "owners" },
        { Dialect.SqlServer, Case.DefaultLikeOtherTablesPrimaryKey, "sqlserver keeps constraint names in one namespace per schema", "owners" },
        { Dialect.SqlServer, Case.IndexLikeOwnPrimaryKey, "sqlserver keeps index names, the primary and unique keys' indexes included, in one namespace per table", "accounts" },
        { Dialect.SqlServer, Case.IndexLikeOtherTablesIndex, null, null },
        { Dialect.MySql, Case.CheckLikeOtherTablesCheck, "mysql keeps check constraint names in one namespace per schema", "owners" },
        { Dialect.MySql, Case.CheckLikeOtherTablesForeignKey, null, null },
        { Dialect.MySql, Case.IndexLikeOwnUnique, "mysql keeps index and unique key names in one namespace per table", "accounts" },
        { Dialect.MySql, Case.IndexLikeOtherTablesIndex, null, null },
        { Dialect.Oracle, Case.IndexLikeOtherTablesIndex, "oracle keeps index names, the keys' indexes included, in one namespace per schema", "owners" },
        { Dialect.Oracle, Case.CheckLikeOtherTablesForeignKey, "oracle keeps constraint names in one namespace per schema", "accounts" },
        { Dialect.Oracle, Case.KeySharesItsIndexName, null, null },
        { Dialect.Sqlite, Case.IndexLikeTable, "sqlite keeps index names in one namespace per schema, shared with its tables and views", "accounts" },
        { Dialect.Sqlite, Case.IndexLikeOtherTablesIndex, "sqlite keeps index names in one namespace per schema", "owners" },
        { Dialect.Sqlite, Case.CheckLikeOtherTablesCheck, null, null },
    };

    [Theory]
    [MemberData(nameof(Namespaces))]
    public void Each_dialect_checks_its_own_namespaces(Dialect dialect, Case scenario, string? namespaceText, string? reporter)
    {
        var (b, db, accounts, owners) = Model(dialect, 2906);
        const string name = "shared_name";
        TableIndex Index(Table t, int column, string? indexName = name) =>
            new() { Id = b.NewId(), Name = indexName, Columns = [new IndexColumn { Column = t.Columns[column].Id }] };
        CheckConstraint Check(string checkName) => new() { Id = b.NewId(), Name = checkName, Expression = Sql("id > 0") };
        switch (scenario)
        {
            case Case.IndexLikeOtherTablesUnique:
                owners.Edit(t => t with { Uniques = [new UniqueConstraint { Id = b.NewId(), Name = name, Columns = [t.Columns[0].Id] }] });
                accounts.Edit(t => t with { Indexes = [Index(t, 1)] });
                break;
            case Case.CheckLikeOwnForeignKey:
                accounts.ForeignKey(owners, "owner_id").Edit(t => t with { ForeignKeys = [t.ForeignKeys[0] with { Name = name }], Checks = [Check(name)] });
                break;
            case Case.CheckLikeOtherTablesCheck:
                owners.Edit(t => t with { Checks = [Check(name)] });
                accounts.Edit(t => t with { Checks = [Check(name)] });
                break;
            case Case.CheckLikeOtherTablesForeignKey:
                var notes = b.Table("aaa_notes", db).Column("id", "int64", nullable: false).Column("owner_id", "int64", nullable: true).PrimaryKey("id")
                    .ForeignKey(owners, "owner_id");
                notes.Edit(t => t with { ForeignKeys = [t.ForeignKeys[0] with { Name = name }] });
                accounts.Edit(t => t with { Checks = [Check(name)] });
                break;
            case Case.IndexLikeOtherTablesIndex:
                owners.Edit(t => t with { Indexes = [Index(t, 0)] });
                accounts.Edit(t => t with { Indexes = [Index(t, 1)] });
                break;
            case Case.IndexLikeOwnPrimaryKey:
                accounts.Edit(t => t with { PrimaryKey = t.PrimaryKey! with { Name = name }, Indexes = [Index(t, 1)] });
                break;
            case Case.IndexLikeOwnUnique:
                accounts.Edit(t => t with { Uniques = [new UniqueConstraint { Id = b.NewId(), Name = name, Columns = [t.Columns[1].Id] }], Indexes = [Index(t, 2)] });
                break;
            case Case.CheckLikeRoutine:
                b.Add(new Routine { Id = b.NewId(), Name = name, Database = db.Id, Body = Sql("SELECT 1") });
                accounts.Edit(t => t with { Checks = [Check(name)] });
                break;
            case Case.DefaultLikeOtherTablesPrimaryKey:
                owners.Edit(t => t with { PrimaryKey = t.PrimaryKey! with { Name = name } });
                accounts.Edit(t => t with
                {
                    Columns = [t.Columns[0], t.Columns[1] with { Default = System.Text.Json.JsonSerializer.SerializeToElement("x"), DefaultName = name }, t.Columns[2]],
                });
                break;
            case Case.IndexLikeTable:
                accounts.Edit(t => t with { Indexes = [Index(t, 1, "owners")] });
                break;
            case Case.KeySharesItsIndexName:
                accounts.Edit(t => t with { PrimaryKey = t.PrimaryKey! with { Name = name }, Indexes = [Index(t, 1, "other_name")], Checks = [Check("other_name")] });
                break;
        }

        var findings = Findings(b);
        if (namespaceText is null)
        {
            Assert.Empty(findings);
            return;
        }

        var finding = Assert.Single(findings);
        Assert.Contains(namespaceText, finding.Message, StringComparison.Ordinal);
        Assert.Equal(reporter == "owners" ? owners.Id : accounts.Id, finding.ElementId);
    }

    [Fact]
    public void Derived_name_length_counts_utf8_bytes_on_postgresql()
    {
        foreach (var dialect in new[] { Dialect.PostgreSql, Dialect.SqlServer })
        {
            var b = new ModelBuilder(seed: 2907);
            var db = b.Database("db", dialect);
            var name = new string('é', 31);
            var table = b.Table(name, db).Column("id", "int64", nullable: false).PrimaryKey("id");

            var findings = Findings(b, "MQ4001");
            if (dialect == Dialect.SqlServer)
            {
                Assert.Empty(findings);
                continue;
            }

            var finding = Assert.Single(findings);
            Assert.Equal(table.Id, finding.ElementId);
            Assert.Equal($"Primary key name 'pk_{name}' in database 'db' is 65 bytes long; the postgresql limit is 63.", finding.Message);
        }
    }
}
