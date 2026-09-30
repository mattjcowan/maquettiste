using System;
using System.Collections.Generic;
using System.Data;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Processes.Data.Purchasing;
using Processes.Data.Runtime;

namespace Processes.Data.Sales;

// The hand-written half of SalesOrderLifecycleStore: the lifecycle lives on its subject. SalesOrder.status holds the root-level
// state, SalesOrder.configuration the whole snapshot (the nested states the status cannot hold), total and creditLimit the
// context; SalesOrderHistory gets one row per status change and GateSignature one row per gate audit record. Storage is what
// the model maps; the generated repositories do the SQL.
/// <summary>The lifecycle's store over the SalesOrder, SalesOrderHistory and GateSignature repositories.</summary>
/// <param name="connection">The database connection.</param>
public sealed partial class SalesOrderLifecycleStore(IDbConnection connection)
{
    private readonly SalesOrderRepository _orders = new(connection);
    private readonly SalesOrderHistoryRepository _history = new(connection);
    private readonly GateSignatureRepository _signatures = new(connection);

    /// <inheritdoc/>
    public async Task<ProcessSnapshot<SalesOrderLifecycleContext>?> LoadAsync(string instance, CancellationToken cancellationToken)
    {
        var order = await _orders.GetAsync(Guid.Parse(instance), cancellationToken).ConfigureAwait(false);
        if (order?.Configuration is not { } configuration)
            return null;
        var snapshot = configuration.Deserialize<ProcessSnapshot<SalesOrderLifecycleContext>>()!;
        snapshot.Context = new SalesOrderLifecycleContext { Total = order.Total, CreditLimit = order.CreditLimit };
        return snapshot;
    }

    /// <inheritdoc/>
    public async Task SaveAsync(ProcessSnapshot<SalesOrderLifecycleContext> snapshot, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var order = await _orders.GetAsync(Guid.Parse(snapshot.Instance), cancellationToken).ConfigureAwait(false);
        var existing = order is not null;

        // The stored snapshot carries its version: a save over a version this command did not load lost a race and is refused.
        var stored = order?.Configuration is { } configuration ? configuration.Deserialize<ProcessSnapshot<SalesOrderLifecycleContext>>()?.Version ?? 0 : 0;
        if (stored != expectedVersion)
            throw ProcessConcurrencyException.Conflict(snapshot.Instance, expectedVersion, stored);
        order ??= new SalesOrder { Id = Guid.Parse(snapshot.Instance) };
        order.Status = SalesOrderLifecycleStates.ToStatus(snapshot.States);
        order.Configuration = JsonSerializer.SerializeToElement(snapshot);
        order.Total = snapshot.Context.Total;
        order.CreditLimit = snapshot.Context.CreditLimit;
        if (existing)
            await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);
        else
            await _orders.InsertAsync(order, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task AppendHistoryAsync(IReadOnlyList<SalesOrderLifecycleTransitioned> transitions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transitions);
        foreach (var t in transitions)
        {
            var to = SalesOrderLifecycleStates.ToStatus(t.To);
            var from = t.From.Count == 0 ? (SalesOrderStatus?)null : SalesOrderLifecycleStates.ToStatus(t.From);
            if (from == to)
                continue;
            await _history.InsertAsync(new SalesOrderHistory
            {
                SalesOrderId = Guid.Parse(t.Instance), FromState = from?.ToString(), ToState = to.ToString(), At = t.At,
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task AppendAuditAsync(IReadOnlyList<GateAuditRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var r in records)
            await _signatures.InsertAsync(GateSignatures.From(r), cancellationToken).ConfigureAwait(false);
    }
}
