using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// A temporary repo holding a copy of the billing fixture (<c>tests/fixtures/models/billing</c>) and real Scriban packs, driven only
/// through the public API: <c>new ModelStore(options)</c> and <c>new GenerationService(store, options)</c>, which compose the real
/// loader, validator, resolver, sandbox, renderer, post-processor, formatter runner, path policy, writer, manifest and journal
/// through <c>EngineServices.Create</c>.
/// </summary>
/// <remarks>
/// Packs: <c>e2e</c> (tests/fixtures/integration/packs/e2e, output <c>db/e2e</c>), <c>billing-demo</c>
/// (tests/fixtures/templates/billing-demo, output <c>src/Generated/demo</c>) and, when asked for, <c>migrations</c>
/// (tests/fixtures/integration/packs/migrations, output <c>db</c>, schema diff).
/// </remarks>
internal sealed class E2ERepo : IAsyncDisposable
{
    public const string CustomerId = "01J92P0V0ETQKXXP951CMMNHH3";
    public const string CustomerNameAttributeId = "01J92P0V0MS09YFZHX07JQ3KMN";
    public const string ProductId = "01J92P0V0JR8BE8253SKT29ZG7";
    public const string ProductListPriceAttributeId = "01J92P0V1935QZTM8ZQ240DJ6Q";
    public const string InvoiceId = "01J92P0V0FJ23CGSNKM7P1W5V7";
    public const string MainDatabaseId = "01J92P0V1QRN2181XM2ZWE02W4";

    private E2ERepo(TempRepo repo, int parallelism)
    {
        Repo = repo;
        Options = repo.Options with { MaxDegreeOfParallelism = parallelism };
        Open();
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TempRepo Repo { get; }

    public EngineOptions Options { get; }

    public ModelStore Store { get; private set; } = null!;

    public GenerationService Service { get; private set; } = null!;

    /// <summary>Creates a repo with the billing model and the packs.</summary>
    /// <param name="parallelism">EngineOptions.MaxDegreeOfParallelism.</param>
    /// <param name="settings">Edits maquettiste.json (after the packs are configured).</param>
    /// <param name="demo">Whether to install billing-demo.</param>
    /// <param name="migrations">Whether to install the migrations pack.</param>
    /// <returns>The repo.</returns>
    public static E2ERepo Create(int parallelism = 2, Action<JsonObject>? settings = null, bool demo = true, bool migrations = false)
    {
        var repo = new TempRepo();
        CopyTree(Fixtures.Path("models", "billing"), repo.RepoRoot);
        var packs = new List<(string Name, string Source, string Output)> { ("e2e", Fixtures.Path("integration", "packs", "e2e"), "db/e2e") };
        if (demo)
            packs.Add(("billing-demo", Fixtures.Path("templates", "billing-demo"), "src/Generated/demo"));
        if (migrations)
            packs.Add(("migrations", Fixtures.Path("integration", "packs", "migrations"), "db"));
        foreach (var (name, source, _) in packs)
            CopyTree(source, Path.Combine(repo.ModelRoot, "templates", name));

        var settingsPath = Path.Combine(repo.ModelRoot, "maquettiste.json");
        var node = JsonNode.Parse(File.ReadAllBytes(settingsPath))!.AsObject();
        var packSettings = node["packs"]!.AsObject();
        foreach (var (name, _, output) in packs)
            packSettings[name] = new JsonObject { ["output"] = output };
        settings?.Invoke(node);
        File.WriteAllBytes(settingsPath, TestServices.Json.Write(node, "maquettiste.json", "maquettiste.json"));
        return new E2ERepo(repo, parallelism);
    }

    /// <summary>Recreates the store and the service over the same folders (a host restart).</summary>
    public async Task RestartAsync()
    {
        await Store.DisposeAsync();
        Open();
    }

    private void Open()
    {
        Store = new ModelStore(Options);
        Service = new GenerationService(Store, Options);
    }

    public Task<GenerationResult> RunAsync(GenerationMode mode = GenerationMode.Apply, int? jobs = 2, bool force = false, HandEditPolicy? handEdits = null,
        IReadOnlyList<string>? packs = null, IProgress<ProgressUpdate>? progress = null, CancellationToken? ct = null, bool diffs = false,
        LockMode lockMode = LockMode.Wait) =>
        Service.RunAsync(new GenerationRequest
        {
            Mode = mode, Jobs = jobs, Force = force, HandEdits = handEdits, Packs = packs, IncludeDiffs = diffs, Lock = lockMode,
        }, progress, ct ?? Ct);

    /// <summary>Runs an apply and asserts it succeeded.</summary>
    public async Task<GenerationResult> ApplyAsync(int? jobs = 2, bool force = false)
    {
        var result = await RunAsync(GenerationMode.Apply, jobs, force);
        AssertOutcome(RunOutcome.Succeeded, result);
        return result;
    }

    public static void AssertOutcome(RunOutcome expected, GenerationResult result) =>
        Assert.True(expected == result.Outcome, $"Expected {expected}, got {result.Outcome}.\n" + Describe(result));

    public static string Describe(GenerationResult result) =>
        string.Join('\n', result.Diagnostics.Select(d => $"{d.Severity} {d.Rule} {d.FilePath}:{d.Line}:{d.Column} {d.JsonPointer} {d.Message}")
            .Concat(result.Changes.Select(c => $"{c.Kind} {c.Path} ({c.Pack} {c.UnitKey})")));

    /// <summary>Every file under the output roots and the engine's manifest folders, path → bytes.</summary>
    public SortedDictionary<string, byte[]> Tree()
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Repo.ListFiles())
        {
            if (IsOutput(path) || path.StartsWith(".maquettiste/manifest/", StringComparison.Ordinal)
                || path.StartsWith(".maquettiste/.cache/manifest/", StringComparison.Ordinal)
                || path.StartsWith(".maquettiste/snapshots/", StringComparison.Ordinal))
                files[path] = File.ReadAllBytes(Repo.PathOf(path));
        }

