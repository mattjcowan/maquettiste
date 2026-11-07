using System;
using App.Model.Processes;

namespace App.Model.Dispatch;

// Whether an object, or one of its links, is frozen. Maquettiste wrote this file once and never touches it again. Where "frozen" is
// held is your system's business: answer from your own data. Each generated command handler, and each rights check, asks it when a
// check needs it, and may ask more than once for one command.
//
// As written it answers "not frozen": nothing is refused for freezing, and no right with whileFrozen holds, until you write it.
public sealed class FrozenState : IFrozenState
{
    /// <inheritdoc/>
    public bool IsFrozen(FrozenQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return false;
    }
}
