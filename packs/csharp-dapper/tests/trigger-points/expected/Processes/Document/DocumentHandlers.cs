using System;
using App.Model.Runtime;

namespace App.Model.Docs;

// The hand-written half of DocumentHandlers. Maquettiste wrote this file once and never touches it again. Implement here the
// guards and actions whose expression the generated half could not translate (or that have none); a guard or action added to
// the model later is declared in DocumentHandlers.g.cs and fails the build until you add it here.
public sealed partial class DocumentHandlers
{
    // The object's active state paths in lifecycle Legal, which could say no to, or whose trigger points run on, a verb an event names: read them from your
    // system for the instance's object. None means the initial state; null means Legal doesn't govern the object, so it isn't asked
    // (a process with several subjects). It throws until you write it.
    private partial global::System.Collections.Generic.IReadOnlyList<string>? StatesInLegal(StatechartCall<DocumentContext> call) =>
        throw new NotImplementedException("The states of the object in lifecycle Legal are not read yet.");
}
