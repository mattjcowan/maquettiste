using System.Collections.Concurrent;
using System.Text;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Loading;

/// <summary>An engine-write guard that allows model writes under the model root and cache writes under the cache folder.</summary>
internal sealed class FakePathPolicy(EngineOptions options) : IOutputPathPolicy
{
    private readonly string _modelRoot = Path.GetFullPath(options.EffectiveModelRoot);
    private readonly string _cache = Path.GetFullPath(options.CacheDirectory);

    /// <summary>Every engine write checked, in order.</summary>
    public ConcurrentQueue<(WriteTarget Target, string Path)> Checked { get; } = new();

    /// <summary>Paths to refuse (full paths, compared ordinally).</summary>
    public Func<string, bool> Refuse { get; set; } = _ => false;

    public PathCheck Check(string repoRelativePath) => new(false, repoRelativePath, null, "MQ6004", "Not an output test.");

    public PathCheck CheckEngineWrite(WriteTarget target, string fullPath)
    {
        Checked.Enqueue((target, fullPath));
        var root = target switch
        {
            WriteTarget.Model => _modelRoot,
            WriteTarget.Cache => _cache,
            _ => null,
        };
        var allowed = root is not null && Path.GetFullPath(fullPath).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Refuse(fullPath);
        return new PathCheck(allowed, fullPath, null, allowed ? null : "MQ6004", allowed ? null : "refused by the test policy");
    }
}

/// <summary>A validator whose findings come from a function; records every call.</summary>
internal sealed class FakeValidator : IModelValidator
{
    public Func<ModelSnapshot, ValidationScope, IEnumerable<Diagnostic>> Rule { get; set; } = (_, _) => [];

    public ConcurrentQueue<(ModelSnapshot Model, ValidationScope Scope)> Calls { get; } = new();

    public Task<ValidationReport> ValidateAsync(ModelSnapshot model, ValidationScope scope, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Enqueue((model, scope));
        return Task.FromResult(ValidationReport.From(Rule(model, scope)));
    }
}

/// <summary>A loader wrapper that counts calls.</summary>
internal sealed class CountingLoader(IModelLoader inner) : IModelLoader
{
    private int _calls;

    public int Calls => _calls;

    public async Task<LoadResult> LoadAsync(LoadRequest request, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        await Task.Yield();
        return await inner.LoadAsync(request, progress, ct);
    }
}

/// <summary>A synchronous progress sink (Progress&lt;T&gt; posts asynchronously).</summary>
internal sealed class SyncProgress(Action<ProgressUpdate> onReport) : IProgress<ProgressUpdate>
{
    public void Report(ProgressUpdate value) => onReport(value);
}

/// <summary>A temp repo with a real loader, a fake path policy and a fake validator.</summary>
internal sealed class LoaderHarness : IDisposable
{
    public LoaderHarness(int parallelism = 2)
    {
        Repo = new TempRepo();
        Options = Repo.Options with { MaxDegreeOfParallelism = parallelism };
        Policy = new FakePathPolicy(Options);
        Validator = new FakeValidator();
    }

    public TempRepo Repo { get; }

    public EngineOptions Options { get; }

    public FakePathPolicy Policy { get; }

    public FakeValidator Validator { get; }

    public ModelLoader NewLoader() => new(Options, TestServices.Schemas, TestServices.Json, Policy);

    public EngineServices Services(IModelLoader? loader = null) =>
        EngineServices.Create(Options) with
        {
            Schemas = TestServices.Schemas,
            Json = TestServices.Json,
            EnginePaths = Policy,
            Loader = loader ?? NewLoader(),
            Validator = Validator,
        };

    public ModelStore NewStore(IModelLoader? loader = null) => new(Options, Services(loader));

    public void CopyFixture(params string[] fixture)
    {
        var source = Fixtures.Path([.. fixture, ".maquettiste"]);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(Repo.ModelRoot, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    public string Model(string modelPath) => Path.Combine(Repo.ModelRoot, modelPath.Replace('/', Path.DirectorySeparatorChar));

    public void Write(string modelPath, string text)
    {
        var path = Model(modelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
    }

    public string Read(string modelPath) => File.ReadAllText(Model(modelPath), Encoding.UTF8);

    public bool Exists(string modelPath) => File.Exists(Model(modelPath));

    public void Dispose() => Repo.Dispose();
}

/// <summary>Ids of the billing fixture (tests/fixtures/models/billing).</summary>
internal static class Billing
{
    public static string IdOf(ModelSnapshot model, string kind, string name) =>
        model.Documents.Single(d => d.Element.KindName == kind && d.Element.Name == name).Element.Id;
}
