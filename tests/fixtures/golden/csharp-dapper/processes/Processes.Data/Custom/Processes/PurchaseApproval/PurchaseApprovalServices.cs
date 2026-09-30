using System;
using System.Threading;
using System.Threading.Tasks;
using Processes.Data.Runtime;

namespace Processes.Data.Purchasing;

// The hand-written half of PurchaseApprovalServices. Maquettiste wrote this file once and never touches it again: implement the
// service tasks here (a service added to the model later fails the build until it is implemented), and the optional
// human-task hooks.
public sealed partial class PurchaseApprovalServices
{
    /// <inheritdoc/>
    public Task<bool> CheckBudgetAsync(InvokeRequest request, CancellationToken cancellationToken) =>
        throw new NotImplementedException("Service checkBudget of process PurchaseApproval is not implemented yet.");

    /// <inheritdoc/>
    public Task<bool> CreatePurchaseOrderAsync(InvokeRequest request, CancellationToken cancellationToken) =>
        throw new NotImplementedException("Service createPurchaseOrder of process PurchaseApproval is not implemented yet.");
}
