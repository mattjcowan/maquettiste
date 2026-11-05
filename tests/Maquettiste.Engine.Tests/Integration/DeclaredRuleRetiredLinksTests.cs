using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// <c>excludeRetired</c> on a declared rule's links, on a copy of the billing fixture: an invented kind <c>policy</c>, a property
/// <c>meets</c> that links to it, and a rule that every policy is met by something.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class DeclaredRuleRetiredLinksTests
{
    private const string Policy = "01JRDR00000000000000000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<E2ERepo> CopyAsync(bool excludeRetired)
    {
        var repo = E2ERepo.Create(demo: false);
        repo.Repo.WriteFile(".maquettiste/extensions/kinds/policy.json", """{ "type": "policy", "properties": { "text": { "type": "string" } } }""");
        repo.Repo.WriteFile(".maquettiste/extensions/traces.json", """
            { "name": "traces", "appliesTo": { "kinds": ["entity", "attribute"] },
              "properties": { "meets": { "type": "array", "items": { "type": "string", "x-ref": { "kind": "custom:policy", "optional": true } } } } }
            """);
        repo.Repo.WriteFile(".maquettiste/extensions/rules/policies.json", $$"""
            { "rules": [ { "id": "policy-met", "severity": "warning", "select": { "kind": "custom:policy" },
                "require": { "referencedBy": { "kind": ["entity", "attribute"], "field": "meets", "excludeRetired": {{(excludeRetired ? "true" : "false")}} } },
                "message": "{name} is met by nothing." } ] }
            """);
        await repo.RestartAsync();
        await repo.CreateElementAsync($$"""{ "kind": "custom", "id": "{{Policy}}", "name": "keeps_lines", "type": "policy", "content": { "text": "x" } }""");
        return repo;
    }

    private static void Link(JsonObject holder) =>
        holder["properties"] = new JsonObject { ["meets"] = new JsonArray(JsonValue.Create(Policy)) };

    private static async Task<bool> FlaggedAsync(E2ERepo repo) =>
        (await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics.Any(d => d.Rule == "x/policy-met");

    [Fact]
    public async Task A_retired_holder_counts_unless_the_rule_excludes_it()
    {
        await using var counting = await CopyAsync(excludeRetired: false);
        await counting.EditAsync(E2ERepo.CustomerId, entity =>
        {
            Link(entity);
            entity["status"] = "retired";
        });
        Assert.False(await FlaggedAsync(counting));

        await using var excluding = await CopyAsync(excludeRetired: true);
        await excluding.EditAsync(E2ERepo.CustomerId, entity => Link(entity));
        Assert.False(await FlaggedAsync(excluding));

        await excluding.EditAsync(E2ERepo.CustomerId, entity => entity["status"] = "retired");
        Assert.True(await FlaggedAsync(excluding));
    }

    [Fact]
    public async Task A_part_of_a_retired_element_does_not_count_and_a_part_retired_itself_does_not_either()
    {
        await using var repo = await CopyAsync(excludeRetired: true);
        await repo.EditAsync(E2ERepo.CustomerId, entity =>
            Link(entity["attributes"]!.AsArray().Select(a => a!.AsObject()).Single(a => (string?)a["id"] == E2ERepo.CustomerNameAttributeId)));
        Assert.False(await FlaggedAsync(repo));

        await repo.EditAsync(E2ERepo.CustomerId, entity => entity["status"] = "retired");
        Assert.True(await FlaggedAsync(repo));

        await repo.EditAsync(E2ERepo.CustomerId, entity =>
        {
            entity.Remove("status");
            entity["attributes"]!.AsArray().Select(a => a!.AsObject()).Single(a => (string?)a["id"] == E2ERepo.CustomerNameAttributeId)["status"] = "retired";
        });
        Assert.True(await FlaggedAsync(repo));
    }
}
