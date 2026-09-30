using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Processes.Data.Dispatch;

// The hand-written half of the pipeline. Maquettiste wrote this file once and never touches it again: order the behaviours,
// add the project's own, and implement the policy hooks the generated Behaviours.g.cs declares.
public sealed partial class Pipeline
{
    // The default order: validation, authorization, logging, transaction, outbox (outermost first).
    private partial IReadOnlyList<IPipelineBehaviour> Order() =>
    [
        new ValidationBehaviour(this),
        new AuthorizationBehaviour(this),
        new LoggingBehaviour(this),
        new TransactionBehaviour(this),
        new OutboxBehaviour(this),
    ];

    // Whether the caller may act as the envelope's actor. The fixture has no principals: a caller acts as any actor it names,
    // and the generated authorization behaviour still checks that actor against the event's actors.
    private partial bool Authorize(CommandContext context) => true;

    // The unit of work around the handler, the store and the outbox; none by default.
    private partial Task<IProcessTransaction> BeginTransactionAsync(CommandContext context, CancellationToken cancellationToken) =>
        Task.FromResult<IProcessTransaction>(NoProcessTransaction.Instance);
}
