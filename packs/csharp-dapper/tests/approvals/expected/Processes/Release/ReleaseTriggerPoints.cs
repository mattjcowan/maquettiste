using System;
using System.Threading.Tasks;
using App.Model.Processes;

namespace App.Model.Docs;

// The hand-written half of lifecycle Release's trigger points. Maquettiste wrote this file once and never touches it again. Generated
// code can't read the object yet: load it from call.Key or call.Instance, or read call.Old and call.New when the caller gives them. A
// before action fills call.Proposed and writes nothing; its records method writes those values once every check passes.
public static partial class ReleaseTriggerPoints
{
    // VetoCheck (check)
    public static partial bool VetoCheck(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point VetoCheck of lifecycle Release is not implemented yet.");
}
