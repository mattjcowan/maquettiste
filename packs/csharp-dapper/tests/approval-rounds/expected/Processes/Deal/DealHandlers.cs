using System;
using App.Model.Runtime;

namespace App.Model.Deals;

// The hand-written half of DealHandlers. Maquettiste wrote this file once and never touches it again. Implement here the
// guards and actions whose expression the generated half could not translate (or that have none); a guard or action added to
// the model later is declared in DealHandlers.g.cs and fails the build until you add it here.
public sealed partial class DealHandlers
{
    // Whether the person a signature is delegated to (the envelope's Signer) is DeputyController: look them up in your system. A delegation to
    // someone who isn't is refused. It refuses until you write it.
    private partial bool IsDeputyController(ProcessSnapshot<DealContext> snapshot, ProcessEnvelope envelope) => false;
}

// The conditions of the named signatures of Deal. A signature with one is needed only when it is true, judged when its round
// starts; read the object for the call's instance. Answer true when you can't tell: an unknown answer means needed. A stage with one
// runs only when it is true, and is skipped otherwise; an unknown answer means it runs.
public static partial class DealApprovalConditions
{
    // Signoff, stage Legal: subject.cost > 50000
    public static partial bool SignoffLegalStage(ApprovalCall<DealContext> call) =>
        throw new NotImplementedException("The condition of stage Legal of gate Signoff of Deal is not implemented yet.");

    // Signoff, stage Board: subject.cost > 100000
    public static partial bool SignoffBoardStage(ApprovalCall<DealContext> call) =>
        throw new NotImplementedException("The condition of stage Board of gate Signoff of Deal is not implemented yet.");
}
