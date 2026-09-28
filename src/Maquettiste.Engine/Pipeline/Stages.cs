using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Scripting;

namespace Maquettiste.Engine.Pipeline;

// Every stage is async, takes a CancellationToken, observes it at least between files, and reports ProgressUpdates at
// file (or element) granularity. Implementations hold no static mutable state (engine-design.md section 4.2).

/// <summary>Stage 1: loads model files into a <see cref="ModelSnapshot"/> (W1).</summary>
public interface IModelLoader
{
    /// <summary>Loads the model: a full scan, or an incremental refresh of changed paths over a previous snapshot.</summary>
    /// <param name="request">What to load.</param>
    /// <param name="progress">Progress, per file.</param>
    /// <param name="ct">Cancellation, observed between files.</param>
    /// <returns>The snapshot and what changed relative to <see cref="LoadRequest.Previous"/>.</returns>
    Task<LoadResult> LoadAsync(LoadRequest request, IProgress<ProgressUpdate>? progress, CancellationToken ct);
}

/// <summary>A load request.</summary>
/// <param name="Previous">The previous snapshot, or <see langword="null"/> for a cold load.</param>
/// <param name="ChangedPaths">Repo-relative paths to re-read; <see langword="null"/> means a full scan.</param>
/// <param name="VerifyHashes">Whether to re-hash every file instead of trusting the index cache's length and mtime.</param>
public sealed record LoadRequest(ModelSnapshot? Previous, IReadOnlyCollection<string>? ChangedPaths, bool VerifyHashes);

/// <summary>The result of a load.</summary>
/// <param name="Snapshot">The new snapshot.</param>
/// <param name="Changes">Elements changed or deleted relative to the previous snapshot.</param>
public sealed record LoadResult(ModelSnapshot Snapshot, ChangeSet Changes);

/// <summary>Stage 2: validates a snapshot (W2).</summary>
public interface IModelValidator
{
    /// <summary>Validates the model or part of it.</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="scope">What to validate.</param>
    /// <param name="progress">Progress, per element.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The report, load diagnostics included.</returns>
    Task<ValidationReport> ValidateAsync(ModelSnapshot model, ValidationScope scope, IProgress<ProgressUpdate>? progress, CancellationToken ct);
}

/// <summary>Stage 3: resolves a valid snapshot into the resolved model templates see (W3).</summary>
public interface IModelResolver
{
    /// <summary>
    /// Resolves conventions and mappings in one deterministic pass. Only the result for a snapshot without validation errors is
    /// used, but a pipelined run starts resolution beside validation, so the resolver must also terminate on any snapshot the loader
    /// produces (cycles, dangling references and other invalid shapes included; its result or exception there is discarded).
    /// </summary>
    /// <param name="model">A loaded snapshot; its result is used only when the snapshot has no validation errors.</param>
    /// <param name="progress">Progress, per element.</param>
    /// <param name="ct">Cancellation, observed between elements and inside recursive expansions.</param>
    /// <returns>The resolved model.</returns>
    Task<ResolvedModel> ResolveAsync(ModelSnapshot model, IProgress<ProgressUpdate>? progress, CancellationToken ct);
}

/// <summary>Part of stage 4: discovers and loads template packs (W6).</summary>
public interface IPackLoader
{
    /// <summary>Loads every enabled pack, or the named ones.</summary>
    /// <param name="model">The snapshot (for settings).</param>
    /// <param name="packNames">Pack names to load; <see langword="null"/> means every enabled pack.</param>
    /// <param name="progress">Progress, per file.</param>
    /// <param name="ct">Cancellation, observed between files.</param>
    /// <returns>The packs and their diagnostics.</returns>
    Task<PackSet> LoadAsync(ModelSnapshot model, IReadOnlyCollection<string>? packNames, IProgress<ProgressUpdate>? progress, CancellationToken ct);
}

/// <summary>Stage 4: expands packs into render units (W6).</summary>
public interface IUnitPlanner
{
    /// <summary>Plans units: template × element, filtered by <c>where</c> and generation hints.</summary>
    /// <param name="model">The resolved model.</param>
    /// <param name="packs">The packs.</param>
    /// <param name="scripts">The sandbox factory for selectors and filters.</param>
    /// <param name="progress">Progress.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan.</returns>
    Task<UnitPlan> PlanAsync(ResolvedModel model, PackSet packs, IScriptSandboxFactory scripts, IProgress<ProgressUpdate>? progress, CancellationToken ct);
}

/// <summary>Stage 5: selects the units whose inputs or outputs changed (W6).</summary>
public interface IChangeDetector
{
    /// <summary>Splits the plan into units to render and units to skip.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="hasher">Current dependency hashes.</param>
    /// <param name="state">Stored unit states.</param>
    /// <param name="manifests">The manifests.</param>
    /// <param name="mode">The run mode; <see cref="GenerationMode.Check"/> renders everything.</param>
    /// <param name="force">Whether to ignore the cache.</param>
    /// <param name="progress">Progress.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The selection.</returns>
    Task<SkipResult> SelectAsync(UnitPlan plan, IDependencyHasher hasher, IUnitStateStore state, ManifestSet manifests,
        GenerationMode mode, bool force, IProgress<ProgressUpdate>? progress, CancellationToken ct);
}

