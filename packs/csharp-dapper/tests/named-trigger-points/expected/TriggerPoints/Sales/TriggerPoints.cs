using System;
using System.Threading.Tasks;
using App.Model.Processes;

namespace App.Model.Sales;

// The hand-written half of the named trigger points of package Sales. Maquettiste wrote this file once and never touches it again. Each
// method is written once however many types its point covers. Generated code can't read the object yet: load it from call.Key, or read
// call.Old and call.New when the caller gives them; call.Entity says which type it is. A before action fills call.Proposed and writes
// nothing; its records method writes those values once every check passes.
public static partial class TriggerPoints
{
    // CreditCheck (check)
    public static partial bool CreditCheck(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point CreditCheck of package Sales is not implemented yet.");

    // CustomerNumber (action, before)
    public static partial void CustomerNumber(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point CustomerNumber of package Sales is not implemented yet.");

    // NameLength (check)
    public static partial bool NameLength(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point NameLength of package Sales is not implemented yet.");

    // NameRequired (check)
    public static partial bool NameRequired(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point NameRequired of package Sales is not implemented yet.");

    // Welcome (action, after-commit)
    public static partial Task Welcome(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point Welcome of package Sales is not implemented yet.");
}

public static partial class TriggerRecords
{
    // Writes what CustomerNumber filled in call.Proposed.
    public static partial void CustomerNumber(TriggerPointCall call) =>
        throw new NotImplementedException("The records of trigger point CustomerNumber of package Sales are not written yet.");
}
