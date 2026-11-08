using System;
using App.Model.Processes;

namespace App.Model.Dispatch;

// Whether a bulk load may skip the trigger points not marked noBypass, and where each skip is logged. Maquettiste wrote this file once
// and never touches it again. Who may bypass is your system's permission, as who is signed in is: answer from your own data. Each
// per-type runner asks it when a call sets SkipOptional, and logs each point it skipped here. Replaces and points marked noBypass always
// run. Delete this file if you turn the pack's bulkLoadBypass parameter off: it implements an interface that is then no longer written.
//
// As written it refuses: no trigger point is skipped, and nothing is logged, until you write it.
public sealed class TriggerBypass : ITriggerBypass
{
    /// <inheritdoc/>
    public bool Allows(TriggerPointCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return false;
    }

    /// <inheritdoc/>
    public void Skipped(TriggerPointCall call, string triggerPoint)
    {
        ArgumentNullException.ThrowIfNull(call);
    }
}
