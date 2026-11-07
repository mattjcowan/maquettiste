using System;
using App.Model.Runtime;

namespace App.Model.Docs;

// The hand-written half of DocumentLifecycleHandlers. Maquettiste wrote this file once and never touches it again. Implement here the
// guards and actions whose expression the generated half could not translate (or that have none); a guard or action added to
// the model later is declared in DocumentLifecycleHandlers.g.cs and fails the build until you add it here.
public sealed partial class DocumentLifecycleHandlers
{
    // Whether the caller is DocumentOwner, the document's owner: read the object for the instance
    // and compare it with the caller (the envelope's Signer for an event, or the host's principal). It refuses until you write it.
    private partial bool IsDocumentOwner(ProcessSnapshot<DocumentLifecycleContext> snapshot, ProcessEnvelope envelope) => false;

    // The object's active state paths in lifecycle Records, which could say no to a verb an event names: read them from your
    // system for the instance's object. None means the initial state; null means Records doesn't govern the object, so it isn't asked
    // (a process with several subjects). It throws until you write it.
    private partial global::System.Collections.Generic.IReadOnlyList<string>? StatesInRecords(StatechartCall<DocumentLifecycleContext> call) =>
        throw new NotImplementedException("The states of the object in lifecycle Records are not read yet.");
}
