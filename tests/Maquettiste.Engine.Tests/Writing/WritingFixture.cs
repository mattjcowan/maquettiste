using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Writing;

/// <summary>An in-memory unit state store (W6's store is a fake here).</summary>
internal sealed class InMemoryUnitStateStore : IUnitStateStore
{
    public ConcurrentDictionary<string, IReadOnlyDictionary<string, UnitState>> Packs { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyDictionary<string, UnitState>> LoadAsync(string pack, CancellationToken ct) =>
        Task.FromResult(Packs.TryGetValue(pack, out var states) ? states : new Dictionary<string, UnitState>());

    public Task SaveAsync(string pack, IReadOnlyCollection<UnitState> states, CancellationToken ct)
    {
        Packs[pack] = states.ToDictionary(s => s.Key, StringComparer.Ordinal);
        return Task.CompletedTask;
    }
}

/// <summary>A temp repo with committed root <c>db</c>, built root <c>src/Generated</c>, and the W7 components over it.</summary>
internal sealed class WritingFixture : IDisposable
{
    public WritingFixture(HandEditPolicy policy = HandEditPolicy.Fail, IReadOnlyList<string>? deny = null)
    {
        Repo = new TempRepo();
        Settings = new ProjectSettings
        {
            FormatVersion = 1,
            Outputs = new OutputSettings
            {
                Allow = [new OutputRoot { Path = "db", Commit = true }, new OutputRoot { Path = "src/Generated" }],
                Deny = deny ?? [],
            },
        };
        Policy = policy;
        Paths = new OutputPathPolicy(Repo.Options, Settings);
        EnginePaths = new OutputPathPolicy(Repo.Options, null);
        Manifests = new ManifestStore(Repo.Options, TestServices.Json, EnginePaths);
        Journal = new RunJournal(Repo.Options, EnginePaths);
        Writer = new OutputWriter(Repo.Options, Paths, Manifests, new DiffGenerator());
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TempRepo Repo { get; }

    public ProjectSettings Settings { get; }

    public HandEditPolicy Policy { get; set; }

    public OutputPathPolicy Paths { get; }

    public OutputPathPolicy EnginePaths { get; }

    public ManifestStore Manifests { get; }

    public RunJournal Journal { get; }

    public OutputWriter Writer { get; set; }

    public InMemoryUnitStateStore State { get; } = new();

    public static LoadedPack Pack(string name = "p") => new(
        name, 0, "/packs/" + name, "templates/" + name,
        new PackManifest { Name = name, Version = "1.0.0", Engine = ">=1.0 <2.0", Units = [] },
        new PackSettings(), ImmutableDictionary<string, JsonElement>.Empty, [], "scripts", ImmutableDictionary<string, IReadOnlyDictionary<string, string>>.Empty);

    public static PlannedUnit Planned(string key)
    {
        var slash = key.IndexOf('/', StringComparison.Ordinal);
        var pack = key[..slash];
        var unitId = key[(slash + 1)..].Split(':')[0];
        return new PlannedUnit(key, Pack(pack), new PackUnit { Id = unitId, Template = "t.scriban", For = "model" }, null, "static");
    }

    public static OutputFile Out(string path, string text, OutputMode mode = OutputMode.Overwrite, FileRole role = FileRole.Main, bool commit = false)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = ContentHash.Of(bytes);
        var manifestHash = mode == OutputMode.Regions
            ? ManifestHashes.RegionsPrefix + ContentHash.Of(ManifestHashes.Skeleton(bytes))
            : mode == OutputMode.Once || role == FileRole.Companion ? ManifestHashes.OwnedPrefix + hash : hash;
        var root = path.StartsWith("db/", StringComparison.Ordinal) ? new OutputRootInfo("db", true) : new OutputRootInfo("src/Generated", commit);
        return new OutputFile(path, bytes, hash, manifestHash, mode, role, root);
    }

    public static ProcessedUnit Unit(string key, params OutputFile[] files) => Unit(key, false, files);

    public static ProcessedUnit Unit(string key, bool failed, params OutputFile[] files)
    {
        var rendered = new RenderedUnit(Planned(key), [], ["e:b", "e:a"], "input-" + key, [], failed);
        return new ProcessedUnit(rendered, files, [], failed);
    }

    public static async IAsyncEnumerable<ProcessedUnit> Stream(IEnumerable<ProcessedUnit> units, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var unit in units)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return unit;
        }
    }

    public async Task<ManifestSet> LoadManifestsAsync(bool overlayJournal = false)
    {
        var set = await Manifests.LoadAsync([], Ct);
        if (overlayJournal && await Journal.ReadUnfinishedAsync(Ct) is { } records)
            set = set.WithJournalOverlay(records);
        return set;
    }

    public WriteContext Context(ManifestSet manifests, GenerationMode mode = GenerationMode.Apply, IReadOnlyList<SkippedUnit>? skipped = null,
        IReadOnlyDictionary<string, int>? counts = null, bool allPacks = false, RootSelection roots = RootSelection.All, bool diffs = false,
        IReadOnlySet<string>? planned = null, bool journal = true) =>
        new(mode, "01HRUN0000000000000000000", new Dictionary<string, HandEditPolicy> { ["p"] = Policy, ["q"] = Policy }, manifests,
            skipped ?? [], counts ?? new Dictionary<string, int>(), allPacks, roots, diffs, journal && mode == GenerationMode.Apply ? Journal : null,
            State, planned);

    /// <summary>Runs one complete apply (journal begin and end, as the orchestrator does) and returns the summary.</summary>
    public async Task<WriteSummary> RunAsync(IEnumerable<ProcessedUnit> units, GenerationMode mode = GenerationMode.Apply,
        IReadOnlyList<SkippedUnit>? skipped = null, bool allPacks = false, RootSelection roots = RootSelection.All, bool diffs = false,
        IReadOnlySet<string>? planned = null, IReadOnlyDictionary<string, int>? counts = null)
    {
        var manifests = await LoadManifestsAsync(overlayJournal: true);
        var apply = mode == GenerationMode.Apply;
        if (apply)
            await Journal.BeginAsync("01HRUN0000000000000000000", null, ["p", "q"], Ct);
        var list = units.ToList();
        if (counts is null)
        {
            // As the orchestrator does: every run pack with its number of units, so a pack closes when its last unit arrives.
            var byPack = list.GroupBy(u => u.Rendered.Unit.Pack.Name).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            byPack.TryAdd("p", 0);
            counts = byPack;
        }

        var summary = await Writer.WriteAsync(Stream(list, Ct), Context(manifests, mode, skipped, counts, allPacks, roots, diffs, planned), null, Ct);
        if (apply)
            await Journal.EndAsync(Ct);
        return summary;
    }

    public string ManifestText(string pack, bool committed)
    {
        var file = Manifests.FileOf(pack, committed);
        return System.IO.File.Exists(file) ? System.IO.File.ReadAllText(file, Encoding.UTF8) : "";
    }

    public IReadOnlyList<string> TempFiles() =>
        [.. Directory.EnumerateFiles(Repo.Root, "*.tmp", SearchOption.AllDirectories)];

    public void Dispose()
    {
        Journal.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Repo.Dispose();
    }
}
