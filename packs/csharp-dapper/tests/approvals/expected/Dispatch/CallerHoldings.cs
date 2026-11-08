using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using App.Model.Runtime;

namespace App.Model.Dispatch;

// What a caller holds with no context, besides the actor it acts as: the roles, groups and organizations the host's principal holds.
// Maquettiste wrote this file once and never touches it again. Each generated command handler asks it once per command; it is never
// read from the request body or the command, so a caller can't give itself roles.
//
// As written it returns none. The caller is then checked on rights only as the actor it acts as: a deny on a role it holds but isn't
// acting as doesn't reach it, and neither does an allow. Answer from the host's principal, as Pipeline.Authorize does.
public sealed class CallerHoldings : ICallerHoldings
{
    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> HeldAsync(ProcessEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return ValueTask.FromResult<IReadOnlyList<string>>([]);
    }
}
