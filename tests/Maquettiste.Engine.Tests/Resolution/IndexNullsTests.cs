using System.Collections.Immutable;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Index column nulls (next-phase-1-design.md section 3.4): <c>first</c> or <c>last</c> reaches the resolved model, the snapshot's
/// index column and key, and PostgreSQL's DDL (NULLS FIRST or LAST) only when it differs from the direction's default; other
/// dialects leave it out with MQ4056.
/// </summary>
public sealed class IndexNullsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A table <c>events</c> with two indexes: <c>ix_events_at</c> on <c>at</c> ascending and <c>ix_events_priority</c> on
    /// <c>priority</c> descending, each column's nulls as given.
    /// </summary>
    private static ModelBuilder Model(Dialect dialect, IndexNulls? atNulls, IndexNulls? priorityNulls)
    {
        var b = new ModelBuilder(seed: 240);
        b.Settings(s => s with
        {
            Outputs = new OutputSettings { Allow = [new OutputRoot { Path = "db" }] },
            Packs = ImmutableDictionary<string, PackSettings>.Empty.Add("sql-ddl", new PackSettings { Output = "db" }),
        });
        var db = b.Database("db", dialect);
        b.Table("events", db).Column("id", "int64", nullable: false).Column("at", "datetime", nullable: true).Column("priority", "int32", nullable: true)
            .PrimaryKey("id")
            .Edit(t => t with
            {
                Indexes =
                [
                    new TableIndex { Id = "01JB2Q0M8X4T5V6W7Y8Z9A0N01", Name = "ix_events_at", Columns = [new IndexColumn { Column = t.Columns[1].Id, Nulls = atNulls }] },
                    new TableIndex
                    {
                        Id = "01JB2Q0M8X4T5V6W7Y8Z9A0N02", Name = "ix_events_priority",
                        Columns = [new IndexColumn { Column = t.Columns[2].Id, Descending = true, Nulls = priorityNulls }],
                    },
                ],
            });
        return b;
    }

    /// <summary>Writes the model with the sql-ddl pack to a temporary repo, generates, and returns the database's schema script.</summary>
    private static async Task<string> SchemaSqlAsync(ModelBuilder builder)
    {
        using var repo = new TempRepo();
        await builder.WriteToAsync(repo.ModelRoot, Ct);
        var source = Path.Combine(Fixtures.RepoRoot, "packs", "sql-ddl");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(repo.ModelRoot, "templates", "sql-ddl", Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        await using var store = new ModelStore(repo.Options);
        var run = await new GenerationService(store, repo.Options).RunAsync(new GenerationRequest { Packs = ["sql-ddl"] }, null, Ct);
        Assert.True(run.Outcome == RunOutcome.Succeeded, string.Join("; ", run.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        return repo.ReadFile("db/db/schema.sql");
    }

    private static string Index(string ddl, string name) =>
        ddl.Split('\n').Single(l => l.Contains("INDEX " + name + " ", StringComparison.Ordinal));

    [Fact]
    public async Task Nulls_reach_the_resolved_model_snapshot_key_and_postgresql_ddl()
    {
        var builder = Model(Dialect.PostgreSql, IndexNulls.First, IndexNulls.Last);

        var db = ResolutionKit.Resolve(builder).Db("db");
        var table = db.Table("events");
        Assert.Equal("first", table.Indexes.Single(i => i.Name == "ix_events_at").Columns[0].Nulls);
        Assert.Equal("last", table.Indexes.Single(i => i.Name == "ix_events_priority").Columns[0].Nulls);

        var snapshot = SnapshotCapture.Capture(db, 1, ct: Ct).Tables.Single(t => t.Name == "events");
        var at = snapshot.Indexes.Single(i => i.Name == "ix_events_at");
        var priority = snapshot.Indexes.Single(i => i.Name == "ix_events_priority");
        Assert.Equal(("first", "last"), (at.Columns[0].Nulls, priority.Columns[0].Nulls));
        Assert.EndsWith(" nulls first", at.Key, StringComparison.Ordinal);
        Assert.EndsWith(" desc nulls last", priority.Key, StringComparison.Ordinal);
        Assert.Equal(["at nulls first"], SchemaDiffer.IndexColumns([at.Columns[0] with { Column = "at" }]));

        var ddl = await SchemaSqlAsync(builder);
        Assert.EndsWith("(at NULLS FIRST);", Index(ddl, "ix_events_at"), StringComparison.Ordinal);
        Assert.EndsWith("(priority DESC NULLS LAST);", Index(ddl, "ix_events_priority"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nulls_equal_to_the_directions_default_change_nothing()
    {
        var plain = Model(Dialect.PostgreSql, null, null);
        var defaults = Model(Dialect.PostgreSql, IndexNulls.Last, IndexNulls.First);

        var plainDb = ResolutionKit.Resolve(plain).Db("db");
        var defaultsDb = ResolutionKit.Resolve(defaults).Db("db");
        Assert.All(defaultsDb.Table("events").Indexes, i => Assert.Null(i.Columns[0].Nulls));

        var a = SnapshotCapture.Capture(plainDb, 1, ct: Ct).Tables.Single(t => t.Name == "events").Indexes;
        var b = SnapshotCapture.Capture(defaultsDb, 1, ct: Ct).Tables.Single(t => t.Name == "events").Indexes;
        Assert.Equal(a.Select(i => i.Key), b.Select(i => i.Key));
        Assert.Equal(a.Select(i => i.Columns[0]), b.Select(i => i.Columns[0]));
        Assert.Equal(
            JsonSerializer.Serialize(SnapshotCapture.Capture(plainDb, 1, ct: Ct), EngineJson.Options),
            JsonSerializer.Serialize(SnapshotCapture.Capture(defaultsDb, 1, ct: Ct), EngineJson.Options));

        var plainDdl = await SchemaSqlAsync(plain);
        Assert.Equal(plainDdl, await SchemaSqlAsync(defaults));
        Assert.DoesNotContain("NULLS", plainDdl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nulls_outside_postgresql_is_MQ4056_and_left_out()
    {
        var builder = Model(Dialect.SqlServer, IndexNulls.First, IndexNulls.Last);

        var findings = (await ValidationFixture.Validator().ValidateAsync(builder.Build(), ValidationScope.All, null, Ct)).Diagnostics
            .Where(d => d.Rule == "MQ4056").ToList();
        Assert.Equal(["/indexes/0/columns/0/nulls", "/indexes/1/columns/0/nulls"], findings.Select(d => d.JsonPointer).Order(StringComparer.Ordinal));
        Assert.Contains("sorts nulls first (NULLS FIRST), which sqlserver", findings.Single(d => d.JsonPointer == "/indexes/0/columns/0/nulls").Message,
            StringComparison.Ordinal);

        var db = ResolutionKit.Resolve(builder).Db("db");
        Assert.All(db.Table("events").Indexes, i => Assert.Null(i.Columns[0].Nulls));
        Assert.All(SnapshotCapture.Capture(db, 1, ct: Ct).Tables.Single().Indexes, i => Assert.Null(i.Columns[0].Nulls));
        Assert.DoesNotContain("NULLS", await SchemaSqlAsync(builder), StringComparison.Ordinal);
    }
}
