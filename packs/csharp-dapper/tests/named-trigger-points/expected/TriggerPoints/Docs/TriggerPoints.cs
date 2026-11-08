using System;
using System.Threading.Tasks;
using App.Model.Processes;

namespace App.Model.Docs;

// The hand-written half of the named trigger points of package Docs. Maquettiste wrote this file once and never touches it again. Each
// method is written once however many types its point covers. Generated code can't read the object yet: load it from call.Key, or read
// call.Old and call.New when the caller gives them; call.Entity says which type it is. A before action fills call.Proposed and writes
// nothing; its records method writes those values once every check passes.
public static partial class TriggerPoints
{
    // Stamp (action, with-change)
    public static partial void Stamp(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point Stamp of package Docs is not implemented yet.");

    // TitleLog (action, with-change)
    public static partial void TitleLog(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point TitleLog of package Docs is not implemented yet.");

    // TitleStamp (action, with-change)
    public static partial void TitleStamp(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point TitleStamp of package Docs is not implemented yet.");

    // TitleWarning (check)
    public static partial bool TitleWarning(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point TitleWarning of package Docs is not implemented yet.");
}
