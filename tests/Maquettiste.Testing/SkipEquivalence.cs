using Maquettiste.Engine;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;

namespace Maquettiste.Testing;

/// <summary>
/// The skip check by key against the full check (change-driven-edits-design.md section 5): two long-lived hosts (a model store and a
/// generation service each, as the editor and <c>watch</c> run) over two copies of one model, one skipping by key with every unit it
/// accepts checked again in full, the other computing every unit's input hash (the check before the skip by key). After the same edits
/// both must plan the same units to render and to skip, render the same, and leave the same files, manifests and unit states (output
/// write times apart).
/// </summary>
public sealed class SkipEquivalence : IAsyncDisposable
{
    private readonly ModelStore _keyedStore;
    private readonly ModelStore _fullStore;
    private readonly GenerationService _keyed;
    private readonly GenerationService _full;
    private readonly ChangeDetector _detector;
    private readonly ChangeDetector _fullDetector;
    private readonly EngineOptions _keyedOptions;
    private readonly EngineOptions _fullOptions;

    /// <summary>Opens both hosts.</summary>
    /// <param name="keyed">The options of the copy that skips by key.</param>
    /// <param name="full">The options of the copy that checks every unit in full.</param>
    public SkipEquivalence(EngineOptions keyed, EngineOptions full)
    {
        ArgumentNullException.ThrowIfNull(keyed);
        ArgumentNullException.ThrowIfNull(full);
        _keyedOptions = keyed;
        _fullOptions = full;
        // The cross-check hashes with a hasher of its own, so the keyed host's runs leave the record they would leave in production.
        _detector = new ChangeDetector(keyed) { CrossCheck = true };
        _fullDetector = new ChangeDetector(full) { KeyDriven = false };
        _keyedStore = new ModelStore(keyed);
        _fullStore = new ModelStore(full);
        _keyed = new GenerationService(_keyedStore, keyed, EngineServices.Create(keyed) with { ChangeDetector = _detector });
        _full = new GenerationService(_fullStore, full, EngineServices.Create(full) with { ChangeDetector = _fullDetector });
    }

    /// <summary>Units the last skip check of the keyed copy accepted by key.</summary>
    public int AcceptedByKey => _detector.LastStats?.ByKey ?? 0;

    /// <summary>Keys whose previous hash the keyed host's last skip check took from its own record, not the previous run's hasher cache.</summary>
    public int KeysFromRecord => _detector.LastStats?.KeysFromRecord ?? 0;

    /// <summary>Units accepted by key whose full check disagreed (must stay empty).</summary>
    public IReadOnlyList<string> CrossCheckFailures => [.. _detector.CrossCheckFailures];

    /// <summary>Applies an edit to both copies' repo roots.</summary>
    /// <param name="edit">The edit, given a repo root.</param>
    public void Edit(Action<string> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        edit(_keyedOptions.RepoRoot);
        edit(_fullOptions.RepoRoot);
    }

    /// <summary>
    /// Plans, then applies, in both copies, and describes every difference: the units planned to render or skip, the run's outcome,
    /// counts and changes, the repo's files (the cache folder apart) and the unit states. Empty when both agree.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The differences.</returns>
    public Task<IReadOnlyList<string>> RunAsync(CancellationToken ct) => RunAsync(GenerationMode.Apply, null, ct);

    /// <summary>
    /// <see cref="RunAsync(CancellationToken)"/> in a mode (an apply run is planned first) and over some packs only.
    /// </summary>
    /// <param name="mode">The mode.</param>
    /// <param name="packs">The packs, or <see langword="null"/> for all.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The differences.</returns>
    public async Task<IReadOnlyList<string>> RunAsync(GenerationMode mode, IReadOnlyList<string>? packs, CancellationToken ct)
    {
        var differences = new List<string>();
        var request = new GenerationRequest { Jobs = 2, Mode = mode, Packs = packs };
        if (mode == GenerationMode.Apply)
        {
            var keyedPlan = await _keyed.PlanAsync(request, null, ct).ConfigureAwait(false);
            var fullPlan = await _full.PlanAsync(request, null, ct).ConfigureAwait(false);
            Compare(differences, "plan outcome", keyedPlan.Outcome, fullPlan.Outcome);
            Compare(differences, "planned units", Units(keyedPlan.Plan), Units(fullPlan.Plan));
        }

        var keyed = await _keyed.RunAsync(request, null, ct).ConfigureAwait(false);
        var full = await _full.RunAsync(request, null, ct).ConfigureAwait(false);
        Compare(differences, "outcome", keyed.Outcome, full.Outcome);
        Compare(differences, "rendered", keyed.UnitsRendered, full.UnitsRendered);
        Compare(differences, "skipped", keyed.UnitsSkipped, full.UnitsSkipped);
        Compare(differences, "changes", string.Join('\n', keyed.Changes.Select(c => $"{c.Kind} {c.Path} {c.UnitKey}").Order(StringComparer.Ordinal)),
            string.Join('\n', full.Changes.Select(c => $"{c.Kind} {c.Path} {c.UnitKey}").Order(StringComparer.Ordinal)));
        if (keyed.Outcome != RunOutcome.Succeeded)
            differences.Add("the keyed run did not succeed: " + string.Join("; ", keyed.Diagnostics.Select(d => d.Rule + " " + d.Message)));

        var keyedFiles = Files(_keyedOptions);
        var fullFiles = Files(_fullOptions);
        Compare(differences, "files", string.Join('\n', keyedFiles.Keys), string.Join('\n', fullFiles.Keys));
        foreach (var (path, bytes) in keyedFiles)
        {
            if (fullFiles.TryGetValue(path, out var other) && !bytes.AsSpan().SequenceEqual(other))
                differences.Add("file differs: " + path);
        }

        Compare(differences, "unit states", States(_keyedOptions), States(_fullOptions));
        return differences;
    }

