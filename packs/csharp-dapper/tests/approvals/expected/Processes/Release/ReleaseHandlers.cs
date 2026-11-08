using System;
using App.Model.Runtime;

namespace App.Model.Docs;

// The hand-written half of ReleaseHandlers. Maquettiste wrote this file once and never touches it again. Implement here the
// guards and actions whose expression the generated half could not translate (or that have none); a guard or action added to
// the model later is declared in ReleaseHandlers.g.cs and fails the build until you add it here.
public sealed partial class ReleaseHandlers
{
}

// The conditions of the named signatures of Release. A signature with one is needed only when it is true, judged when its round
// starts; read the object for the call's instance. Answer true when you can't tell: an unknown answer means needed.
public static partial class ReleaseApprovalConditions
{
    // Release, FinanceCheck: subject.cost > 10000
    public static partial bool ReleaseFinanceCheck(ApprovalCall<ReleaseContext> call) =>
        throw new NotImplementedException("The condition of signature FinanceCheck of gate Release of Release is not implemented yet.");

    // Release, ApprovalConditions: subject.cost > 100000
    public static partial bool ReleaseApprovalConditionsValue(ApprovalCall<ReleaseContext> call) =>
        throw new NotImplementedException("The condition of signature ApprovalConditions of gate Release of Release is not implemented yet.");

    // ReleaseFinance, Check: subject.cost > 50000
    public static partial bool ReleaseFinanceCheck000000N5(ApprovalCall<ReleaseContext> call) =>
        throw new NotImplementedException("The condition of signature Check of gate ReleaseFinance of Release is not implemented yet.");

    // ReleaseFinance, check: subject.cost > 75000
    public static partial bool ReleaseFinanceCheck000000N8(ApprovalCall<ReleaseContext> call) =>
        throw new NotImplementedException("The condition of signature check of gate ReleaseFinance of Release is not implemented yet.");
}
