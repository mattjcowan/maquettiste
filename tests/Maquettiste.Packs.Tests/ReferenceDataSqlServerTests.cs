using System.Diagnostics.CodeAnalysis;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The sql-ddl reference-data realizations (lookup table, check, native) run on SQL Server (reference-types-seeds-localization.md
/// section 2): schema then seed twice, the seed after a code is added (a row uses it, then stops) and after it is retired, the
/// first migration then seed twice, and a row holding a code no seed has refused. The server is the container named by
/// <c>MAQUETTISTE_TEST_SQLSERVER_CONTAINER</c> (with <c>MAQUETTISTE_TEST_SQLSERVER_PASSWORD</c>), else one this class starts from
/// <see cref="SqlServerContainer.Image"/> when that image is available locally and removes afterwards; otherwise the tests skip
/// with the reason. Scripts go through the image's own sqlcmd, so the test project needs no database provider.
/// </summary>
public sealed class ReferenceDataSqlServerTests(SqlServerContainer server) : IClassFixture<SqlServerContainer>
{
    [Theory]
    [InlineData("lookup-table")]
    [InlineData("check")]
    [InlineData("native")]
    public async Task Sql_server_runs_the_scripts_twice_and_after_a_code_is_added_and_retired(string strategy)
    {
        if (server.SkipReason is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        var scripts = await ReferenceDataTests.ScriptsAsync(strategy, "reporting");
        const string count = "SELECT 'rows=' + CAST(COUNT(*) AS varchar(10)) FROM dbo.recipe_ingredient;";
        var (code, output) = await server.RunAsync(
            scripts.Schema, scripts.Seed, scripts.Seed, scripts.SeedAdded,
            "UPDATE dbo.ingredients SET default_unit = N'cup' WHERE sku = N'salt';\nUPDATE dbo.ingredients SET default_unit = N'g' WHERE sku = N'salt';",
            count, scripts.SeedRetired, count);
        Assert.True(code == 0, output);
        Assert.Contains("rows=2", output, StringComparison.Ordinal);

        (code, output) = await server.RunAsync(scripts.Migration, scripts.Seed, scripts.Seed);
        Assert.True(code == 0, output);

        (code, output) = await server.RunAsync(scripts.Schema, scripts.Seed, "UPDATE dbo.ingredients SET default_unit = N'nope' WHERE sku = N'salt';");
        Assert.NotEqual(0, code);
        Assert.Contains("default_unit_ref", output, StringComparison.Ordinal);
    }
}

/// <summary>A SQL Server for the tests: the one named in the environment, or a container started from a local image.</summary>
[SuppressMessage("Design", "CA1063:Implement IDisposable correctly", Justification = "The fixture owns one container.")]
public sealed class SqlServerContainer : IAsyncLifetime
{
    /// <summary>The image started when no container is named.</summary>
    public const string Image = "mcr.microsoft.com/mssql/server:2022-latest";

    private string? docker;
    private string? container;
    private string? password;
    private bool owned;

    /// <summary>Why the tests cannot run, or null when the server is up.</summary>
    public string? SkipReason { get; private set; }

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        docker = ProcessRunner.FindOnPath("docker");
        if (docker is null)
        {
            SkipReason = "docker is not on the PATH; SQL Server runs in a container.";
            return;
        }

        container = Environment.GetEnvironmentVariable("MAQUETTISTE_TEST_SQLSERVER_CONTAINER");
        password = Environment.GetEnvironmentVariable("MAQUETTISTE_TEST_SQLSERVER_PASSWORD");
        if (!string.IsNullOrEmpty(container) && !string.IsNullOrEmpty(password))
            return;

        var cwd = Environment.CurrentDirectory;
        var inspect = await ProcessRunner.RunAsync(docker, ["image", "inspect", "--format", "{{.Id}}", Image], cwd, TimeSpan.FromSeconds(30));
        if (inspect.ExitCode != 0)
        {
            SkipReason = $"The image {Image} is not available locally (docker pull {Image}), and no container is named by MAQUETTISTE_TEST_SQLSERVER_CONTAINER.";
            return;
        }

        // A strong sa password (upper and lower case, digits and a symbol), new per run.
        password = "Mq!" + Guid.NewGuid().ToString("N")[..16] + "aZ9";
        container = "mq-test-mssql-" + Guid.NewGuid().ToString("N")[..8];
        var run = await ProcessRunner.RunAsync(docker, ["run", "-d", "--rm", "--name", container, "-e", "ACCEPT_EULA=Y", "-e", "MSSQL_SA_PASSWORD=" + password, Image], cwd, TimeSpan.FromMinutes(2));
        if (run.ExitCode != 0)
        {
            SkipReason = $"docker run {Image} failed: {run.Output}";
            return;
        }

        owned = true;
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (true)
        {
            var ready = await SqlcmdAsync("master", "SELECT 1;");
            if (ready.ExitCode == 0)
                return;
            if (DateTime.UtcNow > deadline)
            {
                SkipReason = $"SQL Server in {container} did not accept connections within two minutes: {ready.Output}";
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (owned && docker is not null && container is not null)
            await ProcessRunner.RunAsync(docker, ["rm", "-f", container], Environment.CurrentDirectory, TimeSpan.FromMinutes(1));
    }

    /// <summary>Runs the scripts one after the other (each its own batches) in a fresh database, dropped afterwards.</summary>
    /// <param name="scripts">The scripts.</param>
    /// <returns>sqlcmd's exit code (non-zero on the first error) and output.</returns>
    public async Task<(int Code, string Output)> RunAsync(params string[] scripts)
    {
        var name = "mq_ref_" + Guid.NewGuid().ToString("N")[..8];
        await SqlcmdAsync("master", $"CREATE DATABASE [{name}];\nGO\n");
        try
        {
            var run = await SqlcmdAsync(name, string.Join("\nGO\n", scripts) + "\nGO\n");
            return (run.ExitCode, run.Output);
        }
        finally
        {
            await SqlcmdAsync("master", $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;\nDROP DATABASE [{name}];\nGO\n");
        }
    }

    private Task<ProcessRunner.Result> SqlcmdAsync(string database, string script) =>
        ProcessRunner.RunAsync(docker!, ["exec", "-i", container!, "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-P", password!, "-C", "-b", "-d", database, "-i", "/dev/stdin"],
            Environment.CurrentDirectory, TimeSpan.FromMinutes(2), script);
}
