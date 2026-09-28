using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Rendering;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Writing;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Rendering;

/// <summary>A dependency hasher for tests: the input hash is H(static hash, keys…); current hashes are not needed.</summary>
internal sealed class TestHasher : IDependencyHasher
{
    public string CurrentHash(string dependencyKey) => "h:" + dependencyKey;

    public string InputHash(string staticHash, IReadOnlyList<string> sortedReadKeys) => HashBuilder.Of([staticHash, .. sortedReadKeys]);
}

/// <summary>Loads and resolves the billing fixture (tests/fixtures/models/billing) once per test process.</summary>
internal static class BillingModel
{
    private static readonly Lazy<Task<ResolvedModel>> Resolved = new(LoadAsync);

    public static Task<ResolvedModel> GetAsync() => Resolved.Value;

    private static async Task<ResolvedModel> LoadAsync()
    {
        var cache = Path.Combine(Path.GetTempPath(), "maquettiste-tests", "render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        var options = new EngineOptions { RepoRoot = Fixtures.Path("models", "billing"), CacheDirectory = cache, MaxDegreeOfParallelism = 2 };
        var loader = new ModelLoader(options, TestServices.Schemas, TestServices.Json, new OutputPathPolicy(options, null));
        var loaded = await loader.LoadAsync(new LoadRequest(null, null, false), null, CancellationToken.None);
        if (loaded.Snapshot.LoadDiagnostics.Count > 0)
            throw new InvalidOperationException("The billing fixture has load diagnostics: " + string.Join("; ", loaded.Snapshot.LoadDiagnostics));
        var resolved = await new ModelResolver(options).ResolveAsync(loaded.Snapshot, null, CancellationToken.None);
        try
        {
            Directory.Delete(cache, recursive: true);
        }
        catch (IOException)
        {
        }

        return resolved;
    }
}

/// <summary>Builds packs, plans units and renders them for renderer tests.</summary>
internal static class RenderKit
{
    public const string DemoPack = "billing-demo";

    public static string FixturePackRoot(string name) => Fixtures.Path("templates", name);

    public static EngineOptions Options { get; } = new() { RepoRoot = "/repo", CacheDirectory = "/cache", MaxDegreeOfParallelism = 4 };

    /// <summary>Loads a pack folder: pack.json, *.js scripts (ordinal) and types/*.json.</summary>
    public static LoadedPack LoadPack(string root, string name, PackSettings? settings = null, string? relativePath = null)
    {
        var manifest = JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(Path.Combine(root, "pack.json")), EngineJson.Options)!;
        return FromManifest(root, manifest, settings, relativePath ?? ".maquettiste/templates/" + name);
    }

    public static LoadedPack FromManifest(string root, PackManifest manifest, PackSettings? settings = null, string? relativePath = null)
    {
        var scripts = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.js").Order(StringComparer.Ordinal)
                .Select(p => new ScriptSource((relativePath ?? ".maquettiste/templates/" + manifest.Name) + "/" + Path.GetFileName(p), File.ReadAllText(p), ContentHash.Of(File.ReadAllBytes(p))))
                .ToArray()
            : [];
        var typeMaps = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var types = Path.Combine(root, "types");
        if (Directory.Exists(types))
        {
            foreach (var file in Directory.EnumerateFiles(types, "*.json").Order(StringComparer.Ordinal))
                typeMaps[Path.GetFileNameWithoutExtension(file)] = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!;
        }

        settings ??= new PackSettings();
        var parameters = manifest.Parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var (key, value) in settings.Parameters)
            parameters[key] = value;
        return new LoadedPack(manifest.Name, 0, root, relativePath ?? ".maquettiste/templates/" + manifest.Name, manifest, settings,
            parameters, scripts, HashBuilder.Of([.. scripts.Select(s => s.Hash)]), typeMaps);
    }

    /// <summary>Plans a pack's units the simple way (model, each entity/table/package/relation/enum/value object; where.database for tables).</summary>
    public static IReadOnlyList<PlannedUnit> Plan(ResolvedModel model, LoadedPack pack, params string[] unitIds)
    {
        var units = new List<PlannedUnit>();
        foreach (var unit in pack.Manifest.Units)
        {
            if (unitIds.Length > 0 && !unitIds.Contains(unit.Id))
                continue;
            IEnumerable<IResolvedObject?> elements = unit.For switch
            {
                "model" => [null],
                "each entity" => model.Entities,
                "each package" => model.Packages,
                "each relation" => model.Relations,
                "each enum" => model.Enums,
                "each value object" => model.ValueObjects,
                "each table" => model.Databases.Where(d => unit.Where?.Database is null || d.Name == unit.Where.Database).SelectMany(d => d.Tables),
                _ => throw new NotSupportedException(unit.For),
            };
            foreach (var element in elements)
            {
                var key = element is null ? $"{pack.Name}/{unit.Id}" : $"{pack.Name}/{unit.Id}:{element.Id}";
                units.Add(new PlannedUnit(key, pack, unit, element, "static:" + key));
            }
        }

        return units;
    }

    public static RenderContext Context(ResolvedModel model, IReadOnlyList<LoadedPack> packs, int parallelism = 4,
        IReadOnlyDictionary<string, SchemaDiffResult>? diffs = null) =>
        new(model, new PackSet(packs, []), diffs ?? ImmutableDictionary<string, SchemaDiffResult>.Empty, new ScriptSandboxFactory(), new TestHasher(), parallelism);

    public static Renderer NewRenderer(ITemplateCache? cache = null) => new(Options, cache ?? new TemplateCache());

    public static async Task<IReadOnlyList<RenderedUnit>> RenderAllAsync(Renderer renderer, IReadOnlyList<PlannedUnit> units, RenderContext context)
    {
        var list = new List<RenderedUnit>();
        await foreach (var unit in renderer.RenderAsync(units, context, null, TestContext.Current.CancellationToken))
            list.Add(unit);
        return list;
    }

    /// <summary>Writes rendered files (and a diagnostics listing) under a folder, for golden comparison.</summary>
    public static void WriteTree(string folder, IEnumerable<RenderedUnit> units, bool includeReadKeys = false)
    {
        var utf8 = new UTF8Encoding(false);
        var diagnostics = new StringBuilder();
        foreach (var unit in units)
        {
            foreach (var file in unit.Files)
            {
                var target = Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, utf8.GetBytes(file.Text));
            }

            foreach (var d in unit.Diagnostics)
                diagnostics.Append(unit.Unit.Key).Append(": ").Append(d.Rule).Append(' ').Append(d.FilePath).Append(' ').Append(d.Line).Append(':').Append(d.Column).Append(' ').Append(d.Message).Append('\n');
            if (includeReadKeys)
                diagnostics.Append(unit.Unit.Key).Append(" reads ").Append(string.Join(' ', unit.ReadKeys)).Append('\n');
        }

        if (diagnostics.Length > 0)
            File.WriteAllBytes(Path.Combine(folder, "diagnostics.txt"), utf8.GetBytes(diagnostics.ToString()));
    }

    /// <summary>A temporary pack folder with the given files.</summary>
    public static TempPack TempPack(string name, IReadOnlyDictionary<string, string> files) => new(name, files);

    /// <summary>A manifest with one unit.</summary>
    public static PackManifest Manifest(string name, params PackUnit[] units) =>
        new() { Name = name, Version = "1.0.0", Engine = ">=1.0 <2.0", Units = units };
}

