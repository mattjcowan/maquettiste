using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The sql-ddl pack over the PostgreSQL feature model (<see cref="PostgresFeatures"/>): byte-exact goldens of the create script
/// (step 1) and of the tree after the last step, plain and idempotent, under <c>tests/fixtures/golden/sql-ddl/postgres-features</c>;
/// the statements each feature writes; and, on the PostgreSQL server <see cref="DbServer"/> finds (18 or later), the step 1 and final
/// create scripts and the migration chain, each twice when idempotent.
/// </summary>
public sealed class PostgresFeatureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_scripts_and_every_migration_match_the_golden_trees(bool idempotent)
    {
        using var repo = PostgresFeatures.Create(idempotent);
        var golden = Fixtures.Path("golden", "sql-ddl", idempotent ? "postgres-features-idempotent" : "postgres-features");
        var first = await repo.GenerateCleanlyAsync();
        Assert.DoesNotContain(first.Diagnostics, d => d.Rule is "MQ4056" or "MQ4063");
        Golden.AssertMatches(Path.Combine(golden, "step1"), repo.PathOf("db"));
        for (var step = 2; step <= PostgresFeatures.Steps; step++)
        {
            PostgresFeatures.Write(repo, step);
            var result = await repo.GenerateCleanlyAsync();
            Assert.DoesNotContain(result.Diagnostics, d => d.Rule is "MQ4056" or "MQ4063");
        }

        Golden.AssertMatches(Path.Combine(golden, "final"), repo.PathOf("db"));
    }

    [Fact]
    public async Task Routines_write_their_volatility_and_settings_and_a_change_creates_them_again()
    {
        var scripts = await ScriptsAsync(idempotent: false);
        Assert.Contains("RETURNS bigint\nLANGUAGE sql\nSTABLE\nAS $$", scripts.First, StringComparison.Ordinal);
        Assert.Contains("LANGUAGE sql\nSTABLE\nSECURITY DEFINER\nSET search_path = app, pg_temp\nAS $$", scripts.First, StringComparison.Ordinal);
        Assert.Contains("LANGUAGE sql\nIMMUTABLE\nSET work_mem = '64MB'\nAS $$", scripts.Migrations[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_view_writes_its_security_options_and_a_change_creates_it_again()
    {
        var scripts = await ScriptsAsync(idempotent: false);
        Assert.Contains("CREATE VIEW app.my_documents WITH (security_barrier = true, security_invoker = true) AS", scripts.First, StringComparison.Ordinal);
        Assert.Contains("DROP VIEW app.my_documents;", scripts.Migrations[1], StringComparison.Ordinal);
        Assert.Contains("CREATE VIEW app.my_documents WITH (security_invoker = true) AS", scripts.Migrations[1], StringComparison.Ordinal);
        // The grant that depends on the view runs again after it.
        AssertBefore(scripts.Migrations[1], "CREATE VIEW app.my_documents", "GRANT SELECT ON app.my_documents TO PUBLIC;");
        Assert.Contains("the SQL objects that depend on them run again below", scripts.Migrations[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tables_and_indexes_write_their_storage_operator_classes_and_methods_and_a_change_alters_them()
    {
        var scripts = await ScriptsAsync(idempotent: false);
        // The stereotype's profile under the table's own fillfactor.
        Assert.Contains(") WITH (autovacuum_enabled = true, autovacuum_vacuum_scale_factor = 0.03, fillfactor = 90);", scripts.First, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX ix_documents_title_trgm ON app.documents USING gin (title gin_trgm_ops);", scripts.First, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX ix_documents_created ON app.documents USING brin (created_at) WITH (pages_per_range = 32);", scripts.First, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX ix_documents_embedding ON app.documents USING hnsw (embedding vector_cosine_ops) WITH (ef_construction = 64, m = 16);", scripts.First, StringComparison.Ordinal);
        var second = scripts.Migrations[1];
        Assert.Contains("ALTER TABLE app.documents SET (fillfactor = 80);", second, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE app.documents RESET (autovacuum_enabled);", second, StringComparison.Ordinal);
        Assert.Contains("ALTER INDEX app.ix_documents_created SET (pages_per_range = 64);", second, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP INDEX", second, StringComparison.Ordinal);
        Assert.Contains("DROP INDEX app.ix_documents_title_trgm;", scripts.Migrations[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Temporal_keys_write_without_overlaps_and_period()
    {
        var scripts = await ScriptsAsync(idempotent: false);
        Assert.Contains("CONSTRAINT pk_room_rates PRIMARY KEY (room_id, valid WITHOUT OVERLAPS)", scripts.First, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (room_id, PERIOD stay) REFERENCES app.room_rates (room_id, PERIOD valid)", scripts.First, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT uq_bookings_guest UNIQUE (guest_id, stay WITHOUT OVERLAPS);", scripts.Migrations[1], StringComparison.Ordinal);
        Assert.Contains("DROP CONSTRAINT uq_bookings_guest;", scripts.Migrations[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exclusion_constraints_are_created_changed_and_renamed()
    {
        var scripts = await ScriptsAsync(idempotent: false);
        Assert.Contains("CONSTRAINT ex_reservations_overlap EXCLUDE USING gist (room_id WITH =, during WITH &&) WHERE (NOT cancelled)", scripts.First, StringComparison.Ordinal);
        AssertBefore(scripts.Migrations[1], "ALTER TABLE app.reservations DROP CONSTRAINT ex_reservations_overlap;",
            "ALTER TABLE app.reservations ADD CONSTRAINT ex_reservations_overlap EXCLUDE USING gist (room_id WITH =, during WITH &&) WHERE (NOT cancelled) DEFERRABLE INITIALLY DEFERRED;");
        Assert.Contains("ALTER TABLE app.reservations RENAME CONSTRAINT ex_reservations_overlap TO ex_reservations_room_overlap;", scripts.Migrations[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_partitioned_table_creates_its_partitions_with_its_storage_and_a_migration_changes_them()
    {
        var scripts = await ScriptsAsync(idempotent: false);
        Assert.Contains(") PARTITION BY RANGE (occurred_at);", scripts.First, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE app.audit_events_2026_01 PARTITION OF app.audit_events FOR VALUES FROM ('2026-01-01') TO ('2026-02-01') WITH (autovacuum_enabled = true, autovacuum_vacuum_scale_factor = 0.03, fillfactor = 70);",
            scripts.First, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE app.audit_events_default PARTITION OF app.audit_events DEFAULT WITH (", scripts.First, StringComparison.Ordinal);
        var second = scripts.Migrations[1];
        Assert.Contains("CREATE TABLE app.audit_events_2026_03 PARTITION OF app.audit_events FOR VALUES FROM ('2026-03-01') TO ('2026-04-01')", second, StringComparison.Ordinal);
        AssertBefore(second, "ALTER TABLE app.audit_events_2026_02 RENAME TO audit_events_2026_02_full;", "ALTER TABLE app.audit_events DETACH PARTITION app.audit_events_2026_02_full;");
        AssertBefore(second, "ALTER TABLE app.audit_events DETACH PARTITION app.audit_events_2026_02_full;",
            "ALTER TABLE app.audit_events ATTACH PARTITION app.audit_events_2026_02_full FOR VALUES FROM ('2026-02-01') TO ('2026-03-01');");
        Assert.Contains("ALTER TABLE app.audit_events_default RENAME TO audit_events_rest;", second, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE app.audit_events_rest RESET (autovacuum_enabled);", second, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE app.audit_events SET", second, StringComparison.Ordinal);
        Assert.Contains("DROP TABLE app.audit_events_2026_03;", scripts.Migrations[2], StringComparison.Ordinal);
    }

    private static void AssertBefore(string text, string first, string second)
    {
        var a = text.IndexOf(first, StringComparison.Ordinal);
        var b = text.IndexOf(second, StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a, $"Expected\n  {first}\nbefore\n  {second}\nin\n{text}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_server_runs_the_create_scripts_and_the_migration_chain(bool idempotent)
    {
        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip("Name a PostgreSQL 18 container in the environment to run the DDL on it (see DbServer).");
            return;
        }

        var scripts = await ScriptsAsync(idempotent);
        var twice = idempotent ? 2 : 1;
        await db.RunAsync("pgf_create", [.. Enumerable.Repeat(scripts.First, twice)]);
        await db.RunAsync("pgf_final", [.. Enumerable.Repeat(scripts.Final, twice)]);
        string[] chain = [.. scripts.Migrations.SelectMany(m => Enumerable.Repeat(m, twice)),
            "SELECT 'routines=' || count(*) FROM pg_proc WHERE pronamespace = 'app'::regnamespace;",
            "SELECT 'granted=' || has_table_privilege('public', 'app.my_documents', 'SELECT');"];
        var output = await db.RunAsync("pgf_chain", chain);
        Assert.Contains("granted=true", output, StringComparison.Ordinal);
        output = await db.RunAsync("pgf_count", [.. scripts.Migrations.SelectMany(m => Enumerable.Repeat(m, twice)),
            "SELECT 'routines=' || count(*) FROM pg_proc WHERE pronamespace = 'app'::regnamespace;"]);
        Assert.Contains("routines=1", output, StringComparison.Ordinal);
    }

    private sealed record Scripts(string First, string Final, IReadOnlyList<string> Migrations);

    private static async Task<Scripts> ScriptsAsync(bool idempotent)
    {
        using var repo = PostgresFeatures.Create(idempotent);
        await repo.GenerateCleanlyAsync();
        var first = repo.Read("db/pg/schema.sql");
        for (var step = 2; step <= PostgresFeatures.Steps; step++)
        {
            PostgresFeatures.Write(repo, step);
            await repo.GenerateCleanlyAsync();
        }

        var migrations = Directory.GetFiles(repo.PathOf("db/pg/migrations"), "*.sql").Order(StringComparer.Ordinal).Select(File.ReadAllText).ToList();
        return new Scripts(first, repo.Read("db/pg/schema.sql"), migrations);
    }
}
