using System;
using App.Model.Runtime;

namespace App.Model.Docs;

// The hand-written half of RecordsHandlers. Maquettiste wrote this file once and never touches it again. Implement here the
// guards and actions whose expression the generated half could not translate (or that have none); a guard or action added to
// the model later is declared in RecordsHandlers.g.cs and fails the build until you add it here.
public sealed partial class RecordsHandlers
{
    // Whether the caller is DocumentOwner, the document's owner: read the object for the instance
    // and compare it with the caller (the envelope's Signer for an event, or the host's principal). It refuses until you write it.
    private partial bool IsDocumentOwner(ProcessSnapshot<RecordsContext> snapshot, ProcessEnvelope envelope) => false;
}

// The conditions of the rights of lifecycle Records. A right holds only when its condition is true; read the object for the request.
public static partial class RecordsRightConditions
{
    // HoldOnLegal: subject.legalHold = true
    public static partial bool HoldOnLegal(global::App.Model.Processes.RightRequest request) =>
        throw new NotImplementedException("The condition of right HoldOnLegal of lifecycle Records is not implemented yet.");
}
