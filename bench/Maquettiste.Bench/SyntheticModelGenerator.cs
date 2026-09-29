using Maquettiste.Bench.Synthetic;

namespace Maquettiste.Bench;

/// <summary>
/// Writes a deterministic synthetic model through the canonical writer (engine-design.md section 17; W11): the same options give
/// byte-identical files on every machine. Processes are out of phase 1, so the model has none.
/// </summary>
public static class SyntheticModelGenerator
{
    /// <summary>Writes the model, packs and settings into a repo folder (created when missing; files it owns are overwritten).</summary>
    /// <param name="repoRoot">The repo root.</param>
    /// <param name="options">The model options.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task WriteAsync(string repoRoot, SyntheticModelOptions options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        ArgumentNullException.ThrowIfNull(options);
        var model = await Task.Run(() => SyntheticModel.Build(options), ct).ConfigureAwait(false);
        var writer = new RepoWriter(repoRoot);
        await writer.WriteAsync(model, options, ct).ConfigureAwait(false);
        if (options.Locales > 0)
            await LocaleShards.WriteAsync(repoRoot, writer, options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Edits one attribute of one entity on disk, as a developer would between two runs: the model's middle entity outside any
    /// hierarchy gets a description on its first non-key attribute and that attribute's <c>required</c> flag flips.
    /// </summary>
    /// <param name="repoRoot">The repo root the model was written to.</param>
    /// <param name="options">The options it was written with.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The edited entity's id.</returns>
    public static async Task<string> EditOneEntityAsync(string repoRoot, SyntheticModelOptions options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        ArgumentNullException.ThrowIfNull(options);
        var model = await Task.Run(() => SyntheticModel.Build(options), ct).ConfigureAwait(false);
        var index = model.Elements.Select((e, i) => (e, i)).First(x => ReferenceEquals(x.e, model.EditTarget)).i;
        var path = RepoWriter.PathsOf(model.Elements)[index];
        var edited = model.EditedTarget();
        await new RepoWriter(repoRoot).WriteElementAsync(edited, path, ct).ConfigureAwait(false);
        return edited.Id;
    }

    /// <summary>Writes the entity <see cref="EditOneEntityAsync"/> edits back as generated (a second one-entity edit).</summary>
    /// <param name="repoRoot">The repo root.</param>
    /// <param name="options">The model options.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The id of the entity written.</returns>
    internal static async Task<string> RevertEditAsync(string repoRoot, SyntheticModelOptions options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        ArgumentNullException.ThrowIfNull(options);
        var model = await Task.Run(() => SyntheticModel.Build(options), ct).ConfigureAwait(false);
        var index = model.Elements.Select((e, i) => (e, i)).First(x => ReferenceEquals(x.e, model.EditTarget)).i;
        var path = RepoWriter.PathsOf(model.Elements)[index];
        await new RepoWriter(repoRoot).WriteElementAsync(model.EditTarget, path, ct).ConfigureAwait(false);
        return model.EditTarget.Id;
    }
}
