using System;
using App.Model.Runtime;

namespace App.Model.Ops;

// The hand-written half of OrderFlowHandlers. Maquettiste wrote this file once and never touches it again. Implement here the
// guards and actions whose expression the generated half could not translate (or that have none); a guard or action added to
// the model later is declared in OrderFlowHandlers.g.cs and fails the build until you add it here.
public sealed partial class OrderFlowHandlers
{
    // Whether the caller is AccountManager, the order's account manager: read the object for the instance
    // and compare it with the caller (the envelope's Signer for an event, or the host's principal). It refuses until you write it.
    private partial bool IsAccountManager(ProcessSnapshot<OrderFlowContext> snapshot, ProcessEnvelope envelope) => false;

    // Whether the caller is Owner, the order's owner, held in Order.ownerLogin: read the object for the instance
    // and compare it with the caller (the envelope's Signer for an event, or the host's principal). It refuses until you write it.
    private partial bool IsOwner(ProcessSnapshot<OrderFlowContext> snapshot, ProcessEnvelope envelope) => false;

    // Whether the caller is ProjectApprover, members of Approver within the order's project: read the object for the instance
    // and compare it with the caller (the envelope's Signer for an event, or the host's principal). It refuses until you write it.
    private partial bool IsProjectApprover(ProcessSnapshot<OrderFlowContext> snapshot, ProcessEnvelope envelope) => false;
}
