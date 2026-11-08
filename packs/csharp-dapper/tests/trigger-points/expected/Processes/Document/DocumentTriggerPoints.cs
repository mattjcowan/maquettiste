using System;
using System.Threading.Tasks;
using App.Model.Processes;

namespace App.Model.Docs;

// The hand-written half of lifecycle Document's trigger points. Maquettiste wrote this file once and never touches it again. Generated
// code can't read the object yet: load it from call.Key or call.Instance, or read call.Old and call.New when the caller gives them. A
// before action fills call.Proposed and writes nothing; its records method writes those values once every check passes.
public static partial class DocumentTriggerPoints
{
    // AssignNumber (action, before)
    public static partial void AssignNumber(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point AssignNumber of lifecycle Document is not implemented yet.");

    // TitleBeforeReview (check): coalesce(subject.title, '') <> ''
    public static partial bool TitleBeforeReview(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point TitleBeforeReview of lifecycle Document is not implemented yet.");

    // X (check): coalesce(old.price, 0) = 0 or subject.price >= old.price * 0.9
    public static partial bool X(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point X of lifecycle Document is not implemented yet.");

    // XWhen (action, with-change)
    public static partial void XWhen(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point XWhen of lifecycle Document is not implemented yet.");

    // CheckModify (check)
    public static partial bool CheckModify(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point CheckModify of lifecycle Document is not implemented yet.");

    // StampRelease (action, with-change)
    public static partial void StampRelease(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point StampRelease of lifecycle Document is not implemented yet.");

    // PublishToPortal (action, after-commit)
    public static partial Task PublishToPortal(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point PublishToPortal of lifecycle Document is not implemented yet.");

    // Archive (replace)
    public static partial void Archive(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point Archive of lifecycle Document is not implemented yet.");

    // DocumentTriggerPoints (check)
    public static partial bool DocumentTriggerPointsValue(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point DocumentTriggerPoints of lifecycle Document is not implemented yet.");

    // AskRegistry (check)
    public static partial bool AskRegistry(TriggerPointCall call) =>
        throw new NotImplementedException("Trigger point AskRegistry of lifecycle Document is not implemented yet.");
}

public static partial class DocumentTriggerConditions
{
    // X runs when: subject.price > 0
    public static partial bool X(TriggerPointCall call) =>
        throw new NotImplementedException("The when of trigger point X of lifecycle Document is not implemented yet.");

    // Archive runs when: coalesce(subject.title, '') <> ''
    public static partial bool Archive(TriggerPointCall call) =>
        throw new NotImplementedException("The when of trigger point Archive of lifecycle Document is not implemented yet.");
}

public static partial class DocumentTriggerRecords
{
    // Writes what AssignNumber filled in call.Proposed.
    public static partial void AssignNumber(TriggerPointCall call) =>
        throw new NotImplementedException("The records of trigger point AssignNumber of lifecycle Document are not written yet.");
}