        return files;
    }

    /// <summary>Every output file (under <c>db/</c> and <c>src/</c>), path → text.</summary>
    public SortedDictionary<string, string> Outputs()
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Repo.ListFiles().Where(IsOutput))
            files[path] = Repo.ReadFile(path);
        return files;
    }

    /// <summary>Last-write times of every file under the output roots and manifests.</summary>
    public SortedDictionary<string, DateTime> WriteTimes()
    {
        var times = new SortedDictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var path in Tree().Keys)
            times[path] = File.GetLastWriteTimeUtc(Repo.PathOf(path));
        return times;
    }

    /// <summary>Sets every output and manifest file's mtime into the past, so any later write is visible even on coarse clocks.</summary>
    public void AgeFiles()
    {
        var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var path in Tree().Keys)
            File.SetLastWriteTimeUtc(Repo.PathOf(path), past);
    }

    public static bool IsOutput(string path) => path.StartsWith("db/", StringComparison.Ordinal) || path.StartsWith("src/", StringComparison.Ordinal);

    /// <summary>Edits an element through <see cref="ModelStore.SaveAsync"/> with its current hash.</summary>
    public async Task<SaveResult> EditAsync(string id, Action<JsonObject> edit)
    {
        var document = await Store.GetElementAsync(id, Ct);
        Assert.NotNull(document);
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        edit(node);
        var result = await Store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, result.Outcome + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        return result;
    }

    /// <summary>Creates an element through <see cref="ModelStore.CreateAsync"/>.</summary>
    public async Task<SaveResult> CreateElementAsync(string json)
    {
        var result = await Store.CreateAsync(Encoding.UTF8.GetBytes(json), ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, result.Outcome + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.JsonPointer + " " + d.Message)));
        return result;
    }

    public string Manifest(string pack) => Repo.ReadFile(".maquettiste/manifest/" + pack + ".json");

    public static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        Repo.Dispose();
    }
}

/// <summary>Collects progress updates synchronously (Progress&lt;T&gt; would post to the thread pool and lose ordering).</summary>
internal sealed class ProgressLog : IProgress<ProgressUpdate>
{
    private readonly Lock _gate = new();
    private readonly List<ProgressUpdate> _updates = [];
    private readonly Action<ProgressUpdate>? _onReport;

    public ProgressLog(Action<ProgressUpdate>? onReport = null) => _onReport = onReport;

    public IReadOnlyList<ProgressUpdate> Updates
    {
        get
        {
            lock (_gate)
                return [.. _updates];
        }
    }

    public void Report(ProgressUpdate value)
    {
        lock (_gate)
            _updates.Add(value);
        _onReport?.Invoke(value);
    }
}

/// <summary>
/// The end-to-end tests run one at a time (they share no state, but each drives a whole pipeline with up to eight workers, Jint
/// engines and external processes): running them all at once starves the thread pool that timing- and parallelism-sensitive unit
/// tests elsewhere in the assembly rely on.
/// </summary>
[CollectionDefinition(Name)]
public sealed class IntegrationCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "Integration";
}
