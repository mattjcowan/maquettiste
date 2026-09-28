using System.Collections.Immutable;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Model;

/// <summary>Reference groups cannot be written through the public API, and the index build honours parallelism and cancellation.</summary>
public sealed class ModelSnapshotIndexTests
{
    [Fact]
    public void Reference_groups_are_immutable_views_shared_with_patched_snapshots()
    {
        var (model, target) = Model();
        var references = model.ReferencesTo(target);
        Assert.NotEmpty(references);
        Assert.IsNotType<ReferenceInfo[]>(references, exactMatch: false);
        Assert.IsType<ImmutableArray<ReferenceInfo>>(references);
        Assert.Throws<NotSupportedException>(() => ((IList<ReferenceInfo>)references)[0] = references[0]);
        var from = model.ReferencesFrom(references[0].FromElementId);
        Assert.IsType<ImmutableArray<ReferenceInfo>>(from);
        Assert.Empty(model.ReferencesTo("no-such-id"));
        Assert.Empty(model.ReferencesFrom("no-such-id"));
    }

    [Fact]
    public void The_index_is_the_same_at_any_parallelism()
    {
        var (model, _) = Model();
        var ct = TestContext.Current.CancellationToken;
        foreach (var parallelism in new[] { 1, 2, 7 })
        {
            var built = ModelSnapshot.CreateAfter(null, model.Documents, model.Settings, model.SettingsHash, [], [], 1, null, parallelism, ct);
            Assert.Equal(model.Summaries(), built.Summaries());
            foreach (var document in model.Documents)
            {
                Assert.Equal(model.ReferencesFrom(document.Element.Id), built.ReferencesFrom(document.Element.Id));
                Assert.Equal(model.ReferencesTo(document.Element.Id), built.ReferencesTo(document.Element.Id));
            }
        }
    }

    [Fact]
    public void A_cancelled_index_build_throws()
    {
        var (model, _) = Model();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ModelSnapshot.CreateAfter(null, model.Documents, model.Settings, model.SettingsHash, [], [], 1, null, 4, cancelled.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ModelSnapshot.CreateAfter(null, model.Documents, model.Settings, model.SettingsHash, [], [], 1, null, 0, CancellationToken.None));
    }

    private static (ModelSnapshot Model, string Target) Model()
    {
        var b = new ModelBuilder(11);
        var sales = b.Package("Sales");
        var customer = b.Entity("Customer", sales).Key("id", "uuid").Attr("name", "string");
        var order = b.Entity("Order", sales).Key("id", "uuid");
        b.Relation("placed", customer, order);
        return (b.Build(), customer.Id);
    }
}
