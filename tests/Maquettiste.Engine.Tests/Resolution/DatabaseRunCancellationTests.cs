using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Databases resolving side by side (Resolution/README.md, "Bindings at benchmark scale") are cancelled as one-by-one runs are: the caller gets
/// an <see cref="OperationCanceledException"/>, never an aggregate of the runs' tasks.
/// </summary>
public sealed class DatabaseRunCancellationTests
{
    [Fact]
    public void Cancelling_while_databases_resolve_side_by_side_throws_operation_canceled()
    {
        var b = new ModelBuilder(seed: 91);
        var customer = b.Entity("Customer").Key("id", "uuid").Attr("name", "string");
        var order = b.Entity("Order").Key("id", "uuid");
        b.Relation("places", customer, order, fromMax: MaxCardinality.One);
        b.Database("audit", Dialect.PostgreSql);
        b.Database("main", Dialect.PostgreSql);
        b.Database("edge", Dialect.Sqlite);
        var snapshot = b.Build();

        // Cancelled as the runs start, so they are the ones that meet it.
        using var cancel = new CancellationTokenSource();
        var started = false;
        var run = new ResolveRun(snapshot, null, cancel.Token, parallelism: 4)
        {
            DatabaseRunsStarting = () =>
            {
                started = true;
                cancel.Cancel();
            },
        };
        var failure = Record.Exception(() => run.Run());

        Assert.True(started);
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
    }
}
