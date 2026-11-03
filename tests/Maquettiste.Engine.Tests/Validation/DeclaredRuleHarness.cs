using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Integration;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// A billing copy with rule declaration files (next-phase-2-design.md section 5.6) and a store whose validator, checks and sandbox the
/// tests can count.
/// </summary>
internal sealed class DeclaredRuleHarness : IAsyncDisposable
{
    private DeclaredRuleHarness(E2ERepo repo, ModelStore store, ModelValidator validator, ResolvedChecks checks, CountingSandboxFactory factory)
    {
        Repo = repo;
        Store = store;
        Validator = validator;
        Checks = checks;
        Factory = factory;
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public E2ERepo Repo { get; }

    public ModelStore Store { get; }

    public ModelValidator Validator { get; }

    public ResolvedChecks Checks { get; }

    public CountingSandboxFactory Factory { get; }

    /// <summary>Creates the copy and writes the files (model-relative path, text) before the store opens.</summary>
    public static DeclaredRuleHarness Create(IReadOnlyDictionary<string, string> files, Action<JsonObject>? settings = null, int parallelism = 2,
        Action<string>? edit = null)
    {
        var repo = E2ERepo.Create(parallelism, settings, demo: false);
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(repo.Repo.ModelRoot, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllText(full, text);
        }

        edit?.Invoke(repo.Repo.ModelRoot);
        var factory = new CountingSandboxFactory();
        var services = EngineServices.Create(repo.Options);
        var calculations = new CalculationService(factory, 2);
        var resolver = new ModelResolver(repo.Options, factory, services.SeedRows, calculations);
        var validator = new ModelValidator(repo.Options, services.Schemas, factory, resolver, services.SeedRows);
        var checks = new ResolvedChecks(repo.Options, factory, calculations);
        services = services with { Scripts = factory, Resolver = resolver, Calculations = calculations, Validator = validator, Checks = checks };
        return new DeclaredRuleHarness(repo, new ModelStore(repo.Options, services), validator, checks, factory);
    }

    /// <summary>Creates the copy with one declaration file, <c>extensions/rules/rules.json</c>, holding the rules.</summary>
    public static DeclaredRuleHarness WithRules(params string[] rules) => Create(new Dictionary<string, string> { ["extensions/rules/rules.json"] = File(rules) });

    /// <summary>A declaration file's text.</summary>
    public static string File(params string[] rules) => "{\n  \"rules\": [\n" + string.Join(",\n", rules) + "\n  ]\n}\n";

    /// <summary>The whole-model validation.</summary>
    public Task<ValidationReport> ValidateAsync() => Store.ValidateAsync(ValidationScope.All, Ct);

    /// <summary>The names of the elements a rule's findings are on, ordinal (an element's name, else its id).</summary>
    public async Task<IReadOnlyList<string>> FoundOnAsync(ValidationReport report, string rule)
    {
        var snapshot = await Store.GetSnapshotAsync(Ct);
        return [.. report.Diagnostics.Where(d => d.Rule == rule)
            .Select(d => d.ElementId is { } id && snapshot.GetDocument(id)?.Element is { } element ? NameOf(element) : d.ElementId ?? "")
            .Order(StringComparer.Ordinal)];
    }

    private static string NameOf(Element element) => element switch
    {
        Entity e => e.Name,
        EnumType e => e.Name,
        Table t => t.Name ?? t.Id,
        _ => element.Id,
    };

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        await Repo.DisposeAsync();
    }
}
