using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Text;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// A resolver that resolves the conceptual layer and some databases without the rest of the model: what fact recording and the plans of
/// the binding operations (next-phase-2-design.md section 3.3, decision 10) use, so they never resolve the whole model.
/// </summary>
internal interface IDatabaseResolver
{
    /// <summary>Resolves the conceptual layer once and then each database of <paramref name="databaseIds"/>.</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="databaseIds">The database element ids.</param>
    /// <param name="freeze">Whether to keep the dependency keys of the resolved objects.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Per id, the resolved database (null when no database has it), and the diagnostics of that partial run.</returns>
    Task<(IReadOnlyList<RDatabase?> Databases, IReadOnlyList<Diagnostic> Diagnostics)> ResolveDatabasesAsync(ModelSnapshot model, IReadOnlyList<string> databaseIds,
        bool freeze, CancellationToken ct);
}

/// <summary>Stage 3: conventions, mappings and the resolved model (W3; engine-design.md section 7).</summary>
/// <param name="options">The engine options.</param>
/// <param name="scripts">The sandbox factory that runs seeds' rows scripts (next-phase-2-design.md section 3.4); a factory of its own
/// when absent.</param>
/// <param name="seedRows">The engine's rows cache; a cache of its own when absent.</param>
/// <param name="calculations">The engine's calculated values service (next-phase-2-design.md section 5.5); one of its own when absent.</param>
internal sealed class ModelResolver(EngineOptions options, Scripting.IScriptSandboxFactory? scripts = null, SeedRowsCache? seedRows = null,
    CalculationService? calculations = null)
    : IModelResolver, IDatabaseResolver
{
    /// <summary>The engine options (the resolver needs none today; kept for the constructor contract of section 18).</summary>
    internal EngineOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>The sandbox factory of rows scripts.</summary>
    internal Scripting.IScriptSandboxFactory Scripts { get; } = scripts ?? new Scripting.ScriptSandboxFactory();

    /// <summary>The rows cache: a seed's scripted rows are built on first read, through it (decision 15).</summary>
    internal SeedRowsCache SeedRows { get; } = seedRows ?? new SeedRowsCache();

    /// <summary>The calculated values service: a snapshot with calculation files gets a host per resolution, one without none.</summary>
    internal CalculationService Calculations { get; } = calculations ?? new CalculationService(scripts ?? new Scripting.ScriptSandboxFactory(),
        (options ?? throw new ArgumentNullException(nameof(options))).EffectiveParallelism);

    /// <summary>The inflector of the last resolution and the settings it was made for (its plural cache carries over between runs).</summary>
    private InflectorEntry? _inflector;

    /// <summary>
    /// The inflector for a settings section: the previous one when the section is the same object (an incremental run whose
    /// <c>maquettiste.json</c> did not change), so names pluralized in an earlier run are not pluralized again. An inflector is a pure
    /// function of its settings and thread-safe, so sharing it cannot change a result. Its memo grows with every distinct name ever
    /// inflected (renames in a long-lived host), so it is carried over only while it holds at most <see cref="MaxCarriedWords"/>
    /// for the model: past that a fresh inflector starts from the current model's names.
    /// </summary>
    private Inflector InflectorFor(Model.InflectionSettings? settings, int documents)
    {
        var last = Volatile.Read(ref _inflector);
        if (last is not null && ReferenceEquals(last.Settings, settings) && last.Inflector.MemoizedWords <= MaxCarriedWords(documents))
            return last.Inflector;
        var inflector = new Inflector(settings);
        Volatile.Write(ref _inflector, new InflectorEntry(settings, inflector));
        return inflector;
    }

    /// <summary>
    /// The largest memo carried over to the next run: eight words per element file, and at least 16,384 (the benchmark model
    /// memoizes about one word per file, so only names a long-lived host no longer uses push a memo past this).
    /// </summary>
    /// <param name="documents">The element files of the model being resolved.</param>
    /// <returns>The limit.</returns>
    internal static int MaxCarriedWords(int documents) => Math.Max(16_384, 8 * documents);

    /// <summary>The inflector the next run with the same settings would reuse (tests).</summary>
    internal Inflector? LastInflector => Volatile.Read(ref _inflector)?.Inflector;

    private sealed record InflectorEntry(Model.InflectionSettings? Settings, Inflector Inflector);

    /// <inheritdoc/>
    public Task<ResolvedModel> ResolveAsync(ModelSnapshot model, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        ct.ThrowIfCancellationRequested();
        var host = Calculations.CreateHost(model);
        return Task.Run(() => CalculationHost.Build(host, () =>
            new ResolveRun(model, progress, ct, Options.EffectiveParallelism, InflectorFor(model.Settings.Inflection, model.Documents.Count))
            {
                Scripts = Scripts, SeedRows = SeedRows, Calculations = host,
            }.Run()), ct);
    }

    /// <summary>Resolves the conceptual layer and one database only (<see cref="ResolveRun.RunOneDatabase"/>).</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="databaseId">The database element's id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="freeze">Whether to keep the dependency keys of the resolved objects (<see cref="ResolveRun.RunDatabases"/>).</param>
    /// <returns>The resolved database (null when no database has the id) and the diagnostics of that partial run.</returns>
    internal async Task<(RDatabase? Database, IReadOnlyList<Diagnostic> Diagnostics)> ResolveDatabaseAsync(ModelSnapshot model, string databaseId, CancellationToken ct,
        bool freeze = false)
    {
        ArgumentNullException.ThrowIfNull(databaseId);
        var (databases, diagnostics) = await ResolveDatabasesAsync(model, [databaseId], freeze, ct).ConfigureAwait(false);
        return (databases[0], diagnostics);
    }

    /// <summary>
    /// Resolves the conceptual layer once and then each database of <paramref name="databaseIds"/> (<see cref="ResolveRun.RunDatabases"/>):
    /// the databases a write can reshape share one conceptual run when migration facts are recorded.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="databaseIds">The database element ids.</param>
    /// <param name="freeze">Whether to keep the dependency keys of the resolved objects.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Per id, the resolved database (null when no database has it), and the diagnostics of that partial run.</returns>
    public Task<(IReadOnlyList<RDatabase?> Databases, IReadOnlyList<Diagnostic> Diagnostics)> ResolveDatabasesAsync(ModelSnapshot model, IReadOnlyList<string> databaseIds,
        bool freeze, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(databaseIds);
        ct.ThrowIfCancellationRequested();
        return Task.Run(() => new ResolveRun(model, null, ct, Options.EffectiveParallelism, InflectorFor(model.Settings.Inflection, model.Documents.Count))
        {
            Scripts = Scripts, SeedRows = SeedRows,
        }.RunDatabases(databaseIds, freeze), ct);
    }
}
