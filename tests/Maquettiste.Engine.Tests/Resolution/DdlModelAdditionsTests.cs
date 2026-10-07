using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// The DDL model additions of the coverage review: expression index columns and key prefix lengths, unique constraints with nulls not
/// distinct, identity options, views with a column list, check option, materialization and dependencies (explicit and read from the
/// body), what the schema diff records of them, and the MQ4056 warnings for what a dialect lacks.
/// </summary>
public sealed class DdlModelAdditionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<Diagnostic>> Findings(ModelSnapshot model, params string[] rules) =>
        [.. (await ValidationFixture.Validator().ValidateAsync(model, ValidationScope.All, null, Ct)).Diagnostics.Where(d => rules.Contains(d.Rule))];

    private static View View(ModelBuilder b, DatabaseBuilder db, string name, string body, Func<View, View>? edit = null)
    {
        var view = new View { Id = b.NewId(), Name = name, Database = db.Id, Body = new Dictionary<string, string> { ["*"] = body } };
        view = edit?.Invoke(view) ?? view;
        b.Add(view);
        return view;
    }

    [Fact]
    public void Index_expressions_prefix_lengths_nulls_not_distinct_and_identity_options_resolve()
    {
        var b = new ModelBuilder(seed: 90);
        var db = b.Database("db", Dialect.PostgreSql);
        b.Table("things", db).Column("id", "int64", nullable: false).Column("email", "string", length: 100).PrimaryKey("id").Index(false, "email")
            .Edit(t => t with
            {
                Columns = [t.Columns[0] with { Generated = ColumnGeneration.Identity, Identity = new ColumnIdentity { Seed = 100, Increment = 5, Always = true } }, t.Columns[1]],
                Uniques = [new UniqueConstraint { Id = "01JB2Q0M8X4T5V6W7Y8Z9A0C10", Columns = [t.Columns[1].Id], NullsNotDistinct = true }],
                Indexes =
                [
                    t.Indexes[0] with { Name = "ix_prefix", Columns = [new IndexColumn { Column = t.Columns[1].Id, Length = 10 }] },
                    new TableIndex { Id = b.NewId(), Name = "ix_lower", Columns = [new IndexColumn { Expression = new Dictionary<string, string> { ["postgresql"] = "lower(email)" }, Descending = true }] },
                    new TableIndex { Id = b.NewId(), Name = "ix_other", Columns = [new IndexColumn { Expression = new Dictionary<string, string> { ["mysql"] = "(lower(email))" } }] },
                ],
            });

        var table = ResolutionKit.Resolve(b).Db("db").Table("things");

        var id = table.Column("id");
        Assert.Equal((true, 100L, 5L, true), (id.Identity, id.IdentitySeed, id.IdentityIncrement, id.IdentityAlways));
        Assert.True(Assert.Single(table.Uniques).NullsNotDistinct);
        Assert.Equal(["ix_lower", "ix_prefix"], table.Indexes.Select(i => i.Name).Order(StringComparer.Ordinal));
        var lower = table.Indexes.Single(i => i.Name == "ix_lower").Columns[0];
        Assert.Equal((null, "lower(email)", true), (lower.Column, lower.Expression, lower.Descending));
        Assert.Equal(10, table.Indexes.Single(i => i.Name == "ix_prefix").Columns[0].Length);
    }

    [Fact]
    public void A_view_depends_on_what_its_file_names_and_on_the_views_its_body_reads()
    {
        var b = new ModelBuilder(seed: 91);
        var db = b.Database("db", Dialect.PostgreSql);
        b.Table("orders", db).Column("id", "int64", nullable: false).PrimaryKey("id");
        var basis = View(b, db, "order_list", "SELECT id FROM orders");
        var other = View(b, db, "audit", "SELECT 1 AS one");
        View(b, db, "order_totals", "SELECT count(*) FROM \"Order_List\" -- order_listing is not a view", v => v with
        {
            DependsOn = [other.Id],
            ColumnList = true,
            Columns = [new ViewColumn { Name = "n" }],
            WithCheckOption = true,
            Materialized = true,
        });

        var resolved = ResolutionKit.Resolve(b).Db("db");
        var totals = resolved.Views.Single(v => v.Name == "order_totals");

        Assert.Equal([other.Id, basis.Id], totals.DependsOn.Select(d => ((RView)d).Id));
        Assert.Empty(resolved.Views.Single(v => v.Name == "order_list").DependsOn);
        Assert.Equal((true, true, true), (totals.ColumnList, totals.WithCheckOption, totals.Materialized));
        Assert.Contains(totals.Dependencies, k => k.Contains(basis.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void The_snapshot_and_the_diff_carry_the_additions()
    {
        static ModelBuilder Model(bool changed)
        {
            var b = new ModelBuilder(seed: 92);
            var db = b.Database("db", Dialect.PostgreSql);
            b.Table("things", db).Column("id", "int64", nullable: false).Column("email", "string", length: 100).PrimaryKey("id")
                .Edit(t => t with
                {
                    Columns = [t.Columns[0] with { Generated = ColumnGeneration.Identity, Identity = changed ? new ColumnIdentity { Seed = 7, Always = true } : null }, t.Columns[1]],
                    Uniques = [new UniqueConstraint { Id = "01JB2Q0M8X4T5V6W7Y8Z9A0C11", Columns = [t.Columns[1].Id], NullsNotDistinct = changed }],
                    Indexes = [new TableIndex { Id = "01JB2Q0M8X4T5V6W7Y8Z9A0C01", Name = "ix_lower", Columns = [new IndexColumn { Expression = new Dictionary<string, string> { ["*"] = changed ? "upper(email)" : "lower(email)" } }] }],
                });
            var basis = new View { Id = "01JB2Q0M8X4T5V6W7Y8Z9A0C02", Name = "things_list", Database = db.Id, Body = new Dictionary<string, string> { ["*"] = "SELECT id FROM things" } };
            var top = new View
            {
                Id = "01JB2Q0M8X4T5V6W7Y8Z9A0C03",
                Name = "things_top",
                Database = db.Id,
                Body = new Dictionary<string, string> { ["*"] = "SELECT id FROM things_list" },
                Materialized = changed,
            };
            b.Add(basis);
            b.Add(top);
            return b;
        }

        var differ = new SchemaDiffer();
        var before = differ.Capture(ResolutionKit.Resolve(Model(false)).Db("db"), 1);
        var topBefore = before.Views.Single(v => v.Name == "things_top");
        Assert.Equal(["01JB2Q0M8X4T5V6W7Y8Z9A0C02"], topBefore.DependsOn);
        Assert.Matches("^ix:expr:[0-9a-f]{16}$", Assert.Single(before.Tables[0].Indexes).Key);

        var diff = differ.Diff(before, ResolutionKit.Resolve(Model(true)).Db("db"));

        var table = Assert.Single(diff.Tables);
        var id = table.Columns.Single(c => c.NewName == "id");
        Assert.Equal(["identitySeed", "identityAlways"], id.Changes.Select(c => c.Property));
        var unique = Assert.Single(table.Uniques);
        Assert.Equal("nullsNotDistinct", Assert.Single(unique.Changes).Property);
        // What a migration needs to drop the foreign keys that rely on a key first: its columns and whether it was a key.
        Assert.Equal([before.Tables[0].Columns.Single(c => c.Name == "email").Key], unique.OldColumns);
        Assert.True(unique.OldUnique);
        // A new expression is a new index (the expression forms its key).
        Assert.Equal([ChangeKind.Added, ChangeKind.Dropped], table.Indexes.Select(i => i.Kind));
        Assert.Equal([null], table.Indexes[1].OldColumns);
        Assert.False(table.Indexes[1].OldUnique);
        Assert.Empty(table.Indexes[0].OldColumns);
        var view = Assert.Single(diff.Views);
        Assert.Equal("materialized", Assert.Single(view.Changes).Property);
        Assert.Equal(["01JB2Q0M8X4T5V6W7Y8Z9A0C02"], view.OldDependsOn);
        Assert.False(view.OldMaterialized);
    }

    [Fact]
    public async Task What_a_dialect_lacks_of_the_additions_is_a_warning()
    {
        var b = new ModelBuilder(seed: 93);
        var oracle = b.Database("ora", Dialect.Oracle);
        var parent = b.Table("parents", oracle).Column("id", "int64", nullable: false).PrimaryKey("id");
        b.Table("children", oracle).Column("id", "int64", nullable: false).Column("parent_id", "int64").Column("twice", "int64").PrimaryKey("id")
            .ForeignKey(parent, "parent_id")
            .Edit(t => t with
            {
                Columns = [t.Columns[0], t.Columns[1], t.Columns[2] with { Computed = "id * 2", ComputedStored = true }],
                ForeignKeys = [t.ForeignKeys[0] with { OnUpdate = ReferentialAction.Cascade, OnDelete = ReferentialAction.Restrict }],
                Uniques = [new UniqueConstraint { Id = "01JB2Q0M8X4T5V6W7Y8Z9A0C12", Columns = [t.Columns[1].Id], NullsNotDistinct = true }],
            });
        var mysql = b.Database("my", Dialect.MySql);
        b.Table("notes", mysql).Column("id", "int64", nullable: false).Column("body", "text").PrimaryKey("id").Index(false, "body")
            .Edit(t => t with
            {
                Columns = [t.Columns[0] with { Generated = ColumnGeneration.Identity, Identity = new ColumnIdentity { Seed = 10, Increment = 2, Always = true } }, t.Columns[1]],
            });
        View(b, mysql, "stored", "SELECT 1 AS one", v => v with { Materialized = true });
        var sqlserver = b.Database("ms", Dialect.SqlServer);
        b.Table("people", sqlserver).Column("id", "int64", nullable: false).Column("name", "string", length: 20).PrimaryKey("id").Index(false, "name")
            .Edit(t => t with
            {
                Indexes = [t.Indexes[0] with { Columns = [new IndexColumn { Expression = new Dictionary<string, string> { ["*"] = "lower(name)" } }, new IndexColumn { Column = t.Columns[1].Id, Length = 5 }] }],
            });
        var sqlite = b.Database("lite", Dialect.Sqlite);
        View(b, sqlite, "checked", "SELECT 1 AS one", v => v with { WithCheckOption = true });
        var pg = b.Database("pg", Dialect.PostgreSql);
        View(b, pg, "stored_checked", "SELECT 1 AS one", v => v with { WithCheckOption = true, Materialized = true });

        var findings = await Findings(b.Build(), "MQ4056");

        string[] expected =
        [
            "/columns/0/identity/always", "/columns/0/identity/increment", "/columns/2/computedStored", "/foreignKeys/0/onDelete",
            "/foreignKeys/0/onUpdate", "/indexes/0/columns/0/expression", "/indexes/0/columns/0/length", "/indexes/0/columns/1/length",
            "/materialized", "/uniques/0/nullsNotDistinct", "/withCheckOption", "/withCheckOption",
        ];
        Assert.Equal(expected, findings.Select(d => d.JsonPointer).Order(StringComparer.Ordinal));
        Assert.All(findings, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.Contains(findings, d => d.Message.Contains("without a key prefix length", StringComparison.Ordinal));
        Assert.Contains(findings, d => d.Message.Contains("leaves the index out", StringComparison.Ordinal));

    }

    /// <summary>Folders keyed by (tenant_id, id) and documents whose key (tenant_id, folder_id) to them sets only folder_id on delete.</summary>
    private static ModelBuilder TenantFolders(Dialect dialect, ReferentialAction onDelete, params string[] onDeleteColumns)
    {
        var b = new ModelBuilder(seed: 94);
        var db = b.Database("db", dialect);
        var folders = b.Table("folders", db).Column("tenant_id", "int64", nullable: false).Column("id", "int64", nullable: false).PrimaryKey("tenant_id", "id");
        var documents = b.Table("documents", db).Column("tenant_id", "int64", nullable: false).Column("id", "int64", nullable: false).Column("folder_id", "int64")
            .PrimaryKey("tenant_id", "id").ForeignKey(folders, "tenant_id", "folder_id");
        var ids = onDeleteColumns.Select(n => n == "missing" ? "missing" : documents.ColumnId(n)).ToList();
        documents.Edit(t => t with { ForeignKeys = [t.ForeignKeys[0] with { OnDelete = onDelete, OnDeleteColumns = ids }] });
        return b;
    }

    [Fact]
    public async Task A_foreign_key_resolves_the_columns_it_sets_on_delete_and_the_snapshot_diff_records_a_change()
    {
        var b = TenantFolders(Dialect.PostgreSql, ReferentialAction.SetNull, "folder_id");
        Assert.Empty(await Findings(b.Build(), "MQ4060", "MQ4056"));
        var key = Assert.Single(ResolutionKit.Resolve(b).Db("db").Table("documents").ForeignKeys);
        Assert.Equal(["folder_id"], key.OnDeleteColumns.Select(c => c.Name));

        var differ = new SchemaDiffer();
        var before = differ.Capture(ResolutionKit.Resolve(TenantFolders(Dialect.PostgreSql, ReferentialAction.SetNull)).Db("db"), 1);
        var diff = differ.Diff(before, ResolutionKit.Resolve(b).Db("db"));
        var change = Assert.Single(Assert.Single(diff.Tables).ForeignKeys);
        Assert.Equal("onDeleteColumns", Assert.Single(change.Changes).Property);
    }

    [Fact]
    public async Task A_column_list_on_delete_is_refused_off_set_null_or_off_the_key_and_warned_outside_PostgreSQL()
    {
        Assert.Equal(["/foreignKeys/0/onDeleteColumns"],
            (await Findings(TenantFolders(Dialect.PostgreSql, ReferentialAction.Cascade, "folder_id").Build(), "MQ4060")).Select(d => d.JsonPointer));
        Assert.Equal(["/foreignKeys/0/onDeleteColumns/1", "/foreignKeys/0/onDeleteColumns/2"],
            (await Findings(TenantFolders(Dialect.PostgreSql, ReferentialAction.SetDefault, "folder_id", "id", "folder_id").Build(), "MQ4060")).Select(d => d.JsonPointer));

        var sqlServer = Assert.Single(await Findings(TenantFolders(Dialect.SqlServer, ReferentialAction.SetNull, "folder_id").Build(), "MQ4056", "MQ4060"));
        Assert.Equal(("MQ4056", "/foreignKeys/0/onDeleteColumns", DiagnosticSeverity.Warning), (sqlServer.Rule, sqlServer.JsonPointer, sqlServer.Severity));
    }

    [Fact]
    public void Setting_a_not_null_column_on_delete_is_a_warning_that_names_the_column()
    {
        // The resolver reports it (ModelStore adds the resolver's findings to every validate path): nullability is the resolved one.
        static List<Diagnostic> Sets(ModelBuilder b) => [.. ResolutionKit.Resolve(b).Diagnostics.Where(d => d.Rule == "MQ4061")];

        var finding = Assert.Single(Sets(TenantFolders(Dialect.PostgreSql, ReferentialAction.SetNull)));
        Assert.Equal(("/foreignKeys/0/onDelete", DiagnosticSeverity.Warning), (finding.JsonPointer, finding.Severity));
        Assert.Contains("tenant_id is not nullable", finding.Message, StringComparison.Ordinal);
        Assert.Contains("onDeleteColumns", finding.Message, StringComparison.Ordinal);

        // Only the columns the action sets count: folder_id is nullable.
        Assert.Empty(Sets(TenantFolders(Dialect.PostgreSql, ReferentialAction.SetNull, "folder_id")));
        Assert.Empty(Sets(TenantFolders(Dialect.PostgreSql, ReferentialAction.SetDefault, "folder_id")));
        Assert.Empty(Sets(TenantFolders(Dialect.PostgreSql, ReferentialAction.Cascade)));
        Assert.Contains("without a default",
            Assert.Single(Sets(TenantFolders(Dialect.PostgreSql, ReferentialAction.SetDefault))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task View_security_options_resolve_and_are_warned_outside_PostgreSQL_or_on_a_materialized_view()
    {
        var b = new ModelBuilder(seed: 95);
        var pg = b.Database("pg", Dialect.PostgreSql);
        View(b, pg, "mine", "SELECT 1 AS one", v => v with { SecurityInvoker = true, SecurityBarrier = true });
        View(b, pg, "stored", "SELECT 1 AS one", v => v with { Materialized = true, SecurityInvoker = true });
        var ms = b.Database("ms", Dialect.SqlServer);
        View(b, ms, "theirs", "SELECT 1 AS one", v => v with { SecurityBarrier = true });

        var mine = ResolutionKit.Resolve(b).Db("pg").Views.Single(v => v.Name == "mine");
        Assert.True(mine.SecurityInvoker && mine.SecurityBarrier);
        var findings = await Findings(b.Build(), "MQ4056");
        Assert.Equal(["/securityBarrier", "/securityInvoker"], findings.Select(d => d.JsonPointer).Order(StringComparer.Ordinal));
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, System.Text.Json.JsonElement>> StorageOf(string dialect, string json) =>
        new Dictionary<string, IReadOnlyDictionary<string, System.Text.Json.JsonElement>>
        {
            [dialect] = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json)!,
        };

    [Fact]
    public async Task Table_storage_takes_the_stereotypes_profile_under_its_own_and_writes_values_as_the_dialect_does()
    {
        var b = new ModelBuilder(seed: 96);
        b.Add(new Stereotype
        {
            Id = b.NewId(), Key = "high-churn", Name = "High churn", AppliesTo = ["table"],
            Storage = StorageOf("postgresql", """{ "fillfactor": 70, "autovacuum_enabled": true, "autovacuum_vacuum_scale_factor": 0.03 }"""),
        });
        var pg = b.Database("pg", Dialect.PostgreSql);
        b.Table("readings", pg).Column("id", "int64", nullable: false).PrimaryKey("id").Stereotype("high-churn")
            .Edit(t => t with { Storage = StorageOf("postgresql", """{ "fillfactor": 90 }""") });
        var ms = b.Database("ms", Dialect.SqlServer);
        b.Table("readings", ms).Column("id", "int64", nullable: false).PrimaryKey("id").Stereotype("high-churn")
            .Edit(t => t with { Storage = StorageOf("sqlserver", """{ "data_compression": "PAGE", "ignore_dup_key": false }""") });

        var model = ResolutionKit.Resolve(b);
        Assert.Equal(["autovacuum_enabled = true", "autovacuum_vacuum_scale_factor = 0.03", "fillfactor = 90"],
            model.Db("pg").Table("readings").Storage.Select(p => p.Name + " = " + p.Value));
        // Only the entries of the database's dialect: the stereotype's PostgreSQL profile reaches no SQL Server table.
        Assert.Equal(["data_compression = PAGE", "ignore_dup_key = OFF"], model.Db("ms").Table("readings").Storage.Select(p => p.Name + " = " + p.Value));
        Assert.Empty(await Findings(b.Build(), "MQ4064", "MQ4056"));
    }

    [Fact]
    public async Task A_storage_parameter_PostgreSQL_does_not_take_and_what_a_dialect_lacks_are_warned()
    {
        var b = new ModelBuilder(seed: 97);
        b.Add(new Stereotype { Id = b.NewId(), Key = "tuned", Name = "Tuned", AppliesTo = ["entity"], Storage = StorageOf("postgresql", """{ "fillfactr": 80 }""") });
        var pg = b.Database("pg", Dialect.PostgreSql);
        b.Table("docs", pg).Column("id", "int64", nullable: false).Column("body", "text").PrimaryKey("id").Index(false, "body")
            .Edit(t => t with
            {
                Storage = StorageOf("postgresql", """{ "toast.autovacuum_enabled": false, "toast.autovacuum_analyze_threshold": 5, "autovacuum_enabled": false }"""),
                Indexes = [t.Indexes[0] with { Method = IndexMethod.Brin, Storage = StorageOf("postgresql", """{ "pages_per_range": 16, "m": 4 }""") }],
            });
        var my = b.Database("my", Dialect.MySql);
        b.Table("docs", my).Column("id", "int64", nullable: false).Column("code", "string", length: 10).PrimaryKey("id").Index(false, "code")
            .Edit(t => t with
            {
                Indexes = [t.Indexes[0] with { Columns = [t.Indexes[0].Columns[0] with { OperatorClass = "text_pattern_ops" }], Storage = StorageOf("mysql", """{ "key_block_size": 8 }""") }],
            });

        var findings = await Findings(b.Build(), "MQ4064", "MQ4056");
        Assert.Equal(
            [
                "MQ4056 /indexes/0/columns/0/operatorClass", "MQ4056 /indexes/0/storage/mysql", "MQ4064 /indexes/0/storage/postgresql/m",
                "MQ4064 /storage", "MQ4064 /storage/postgresql/fillfactr", "MQ4064 /storage/postgresql/toast.autovacuum_analyze_threshold",
            ],
            findings.Select(d => d.Rule + " " + d.JsonPointer).Order(StringComparer.Ordinal));
    }

    /// <summary>rates (room_id, valid) with a temporal key when asked, and bookings referencing it by (room_id, stay).</summary>
    private static ModelBuilder Temporal(Dialect dialect, bool temporalKey, Func<ForeignKey, ForeignKey> fk, int keyColumns = 2)
    {
        var b = new ModelBuilder(seed: 98);
        var db = b.Database("db", dialect);
        var rates = b.Table("rates", db).Column("room_id", "int64", nullable: false).Column("valid", "string", nullable: false)
            .PrimaryKey([.. new[] { "room_id", "valid" }.TakeLast(keyColumns)]).Edit(t => t with { PrimaryKey = t.PrimaryKey! with { WithoutOverlaps = temporalKey } });
        b.Table("bookings", db).Column("id", "int64", nullable: false).Column("room_id", "int64", nullable: false).Column("stay", "string", nullable: false)
            .PrimaryKey("id").ForeignKey(rates, "room_id", "stay").Edit(t => t with { ForeignKeys = [fk(t.ForeignKeys[0])] });
        return b;
    }

    [Fact]
    public async Task Temporal_keys_resolve_and_MQ4065_refuses_what_PostgreSQL_refuses()
    {
        var ok = Temporal(Dialect.PostgreSql, true, f => f with { Period = true });
        Assert.Empty(await Findings(ok.Build(), "MQ4065", "MQ4056"));
        var db = ResolutionKit.Resolve(ok).Db("db");
        Assert.True(db.Table("rates").PrimaryKey!.WithoutOverlaps);
        Assert.True(Assert.Single(db.Table("bookings").ForeignKeys).Period);

        async Task<string[]> Pointers(ModelBuilder b) => [.. (await Findings(b.Build(), "MQ4065")).Select(d => d.JsonPointer!).Order(StringComparer.Ordinal)];
        Assert.Equal(["/foreignKeys/0/period"], await Pointers(Temporal(Dialect.PostgreSql, true, f => f)));
        Assert.Equal(["/foreignKeys/0/period"], await Pointers(Temporal(Dialect.PostgreSql, false, f => f with { Period = true })));
        Assert.Equal(["/foreignKeys/0/onDelete", "/foreignKeys/0/onUpdate"],
            await Pointers(Temporal(Dialect.PostgreSql, true, f => f with { Period = true, OnDelete = ReferentialAction.Cascade, OnUpdate = ReferentialAction.Restrict })));
        Assert.Contains("/primaryKey/withoutOverlaps", await Pointers(Temporal(Dialect.PostgreSql, true, f => f, keyColumns: 1)));

        var elsewhere = await Findings(Temporal(Dialect.SqlServer, true, f => f with { Period = true }).Build(), "MQ4056");
        Assert.Equal(["/foreignKeys/0/period", "/primaryKey/withoutOverlaps"], elsewhere.Select(d => d.JsonPointer).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Exclusion_constraints_resolve_with_a_conventional_name_and_are_warned_outside_PostgreSQL()
    {
        ModelBuilder Build(Dialect dialect, string column)
        {
            var b = new ModelBuilder(seed: 99);
            var db = b.Database("db", dialect);
            var table = b.Table("reservations", db).Column("id", "int64", nullable: false).Column("room_id", "int64", nullable: false).Column("during", "string").PrimaryKey("id");
            table.Edit(t => t with
            {
                Exclusions =
                [
                    new ExclusionConstraint
                    {
                        Id = "01JB2Q0M8X4T5V6W7Y8Z9A0C40",
                        Elements = [new ExclusionElement { Column = column == "room_id" ? t.Columns[1].Id : column, Operator = "=" }, new ExclusionElement { Column = t.Columns[2].Id, Operator = "&&" }],
                    },
                ],
            });
            return b;
        }

        var resolved = Assert.Single(ResolutionKit.Resolve(Build(Dialect.PostgreSql, "room_id")).Db("db").Table("reservations").Exclusions);
        Assert.Equal(("ex_reservations_room_id_during", "gist", "room_id = during &&"),
            (resolved.Name, resolved.Method, string.Join(" ", resolved.Elements.Select(e => e.Column!.Name + " " + e.Operator))));
        var snapshot = new SchemaDiffer().Capture(ResolutionKit.Resolve(Build(Dialect.PostgreSql, "room_id")).Db("db"), 1);
        Assert.Matches("^ex:[0-9a-f]{16}$", Assert.Single(snapshot.Tables[0].Exclusions).Key);

        Assert.Equal("/exclusions/0", Assert.Single(await Findings(Build(Dialect.MySql, "room_id").Build(), "MQ4056")).JsonPointer);
        Assert.Equal("/exclusions/0/elements/0/column", Assert.Single(await Findings(Build(Dialect.PostgreSql, "nope").Build(), "MQ4008")).JsonPointer);
    }

    [Fact]
    public async Task Partitioning_resolves_and_MQ4066_refuses_what_PostgreSQL_refuses()
    {
        ModelBuilder Build(Dialect dialect, PartitionStrategy strategy, bool keyHasColumn, params TablePartition[] partitions)
        {
            var b = new ModelBuilder(seed: 100);
            var db = b.Database("db", dialect);
            b.Table("events", db).Column("id", "int64", nullable: false).Column("at", "date", nullable: false)
                .PrimaryKey(keyHasColumn ? ["id", "at"] : ["id"])
                .Edit(t => t with { PartitionBy = new PartitionBy { Strategy = strategy, Columns = [t.Columns[1].Id] }, Partitions = partitions });
            return b;
        }

        TablePartition Part(string name, bool isDefault = false) =>
            new() { Id = "01JB2Q0M8X4T5V6W7Y8Z9A0C" + (50 + name.Length).ToString(System.Globalization.CultureInfo.InvariantCulture), Name = name, Bounds = isDefault ? null : "FROM (MINVALUE) TO (MAXVALUE)", Default = isDefault };

        var ok = Build(Dialect.PostgreSql, PartitionStrategy.Range, true, Part("events_all"), Part("events_rest", isDefault: true));
        Assert.Empty(await Findings(ok.Build(), "MQ4066", "MQ4056"));
        var table = ResolutionKit.Resolve(ok).Db("db").Table("events");
        Assert.Equal(("range", "at"), (table.PartitionBy!.Strategy, table.PartitionBy.Columns[0].Name));
        Assert.Equal([(false, "events_all"), (true, "events_rest")], table.Partitions.Select(p => (p.IsDefault, p.Name)));

        async Task<string[]> Pointers(ModelBuilder b) => [.. (await Findings(b.Build(), "MQ4066")).Select(d => d.JsonPointer!).Order(StringComparer.Ordinal)];
        Assert.Equal(["/primaryKey/columns"], await Pointers(Build(Dialect.PostgreSql, PartitionStrategy.Range, false)));
        Assert.Equal(["/partitions/0/default"], await Pointers(Build(Dialect.PostgreSql, PartitionStrategy.Hash, true, Part("events_d", isDefault: true))));
        Assert.Equal(["/partitionBy"], (await Findings(ok.Build(), "MQ4056")).Select(d => d.JsonPointer).Concat((await Findings(
            Build(Dialect.MySql, PartitionStrategy.Range, true).Build(), "MQ4056")).Select(d => d.JsonPointer)));
    }
}
