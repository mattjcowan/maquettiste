using System;
using App.Model.Runtime;

namespace App.Model.Docs;

// The hand-written half of RevisingHandlers. Maquettiste wrote this file once and never touches it again. Implement here the
// guards and actions whose expression the generated half could not translate (or that have none); a guard or action added to
// the model later is declared in RevisingHandlers.g.cs and fails the build until you add it here.
public sealed partial class RevisingHandlers
{
    // The object's active state paths in lifecycle Document, which could say no to, or whose trigger points run on, a verb an event names: read them from your
    // system for the instance's object. None means the initial state; null means Document doesn't govern the object, so it isn't asked
    // (a process with several subjects). It throws until you write it.
    private partial global::System.Collections.Generic.IReadOnlyList<string>? StatesInDocument(StatechartCall<RevisingContext> call) =>
        throw new NotImplementedException("The states of the object in lifecycle Document are not read yet.");

    // The entity of the instance's object: read it from your system, for example typeof(Memo) for a memo. A named trigger point that
    // names an entity derived from the subject's tests it on this. It throws until you write it.
    public partial Type EntityOf(StatechartCall<RevisingContext> call) =>
        throw new NotImplementedException("The entity of the object of process Revising is not read yet.");
}
