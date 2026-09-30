using System;
using System.Collections.Generic;
using System.Data;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Processes.Data.Runtime;

namespace Processes.Data.Purchasing;

// The hand-written half of PurchaseApprovalStore: an orchestration keeps its instances in ProcessInstance (the snapshot as
// configuration, the context as context) and its subject in PurchaseRequest; GateSignature gets one row per gate audit record.
// No history entity is mapped for this process, so its Transitioned records only reach the outbox.
/// <summary>The orchestration's store over the ProcessInstance, PurchaseRequest and GateSignature repositories.</summary>
/// <param name="connection">The database connection.</param>
/// <param name="clock">The clock (ProcessInstance.updatedAt).</param>
public sealed partial class PurchaseApprovalStore(IDbConnection connection, IProcessClock clock)
{
    private readonly ProcessInstanceRepository _instances = new(connection);
    private readonly PurchaseRequestRepository _requests = new(connection);
    private readonly GateSignatureRepository _signatures = new(connection);

    /// <inheritdoc/>
    public async Task<ProcessSnapshot<PurchaseApprovalContext>?> LoadAsync(string instance, CancellationToken cancellationToken)
    {
        var row = await _instances.GetAsync(Guid.Parse(instance), cancellationToken).ConfigureAwait(false);
        if (row is null || row.Process != PurchaseApprovalDefinition.ProcessName)
            return null;
        var snapshot = row.Configuration.Deserialize<ProcessSnapshot<PurchaseApprovalContext>>()!;
        snapshot.Context = row.Context?.Deserialize<PurchaseApprovalContext>() ?? new PurchaseApprovalContext();
        return snapshot;
    }

    /// <inheritdoc/>
    public async Task SaveAsync(ProcessSnapshot<PurchaseApprovalContext> snapshot, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var id = Guid.Parse(snapshot.Instance);

        // The stored snapshot carries its version: a save over a version this command did not load lost a race and is refused.
        var existing = await _instances.GetAsync(id, cancellationToken).ConfigureAwait(false);
        var stored = existing is null ? 0 : existing.Configuration.Deserialize<ProcessSnapshot<PurchaseApprovalContext>>()?.Version ?? 0;
        if (stored != expectedVersion)
            throw ProcessConcurrencyException.Conflict(snapshot.Instance, expectedVersion, stored);
        var row = new ProcessInstance
        {
            Id = id,
            Process = PurchaseApprovalDefinition.ProcessName,
            Subject = id,
            Configuration = JsonSerializer.SerializeToElement(snapshot),
            Context = JsonSerializer.SerializeToElement(snapshot.Context),
            UpdatedAt = clock.Now,
        };
        if (existing is not null)
        {
            await _instances.UpdateAsync(row, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _instances.InsertAsync(row, cancellationToken).ConfigureAwait(false);
        await _requests.InsertAsync(new PurchaseRequest { Id = id, Amount = snapshot.Context.Amount }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task AppendHistoryAsync(IReadOnlyList<PurchaseApprovalTransitioned> transitions, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task AppendAuditAsync(IReadOnlyList<GateAuditRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var r in records)
            await _signatures.InsertAsync(GateSignatures.From(r), cancellationToken).ConfigureAwait(false);
    }
}
