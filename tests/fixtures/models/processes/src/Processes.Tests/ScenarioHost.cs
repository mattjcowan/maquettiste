using System.Text.Json;
using Microsoft.Data.Sqlite;
using Processes.Data.Dispatch;
using Processes.Data.Purchasing;
using Processes.Data.Runtime;
using Processes.Data.Sales;
using Xunit;

namespace Processes.Data;

/// <summary>
/// What the generated scenario tests run against: the dispatcher over both processes, with the fixture's stores on an in-memory
/// SQLite database built from the sql-ddl output (the PostgreSQL schema script; its "public" schema is an attached database), and
/// the fixture's services. A scenario states how each service task ended; before such a result reaches the process, the host asks
/// the fixture's service and fails the test when the service would answer otherwise, so a scenario cannot pass against a service
/// that disagrees with it.
/// </summary>
internal sealed class ScenarioHost : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private ScenarioHost(SqliteConnection connection, ScenarioDispatcher dispatcher, SalesOrderLifecycleStore salesOrders)
    {
        _connection = connection;
        Dispatcher = dispatcher;
        SalesOrders = salesOrders;
    }

    /// <summary>The dispatcher.</summary>
    public ScenarioDispatcher Dispatcher { get; }

    /// <summary>The store of SalesOrderLifecycle, for tests that use it directly.</summary>
    public SalesOrderLifecycleStore SalesOrders { get; }

    /// <summary>Opens a fresh database and wires the processes on the given clock.</summary>
    /// <param name="clock">The clock the scenario moves.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The host.</returns>
    public static async Task<ScenarioHost> StartAsync(ManualClock clock, CancellationToken cancellationToken)
    {
        DapperTypeHandlers.Register();
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "ATTACH DATABASE ':memory:' AS public;\n" + await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "schema.sql"), cancellationToken);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var host = new ProcessHost(clock);
        var salesStore = new SalesOrderLifecycleStore(connection);
        var sales = new SalesOrderLifecycleCommandHandler(
            new SalesOrderLifecycleMachine(new SalesOrderLifecycleHandlers(), host, host, host), salesStore);
        var purchaseStore = new PurchaseApprovalStore(connection, host);
        var purchase = new PurchaseApprovalCommandHandler(
            new PurchaseApprovalMachine(new PurchaseApprovalHandlers(), host, host, host), purchaseStore);
        var registry = new HandlerRegistry(purchaseApproval: purchase, salesOrderLifecycle: sales);
        var dispatcher = new Dispatcher(registry, new Pipeline(new InMemoryProcessOutbox()));
        return new ScenarioHost(connection, new ScenarioDispatcher(dispatcher, host, new PurchaseApprovalServices(purchaseStore)), salesStore);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}

/// <summary>
/// The generated tests' dispatcher: checks each service task's reported result against the fixture's service, then sends the
/// command. With MAQUETTISTE_SCENARIO_TRACE naming a folder, it also appends every result to &lt;folder&gt;/&lt;instance&gt;.jsonl (one
/// JSON line per command, the start first), which the pack tests compare with the engine's replay.
/// </summary>
/// <param name="dispatcher">The generated dispatcher.</param>
/// <param name="host">The process host, which lists the started invokes.</param>
/// <param name="purchasing">The services of PurchaseApproval.</param>
internal sealed class ScenarioDispatcher(Dispatcher dispatcher, ProcessHost host, PurchaseApprovalServices purchasing)
{
    private static readonly Lock TraceLock = new();

    /// <summary>Sends a command.</summary>
    /// <typeparam name="TCommand">The command type.</typeparam>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result.</returns>
    public async Task<CommandResult> SendAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : class
    {
        if (command is PurchaseApprovalInvokeResultControl reported
            && host.Invokes.FirstOrDefault(i => i.Instance == reported.Envelope.Instance && i.Invoke == reported.Invoke) is { } request
            && await purchasing.RunAsync(request, cancellationToken) is { } answer)
        {
            Assert.True(answer.Failed == reported.Failed,
                $"Service {reported.Invoke} answers {(answer.Failed ? "error" : "done")} for this instance, but the scenario reports {(reported.Failed ? "error" : "done")}.");
        }

        var result = await dispatcher.SendAsync(command, cancellationToken);
        Trace(command, result);
        return result;
    }

    private static void Trace(object command, CommandResult result)
    {
        if (Environment.GetEnvironmentVariable("MAQUETTISTE_SCENARIO_TRACE") is not { Length: > 0 } folder)
            return;
        var instance = command is IProcessCommand sent ? sent.Envelope.Instance : "";
        var line = JsonSerializer.Serialize(new
        {
            accepted = result.Accepted,
            refusal = result.Refusal,
            states = result.States ?? [],
            final = result.Final,
            audit = result.Audit.Select(a => a.OutcomeText).ToArray(),
            auditActors = result.Audit.Select(a => a.Actor).ToArray(),
            auditMeanings = result.Audit.Select(a => a.Meaning).ToArray(),
        });
        lock (TraceLock)
        {
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, instance + ".jsonl"), line + "\n");
        }
    }
}
