using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// A temporary repo holding the billing fixture model and the example packs from <c>packs/</c>, as <c>maquettiste init</c> would
/// lay them out (<c>db</c> committed, <c>src/Generated</c> built). The <c>dialects</c> variant adds a SQL Server database
/// (<c>reporting</c>) and a SQLite database (<c>local</c>, with enums stored as lookup tables) next to the PostgreSQL one, so
/// every dialect branch of the packs runs, and a <c>CreditNote</c> entity derived from <c>Invoice</c> (table per hierarchy, so a
/// discriminator column).
/// </summary>
internal sealed class PackRepo : IDisposable
{
    /// <summary>The example packs, in the order the engine runs them.</summary>
    public static readonly IReadOnlyList<string> Packs = ["csharp-dapper", "sql-ddl"];

    private const string DialectSettings = """
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
          "databases": {
            "local": {
              "enumStorage": "lookup"
            }
          },
          "packs": {
            "csharp-dapper": {
              "output": "src/Generated"
            },
            "sql-ddl": {
              "output": "db"
            }
          }
        }

        """;

    private const string CreditNote = """
        {
          "$schema": "../../.schema/v1/entity.json",
          "kind": "entity",
          "id": "01J92P0V2C0000000000000001",
          "name": "CreditNote",
          "package": "01J92P0V01KDRN8GX5PGYCNKSX",
          "base": "01J92P0V0FJ23CGSNKM7P1W5V7",
          "attributes": [
            {
              "id": "01J92P0V2C0000000000000002",
              "name": "reason",
              "type": "string",
              "length": 200,
              "required": true
            }
          ]
        }

        """;

    private PackRepo(bool dialects)
    {
        Repo = new TempRepo();
        CopyTree(Fixtures.Path("models", "billing", ".maquettiste"), Repo.ModelRoot);
        foreach (var pack in Packs)
            CopyTree(Path.Combine(Fixtures.RepoRoot, "packs", pack), Path.Combine(Repo.ModelRoot, "templates", pack));
        // The shared fixture's overlays compare the status column with enum values, but the column stores the member codes
        // (varchar(1)), which PostgreSQL rejects; compare with the codes so the generated DDL runs.
        Patch(".maquettiste/model/databases/main/tables/01j92p0v1t0j6rh4my9h81nyb4.json", "status in (0, 1, 2, 3)", "status in ('D', 'I', 'P', 'V')");
        Patch(".maquettiste/model/databases/main/views/outstanding-invoices.json", "where status = 1", "where status = 'I'");
        if (dialects)
        {
            Repo.WriteFile(".maquettiste/maquettiste.json", DialectSettings);
            Repo.WriteFile(".maquettiste/model/databases/reporting/database.json", Database("01J92P0V2A0000000000000001", "reporting", "sqlserver"));
            Repo.WriteFile(".maquettiste/model/databases/local/database.json", Database("01J92P0V2A0000000000000002", "local", "sqlite"));
            Repo.WriteFile(".maquettiste/model/entities/credit-note.json", CreditNote);
        }
    }

    /// <summary>The repo.</summary>
    public TempRepo Repo { get; }

    /// <summary>The billing fixture with its single PostgreSQL database.</summary>
    /// <returns>The repo.</returns>
    public static PackRepo Billing() => new(dialects: false);

    /// <summary>The billing fixture with a PostgreSQL, a SQL Server and a SQLite database.</summary>
    /// <returns>The repo.</returns>
    public static PackRepo BillingDialects() => new(dialects: true);

    /// <summary>Runs generation in process.</summary>
    /// <param name="mode">Apply, dry run or check.</param>
    /// <param name="packs">The packs to run; null for all.</param>
    /// <param name="force">Render every unit.</param>
    /// <param name="jobs">Render parallelism; null for the default.</param>
    /// <returns>The result.</returns>
    public async Task<GenerationResult> GenerateAsync(GenerationMode mode = GenerationMode.Apply, IReadOnlyList<string>? packs = null, bool force = false, int? jobs = null)
    {
        await using var store = new ModelStore(Repo.Options);
        var service = new GenerationService(store, Repo.Options);
        var request = new GenerationRequest { Mode = mode, Packs = packs, Force = force, Jobs = jobs };
        return await service.RunAsync(request, progress: null, TestContext.Current.CancellationToken);
    }

    /// <summary>Runs generation and fails the test unless it succeeded without errors.</summary>
    /// <param name="mode">Apply, dry run or check.</param>
    /// <param name="packs">The packs to run; null for all.</param>
    /// <param name="force">Render every unit.</param>
    /// <param name="jobs">Render parallelism; null for the default.</param>
    /// <returns>The result.</returns>
    public async Task<GenerationResult> GenerateCleanlyAsync(GenerationMode mode = GenerationMode.Apply, IReadOnlyList<string>? packs = null, bool force = false, int? jobs = null)
    {
        var result = await GenerateAsync(mode, packs, force, jobs);
        var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(result.Outcome == RunOutcome.Succeeded && errors.Count == 0, Describe(result));
        return result;
    }

    /// <summary>Describes a result for assertion messages.</summary>
    /// <param name="result">The result.</param>
    /// <returns>The outcome, diagnostics and changes.</returns>
    public static string Describe(GenerationResult result) =>
        $"Outcome {result.Outcome}\n"
        + string.Join('\n', result.Diagnostics.Select(d => $"{d.FilePath}({d.Line},{d.Column}): {d.Severity} {d.Rule}: {d.Message}"))
        + "\n" + string.Join('\n', result.Changes.Select(c => $"{c.Kind} {c.Path}"));

    /// <summary>The absolute path of a repo-relative path.</summary>
    /// <param name="path">The repo-relative path.</param>
    /// <returns>The absolute path.</returns>
    public string PathOf(string path) => Repo.PathOf(path);

    /// <summary>Reads a repo file.</summary>
    /// <param name="path">The repo-relative path.</param>
    /// <returns>The text.</returns>
    public string Read(string path) => Repo.ReadFile(path);

    /// <summary>Writes a repo file.</summary>
    /// <param name="path">The repo-relative path.</param>
    /// <param name="text">The text.</param>
    public void Write(string path, string text) => Repo.WriteFile(path, text);

    /// <inheritdoc/>
    public void Dispose() => Repo.Dispose();

    /// <summary>Copies a folder tree.</summary>
    /// <param name="from">The source folder.</param>
    /// <param name="to">The target folder.</param>
    public static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>Edits a repo JSON file in place.</summary>
    /// <param name="path">The repo-relative path.</param>
    /// <param name="edit">The edit.</param>
    public void EditJson(string path, Action<System.Text.Json.Nodes.JsonNode> edit)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Read(path))!;
        edit(node);
        Write(path, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private void Patch(string path, string from, string to)
    {
        var text = Repo.ReadFile(path);
        Assert.Contains(from, text, StringComparison.Ordinal);
        Repo.WriteFile(path, text.Replace(from, to, StringComparison.Ordinal));
    }

    private static string Database(string id, string name, string dialect) => $$"""
        {
          "$schema": "../../../.schema/v1/database.json",
          "kind": "database",
          "id": "{{id}}",
          "name": "{{name}}",
          "dialect": "{{dialect}}"
        }

        """;
}
