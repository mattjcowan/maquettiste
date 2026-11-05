using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>Declared rules that select one custom kind (<c>custom:&lt;type&gt;</c>), on a copy of the billing fixture with two invented kinds.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class DeclaredRuleCustomTypeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<E2ERepo> CopyAsync(string kind)
    {
        var repo = E2ERepo.Create(demo: false);
        repo.Repo.WriteFile(".maquettiste/extensions/kinds/policy.json", """{ "type": "policy", "properties": { "text": { "type": "string" } } }""");
        repo.Repo.WriteFile(".maquettiste/extensions/kinds/flag.json", """{ "type": "flag", "properties": { "on": { "type": "boolean" } } }""");
        repo.Repo.WriteFile(".maquettiste/extensions/rules/policies.json", $$"""
            { "rules": [ { "id": "policy-has-text", "severity": "error", "select": { "kind": {{kind}} },
                "require": { "has": "content.text" }, "message": "Policy {name} has no text." } ] }
            """);
        await repo.RestartAsync();
        await repo.CreateElementAsync("""{ "kind": "custom", "id": "01JRDC00000000000000000001", "name": "empty", "type": "policy" }""");
        await repo.CreateElementAsync("""{ "kind": "custom", "id": "01JRDC00000000000000000002", "name": "early", "type": "flag", "content": { "on": true } }""");
        return repo;
    }

    private static async Task<IReadOnlyList<Diagnostic>> FindingsAsync(E2ERepo repo) =>
        (await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics;

    [Fact]
    public async Task A_rule_on_one_custom_type_judges_only_that_type()
    {
        await using var repo = await CopyAsync("\"custom:policy\"");

        var found = Assert.Single(await FindingsAsync(repo), d => d.Rule == "x/policy-has-text");

        Assert.Equal("Policy empty has no text.", found.Message);
    }

    [Fact]
    public async Task A_rule_on_custom_still_judges_every_custom_type()
    {
        await using var repo = await CopyAsync("\"custom\"");

        Assert.Equal(["Policy early has no text.", "Policy empty has no text."],
            (await FindingsAsync(repo)).Where(d => d.Rule == "x/policy-has-text").Select(d => d.Message).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_type_no_kind_file_declares_is_MQ5016()
    {
        await using var repo = await CopyAsync("[\"custom:policy\", \"custom:nothing\"]");

        var found = Assert.Single(await FindingsAsync(repo), d => d.Rule == "MQ5016");

        Assert.Contains("'custom:nothing' is not a kind", found.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_type_with_no_elements_selects_no_object()
    {
        await using var repo = await CopyAsync("\"custom:policy\"");
        repo.Repo.WriteFile(".maquettiste/extensions/kinds/other.json", """{ "type": "other", "properties": { "x": { "type": "string" } } }""");
        repo.Repo.WriteFile(".maquettiste/extensions/rules/policies.json", """
            { "rules": [ { "id": "other-has-x", "select": { "kind": "custom:other" }, "require": { "has": "content.x" }, "message": "{name}" } ] }
            """);
        await repo.RestartAsync();

        Assert.Contains(await FindingsAsync(repo), d => d.Rule == "MQ5017");
    }
}
