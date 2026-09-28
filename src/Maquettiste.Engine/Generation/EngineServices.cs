using Maquettiste.Engine.Jobs;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.PostProcessing;
using Maquettiste.Engine.Rendering;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Validation;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Engine.Generation;

/// <summary>
/// The composition root (W6 owns it; the scaffold wires the constructors of engine-design.md section 18). <see cref="Create"/>
/// builds the long-lived components; the <c>Create…</c> methods build per-run ones. Tests substitute fakes through <c>with</c>.
/// Every component that writes a file receives <see cref="EnginePaths"/>, so engine writes go through the path guard by construction.
/// </summary>
internal sealed record EngineServices
{
    /// <summary>The engine options.</summary>
    public required EngineOptions Options { get; init; }

    /// <summary>The engine-write guard (<see cref="IOutputPathPolicy.CheckEngineWrite"/>) for model, cache, journal, plan and job writes.</summary>
    public required IOutputPathPolicy EnginePaths { get; init; }

    /// <summary>The schema registry.</summary>
    public required ISchemaRegistry Schemas { get; init; }

    /// <summary>The canonical JSON writer.</summary>
    public required ICanonicalJson Json { get; init; }

    /// <summary>Stage 1: the model loader.</summary>
    public required IModelLoader Loader { get; init; }

    /// <summary>Stage 2: the validator.</summary>
    public required IModelValidator Validator { get; init; }

    /// <summary>Stage 3: the resolver.</summary>
    public required IModelResolver Resolver { get; init; }

    /// <summary>The script sandbox factory.</summary>
    public required IScriptSandboxFactory Scripts { get; init; }

    /// <summary>Part of stage 4: the pack loader.</summary>
    public required IPackLoader Packs { get; init; }

    /// <summary>Stage 4: the unit planner.</summary>
    public required IUnitPlanner Planner { get; init; }

    /// <summary>Stage 5: the change detector.</summary>
    public required IChangeDetector ChangeDetector { get; init; }

    /// <summary>The unit state store.</summary>
    public required IUnitStateStore UnitState { get; init; }

    /// <summary>The manifest store.</summary>
    public required IManifestStore Manifests { get; init; }

    /// <summary>The run journal.</summary>
    public required IRunJournal Journal { get; init; }

    /// <summary>The run lock.</summary>
    public required IRunLock RunLock { get; init; }

    /// <summary>The unified diff generator.</summary>
    public required IDiffGenerator Diffs { get; init; }

    /// <summary>The formatter runner.</summary>
    public required IFormatterRunner Formatters { get; init; }

    /// <summary>Stage 7: the post-processor.</summary>
    public required IPostProcessor PostProcessor { get; init; }

    /// <summary>The schema snapshot store.</summary>
    public required ISnapshotStore Snapshots { get; init; }

    /// <summary>The schema differ.</summary>
    public required ISchemaDiffer SchemaDiffer { get; init; }

    /// <summary>The plan store (<c>CacheDirectory/plans/</c>).</summary>
    public required PlanStore Plans { get; init; }

    /// <summary>The job record store (<c>CacheDirectory/jobs/</c>).</summary>
    public required JobStore Jobs { get; init; }

    /// <summary>Replaces <see cref="CreateRenderer"/>'s renderer (tests; the renderer is per run, so this is a factory).</summary>
    public Func<IRenderer>? RendererFactory { get; init; }

    /// <summary>Replaces <see cref="CreateWriter"/>'s writer (tests).</summary>
    public Func<IOutputPathPolicy, IOutputWriter>? WriterFactory { get; init; }

    /// <summary>Builds the long-lived components. Performs no I/O.</summary>
    /// <param name="options">The engine options.</param>
    /// <returns>The services.</returns>
    public static EngineServices Create(EngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schemas = new SchemaRegistry();
        var json = new CanonicalJson(schemas);
        var scripts = new ScriptSandboxFactory();
        var formatters = new FormatterRunner(options);
        var enginePaths = new OutputPathPolicy(options, null); // engine-write checks only; no I/O
        return new EngineServices
        {
            Options = options,
            EnginePaths = enginePaths,
            Schemas = schemas,
            Json = json,
            Loader = new ModelLoader(options, schemas, json, enginePaths),
            Validator = new ModelValidator(options, schemas, scripts),
            Resolver = new ModelResolver(options),
            Scripts = scripts,
            Packs = new PackLoader(options, schemas),
            Planner = new UnitPlanner(options),
            ChangeDetector = new ChangeDetector(options),
            UnitState = new UnitStateStore(options, enginePaths),
            Manifests = new ManifestStore(options, json, enginePaths),
            Journal = new RunJournal(options, enginePaths),
            RunLock = new RunLock(options, enginePaths),
            Diffs = new DiffGenerator(),
            Formatters = formatters,
            PostProcessor = new PostProcessor(options, formatters),
            Snapshots = new SnapshotStore(options, json, enginePaths),
            SchemaDiffer = new SchemaDiffer(),
            Plans = new PlanStore(options, enginePaths),
            Jobs = new JobStore(options, enginePaths),
        };
    }

    /// <summary>
    /// Builds the renderer of one run (or one preview) with a fresh template cache, so parsed templates live no longer than the run
    /// even in a long-lived host (engine-design.md section 9).
    /// </summary>
    /// <returns>The renderer.</returns>
    public IRenderer CreateRenderer() => RendererFactory?.Invoke() ?? new Renderer(Options, new TemplateCache());

    /// <summary>Builds the dependency hasher of one run.</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="resolved">The resolved model.</param>
    /// <param name="packs">The packs.</param>
    /// <param name="schemaDiffs">Schema diffs by database name.</param>
    /// <returns>The hasher.</returns>
    public IDependencyHasher CreateHasher(ModelSnapshot model, ResolvedModel resolved, PackSet packs, IReadOnlyDictionary<string, SchemaDiffResult> schemaDiffs) =>
        new DependencyHasher(model, resolved, packs, schemaDiffs);

    /// <summary>Builds the path policy of one run.</summary>
    /// <param name="settings">The project settings.</param>
    /// <returns>The policy.</returns>
    public IOutputPathPolicy CreatePathPolicy(ProjectSettings settings) => new OutputPathPolicy(Options, settings);

    /// <summary>Builds the writer of one run.</summary>
    /// <param name="paths">The path policy.</param>
    /// <returns>The writer.</returns>
    public IOutputWriter CreateWriter(IOutputPathPolicy paths) => WriterFactory?.Invoke(paths) ?? new OutputWriter(Options, paths, Manifests, Diffs);
}
