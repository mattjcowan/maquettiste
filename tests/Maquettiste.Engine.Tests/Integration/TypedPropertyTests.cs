using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The acceptance test of next-phase-1-design.md section 4.1 (scope 3.1): a custom property typed as a link to a member of its own
/// element is an id, so a rename keeps it, and a save that removes the member is refused while the link is required and clears it
/// once the link is optional.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class TypedPropertyTests
{
    private const string IssuedOnId = "01J92P0V0SNXS6PZ42VDK42HP9";
    private const string DueOnId = "01K9XRF00000000000000000F1";
    private const string Listing = """
        {
          "name": "listing",
          "description": "How lists of an entity sort by default.",
          "appliesTo": { "kinds": ["entity"] },
          "properties": {
            "defaultSort": { "type": "string", "x-ref": { "kind": "attribute", "of": "$element"OPTIONAL } }
          }
        }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_default_sort_attribute_survives_rename_and_removal_is_refused_or_cleared()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var written = await repo.Store.WriteExtensionFileAsync("listing.json", Listing.Replace("OPTIONAL", "", StringComparison.Ordinal), null, Ct);
        Assert.Equal(SaveOutcome.Saved, written.Outcome);

        // Rename half: Invoice sorts by issuedOn's id, which a rename of the attribute does not touch.
        await repo.EditAsync(E2ERepo.InvoiceId, n => n["properties"]!["defaultSort"] = IssuedOnId);
        await repo.EditAsync(E2ERepo.InvoiceId, n => Attribute(n, IssuedOnId)["name"] = "issueDate");
        var invoice = (await repo.Store.GetElementAsync(E2ERepo.InvoiceId, Ct))!;
        Assert.Equal(IssuedOnId, invoice.Json.GetProperty("properties").GetProperty("defaultSort").GetString());
        var clean = await repo.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(clean.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(await repo.Store.GetReferencesAsync(IssuedOnId, Ct), r => r.JsonPointer == "/properties/defaultSort" && r.Schema == "extension:listing");

        // Delete half: issuedOn is also named by the register table's index and by queries, so the copy points defaultSort at an
        // invented attribute nothing else references.
        await repo.EditAsync(E2ERepo.InvoiceId, n =>
        {
            n["attributes"]!.AsArray().Add(new JsonObject { ["id"] = DueOnId, ["name"] = "dueOn", ["type"] = "date" });
            n["properties"]!["defaultSort"] = DueOnId;
        });

        var refused = await SaveWithoutDueOnAsync(repo);
        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        var dangling = Assert.Single(refused.Diagnostics, d => d.Rule == "MQ2001");
        Assert.Equal((E2ERepo.InvoiceId, "/properties/defaultSort"), (dangling.ElementId, dangling.JsonPointer));

        var listing = (await repo.Store.ReadExtensionFileAsync("listing.json", Ct))!;
        var optional = await repo.Store.WriteExtensionFileAsync("listing.json", Listing.Replace("OPTIONAL", ", \"optional\": true", StringComparison.Ordinal), listing.Hash, Ct);
        Assert.Equal(SaveOutcome.Saved, optional.Outcome);

        var saved = await SaveWithoutDueOnAsync(repo);
        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        invoice = (await repo.Store.GetElementAsync(E2ERepo.InvoiceId, Ct))!;
        Assert.False(invoice.Json.GetProperty("properties").TryGetProperty("defaultSort", out _));
        Assert.Equal(3650, invoice.Json.GetProperty("properties").GetProperty("retentionDays").GetInt32());
        Assert.DoesNotContain("defaultSort", repo.Repo.ReadFile(invoice.Path), StringComparison.Ordinal);
    }

    private static JsonObject Attribute(JsonObject element, string id) =>
        element["attributes"]!.AsArray().OfType<JsonObject>().Single(a => a["id"]!.GetValue<string>() == id);

    private static async Task<SaveResult> SaveWithoutDueOnAsync(E2ERepo repo)
    {
        var document = (await repo.Store.GetElementAsync(E2ERepo.InvoiceId, Ct))!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        var attributes = node["attributes"]!.AsArray();
        attributes.Remove(Attribute(node, DueOnId));
        return await repo.Store.SaveAsync(E2ERepo.InvoiceId, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
    }
}
