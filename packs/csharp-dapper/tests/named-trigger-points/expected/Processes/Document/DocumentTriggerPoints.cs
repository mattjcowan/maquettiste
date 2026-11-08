using System;
using System.Threading.Tasks;
using App.Model.Processes;

namespace App.Model.Docs;

// The hand-written half of lifecycle Document's trigger points. Maquettiste wrote this file once and never touches it again. Generated
// code can't read the object yet: load it from call.Key or call.Instance, or read call.Old and call.New when the caller gives them. A
// before action fills call.Proposed and writes nothing; its records method writes those values once every check passes.
public static partial class DocumentTriggerPoints
{
    // Stamp (action, with-change)
    public static partial void Stamp(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point Stamp of lifecycle Document is not implemented yet.");

    // MarkRevised (action, with-change)
    public static partial void MarkRevised(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point MarkRevised of lifecycle Document is not implemented yet.");

    // NoteRevision (action, with-change)
    public static partial void NoteRevision(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point NoteRevision of lifecycle Document is not implemented yet.");
}
