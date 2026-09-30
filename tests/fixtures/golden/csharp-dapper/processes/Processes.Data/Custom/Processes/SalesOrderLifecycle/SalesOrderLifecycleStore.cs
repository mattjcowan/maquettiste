using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Processes.Data.Runtime;

namespace Processes.Data.Sales;

// The hand-written half of SalesOrderLifecycleStore. Maquettiste wrote this file once and never touches it again. It starts as an
// in-memory store that checks the version a save expects; adapt it to the entities the project mapped (for example with the
// generated repositories), keeping that check.
public sealed partial class SalesOrderLifecycleStore
{
    private readonly Dictionary<string, string> _instances = new(StringComparer.Ordinal);
    private readonly List<SalesOrderLifecycleTransitioned> _history = [];
    private readonly List<GateAuditRecord> _audit = [];

    /// <summary>The history appended so far.</summary>
    public IReadOnlyList<SalesOrderLifecycleTransitioned> History => _history;

    /// <summary>The audit records appended so far.</summary>
    public IReadOnlyList<GateAuditRecord> Audit => _audit;

    /// <inheritdoc/>
    public Task<ProcessSnapshot<SalesOrderLifecycleContext>?> LoadAsync(string instance, CancellationToken cancellationToken)
    {
        lock (_instances)
            return Task.FromResult(_instances.TryGetValue(instance, out var json) ? JsonSerializer.Deserialize<ProcessSnapshot<SalesOrderLifecycleContext>>(json) : null);
    }

    /// <inheritdoc/>
    public Task SaveAsync(ProcessSnapshot<SalesOrderLifecycleContext> snapshot, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_instances)
        {
            var stored = _instances.TryGetValue(snapshot.Instance, out var json) ? JsonSerializer.Deserialize<ProcessSnapshot<SalesOrderLifecycleContext>>(json)!.Version : 0;
            if (stored != expectedVersion)
                throw ProcessConcurrencyException.Conflict(snapshot.Instance, expectedVersion, stored);
            _instances[snapshot.Instance] = JsonSerializer.Serialize(snapshot);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task AppendHistoryAsync(IReadOnlyList<SalesOrderLifecycleTransitioned> transitions, CancellationToken cancellationToken)
    {
        _history.AddRange(transitions);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task AppendAuditAsync(IReadOnlyList<GateAuditRecord> records, CancellationToken cancellationToken)
    {
        _audit.AddRange(records);
        return Task.CompletedTask;
    }
}
