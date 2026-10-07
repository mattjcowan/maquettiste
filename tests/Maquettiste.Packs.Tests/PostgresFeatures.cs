using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The PostgreSQL feature model: one PostgreSQL database <c>pg</c> (schema <c>app</c>) with the DDL features only PostgreSQL has,
/// written in three steps so the sql-ddl pack writes a create script and a migration for each: step 1 creates, step 2 changes, step 3
/// drops. Every feature the sql-ddl pack writes for PostgreSQL alone is here, so the scripts run on a PostgreSQL 18 server as they are.
/// </summary>
internal static class PostgresFeatures
{
    /// <summary>The last step.</summary>
    public const int Steps = 3;

    /// <summary>A repo with the sql-ddl pack writing to <c>db</c>, its <c>idempotent</c> parameter as given, and the step 1 model.</summary>
    public static PackRepo Create(bool idempotent)
    {
        var repo = PackRepo.Blank();
        PackRepo.CopyTree(Path.Combine(Maquettiste.Testing.Fixtures.RepoRoot, "packs", "sql-ddl"), Path.Combine(repo.Repo.ModelRoot, "templates", "sql-ddl"));
        var settings = new JsonObject
        {
            ["$schema"] = ".schema/v1/maquettiste.json",
            ["formatVersion"] = 1,
            ["name"] = "pg-features",
            ["outputs"] = new JsonObject { ["allow"] = new JsonArray(new JsonObject { ["path"] = "db" }) },
            ["packs"] = new JsonObject
            {
                ["sql-ddl"] = new JsonObject { ["output"] = "db", ["parameters"] = new JsonObject { ["idempotent"] = idempotent } },
            },
        };
        repo.Write(".maquettiste/maquettiste.json", Json(settings));
        Write(repo, 1);
        return repo;
    }

    /// <summary>Replaces the database of the model with its state at a step.</summary>
    public static void Write(PackRepo repo, int step)
    {
        var folder = repo.PathOf(".maquettiste/model/databases");
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        foreach (var (path, node) in Elements(step))
            repo.Write($".maquettiste/model/databases/pg/{path}.json", Json(node));
        repo.Write(".maquettiste/model/vocabularies/stereotypes/high-churn.json", Json(HighChurn(step)));
    }

    /// <summary>
    /// high-churn: a table storage profile (aggressive autovacuum) that documents carries; step 2 drops autovacuum_enabled from it, so
    /// the migration resets it.
    /// </summary>
    private static JsonObject HighChurn(int step)
    {
        var postgres = new JsonObject { ["autovacuum_vacuum_scale_factor"] = 0.03, ["fillfactor"] = 70 };
        if (step == 1)
            postgres["autovacuum_enabled"] = true;
        return new JsonObject
        {
            ["$schema"] = "../../../.schema/v1/stereotype.json",
            ["kind"] = "stereotype",
            ["id"] = Id(3),
            ["key"] = "high-churn",
            ["name"] = "High churn",
            ["appliesTo"] = new JsonArray("table"),
            ["storage"] = new JsonObject { ["postgresql"] = postgres },
        };
    }

    /// <summary>The id of object <paramref name="n"/>.</summary>
    public static string Id(int n) => "01PG" + n.ToString("D22", CultureInfo.InvariantCulture);

