using System.Text;
using Maquettiste.Testing;

namespace Maquettiste.Cli.Tests;

/// <summary>The result of one in-process CLI run.</summary>
public sealed record CliResult(int ExitCode, string Out, string Error)
{
    public override string ToString() => $"exit {ExitCode}\n--- stdout ---\n{Out}\n--- stderr ---\n{Error}";
}

/// <summary>A thread-safe string writer for stderr, which stages and the watcher write from several threads.</summary>
public sealed class SharedWriter : StringWriter
{
    private readonly Lock _gate = new();

    public SharedWriter() => NewLine = "\n";

    public override void Write(char value)
    {
        lock (_gate)
            base.Write(value);
    }

    public override void Write(string? value)
    {
        lock (_gate)
            base.Write(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (_gate)
            base.Write(buffer, index, count);
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
            base.WriteLine(value);
    }

    public override Task WriteAsync(string? value)
    {
        Write(value);
        return Task.CompletedTask;
    }

    public override Task WriteLineAsync(string? value)
    {
        WriteLine(value);
        return Task.CompletedTask;
    }

    public string Text()
    {
        lock (_gate)
            return ToString();
    }
}

/// <summary>Runs the CLI in-process against a temporary repo.</summary>
public sealed class CliRepo : IDisposable
{
    public const string SettingsJson = """
        {
          "$schema": ".schema/v1/maquettiste.json",
          "formatVersion": 1,
          "name": "billing",
          "outputs": {
            "allow": [
              {
                "path": "db",
                "commit": true
              },
              {
                "path": "src/Generated"
              }
            ]
          },
          "packs": {
            "classes": {
              "output": "src/Generated"
            },
            "ddl": {
              "output": "db"
            }
          }
        }

        """;

    private const string DdlPack = """
        {
          "name": "ddl",
          "version": "1.0.0",
          "engine": ">=1.0 <2.0",
          "units": [
            {
              "id": "table",
              "template": "table.scriban",
              "for": "each table",
              "output": "{{ kebab table.database.name }}/{{ table.name }}.sql"
            }
          ]
        }

        """;

    private const string DdlTemplate = """
        -- {{ table.name }}
        CREATE TABLE {{ table.name }} (
        {{- for c in table.columns }}
            {{ c.name }} {{ type_of c table.database.dialect }}{{ if !c.nullable }} NOT NULL{{ end }}{{ if !for.last }},{{ end }}
        {{- end }}
        );

        """;

    private const string ClassesPack = """
        {
          "name": "classes",
          "version": "1.0.0",
          "engine": ">=1.0 <2.0",
          "units": [
            {
              "id": "entity",
              "template": "entity.scriban",
              "for": "each entity",
              "output": "{{ pascal entity.name }}.g.cs"
            }
          ]
        }

        """;

    private const string ClassesTemplate = """
        // {{ entity.name }}
        public partial class {{ pascal entity.name }}
        {
        {{- for a in entity.attributes }}
            // {{ a.name }}
        {{- end }}
        }

        """;

    private CliRepo(TempRepo temp) => Temp = temp;

    public TempRepo Temp { get; }

    public string RepoRoot => Temp.RepoRoot;

    public string ModelRoot => Temp.ModelRoot;

    public string CacheDirectory => Temp.CacheDirectory;

    /// <summary>A repo with nothing in it but the empty <c>.maquettiste/</c> folder that <see cref="TempRepo"/> creates.</summary>
    public static CliRepo Empty()
    {
        var temp = new TempRepo();
        Directory.Delete(temp.ModelRoot);
        return new CliRepo(temp);
    }

    /// <summary>The reference-data fixture model (reference types, seeds, locales en, fr and fr-CA).</summary>
    public static CliRepo ReferenceData()
    {
        var repo = new CliRepo(new TempRepo());
        CopyTree(Fixtures.Path("models", "reference-data", ".maquettiste"), repo.ModelRoot);
        return repo;
    }

    /// <summary>The gate 3 process fixture (phase-3-design.md section 8.1): two processes, eight actors, 13 scenarios.</summary>
    public static CliRepo Processes()
    {
        var repo = new CliRepo(new TempRepo());
        CopyTree(Fixtures.Path("models", "processes", ".maquettiste"), repo.ModelRoot);
        return repo;
    }

    /// <summary>The billing fixture model with two small test packs: <c>ddl</c> (committed root <c>db</c>) and <c>classes</c> (built root).</summary>
    public static CliRepo Billing()
    {
        var repo = new CliRepo(new TempRepo());
        CopyTree(Fixtures.Path("models", "billing", ".maquettiste"), repo.ModelRoot);
        repo.Temp.WriteFile(".maquettiste/maquettiste.json", SettingsJson);
        repo.Temp.WriteFile(".maquettiste/templates/ddl/pack.json", DdlPack);
        repo.Temp.WriteFile(".maquettiste/templates/ddl/table.scriban", DdlTemplate);
        repo.Temp.WriteFile(".maquettiste/templates/classes/pack.json", ClassesPack);
        repo.Temp.WriteFile(".maquettiste/templates/classes/entity.scriban", ClassesTemplate);
        return repo;
    }

    public static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    public string PathOf(string repoRelative) => Temp.PathOf(repoRelative);

    public string Read(string repoRelative) => Temp.ReadFile(repoRelative);

    public void Write(string repoRelative, string text) => Temp.WriteFile(repoRelative, text);

    public void Replace(string repoRelative, string oldText, string newText)
    {
        var text = Read(repoRelative);
        Assert.Contains(oldText, text, StringComparison.Ordinal);
        Write(repoRelative, text.Replace(oldText, newText, StringComparison.Ordinal));
    }

    /// <summary>Runs the CLI with <c>--repo</c> and <c>--cache-dir</c> added.</summary>
    public Task<CliResult> RunAsync(params string[] args) => RunInAsync(RepoRoot, TestContext.Current.CancellationToken, [.. args, "--repo", RepoRoot, "--cache-dir", CacheDirectory]);

    /// <summary>Runs the CLI with exactly these arguments from a working directory.</summary>
    public static async Task<CliResult> RunInAsync(string currentDirectory, CancellationToken ct, params string[] args)
    {
        var output = new SharedWriter();
        var error = new SharedWriter();
        var code = await RunWithAsync(currentDirectory, output, error, ct, _ => null, args);
        return new CliResult(code, output.Text(), error.Text());
    }

    public static Task<int> RunWithAsync(string currentDirectory, TextWriter output, TextWriter error, CancellationToken ct, Func<string, string?> env, params string[] args)
    {
        var environment = new CliEnvironment
        {
            Out = output,
            Error = error,
            CurrentDirectory = currentDirectory,
            GetEnvironmentVariable = env,
            ErrorIsTerminal = false,
        };
        return new CliApp(environment).RunAsync(args, ct);
    }

    /// <summary>Every file under a repo-relative folder, relative to it, ordinal, with its bytes.</summary>
    public IReadOnlyDictionary<string, byte[]> Tree(string folder)
    {
        var root = PathOf(folder);
        if (!Directory.Exists(root))
            return new Dictionary<string, byte[]>();
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal);
    }

    public void Dispose() => Temp.Dispose();
}

public static class Text
{
    public static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
}
