using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Text;

namespace Maquettiste.Engine.Resolution;

/// <summary>Stage 3: conventions, mappings and the resolved model (W3; engine-design.md section 7).</summary>
/// <param name="options">The engine options.</param>
internal sealed class ModelResolver(EngineOptions options) : IModelResolver
{
    /// <summary>The engine options (the resolver needs none today; kept for the constructor contract of section 18).</summary>
    internal EngineOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

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
        return Task.Run(() => new ResolveRun(model, progress, ct, Options.EffectiveParallelism, InflectorFor(model.Settings.Inflection, model.Documents.Count)).Run(), ct);
    }
}
