using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Processes.Data.Runtime;

namespace Processes.Data.Purchasing;

// The hand-written half of PurchaseApprovalServices. Maquettiste wrote this file once and never touches it again: implement the
// service tasks here (a service added to the model later fails the build until it is implemented), and the optional
// human-task hooks.
/// <summary>The budget check, the purchase order and the compliance review hook of PurchaseApproval.</summary>
/// <param name="store">The store, to read the requested amount.</param>
/// <param name="budget">The largest amount the budget check accepts.</param>
public sealed partial class PurchaseApprovalServices(IPurchaseApprovalStore store, decimal budget = 10_000m)
{
    private readonly List<InvokeRequest> _assigned = [];

    /// <summary>The compliance reviews assigned so far.</summary>
    public IReadOnlyList<InvokeRequest> Assigned => _assigned;

    /// <summary>The purchase orders created so far (instance identities).</summary>
    public List<string> Orders { get; } = [];

    /// <inheritdoc/>
    public async Task<bool> CheckBudgetAsync(InvokeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var instance = await store.LoadAsync(request.Instance, cancellationToken).ConfigureAwait(false);
        return instance is not null && instance.Context.Amount <= budget;
    }

    /// <inheritdoc/>
    public Task<bool> CreatePurchaseOrderAsync(InvokeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Orders.Add(request.Instance);
        return Task.FromResult(true);
    }

    partial void OnComplianceReviewAssigned(InvokeRequest request) => _assigned.Add(request);
}