    private static string Json(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

    private static IEnumerable<(string Path, JsonObject Node)> Elements(int step)
    {
        yield return ("database", new JsonObject
        {
            ["$schema"] = "../../../.schema/v1/database.json",
            ["kind"] = "database",
            ["id"] = Id(1),
            ["name"] = "pg",
            ["dialect"] = "postgresql",
            ["byConvention"] = "none",
            ["schemas"] = new JsonArray(new JsonObject { ["id"] = Id(2), ["name"] = "app" }),
        });

        // The extensions the indexes and constraints use, created before the tables.
        foreach (var (n, name) in new[] { (5, "pg_trgm"), (6, "vector"), (7, "btree_gist") })
        {
            var extension = Element("sql-object", n, name);
            extension.Remove("schema");
            extension["objectKind"] = "extension";
            extension["phase"] = "before";
            extension["body"] = new JsonObject { ["postgresql"] = $"CREATE EXTENSION IF NOT EXISTS {name};" };
            yield return ("objects/" + name.Replace('_', '-'), extension);
        }

        foreach (var routine in Routines(step))
            yield return routine;
        yield return ("tables/documents", Documents(step));
        yield return ("tables/room-rates", RoomRates());
        yield return ("tables/bookings", Bookings(step));
        yield return ("tables/reservations", Reservations(step));
        yield return ("tables/audit-events", AuditEvents(step));
        yield return ("views/my-documents", MyDocuments(step));

        // A grant kept in a SQL object that depends on the view: a migration that creates the view again grants again.
        var grant = Element("sql-object", 31, "grant_my_documents");
        grant["objectKind"] = "grant";
        grant["dependsOn"] = Ids(30);
        grant["body"] = new JsonObject { ["postgresql"] = "GRANT SELECT ON app.my_documents TO PUBLIC;" };
        yield return ("objects/grant-my-documents", grant);
    }

    private static JsonObject Column(int n, string name, string type, Action<JsonObject>? more = null)
    {
        var column = new JsonObject { ["id"] = Id(n), ["name"] = name, ["type"] = type };
        more?.Invoke(column);
        return column;
    }

    private static JsonArray Ids(params int[] ns) => new([.. ns.Select(n => (JsonNode)Id(n))]);

    private static JsonObject Table(int n, string name, JsonArray columns)
    {
        var table = Element("table", n, name);
        table["origin"] = "designed";
        table["columns"] = columns;
        return table;
    }

    /// <summary>
    /// documents: a tenant's documents, which my_documents shows to the tenant that asks. Storage: the high-churn stereotype's, with its
    /// own fillfactor over the stereotype's (step 2: 80). Indexes: a trigram index on the title (dropped at step 3), a brin index on
    /// created_at (step 2: more pages per range, an ALTER INDEX), and an hnsw index on the embedding.
    /// </summary>
    private static JsonObject Documents(int step)
    {
        var table = Table(20, "documents", new JsonArray(
            Column(21, "id", "int64", c => { c["nullable"] = false; c["generated"] = "identity"; }),
            Column(22, "tenant_id", "int64", c => c["nullable"] = false),
            Column(23, "title", "string", c => { c["length"] = 200; c["nullable"] = false; }),
            Column(24, "created_at", "datetimeoffset", c => { c["nullable"] = false; c["defaultSql"] = new JsonObject { ["*"] = "CURRENT_TIMESTAMP" }; }),
            Column(25, "embedding", "string", c => c["nativeType"] = "vector(3)")));
        table["stereotypes"] = new JsonArray("high-churn");
        table["primaryKey"] = new JsonObject { ["name"] = "pk_documents", ["columns"] = Ids(21) };
        var indexes = new JsonArray();
        if (step <= 2)
        {
            indexes.Add(new JsonObject
            {
                ["id"] = Id(26), ["name"] = "ix_documents_title_trgm", ["method"] = "gin",
                ["columns"] = new JsonArray(new JsonObject { ["column"] = Id(23), ["operatorClass"] = "gin_trgm_ops" }),
            });
        }

        indexes.Add(new JsonObject
        {
            ["id"] = Id(27), ["name"] = "ix_documents_created", ["method"] = "brin",
            ["columns"] = new JsonArray(new JsonObject { ["column"] = Id(24) }),
            ["storage"] = new JsonObject { ["postgresql"] = new JsonObject { ["pages_per_range"] = step >= 2 ? 64 : 32 } },
        });
        indexes.Add(new JsonObject
        {
            ["id"] = Id(28), ["name"] = "ix_documents_embedding", ["method"] = "hnsw",
            ["columns"] = new JsonArray(new JsonObject { ["column"] = Id(25), ["operatorClass"] = "vector_cosine_ops" }),
            ["storage"] = new JsonObject { ["postgresql"] = new JsonObject { ["m"] = 16, ["ef_construction"] = 64 } },
        });
        table["indexes"] = indexes;
        table["storage"] = new JsonObject { ["postgresql"] = new JsonObject { ["fillfactor"] = step >= 2 ? 80 : 90 } };
        return table;
    }

    /// <summary>room_rates: a room's rate over a period, one rate at a time (a temporal primary key).</summary>
    private static JsonObject RoomRates()
    {
        var table = Table(40, "room_rates", new JsonArray(
            Column(41, "room_id", "int64", c => c["nullable"] = false),
            Column(42, "valid", "string", c => { c["nativeType"] = "tstzrange"; c["nullable"] = false; }),
            Column(43, "rate", "decimal", c => { c["precision"] = 10; c["scale"] = 2; c["nullable"] = false; })));
        table["primaryKey"] = new JsonObject { ["name"] = "pk_room_rates", ["columns"] = Ids(41, 42), ["withoutOverlaps"] = true };
        return table;
    }

    /// <summary>
    /// bookings: a stay in a room, within the periods the room has a rate (a period foreign key); step 2 adds a temporal unique key (a
    /// guest has one booking at a time), step 3 drops it.
    /// </summary>
    private static JsonObject Bookings(int step)
    {
        var table = Table(50, "bookings", new JsonArray(
            Column(51, "id", "int64", c => { c["nullable"] = false; c["generated"] = "identity"; }),
            Column(52, "room_id", "int64", c => c["nullable"] = false),
            Column(53, "guest_id", "int64", c => c["nullable"] = false),
            Column(54, "stay", "string", c => { c["nativeType"] = "tstzrange"; c["nullable"] = false; })));
        table["primaryKey"] = new JsonObject { ["name"] = "pk_bookings", ["columns"] = Ids(51) };
        table["foreignKeys"] = new JsonArray(new JsonObject
        {
            ["id"] = Id(55), ["name"] = "fk_bookings_rate", ["columns"] = Ids(52, 54), ["referencesTable"] = Id(40), ["period"] = true,
        });
        if (step == 2)
            table["uniques"] = new JsonArray(new JsonObject { ["id"] = Id(56), ["name"] = "uq_bookings_guest", ["columns"] = Ids(53, 54), ["withoutOverlaps"] = true });
        return table;
    }

    /// <summary>
    /// reservations: no two reservations that are not cancelled hold a room over overlapping periods (an exclusion constraint); step 2
    /// makes it deferred, step 3 renames it.
    /// </summary>
    private static JsonObject Reservations(int step)
    {
        var table = Table(60, "reservations", new JsonArray(
            Column(61, "id", "int64", c => { c["nullable"] = false; c["generated"] = "identity"; }),
            Column(62, "room_id", "int64", c => c["nullable"] = false),
            Column(63, "during", "string", c => { c["nativeType"] = "tstzrange"; c["nullable"] = false; }),
            Column(64, "cancelled", "bool", c => { c["nullable"] = false; c["default"] = false; })));
        table["primaryKey"] = new JsonObject { ["name"] = "pk_reservations", ["columns"] = Ids(61) };
        var exclusion = new JsonObject
        {
            ["id"] = Id(65),
            ["name"] = step >= 3 ? "ex_reservations_room_overlap" : "ex_reservations_overlap",
            ["elements"] = new JsonArray(
                new JsonObject { ["column"] = Id(62), ["operator"] = "=" },
                new JsonObject { ["column"] = Id(63), ["operator"] = "&&" }),
            ["where"] = "NOT cancelled",
        };
        if (step >= 2)
            exclusion["deferrable"] = "initially-deferred";
        table["exclusions"] = new JsonArray(exclusion);
        return table;
    }

    /// <summary>
    /// audit_events: partitioned by month of occurred_at, high churn (its storage on each partition); step 2 adds the March partition,
    /// widens February's bounds to the end of the month after a rename, renames the default partition and turns autovacuum
    /// tuning down (the stereotype's step 2); step 3 drops March.
    /// </summary>
    private static JsonObject AuditEvents(int step)
    {
        var table = Table(70, "audit_events", new JsonArray(
            Column(71, "id", "int64", c => { c["nullable"] = false; c["generated"] = "identity"; }),
            Column(72, "occurred_at", "datetimeoffset", c => c["nullable"] = false),
            Column(73, "payload", "text")));
        table["stereotypes"] = new JsonArray("high-churn");
        table["primaryKey"] = new JsonObject { ["name"] = "pk_audit_events", ["columns"] = Ids(71, 72) };
        table["partitionBy"] = new JsonObject { ["strategy"] = "range", ["columns"] = Ids(72) };
        var partitions = new JsonArray(
            new JsonObject { ["id"] = Id(74), ["name"] = "audit_events_2026_01", ["bounds"] = "FROM ('2026-01-01') TO ('2026-02-01')" },
            new JsonObject
            {
                ["id"] = Id(75), ["name"] = step >= 2 ? "audit_events_2026_02_full" : "audit_events_2026_02",
                ["bounds"] = step >= 2 ? "FROM ('2026-02-01') TO ('2026-03-01')" : "FROM ('2026-02-01') TO ('2026-02-15')",
            },
            new JsonObject { ["id"] = Id(76), ["name"] = step >= 2 ? "audit_events_rest" : "audit_events_default", ["default"] = true });
        if (step == 2)
            partitions.Add(new JsonObject { ["id"] = Id(77), ["name"] = "audit_events_2026_03", ["bounds"] = "FROM ('2026-03-01') TO ('2026-04-01')" });
        table["partitions"] = partitions;
        return table;
    }

    /// <summary>my_documents: a security-invoker barrier view over documents (step 2: no longer a barrier, so it is created again).</summary>
    private static JsonObject MyDocuments(int step)
    {
        var view = Element("view", 30, "my_documents");
        view["body"] = new JsonObject { ["*"] = "SELECT id, title FROM app.documents WHERE tenant_id = app.current_tenant()" };
        view["securityInvoker"] = true;
        if (step == 1)
            view["securityBarrier"] = true;
        view["dependsOn"] = Ids(10);
        return view;
    }

    private static JsonObject Element(string kind, int n, string name)
    {
        return new JsonObject
        {
            ["$schema"] = $"../../../../.schema/v1/{kind}.json",
            ["kind"] = kind,
            ["id"] = Id(n),
            ["name"] = name,
            ["database"] = Id(1),
            ["schema"] = Id(2),
        };
    }

    /// <summary>
    /// Routines: <c>current_tenant</c>, a stable function that reads a setting (step 2: immutable, with a work_mem setting);
    /// <c>tenant_count</c>, a security-definer function that pins search_path (dropped at step 3).
    /// </summary>
    private static IEnumerable<(string, JsonObject)> Routines(int step)
    {
        var tenant = Element("routine", 10, "current_tenant");
        tenant["returns"] = new JsonObject { ["type"] = "int64" };
        tenant["language"] = "sql";
        tenant["body"] = new JsonObject { ["postgresql"] = step >= 2 ? "SELECT 1::bigint" : "SELECT coalesce(current_setting('app.tenant', true), '0')::bigint" };
        tenant["volatility"] = step >= 2 ? "immutable" : "stable";
        if (step >= 2)
            tenant["settings"] = new JsonObject { ["work_mem"] = "'64MB'" };
        yield return ("routines/current-tenant", tenant);

        if (step <= 2)
        {
            var count = Element("routine", 11, "tenant_count");
            count["returns"] = new JsonObject { ["type"] = "int64" };
            count["language"] = "sql";
            count["body"] = new JsonObject { ["postgresql"] = "SELECT count(*) FROM pg_catalog.pg_namespace" };
            count["volatility"] = "stable";
            count["security"] = "definer";
            count["settings"] = new JsonObject { ["search_path"] = "app, pg_temp" };
            yield return ("routines/tenant-count", count);
        }
    }
}
