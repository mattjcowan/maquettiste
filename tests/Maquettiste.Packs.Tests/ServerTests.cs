namespace Maquettiste.Packs.Tests;

/// <summary>
/// Runs the sql-ddl output on real PostgreSQL and SQL Server servers: the schema script with the seed twice, and the migration
/// chain across model changes (added indexed and defaulted columns, a new relation closing a foreign-key cycle, a renamed and
/// retyped column, a changed foreign key, a new reference row, a dropped column with a default), plus the schema script and first
/// migration of a model with a foreign-key cycle. The scripts go through the servers' own clients in running Docker containers,
/// so the test project needs no database providers. Each test skips unless its container is named in the environment:
/// <c>MAQUETTISTE_TEST_POSTGRES_CONTAINER</c> (user <c>postgres</c>), or <c>MAQUETTISTE_TEST_SQLSERVER_CONTAINER</c> with
/// <c>MAQUETTISTE_TEST_SQLSERVER_PASSWORD</c> (the <c>sa</c> password; the image's <c>/opt/mssql-tools18</c> sqlcmd is used).
/// </summary>
public sealed class ServerTests
{
    [Fact]
    public Task PostgreSQL_runs_the_schema_seed_and_migration_chain() => RunScenarioAsync(Server.Postgres(), "main");

    [Fact]
    public Task Sql_server_runs_the_schema_seed_and_migration_chain() => RunScenarioAsync(Server.SqlServer(), "reporting");

    private static async Task RunScenarioAsync(Server? server, string database)
    {
        if (server is null)
        {
            Assert.Skip("Set MAQUETTISTE_TEST_POSTGRES_CONTAINER, or MAQUETTISTE_TEST_SQLSERVER_CONTAINER and MAQUETTISTE_TEST_SQLSERVER_PASSWORD, to run the DDL on a real server.");
            return;
        }

        using var repo = PackRepo.BillingDialects();
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var folder = "db/" + database + "/";
        var seed = repo.Read(folder + "seed.sql");
        await server.RunAsync("schema", repo.Read(folder + "schema.sql"), seed, seed);
        var first = repo.Read(folder + "migrations/0001.sql");

        ModelChanges.ChangeCustomer(repo);
        ModelChanges.AddFeaturedInvoice(repo);
        ModelChanges.AddDisputedStatus(repo);
        ModelChanges.AddVoucherMethod(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        ModelChanges.RemovePriority(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        seed = repo.Read(folder + "seed.sql");
        await server.RunAsync("chain", first, repo.Read(folder + "migrations/0002.sql"), repo.Read(folder + "migrations/0003.sql"), seed, seed);

        using var cycle = PackRepo.BillingDialects();
        ModelChanges.AddFeaturedInvoice(cycle);
        await cycle.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        await server.RunAsync("cycle_schema", cycle.Read(folder + "schema.sql"));
        await server.RunAsync("cycle_migration", cycle.Read(folder + "migrations/0001.sql"));
    }

    private sealed class Server
    {
        private readonly string docker;
        private readonly string container;
        private readonly string? password;

        private Server(string docker, string container, string? password)
        {
            this.docker = docker;
            this.container = container;
            this.password = password;
        }

        public static Server? Postgres()
        {
            var container = Environment.GetEnvironmentVariable("MAQUETTISTE_TEST_POSTGRES_CONTAINER");
            var docker = ProcessRunner.FindOnPath("docker");
            return string.IsNullOrEmpty(container) || docker is null ? null : new Server(docker, container, null);
        }

        public static Server? SqlServer()
        {
            var container = Environment.GetEnvironmentVariable("MAQUETTISTE_TEST_SQLSERVER_CONTAINER");
            var password = Environment.GetEnvironmentVariable("MAQUETTISTE_TEST_SQLSERVER_PASSWORD");
            var docker = ProcessRunner.FindOnPath("docker");
            return string.IsNullOrEmpty(container) || string.IsNullOrEmpty(password) || docker is null ? null : new Server(docker, container, password);
        }

        /// <summary>Creates a fresh database and runs the scripts in it one after the other, failing on the first error.</summary>
        public async Task RunAsync(string label, params string[] scripts)
        {
            var name = "mq_" + label + "_" + Guid.NewGuid().ToString("N")[..8];
            await ExecAsync(null, password is null ? $"CREATE DATABASE {name};" : $"CREATE DATABASE [{name}];\nGO\n");
            try
            {
                for (var i = 0; i < scripts.Length; i++)
                    await ExecAsync(name, scripts[i], $"{label} script {i + 1}");
            }
            finally
            {
                await ExecAsync(null, password is null ? $"DROP DATABASE IF EXISTS {name};" : $"DROP DATABASE [{name}];\nGO\n");
            }
        }

        private async Task ExecAsync(string? database, string script, string what = "setup")
        {
            string[] args = password is null
                ? ["exec", "-i", container, "psql", "-U", "postgres", "-X", "-q", "-v", "ON_ERROR_STOP=1", "-d", database ?? "postgres"]
                : ["exec", "-i", container, "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-P", password, "-C", "-b", "-d", database ?? "master", "-i", "/dev/stdin"];
            var run = await ProcessRunner.RunAsync(docker, args, Environment.CurrentDirectory, TimeSpan.FromMinutes(2), script);
            Assert.True(run.ExitCode == 0, $"{what} failed on {container}:\n{run.Output}\n--- script ---\n{script}");
        }
    }
}
