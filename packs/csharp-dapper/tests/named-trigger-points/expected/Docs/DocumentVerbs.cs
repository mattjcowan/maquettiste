using System;
using System.Collections.Generic;
using App.Model.Processes;

namespace App.Model.Docs;

// The hand-written half of the Document per-type runner. Maquettiste wrote this file once and never touches it again. When a call
// doesn't give the object's states in a lifecycle, the runner asks here; null means the lifecycle doesn't govern the object.
public sealed partial class DocumentVerbs
{
    private static partial IReadOnlyList<string>? StatesInDocument(TriggerPointCall call) =>
        throw new NotImplementedException("The states of the object in lifecycle Document are not read yet.");
}