    /// <summary>
    /// An apply run in both copies cancelled as soon as its skip check has decided, before anything renders: nothing is written, and
    /// the record the keyed host keeps from it must still lead the next run to the full check's answer.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task RunCancelledAsync(CancellationToken ct)
    {
        foreach (var (service, detector) in new[] { (_keyed, _detector), (_full, _fullDetector) })
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            detector.AfterSelect = cancel.Cancel;
            try
            {
                var result = await service.RunAsync(new GenerationRequest { Jobs = 2 }, null, cancel.Token).ConfigureAwait(false);
                if (result.Outcome != RunOutcome.Cancelled)
                    throw new InvalidOperationException("The run was not cancelled: " + result.Outcome);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // cancelled, as asked
            }
            finally
            {
                detector.AfterSelect = null;
            }
        }
    }

    private static string Units(GenerationPlan? plan) =>
        plan is null ? "no plan" : string.Join('\n', plan.Units.Select(u => (u.Skipped ? "skip " : "render ") + u.Key).Order(StringComparer.Ordinal));

    private static void Compare<T>(List<string> differences, string what, T keyed, T full)
    {
        if (!EqualityComparer<T>.Default.Equals(keyed, full))
            differences.Add($"{what}: keyed {keyed}, full {full}");
    }

    /// <summary>Every file of the repo, by repo-relative path (the cache folder apart: it holds plans, jobs and unit states).</summary>
    private static SortedDictionary<string, byte[]> Files(EngineOptions options)
    {
        var root = Path.GetFullPath(options.RepoRoot);
        var cache = Path.GetFullPath(options.CacheDirectory);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;
            files[Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')] = File.ReadAllBytes(file);
        }

        return files;
    }

    /// <summary>The unit state files, decoded, as text: every field of every state, output write times apart.</summary>
    private static string States(EngineOptions options)
    {
        var folder = Path.Combine(Path.GetFullPath(options.CacheDirectory), "units");
        if (!Directory.Exists(folder))
            return "";
        var text = new System.Text.StringBuilder();
        foreach (var file in Directory.EnumerateFiles(folder, "*.bin").Order(StringComparer.Ordinal))
        {
            text.Append("# ").AppendLine(Path.GetFileName(file));
            var states = UnitStateStore.Decode(File.ReadAllBytes(file));
            if (states is null)
            {
                text.AppendLine("undecodable");
                continue;
            }

            foreach (var state in states.Values.OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                text.Append(state.Key).Append(' ').Append(state.InputHash).Append(' ').AppendJoin(',', state.ReadKeys).AppendLine();
                foreach (var output in state.Outputs)
                    text.Append("  ").Append(output.Path).Append(' ').Append(output.ManifestHash).Append(' ').Append(output.Length).AppendLine();
                text.Append("  hashes ").AppendLine(Convert.ToHexString(state.KeyHashes.Span));
                text.Append("  parts ").AppendLine(state.StaticParts);
                if (state.Names is { } names)
                    text.Append("  names ").AppendJoin(',', names.OrderBy(n => n.Key, StringComparer.Ordinal).Select(n => n.Key + "=" + n.Value)).AppendLine();
                if (state.Exports is { } exports)
                    text.Append("  exports ").Append(exports.InputHash).Append(' ').Append(exports.Values.GetRawText()).Append(' ').AppendJoin(',', exports.ReadKeys).AppendLine();
            }
        }

        return text.ToString();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _keyedStore.DisposeAsync().ConfigureAwait(false);
        await _fullStore.DisposeAsync().ConfigureAwait(false);
    }
}
