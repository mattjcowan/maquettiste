using System;

namespace App.Model.Docs;

// The hand-written half of RightConditions. Maquettiste wrote this file once and never touches it again. A right holds only when its
// condition is true; read the object for the request (its Subject, States and Envelope say which).
public static partial class RightConditions
{
    // SecretOnlyOwner: subject.classification = 'secret'
    public static partial bool SecretOnlyOwner(global::App.Model.Processes.RightRequest request) =>
        throw new NotImplementedException("The condition of named right SecretOnlyOwner is not implemented yet.");
}