/// <summary>Stage 6: renders units with Scriban and the Jint sandbox (W5).</summary>
public interface IRenderer
{
    /// <summary>Renders units in parallel and streams them in pack order.</summary>
    /// <param name="units">The units to render.</param>
    /// <param name="context">The run context.</param>
    /// <param name="progress">Progress, per unit.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rendered units, in input order.</returns>
    IAsyncEnumerable<RenderedUnit> RenderAsync(IReadOnlyList<PlannedUnit> units, RenderContext context, IProgress<ProgressUpdate>? progress, CancellationToken ct);

    /// <summary>Renders one unit (preview and tests).</summary>
    /// <param name="unit">The unit.</param>
    /// <param name="context">The run context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rendered unit.</returns>
    Task<RenderedUnit> RenderOneAsync(PlannedUnit unit, RenderContext context, CancellationToken ct);
}

/// <summary>What the renderer needs for a run.</summary>
/// <param name="Model">The resolved model.</param>
/// <param name="Packs">The packs.</param>
/// <param name="SchemaDiffs">Schema diffs by database name.</param>
/// <param name="Scripts">The sandbox factory.</param>
/// <param name="Hasher">Current dependency hashes, for input hashes.</param>
/// <param name="MaxDegreeOfParallelism">Render workers.</param>
public sealed record RenderContext(
    ResolvedModel Model,
    PackSet Packs,
    IReadOnlyDictionary<string, SchemaDiffResult> SchemaDiffs,
    IScriptSandboxFactory Scripts,
    IDependencyHasher Hasher,
    int MaxDegreeOfParallelism);

/// <summary>Stage 7: normalizes, formats and merges regions (W8).</summary>
public interface IPostProcessor
{
    /// <summary>Post-processes one rendered unit.</summary>
    /// <param name="unit">The rendered unit.</param>
    /// <param name="context">The run context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The processed unit.</returns>
    Task<ProcessedUnit> ProcessAsync(RenderedUnit unit, PostProcessContext context, CancellationToken ct);
}

/// <summary>What post-processing needs for a run.</summary>
/// <param name="RepoRoot">The absolute repo root (formatter working directory).</param>
/// <param name="Paths">The output path policy.</param>
/// <param name="Formatters">Configured formatters.</param>
/// <param name="FormatterVersionsVerified">Whether formatter versions were checked for this run.</param>
public sealed record PostProcessContext(string RepoRoot, IOutputPathPolicy Paths, IReadOnlyList<FormatterSettings> Formatters, bool FormatterVersionsVerified);

/// <summary>Stage 8: decides, writes and records files (W7).</summary>
public interface IOutputWriter
{
    /// <summary>Consumes processed units, writes changed files and orphans, and updates manifests per pack.</summary>
    /// <param name="units">Processed units, pack by pack.</param>
    /// <param name="context">The run context.</param>
    /// <param name="progress">Progress, per file.</param>
    /// <param name="ct">Cancellation, observed between files.</param>
    /// <returns>The summary.</returns>
    Task<WriteSummary> WriteAsync(IAsyncEnumerable<ProcessedUnit> units, WriteContext context, IProgress<ProgressUpdate>? progress, CancellationToken ct);
}

/// <summary>What the writer needs for a run.</summary>
/// <param name="Mode">The run mode.</param>
/// <param name="RunId">The run's ULID (temp file names, journal).</param>
/// <param name="PolicyByPack">The effective hand-edit policy per pack.</param>
/// <param name="Manifests">The manifests (with any journal overlay).</param>
/// <param name="Skipped">Skipped units, whose manifest entries are kept.</param>
/// <param name="UnitCountByPack">Units per pack, so a pack's manifest closes when its last unit arrives.</param>
/// <param name="AllPacks">Whether the run covers every pack (so manifests of removed packs are orphaned).</param>
/// <param name="Roots">Which roots the run covers; orphans are found only within them.</param>
/// <param name="IncludeDiffs">Whether to compute unified diffs.</param>
/// <param name="Journal">The run journal; <see langword="null"/> in dry run and check.</param>
/// <param name="State">The unit state store.</param>
/// <param name="PlannedPaths">
/// When applying a stored plan: every path the plan lists (unit outputs and file decisions). The writer refuses (MQ6004) any write
/// or delete outside it (SPEC section 19); <see langword="null"/> for a direct run.
/// </param>
public sealed record WriteContext(
    GenerationMode Mode,
    string RunId,
    IReadOnlyDictionary<string, HandEditPolicy> PolicyByPack,
    ManifestSet Manifests,
    IReadOnlyList<SkippedUnit> Skipped,
    IReadOnlyDictionary<string, int> UnitCountByPack,
    bool AllPacks,
    RootSelection Roots,
    bool IncludeDiffs,
    IRunJournal? Journal,
    IUnitStateStore State,
    IReadOnlySet<string>? PlannedPaths = null);
