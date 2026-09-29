using Maquettiste.Engine;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Bench.Synthetic;

/// <summary>
/// Writes a <see cref="SyntheticModel"/> into a repo: <c>maquettiste.json</c>, <c>.schema/v1/*</c>, one canonical file per element
/// (through <see cref="ICanonicalJson"/>) and the packs. Every write is checked by the engine-write guard
/// (<see cref="IOutputPathPolicy.CheckEngineWrite"/> with <see cref="WriteTarget.Model"/>).
/// </summary>
internal sealed class RepoWriter
{
    private readonly IOutputPathPolicy _paths;
    private readonly ICanonicalJson _json;
    private readonly string _modelRoot;

    /// <summary>Creates the writer for a repo root.</summary>
    /// <param name="repoRoot">The repo root.</param>
    public RepoWriter(string repoRoot)
    {
        var root = Path.GetFullPath(repoRoot);
        _modelRoot = Path.Combine(root, ".maquettiste");
        var options = new EngineOptions { RepoRoot = root, CacheDirectory = Path.Combine(_modelRoot, ".cache") };
        _paths = new OutputPathPolicy(options, null);
        var schemas = new SchemaRegistry();
        Schemas = schemas;
        _json = new CanonicalJson(schemas);
    }

    /// <summary>The schema registry.</summary>
    public ISchemaRegistry Schemas { get; }

    /// <summary>Returns the model-relative path of every element, in element order (conventional folder and file name, suffixed on a collision).</summary>
    /// <param name="elements">The elements.</param>
    /// <returns>The paths.</returns>
    public static IReadOnlyList<string> PathsOf(IReadOnlyList<Element> elements)
    {
        var databaseFolders = elements.OfType<Database>()
            .ToDictionary(d => d.Id, d => ModelPaths.ConventionalFolder(d, _ => null), StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>(elements.Count);
        foreach (var element in elements)
        {
            var folder = ModelPaths.ConventionalFolder(element, id => databaseFolders.GetValueOrDefault(id));
            var path = ModelPaths.Join(folder, ModelPaths.FileName(element, false));
            if (!used.Add(path))
            {
                path = ModelPaths.Join(folder, ModelPaths.FileName(element, true));
                used.Add(path);
            }

            paths.Add(path);
        }

        return paths;
    }

    /// <summary>Writes the settings, schemas, elements and packs.</summary>
    /// <param name="model">The model.</param>
    /// <param name="options">The options (fanout, example packs).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The number of files written.</returns>
    public async Task<int> WriteAsync(SyntheticModel model, SyntheticModelOptions options, CancellationToken ct)
    {
        var files = 0;
        await WriteAsync(ModelPaths.SettingsFile, _json.Serialize(model.Settings, "maquettiste.json", ".maquettiste/" + ModelPaths.SettingsFile), ct)
            .ConfigureAwait(false);
        files++;

        foreach (var schema in Schemas.FileNames.Order(StringComparer.Ordinal))
        {
            await WriteAsync(".schema/v1/" + schema, Schemas.GetFileBytes(schema).ToArray(), ct).ConfigureAwait(false);
            files++;
        }

        var elements = model.Elements;
        var paths = PathsOf(elements);
        await Parallel.ForAsync(0, elements.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (i, token) => await WriteElementAsync(elements[i], paths[i], token).ConfigureAwait(false)).ConfigureAwait(false);
        files += elements.Count;

        files += await WritePackAsync(FanoutPack.Name, ct).ConfigureAwait(false);
        var manifest = FanoutPack.Manifest(FanoutPack.EffectiveFanout(options));
        await WriteAsync("templates/fanout/pack.json", _json.Serialize(manifest, "pack.json", ".maquettiste/templates/fanout/pack.json"), ct)
            .ConfigureAwait(false);
        files++;

        if (options.IncludeExamplePacks)
        {
            foreach (var pack in EmbeddedPacks.ExamplePacks)
                files += await WritePackAsync(pack, ct).ConfigureAwait(false);
        }

        return files;
    }

    /// <summary>Serializes an element canonically and writes it.</summary>
    /// <param name="element">The element.</param>
    /// <param name="modelPath">Its model-relative path.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public Task WriteElementAsync(Element element, string modelPath, CancellationToken ct) =>
        WriteAsync(modelPath, _json.Serialize(element, KindInfo.Get(element.Kind).SchemaFile, ".maquettiste/" + modelPath), ct);

    private async Task<int> WritePackAsync(string pack, CancellationToken ct)
    {
        var count = 0;
        foreach (var file in EmbeddedPacks.Files(pack))
        {
            // The fanout manifest is generated for the requested fanout; the checked-in one is the default's copy.
            if (pack == FanoutPack.Name && file == "pack.json")
                continue;
            await WriteAsync("templates/" + pack + "/" + file, await EmbeddedPacks.ReadAsync(pack, file, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    /// <summary>Writes a JSON node canonically against one of the schemas, at a model-relative path.</summary>
    /// <param name="node">The document.</param>
    /// <param name="schemaFile">Its schema file name.</param>
    /// <param name="modelPath">Its model-relative path.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public Task WriteNodeAsync(System.Text.Json.Nodes.JsonNode node, string schemaFile, string modelPath, CancellationToken ct) =>
        WriteAsync(modelPath, _json.Write(node, schemaFile, ".maquettiste/" + modelPath), ct);

    private async Task WriteAsync(string modelPath, byte[] bytes, CancellationToken ct)
    {
        var full = Path.Combine(_modelRoot, modelPath.Replace('/', Path.DirectorySeparatorChar));
        var check = _paths.CheckEngineWrite(WriteTarget.Model, full);
        if (!check.Allowed)
            throw new BenchmarkException($"The engine-write guard refused {full}: {check.Reason}");
        Directory.CreateDirectory(Path.GetDirectoryName(check.NormalizedPath)!);
        await File.WriteAllBytesAsync(check.NormalizedPath, bytes, ct).ConfigureAwait(false);
    }
}
