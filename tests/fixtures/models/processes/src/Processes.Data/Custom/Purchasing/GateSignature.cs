using System;
using Processes.Data.Runtime;

namespace Processes.Data.Purchasing;

// The hand-written half of GateSignature. Maquettiste wrote this file once and never touches it again:
// add members, attributes and interfaces here. The generated half is GateSignature.g.cs.
public partial class GateSignature
{
}

/// <summary>GateSignature rows of gate audit records, shared by the process stores.</summary>
public static class GateSignatures
{
    /// <summary>The row of an audit record (a discarded record has no signer or actor: stored as "").</summary>
    /// <param name="record">The record.</param>
    /// <returns>The row.</returns>
    public static GateSignature From(GateAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new GateSignature
        {
            Instance = record.Instance, Gate = record.Gate, Sequence = record.Sequence, Signer = record.Signer ?? "", Actor = record.Actor ?? "",
            Meaning = record.Meaning, Reason = record.Reason, Outcome = record.OutcomeText, At = record.At,
        };
    }
}
