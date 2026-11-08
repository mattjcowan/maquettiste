using System;
using App.Model.Runtime;

namespace App.Model.Docs;

// The hand-written half of DocumentHandlers. Maquettiste wrote this file once and never touches it again. Implement here the
// guards and actions whose expression the generated half could not translate (or that have none); a guard or action added to
// the model later is declared in DocumentHandlers.g.cs and fails the build until you add it here.
public sealed partial class DocumentHandlers
{
    // The entity of the instance's object: read it from your system, for example typeof(Memo) for a memo. A named trigger point that
    // names an entity derived from the subject's tests it on this. It throws until you write it.
    public partial Type EntityOf(StatechartCall<DocumentContext> call) =>
        throw new NotImplementedException("The entity of the object of process Document is not read yet.");
}
