using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Writing;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Expressions;

/// <summary>
/// A project without expressions pays nothing (next-phase-3-design.md section 3.1, decision 23): nothing is typed or lowered, no
/// runtime is loaded and no new key is written to any record or snapshot. The golden tests prove the generated files unchanged.
/// </summary>
public sealed class ExpressionCostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The types that exist only for expressions.</summary>
    private static readonly HashSet<Type> ExpressionTypes = [typeof(RExpression), typeof(REntityCheck), typeof(RConstraint), typeof(RConstraintParameter)];

    [Fact]
    public async Task A_model_without_expressions_loads_no_runtime_and_adds_no_keys()
    {
        foreach (var folder in Directory.EnumerateDirectories(Fixtures.Path("models")).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(folder);
            var model = await ResolveAsync(folder);

            // Nothing was typed: the resolved model holds no expression, so nothing can lower JavaScript or load the runtime.
            var found = Reachable(model).Where(o => ExpressionTypes.Contains(o.GetType())).Select(o => o.GetType().Name).Distinct().ToList();
            Assert.True(found.Count == 0, $"{name}: the resolved model holds {string.Join(", ", found)}.");
            Assert.All(model.Databases.SelectMany(d => d.Tables).SelectMany(t => t.Checks), c => Assert.Null(c.Shape));

            // No new key in a snapshot.
            foreach (var database in model.Databases)
            {
                var captured = SnapshotCapture.Capture(database, 1, ct: Ct);
                var snapshot = Encoding.UTF8.GetString(TestServices.Json.Serialize(captured, "snapshot.json", "snapshots/" + database.Name + ".json"));
                Assert.DoesNotContain("\"shape\"", snapshot, StringComparison.Ordinal);
            }

            // No new key in a record, as the editor's API writes them.
            var records = JsonSerializer.SerializeToNode(ResolvedRecords.Entries(model, "all", null).Select(e => e.Project()).ToList(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsArray();
            Assert.DoesNotContain(records, r => r!["kind"]!.GetValue<string>() == "constraint");
            foreach (var entity in records.Where(r => r!["kind"]!.GetValue<string>() == "entity").Select(r => r!.AsObject()))
            {
                Assert.False(entity.ContainsKey("checks"), $"{name}: entity {entity["name"]} has checks.");
                foreach (var attribute in entity["attributes"]!.AsArray())
                    Assert.False(attribute!["derived"] is JsonObject derived && derived.ContainsKey("value"), $"{name}: a derived value has a value.");
            }
        }
    }

    /// <summary>Every resolved view object reachable from the model.</summary>
    private static IEnumerable<object> Reachable(ResolvedModel model)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        Enqueue(model);
        while (queue.TryDequeue(out var current))
        {
            yield return current;
            foreach (var property in current.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length == 0 && property.PropertyType != typeof(string) && property.Name != nameof(RAnnotated.Calculated))
                    Enqueue(property.GetValue(current));
            }
        }

        void Enqueue(object? value)
        {
            switch (value)
            {
                case null or string:
                    return;
                case System.Collections.IEnumerable items:
                    foreach (var item in items)
                        Enqueue(item);
                    return;
            }

            var type = value.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                Enqueue(type.GetProperty("Value")!.GetValue(value));
            else if (MemberCatalog.IsProxyType(type) && seen.Add(value))
                queue.Enqueue(value);
        }
    }

    private static async Task<ResolvedModel> ResolveAsync(string root)
    {
        var cache = Path.Combine(Path.GetTempPath(), "maquettiste-tests", "expression-cost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        try
        {
            var options = new EngineOptions { RepoRoot = root, CacheDirectory = cache, MaxDegreeOfParallelism = 2 };
            var loader = new ModelLoader(options, TestServices.Schemas, TestServices.Json, new OutputPathPolicy(options, null));
            var loaded = await loader.LoadAsync(new LoadRequest(null, null, false), null, Ct);
            return await new ModelResolver(options).ResolveAsync(loaded.Snapshot, null, Ct);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }
}
