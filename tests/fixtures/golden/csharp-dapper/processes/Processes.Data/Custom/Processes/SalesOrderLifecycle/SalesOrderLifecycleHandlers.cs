using System;
using Processes.Data.Runtime;

namespace Processes.Data.Sales;

// The hand-written half of SalesOrderLifecycleHandlers. Maquettiste wrote this file once and never touches it again. Implement here the
// guards and actions whose expression the generated half could not translate (or that have none); a guard or action added to
// the model later is declared in SalesOrderLifecycleHandlers.g.cs and fails the build until you add it here.
public sealed partial class SalesOrderLifecycleHandlers
{
    /// <summary>Whether nothing has left the warehouse yet; answered by the host.</summary>
    private partial bool GuardNotShipped(StatechartCall<SalesOrderLifecycleContext> call) =>
        throw new NotImplementedException("Guard notShipped of process SalesOrderLifecycle is not implemented yet.");
}
