using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Store;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// A project that uses no relation end with several types keeps its records and keys (several-types-design.md section 12): a hash over the
/// billing fixture's resolved entities, relations and navigations, their records and every dependency key they carry, pinned at the commit
/// before part 1's resolver changes. Re-pinned in 0.29.0, when projection was removed and the billing fixture became bound to designed tables:
/// the keys and records follow its bindings and relation mappings.
/// </summary>
public sealed class RelationTypesUnchangedTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_model_without_such_ends_keeps_its_records_and_keys()
    {
        await using var s = await BillingStore.OpenAsync();
        var model = ResolutionKit.Resolve(s.Model);
        var text = new StringBuilder();
        void Keys(string label, IEnumerable<string> keys) => text.Append(label).Append(':').AppendJoin(',', keys).Append('\n');
        foreach (var entity in model.Entities)
        {
            Keys("entity " + entity.Id, entity.Dependencies);
            Keys("navigations", entity.Navigations.MembershipKeys);
            Keys("relations", entity.Relations.MembershipKeys);
            foreach (var navigation in entity.Navigations)
                Keys("navigation " + navigation.Id + " " + navigation.Name + " " + navigation.Target!.Id, navigation.Dependencies);
        }

        foreach (var relation in model.Relations)
        {
            Keys("relation " + relation.Id, relation.Dependencies);
            Keys("ends", relation.Ends.MembershipKeys);
            foreach (var end in relation.Ends)
                Keys("end " + end.Id + " " + end.Entity!.Id, end.Dependencies);
        }

        foreach (var scope in new[] { "entities", "relations" })
            text.Append(WithoutParts(Engine.ResolvedRecords.Entries(model, scope, null).Select(e => e.Project()).ToList())).Append('\n');

        Assert.Contains("\"navigations\":[{", text.ToString(), StringComparison.Ordinal);
        Assert.Equal("9c99daf43f69f5b416cc63164f8ac25b", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32]);
    }

    /// <summary>
    /// Records as JSON without the members added after the hashes were pinned that no relation storage changes: an entity's <c>parts</c> and
    /// <c>copy</c> (parts-and-copy-design.md), which the billing fixture's composition fills in.
    /// </summary>
    /// <param name="records">The records.</param>
    /// <returns>The JSON.</returns>
    internal static string WithoutParts(List<object> records)
    {
        var node = JsonSerializer.SerializeToNode(records, Web)!.AsArray();
        foreach (var item in node)
        {
            if (item is JsonObject record)
            {
                record.Remove("parts");
                record.Remove("copy");
            }
        }

        return node.ToJsonString(Web);
    }
}
