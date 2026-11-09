using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Loading;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// A project that uses none of part 2 keeps its records, views, join paths and keys (several-types-design.md sections 19.15 and 19.16): a hash
/// per fixture model over the relation mapping records, the entity mapping and binding records, each database view's JSON, the join paths of
/// navigations and entity mappings as templates see them, and the dependency keys of relations, navigations and bindings, pinned at the commit
/// before part 2's resolver changes. The showcase holds a designed junction (<c>uses-in-main.json</c>), the bindings fixture entity bindings.
/// </summary>
public sealed class RelationStorageUnchangedTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("billing", "e4e99d80cc288379e19d5b115a597262", "entity joins ")]
    [InlineData("showcase", "5bc7127bb779a592fd2e110b7b87f106", " junction conditions=0")]
    [InlineData("bindings", "3253c36a83cbe6d2999e0b3cb954fb41", "binding record ")]
    public async Task A_model_without_part_two_keeps_its_storage_records_joins_and_keys(string fixture, string hash, string covers)
    {
        var text = await Dump(fixture);
        Assert.Contains(covers, text, StringComparison.Ordinal);
        Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32]);
    }

    internal static async Task<string> Dump(string fixture)
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", fixture);
        var store = h.NewStore();
        await store.LoadAsync(TestContext.Current.CancellationToken);
        var model = ResolutionKit.Resolve(store.Current!);
        var text = new StringBuilder();
        void Keys(string label, IEnumerable<string> keys) => text.Append(label).Append(':').AppendJoin(',', keys).Append('\n');
        void Json(string label, object? value) => text.Append(label).Append(':').Append(JsonSerializer.Serialize(value, Web)).Append('\n');
        void Path(string label, RJoinPath path)
        {
            text.Append(label).Append(':');
            foreach (var step in path.Steps)
            {
                text.Append(step.FromTable.Key).Append('(').AppendJoin(',', step.FromColumns.Select(c => c.Key)).Append(")>")
                    .Append(step.ToTable.Key).Append('(').AppendJoin(',', step.ToColumns.Select(c => c.Key)).Append(')')
                    .Append(step.ViaJunction ? " junction" : "").Append(" conditions=").Append(step.Conditions.Count).Append(';');
            }

            text.Append(" from=").Append(path.FromType?.Id).Append(" to=").Append(path.ToType?.Id).Append('\n');
        }

        foreach (var relation in model.Relations)
        {
            Keys("relation " + relation.Id, relation.Dependencies);
        }

        foreach (var entity in model.Entities)
        {
            foreach (var navigation in entity.Navigations)
            {
                Keys("navigation " + navigation.Id, navigation.Dependencies);
                foreach (var (db, path) in navigation.Joins.OrderBy(j => j.Key, StringComparer.Ordinal))
                    Path("joins " + db, path);
                text.Append("type_joins ").Append(navigation.TypeJoins.Count).Append('\n');
            }

            foreach (var (db, mapping) in entity.Mappings.OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                text.Append("entity mapping ").Append(entity.Id).Append(' ').Append(db).Append(' ').Append(mapping.Table.Key)
                    .Append(" type_joins ").Append(mapping.TypeJoins.Count).Append('\n');
                foreach (var (name, path) in mapping.Joins.OrderBy(j => j.Key, StringComparer.Ordinal))
                    Path("entity joins " + name, path);
            }

            foreach (var (db, binding) in entity.Bindings.OrderBy(b => b.Key, StringComparer.Ordinal))
            {
                Keys("binding " + binding.Id, binding.Dependencies);
                Json("binding record " + db, BindingViews.Record(binding));
            }
        }

        foreach (var scope in new[] { "entities", "relations" })
            Json(scope, Engine.ResolvedRecords.Entries(model, scope, null).Select(e => e.Project()).ToList());
        foreach (var database in model.Databases)
            Json("database " + database.Name, DatabaseViews.From(database));
        return text.ToString();
    }
}
