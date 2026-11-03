using System.Collections.Immutable;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// The per-element result cache of script rules (next-phase-1-design.md section 5.7): a validator runs a rule again on an element only
/// when the element's file, the rule file, the limits or a key the rule read changed; overrides apply after the cache; files with
/// MQ5005 and failures are never cached; a cached report is byte-identical to a fresh one.
/// </summary>
public sealed class RuleCacheTests
{
    private const string RulePath = ".maquettiste/extensions/rules/probes.js";

    /// <summary>One rule per kind of read, each on entities only, so a test sees which reads bring a rule back.</summary>
    private const string Probes = """
        const probe = (id, read) => maquettiste.rule({
          id, severity: 'warning', kinds: ['entity'],
          check(element, model, report) { report(`${element.name}: ${read(element, model)}`, { pointer: '/name' }); },
        });
        probe('own', element => element.attributes.length);
        probe('get', (element, model) => model.get(element.package).name);
        probe('all', (element, model) => model.all('enum').length);
        probe('tag', (element, model) => model.withTag('core').length);
        probe('refs', (element, model) => model.referencesTo(element.id).length);
        probe('named', (element, model) => model.byQualifiedName('@entity:Billing.Ledger').length);
        maquettiste.rule({ id: 'fails', kinds: ['entity'], check(element) { if (element.name === 'Product') throw new Error('no products'); } });
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Sample(ModelSnapshot Model, string Customer, string Invoice, string Product);

    /// <summary>Billing with three entities; <paramref name="change"/> runs after them, so their ids stay the same.</summary>
    private static Sample Build(string change = "", string? code = Probes, Func<ProjectSettings, ProjectSettings>? settings = null)
    {
        var b = new ModelBuilder(seed: 61);
        var billing = b.Package("Billing");
        b.Enum("Status", billing).Member("Draft", 0);
        var customer = b.Entity("Customer", billing).Key("id", "uuid");
        var invoice = b.Entity("Invoice", billing).Key("id", "uuid");
        var product = b.Entity("Product", billing).Key("id", "uuid");
        switch (change)
        {
            case "element":
                invoice.Attr("number", "string");
                break;
            case "get":
                billing.Description("Billing and payments.");
                break;
            case "all":
                b.Enum("Currency", billing).Member("Eur", 0);
                break;
            case "tag":
                b.ValueObject("Money", billing).Attr("amount", "decimal").Tag("core");
                break;
            case "refs":
                b.Relation("places", customer, invoice, fromMax: MaxCardinality.One, toMax: MaxCardinality.Many);
                break;
            case "named":
                b.Entity("Ledger", billing).Key("id", "uuid");
                break;
            case "unrelated":
                b.Stereotype("audited");
                break;
        }

        if (settings is not null)
            b.Settings(settings);
        var built = b.Build();
        var model = ModelSnapshot.Create(built.Documents, built.Settings, built.SettingsHash, [], code is null ? [] : [new ScriptSource(RulePath, code, Hash(code))], 1);
        return new Sample(model, customer.Id, invoice.Id, product.Id);
    }

    private static string Hash(string code) => Maquettiste.Engine.Hashing.ContentHash.Of(System.Text.Encoding.UTF8.GetBytes(code));

    private static Task<ValidationReport> ValidateAsync(Maquettiste.Engine.Validation.ModelValidator validator, Sample s) =>
        validator.ValidateAsync(s.Model, ValidationScope.All, null, Ct);

    /// <summary>The (rule, entity) calls since the last take, entities named.</summary>
    private static List<string> EntityCalls(CountingSandboxFactory factory, Sample s)
    {
        var names = new Dictionary<string, string> { [s.Customer] = "Customer", [s.Invoice] = "Invoice", [s.Product] = "Product" };
        return [.. factory.TakeRuleCalls().Where(c => names.ContainsKey(c.Element)).Select(c => c.Rule + " " + names[c.Element]).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task An_unchanged_element_reuses_its_results()
    {
        var factory = new CountingSandboxFactory();
        var validator = ValidationFixture.Validator(scripts: factory);
        var s = Build();

        var first = await ValidateAsync(validator, s);
        var firstCalls = factory.TakeRuleCalls();
        var second = await ValidateAsync(validator, Build());

        // Every rule ran once on every entity, the only kind it checks; the other documents were skipped before the cache, so they hold
        // no entry. The second validation of an equal model ran only the rule that failed, on the element it failed on (a failure is
        // never cached).
        Assert.Equal(7 * 3, firstCalls.Count);
        Assert.Equal(7 * 3 - 1, validator.RuleResults.Count);
        Assert.Equal([("fails", s.Product)], factory.TakeRuleCalls());
        Assert.Equal(ValidationFixture.ToGolden(first.Diagnostics), ValidationFixture.ToGolden(second.Diagnostics));
        Assert.Single(second.Diagnostics, d => d.Rule == "MQ5002");

        // A change to an element's own file runs every rule on it again, and only on it.
        await ValidateAsync(validator, Build("element"));
        Assert.Equal(["all Invoice", "fails Invoice", "fails Product", "get Invoice", "named Invoice", "own Invoice", "refs Invoice", "tag Invoice"],
            EntityCalls(factory, s));

        // The rule file's hash and the limits are part of every entry.
        await ValidateAsync(validator, Build("element", Probes + "\n// edited"));
        Assert.Equal(21, EntityCalls(factory, s).Count);
        await ValidateAsync(validator, Build("element", Probes + "\n// edited", settings => settings with { Limits = settings.Limits with { ScriptRecursion = 200 } }));
        Assert.Equal(21, EntityCalls(factory, s).Count);
    }

    public static TheoryData<string, string[]> Changes() => new()
    {
        { "get", ["get Customer", "get Invoice", "get Product"] },
        { "all", ["all Customer", "all Invoice", "all Product"] },
        { "tag", ["tag Customer", "tag Invoice", "tag Product"] },
        { "refs", ["refs Customer", "refs Invoice"] },
        { "named", ["named Customer", "named Invoice", "named Product"] },
        { "unrelated", [] },
    };

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task A_change_to_any_recorded_key_runs_the_rule_again(string change, string[] expected)
    {
        var factory = new CountingSandboxFactory();
        var validator = ValidationFixture.Validator(scripts: factory);
        var s = Build();
        await ValidateAsync(validator, s);
        factory.TakeRuleCalls();

        // A model whose change only a key one rule read sees: that rule runs again, on the entities that read it ("fails" always runs).
        var changed = Build(change);
        var report = await ValidateAsync(validator, changed);

        Assert.Equal([.. expected.Append("fails Product").Order(StringComparer.Ordinal)], EntityCalls(factory, s));
        var fresh = await ValidationFixture.Validator(scripts: new CountingSandboxFactory()).ValidateAsync(changed.Model, ValidationScope.All, null, Ct);
        Assert.Equal(ValidationFixture.ToGolden(fresh.Diagnostics), ValidationFixture.ToGolden(report.Diagnostics));
    }

    [Fact]
    public async Task A_severity_override_applies_to_cached_results()
    {
        var factory = new CountingSandboxFactory();
        var validator = ValidationFixture.Validator(scripts: factory);
        await ValidateAsync(validator, Build());
        factory.TakeRuleCalls();

        static ProjectSettings Rules(ProjectSettings settings, string own) =>
            settings with { Validation = new ValidationSettings { Rules = ImmutableDictionary<string, string>.Empty.Add("x/own", own).Add("x/fails", "off") } };
        var raised = await ValidateAsync(validator, Build(settings: s => Rules(s, "error")));

        // The raw results come from the cache; validation.rules applies after it, and an off rule never runs.
        Assert.Empty(factory.TakeRuleCalls());
        Assert.Equal(3, raised.Diagnostics.Count(d => d.Rule == "x/own" && d.Severity == DiagnosticSeverity.Error));
        Assert.DoesNotContain(raised.Diagnostics, d => d.Rule == "MQ5002");

        var off = await ValidateAsync(validator, Build(settings: s => Rules(s, "off")));
        Assert.Empty(factory.TakeRuleCalls());
        Assert.DoesNotContain(off.Diagnostics, d => d.Rule == "x/own");

        // Back to the defaults: the rules set off were dropped by the whole-model validations that did not run them, so they run
        // again; the others are reused.
        var back = await ValidateAsync(validator, Build());
        Assert.Equal(["fails Customer", "fails Invoice", "fails Product", "own Customer", "own Invoice", "own Product"], EntityCalls(factory, Build()));
        Assert.Equal(3, back.Diagnostics.Count(d => d.Rule == "x/own" && d.Severity == DiagnosticSeverity.Warning));
    }

    [Fact]
    public async Task Files_with_MQ5005_are_never_cached()
    {
        var code = "let calls = 0;\n" + Probes;
        var factory = new CountingSandboxFactory();
        var validator = ValidationFixture.Validator(scripts: factory);
        var s = Build(code: code);

        var first = await ValidateAsync(validator, s);
        var firstCalls = factory.TakeRuleCalls().Count;
        await ValidateAsync(validator, Build(code: code));

        Assert.Equal(firstCalls, factory.TakeRuleCalls().Count);
        Assert.Equal((RulePath, 1), first.Diagnostics.Where(d => d.Rule == "MQ5005").Select(d => (d.FilePath!, d.Line ?? 0)).Single());
        Assert.Equal(0, validator.RuleResults.Count);
    }

    [Fact]
    public async Task Cached_and_fresh_reports_are_byte_identical()
    {
        var factory = new CountingSandboxFactory();
        var validator = ValidationFixture.Validator(scripts: factory);
        var fresh = await ValidateAsync(validator, Build("refs"));
        var count = validator.RuleResults.Count;
        factory.TakeRuleCalls();

        var cached = await ValidateAsync(validator, Build("refs"));
        var other = await ValidationFixture.Validator(scripts: new CountingSandboxFactory(), parallelism: 1).ValidateAsync(Build("refs").Model, ValidationScope.All, null, Ct);

        Assert.Equal(["fails Product"], EntityCalls(factory, Build("refs")));
        Assert.True(count > 0);
        Assert.Equal(ValidationFixture.ToGolden(fresh.Diagnostics), ValidationFixture.ToGolden(cached.Diagnostics));
        Assert.Equal(ValidationFixture.ToGolden(fresh.Diagnostics), ValidationFixture.ToGolden(other.Diagnostics));
        Assert.Equal(18, cached.Diagnostics.Count(d => d.Rule.StartsWith("x/", StringComparison.Ordinal)));

        // A whole-model validation drops the entries it did not use: a model without Product keeps none of Product's.
        var b = new ModelBuilder(seed: 61);
        var billing = b.Package("Billing");
        b.Enum("Status", billing).Member("Draft", 0);
        b.Entity("Customer", billing).Key("id", "uuid");
        var built = b.Build();
        await validator.ValidateAsync(ModelSnapshot.Create(built.Documents, built.Settings, built.SettingsHash, [], [new ScriptSource(RulePath, Probes, Hash(Probes))], 1),
            ValidationScope.All, null, Ct);
        Assert.True(validator.RuleResults.Count < count);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("model rules only")]
    [InlineData("does not load")]
    public async Task A_whole_model_validation_without_element_rules_clears_the_cache(string rules)
    {
        var validator = ValidationFixture.Validator(scripts: new CountingSandboxFactory());
        await ValidateAsync(validator, Build());
        Assert.True(validator.RuleResults.Count > 0);

        // Without script rules the entries stay, for the next validation that runs them.
        var code = rules switch
        {
            "deleted" => null,
            "model rules only" => "maquettiste.rule({ id: 'whole', scope: 'model', check(model, report) {} });",
            _ => "maquettiste.rule({ id: 'broken', check(",
        };
        var changed = Build(code: code);
        await validator.ValidateAsync(changed.Model, new ValidationScope(IncludeScriptRules: false), null, Ct);
        Assert.True(validator.RuleResults.Count > 0);

        // A whole-model validation with script rules whose rule files register no element rule drops every entry.
        await ValidateAsync(validator, changed);
        Assert.Equal(0, validator.RuleResults.Count);
    }
}
