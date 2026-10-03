namespace Maquettiste.Packs.Tests;

/// <summary>
/// A database server in a running Docker container, driven through its own command-line client, so the test project needs no
/// database providers. Each server is named in the environment: <c>MAQUETTISTE_TEST_POSTGRES_CONTAINER</c> (user <c>postgres</c>),
/// <c>MAQUETTISTE_TEST_SQLSERVER_CONTAINER</c> with <c>MAQUETTISTE_TEST_SQLSERVER_PASSWORD</c> (the <c>sa</c> password, the image's
/// <c>/opt/mssql-tools18</c> sqlcmd), <c>MAQUETTISTE_TEST_MYSQL_CONTAINER</c> with <c>MAQUETTISTE_TEST_MYSQL_PASSWORD</c> and
/// <c>MAQUETTISTE_TEST_MARIADB_CONTAINER</c> with <c>MAQUETTISTE_TEST_MARIADB_PASSWORD</c> (the <c>root</c> passwords).
/// </summary>
internal sealed class DbServer
{
    private readonly string docker;
    private readonly string container;
    private readonly string? password;

    private DbServer(string dialect, string docker, string container, string? password, string client)
    {
        Dialect = dialect;
        this.docker = docker;
        this.container = container;
        this.password = password;
        Client = client;
    }

    /// <summary>The dialect the server speaks (<c>mysql</c> for MariaDB too).</summary>
    public string Dialect { get; }

    /// <summary>The client program (mysql or mariadb for the MySQL dialect).</summary>
    private string Client { get; }

    /// <summary>The PostgreSQL server, or <see langword="null"/> when none is configured.</summary>
    public static DbServer? Postgres() => Create("postgresql", "MAQUETTISTE_TEST_POSTGRES_CONTAINER", null, "psql");

    /// <summary>The SQL Server server, or <see langword="null"/>.</summary>
    public static DbServer? SqlServer() => Create("sqlserver", "MAQUETTISTE_TEST_SQLSERVER_CONTAINER", "MAQUETTISTE_TEST_SQLSERVER_PASSWORD", "sqlcmd");

    /// <summary>The MySQL server, or <see langword="null"/>.</summary>
    public static DbServer? MySql() => Create("mysql", "MAQUETTISTE_TEST_MYSQL_CONTAINER", "MAQUETTISTE_TEST_MYSQL_PASSWORD", "mysql");

    /// <summary>The MariaDB server, or <see langword="null"/>.</summary>
    public static DbServer? MariaDb() => Create("mysql", "MAQUETTISTE_TEST_MARIADB_CONTAINER", "MAQUETTISTE_TEST_MARIADB_PASSWORD", "mariadb");

    private static DbServer? Create(string dialect, string containerVariable, string? passwordVariable, string client)
    {
        var container = Environment.GetEnvironmentVariable(containerVariable);
        var password = passwordVariable is null ? null : Environment.GetEnvironmentVariable(passwordVariable);
        var docker = ProcessRunner.FindOnPath("docker");
        if (string.IsNullOrEmpty(container) || docker is null || (passwordVariable is not null && string.IsNullOrEmpty(password)))
            return null;
        return new DbServer(dialect, docker, container, password, client);
    }

    /// <summary>Creates a fresh database, runs the scripts in it one after the other (failing on the first error) and drops it.</summary>
    /// <returns>The output of the last script.</returns>
    public async Task<string> RunAsync(string label, params string[] scripts)
    {
        var name = "mq_" + label + "_" + Guid.NewGuid().ToString("N")[..8];
        await ExecAsync(null, Dialect == "sqlserver" ? $"CREATE DATABASE [{name}];\nGO\n" : $"CREATE DATABASE {name};");
        try
        {
            var output = "";
            for (var i = 0; i < scripts.Length; i++)
                output = await ExecAsync(name, scripts[i], $"{label} script {i + 1}");
            return output;
        }
        finally
        {
            await ExecAsync(null, Dialect == "sqlserver" ? $"DROP DATABASE [{name}];\nGO\n" : $"DROP DATABASE IF EXISTS {name};");
        }
    }

    private async Task<string> ExecAsync(string? database, string script, string what = "setup")
    {
        string[] args = Client switch
        {
            "psql" => ["exec", "-i", container, "psql", "-U", "postgres", "-X", "-q", "-t", "-A", "-v", "ON_ERROR_STOP=1", "-d", database ?? "postgres"],
            "sqlcmd" => ["exec", "-i", container, "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-P", password!, "-C", "-b", "-h", "-1", "-d", database ?? "master", "-i", "/dev/stdin"],
            _ => ["exec", "-i", container, Client, "-uroot", "-p" + password, "--batch", "--skip-column-names", .. database is null ? Array.Empty<string>() : ["--database=" + database]],
        };
        var run = await ProcessRunner.RunAsync(docker, args, Environment.CurrentDirectory, TimeSpan.FromMinutes(2), script);
        Assert.True(run.ExitCode == 0, $"{what} failed on {container}:\n{run.Output}\n--- script ---\n{script}");
        return run.Output;
    }
}
