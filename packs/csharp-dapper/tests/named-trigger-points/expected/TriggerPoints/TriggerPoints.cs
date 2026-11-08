using System;
using System.Threading.Tasks;
using App.Model.Processes;

namespace App.Model;

// The hand-written half of the named trigger points of the root package. Maquettiste wrote this file once and never touches it again. Each
// method is written once however many types its point covers. Generated code can't read the object yet: load it from call.Key, or read
// call.Old and call.New when the caller gives them; call.Entity says which type it is. A before action fills call.Proposed and writes
// nothing; its records method writes those values once every check passes.
public static partial class TriggerPoints
{
    // Portal (check)
    public static partial bool Portal(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point Portal of the root package is not implemented yet.");
}
