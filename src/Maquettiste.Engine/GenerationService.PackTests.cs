using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;

namespace Maquettiste.Engine;

/// <summary>What <see cref="GenerationService.TestPackAsync"/> runs.</summary>
public sealed record PackTestOptions
{
    /// <summary>Case names to run; empty runs every case. Helper tests run when this is empty.</summary>
    public IReadOnlyList<string> Cases { get; init; } = [];

    /// <summary>Rewrite each run case's expected tree from its output.</summary>
    public bool Update { get; init; }

    /// <summary>Parallelism of each case's run; null for the engine default.</summary>
    public int? Jobs { get; init; }
}

public sealed partial class GenerationService
{
    /// <summary>One pack test run at a time per service, so a hosted server is not flooded.</summary>
    private readonly SemaphoreSlim _packTests = new(1, 1);

    /// <summary>
    /// Runs the tests of the named packs (every pack with a tests folder when null or empty), next-phase-2-design.md section 5.4: each
    /// pack's cases in ordinal order, one after another, each as a fresh project in a temporary folder, then its helper tests. It takes
    /// no run lock, since it writes nothing in the project except <c>expected/</c> under <see cref="PackTestOptions.Update"/>.
    /// </summary>
    /// <param name="packs">Pack names; null or empty for every pack with a <c>tests/</c> folder.</param>
    /// <param name="options">The cases, update and parallelism.</param>
    /// <param name="progress">Progress: one update per pack.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The report, packs in ordinal order.</returns>
    /// <exception cref="ArgumentException">A named pack does not exist, or a named case is in none of the tested packs.</exception>
    public async Task<PackTestReport> TestPackAsync(IReadOnlyList<string>? packs, PackTestOptions options, IProgress<ProgressUpdate>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var templates = Path.Combine(new ModelPaths(_options).ModelRoot, "templates");
        bool IsPack(string name) => File.Exists(Path.Combine(templates, name, "pack.json"));
        List<string> names;
        if (packs is { Count: > 0 })
        {
            names = [.. packs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            foreach (var name in names)
            {
                if (!PackAuthoring.NamePattern().IsMatch(name) || !IsPack(name))
                    throw new ArgumentException($"Pack '{name}' does not exist: there is no templates/{name}/pack.json.", nameof(packs));
            }
        }
        else
        {
            names = Directory.Exists(templates)
                ? [.. Directory.EnumerateDirectories(templates).Select(Path.GetFileName).OfType<string>()
                    .Where(n => !n.StartsWith('.') && IsPack(n) && Directory.Exists(Path.Combine(templates, n, PackFileRoles.TestsFolder)))
                    .Order(StringComparer.Ordinal)]
                : [];
        }

        foreach (var unknown in options.Cases.Where(c => !names.Any(n => PackTestRunner.CaseNames(Path.Combine(templates, n)).Contains(c, StringComparer.Ordinal))))
            throw new ArgumentException($"No tested pack has a case named '{unknown}'.", nameof(options));

        await _packTests.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
            var runner = new PackTestRunner(_services, _options);
            var results = new List<PackTestResult>(names.Count);
            for (var i = 0; i < names.Count; i++)
            {
                progress?.Report(new ProgressUpdate(PipelineStage.Plan, i, names.Count, null, names[i]));
                results.Add(await runner.RunAsync(snapshot, names[i], options, ct).ConfigureAwait(false));
            }

            progress?.Report(new ProgressUpdate(PipelineStage.Plan, names.Count, names.Count, null, null));
            var passed = results.Sum(r => r.Cases.Count(c => c.Outcome == PackTestOutcome.Passed) + r.Tests.Count(t => t.Outcome == PackTestOutcome.Passed));
            var failed = results.Sum(r => r.Cases.Count(c => c.Outcome is PackTestOutcome.Failed or PackTestOutcome.Error)
                + r.Tests.Count(t => t.Outcome is PackTestOutcome.Failed or PackTestOutcome.Error)
                + r.Diagnostics.Count(d => d.Severity == Diagnostics.DiagnosticSeverity.Error && d.Rule != "MQ6052"));
            var updated = results.Sum(r => r.Cases.Count(c => c.Outcome == PackTestOutcome.Updated));
            return new PackTestReport(results, passed, failed, updated);
        }
        finally
        {
            _packTests.Release();
        }
    }
}
