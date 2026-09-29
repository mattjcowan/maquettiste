using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Scripting;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// One invalid model per rule family under <c>tests/fixtures/validation/</c>, each with its expected diagnostics as golden JSON
/// (<c>expected.json</c>; rewrite with <c>MAQUETTISTE_UPDATE_GOLDEN=1</c>). The tests also pin the rule ids each family must raise,
/// so a regenerated golden file cannot silently lose a rule.
/// </summary>
public sealed class ValidationFixtureTests
{
    private static FakeSandboxFactory ScriptsFor(string family) => family != "scripts"
        ? new FakeSandboxFactory()
        : new FakeSandboxFactory(
            ("no-draft", doc => doc.Element.Tags.Contains("draft")
                ? [new Diagnostic("x/no-draft", DiagnosticSeverity.Warning, "Draft elements must not be generated.", null, null, "/tags", null, null)]
                : []),
            ("upper-case", doc => doc.Element.Name switch
            {
                "Boom" => throw new InvalidOperationException("boom"),
                "Loop" => throw new ScriptLimitException(new Diagnostic("MQ6007", DiagnosticSeverity.Error, "Statement limit of 5000000 reached.", null, null, null, null, null)),
                _ => [],
            }));

    private static async Task<ValidationReport> ValidateAsync(string family)
    {
        var model = ValidationFixture.Load(family);
        var validator = ValidationFixture.Validator(ValidationFixture.RepoRoot(family), ScriptsFor(family));
        return await validator.ValidateAsync(model, ValidationScope.All, null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Clean_model_yields_zero_diagnostics()
    {
        var report = await ValidateAsync("clean");

        Assert.Empty(report.Diagnostics);
        Assert.Equal((0, 0, 0), (report.Errors, report.Warnings, report.Infos));
        ValidationFixture.AssertGolden("clean", report.Diagnostics);
    }

    [Theory]
    [InlineData("references", new[] { "MQ2001", "MQ2002", "MQ2003", "MQ2004", "MQ2005", "MQ2006", "MQ2007" })]
    [InlineData("cycles", new[] { "MQ3002", "MQ3003", "MQ3004", "MQ3015" })]
    [InlineData("names-keys", new[] { "MQ3001", "MQ3005", "MQ3006", "MQ3007" })]
    [InlineData("relations", new[] { "MQ3001", "MQ3008", "MQ3009", "MQ3010", "MQ3011", "MQ3016" })]
    [InlineData("values", new[] { "MQ3012", "MQ3013", "MQ3017", "MQ3019" })]
    [InlineData("physical", new[] { "MQ2001", "MQ4001", "MQ4002", "MQ4003", "MQ4004", "MQ4005", "MQ4006", "MQ4007", "MQ4008", "MQ4010" })]
    [InlineData("mappings", new[] { "MQ4004", "MQ4009", "MQ4011" })]
    [InlineData("extensions", new[] { "MQ5001", "MQ5004" })]
    [InlineData("scripts", new[] { "MQ2007", "MQ5002", "MQ5003", "x/no-draft" })]
    [InlineData("reference-data", new[] { "MQ2001", "MQ2002", "MQ3019", "MQ7001", "MQ7002", "MQ7006", "MQ7007", "MQ7008", "MQ7010", "MQ7011" })]
    [InlineData("seeds", new[] { "MQ7002", "MQ7003", "MQ7004", "MQ7005", "MQ7009", "MQ7101", "MQ7102", "MQ7103", "MQ7105", "MQ7106" })]
    public async Task Invalid_model_matches_its_golden_diagnostics(string family, string[] rules)
    {
        var report = await ValidateAsync(family);

        ValidationFixture.AssertGolden(family, report.Diagnostics);
        Assert.Equal(rules.Order(StringComparer.Ordinal), report.Diagnostics.Select(d => d.Rule).Distinct().Order(StringComparer.Ordinal));
        Assert.All(report.Diagnostics, d =>
        {
            Assert.NotNull(d.FilePath);
            Assert.NotNull(d.JsonPointer);
            Assert.NotNull(d.Line);
            Assert.NotNull(d.Column);
            if (RuleCatalog.TryGet(d.Rule, out var rule) && d.Rule != "MQ2006")
                Assert.Equal(rule.DefaultSeverity, d.Severity);
        });
        Assert.Equal(report.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error), report.Errors);
        Assert.Equal(report.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning), report.Warnings);
        Assert.Equal(report.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Info), report.Infos);
    }

    [Fact]
    public async Task Positions_point_at_the_offending_value_in_the_file()
    {
        var report = await ValidateAsync("references");

        var dangling = Assert.Single(report.Diagnostics, d => d.Rule == "MQ2001" && d.JsonPointer == "/package");
        var lines = File.ReadAllLines(Path.Combine(ValidationFixture.RepoRoot("references"), dangling.FilePath!));
        var line = lines[dangling.Line!.Value - 1];
        Assert.StartsWith("\"01JPKG", line[(dangling.Column!.Value - 1)..], StringComparison.Ordinal);
        Assert.Contains("\"package\":", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Strict_tag_vocabulary_makes_undeclared_tags_errors()
    {
        var report = await ValidateAsync("references");

        var tag = Assert.Single(report.Diagnostics, d => d.Rule == "MQ2006");
        Assert.Equal(DiagnosticSeverity.Error, tag.Severity);
        Assert.Equal("/tags/1", tag.JsonPointer);
    }

    [Fact]
    public async Task Default_that_names_an_expression_suggests_defaultExpression()
    {
        var report = await ValidateAsync("values");

        var now = Assert.Single(report.Diagnostics, d => d.Rule == "MQ3019" && d.Message.Contains("'now'", StringComparison.Ordinal));
        Assert.Contains("use defaultExpression", now.Message, StringComparison.Ordinal);
        Assert.EndsWith("/default", now.JsonPointer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Script_rule_results_are_attributed_to_the_element_file()
    {
        var report = await ValidateAsync("scripts");

        var draft = Assert.Single(report.Diagnostics, d => d.Rule == "x/no-draft");
        Assert.Equal(DiagnosticSeverity.Warning, draft.Severity);
        Assert.Equal(".maquettiste/model/entities/draft-order.json", draft.FilePath);
        Assert.Equal("/tags", draft.JsonPointer);
        Assert.Contains(report.Diagnostics, d => d.Rule == "MQ5002" && d.FilePath!.EndsWith("boom.json", StringComparison.Ordinal));
        Assert.Contains(report.Diagnostics, d => d.Rule == "MQ5003" && d.FilePath!.EndsWith("loop.json", StringComparison.Ordinal));
        var unknown = Assert.Single(report.Diagnostics, d => d.Rule == "MQ2007");
        Assert.Contains("x/missing-rule", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Results_do_not_depend_on_parallelism()
    {
        foreach (var family in new[] { "references", "physical", "mappings", "extensions" })
        {
            var model = ValidationFixture.Load(family);
            var one = await ValidationFixture.Validator(ValidationFixture.RepoRoot(family), parallelism: 1)
                .ValidateAsync(model, ValidationScope.All, null, TestContext.Current.CancellationToken);
            var many = await ValidationFixture.Validator(ValidationFixture.RepoRoot(family), parallelism: 8)
                .ValidateAsync(model, ValidationScope.All, null, TestContext.Current.CancellationToken);
            Assert.Equal(ValidationFixture.ToGolden(one.Diagnostics), ValidationFixture.ToGolden(many.Diagnostics));
        }
    }

    [Fact]
    public async Task Positions_fall_back_to_the_snapshot_json_when_the_file_is_not_on_disk()
    {
        var model = ValidationFixture.Load("values");
        var onDisk = await ValidationFixture.Validator(ValidationFixture.RepoRoot("values"))
            .ValidateAsync(model, ValidationScope.All, null, TestContext.Current.CancellationToken);
        var inMemory = await ValidationFixture.Validator(Path.Combine(Path.GetTempPath(), "no-such-repo-" + nameof(ValidationFixtureTests)))
            .ValidateAsync(model, ValidationScope.All, null, TestContext.Current.CancellationToken);

        Assert.Equal(ValidationFixture.ToGolden(onDisk.Diagnostics), ValidationFixture.ToGolden(inMemory.Diagnostics));
    }

    [Fact]
    public void Fixture_model_kinds_cover_every_element_kind_in_the_clean_model()
    {
        var model = ValidationFixture.Load("clean");
        var kinds = model.Documents.Select(d => d.Element.Kind).Distinct().Order().ToList();
        Assert.Equal(Enum.GetValues<ElementKind>().Order().ToList(), kinds);
    }
}