/// <summary>Renders ad-hoc templates from a temporary pack.</summary>
internal static class Adhoc
{
    /// <summary>A copy of a resolved model with other settings (for limits and inflection).</summary>
    public static ResolvedModel WithSettings(ResolvedModel model, Func<ProjectSettings, ProjectSettings> change) => new()
    {
        Source = model.Source,
        Settings = change(model.Settings),
        Packages = model.Packages,
        Entities = model.Entities,
        ValueObjects = model.ValueObjects,
        Enums = model.Enums,
        ScalarTypes = model.ScalarTypes,
        Relations = model.Relations,
        Databases = model.Databases,
        Diagnostics = model.Diagnostics,
        ById = model.ById,
    };

    /// <summary>Renders one unit whose template is <paramref name="template"/> (file <c>main.scriban</c>).</summary>
    public static async Task<RenderedUnit> RenderAsync(string template, IResolvedObject? element = null, Func<PackUnit, PackUnit>? unit = null,
        IReadOnlyDictionary<string, string>? files = null, ResolvedModel? model = null, IReadOnlyDictionary<string, SchemaDiffResult>? diffs = null,
        PackSettings? settings = null)
    {
        model ??= await BillingModel.GetAsync();
        var all = new Dictionary<string, string>(files ?? new Dictionary<string, string>(), StringComparer.Ordinal) { ["main.scriban"] = template };
        using var pack = new TempPack("adhoc", all);
        var packUnit = new PackUnit { Id = "main", Template = "main.scriban", For = element is null ? "model" : "each entity", Output = "out.txt" };
        packUnit = unit?.Invoke(packUnit) ?? packUnit;
        var loaded = pack.Load(RenderKit.Manifest("adhoc", packUnit), settings);
        var key = element is null ? "adhoc/main" : "adhoc/main:" + element.Id;
        var planned = new PlannedUnit(key, loaded, packUnit, element, "static:" + key);
        return await RenderKit.NewRenderer().RenderOneAsync(planned, RenderKit.Context(model, [loaded], 1, diffs), TestContext.Current.CancellationToken);
    }

    /// <summary>The main file's text, failing with the unit's diagnostics when it failed.</summary>
    public static string Text(this RenderedUnit unit)
    {
        Assert.False(unit.Failed, string.Join("; ", unit.Diagnostics.Select(d => $"{d.Rule} {d.FilePath}:{d.Line}:{d.Column} {d.Message}")));
        return unit.Files.Single(f => f.Role == FileRole.Main).Text;
    }

    /// <summary>The single diagnostic of a failed unit.</summary>
    public static Diagnostics.Diagnostic Error(this RenderedUnit unit)
    {
        Assert.True(unit.Failed, "The unit did not fail.");
        Assert.Empty(unit.Files);
        return Assert.Single(unit.Diagnostics);
    }
}

/// <summary>A pack folder under the temp directory, deleted on dispose.</summary>
internal sealed class TempPack : IDisposable
{
    public TempPack(string name, IReadOnlyDictionary<string, string> files)
    {
        Name = name;
        Root = Path.Combine(Path.GetTempPath(), "maquettiste-tests", "pack-" + Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(Root);
        foreach (var (path, text) in files)
            Write(path, text);
    }

    public string Name { get; }

    public string Root { get; }

    public void Write(string path, string text)
    {
        var full = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new UTF8Encoding(false).GetBytes(text));
    }

    public LoadedPack Load(PackManifest manifest, PackSettings? settings = null) => RenderKit.FromManifest(Root, manifest, settings);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(Root)!, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
