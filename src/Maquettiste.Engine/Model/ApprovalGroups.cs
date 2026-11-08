namespace Maquettiste.Engine.Model;

/// <summary>
/// The part 2 groups an approval may use (signoff-design.md decision 32). Generated code writes a group's members only when some lifecycle
/// uses it, and the approvals naming key marks each lifecycle with the groups it uses, so a project that uses part 1 only keeps its bytes.
/// </summary>
[Flags]
internal enum ApprovalGroups
{
    /// <summary>None: the approval uses part 1 only.</summary>
    None = 0,

    /// <summary>A gate with more than one stage, or a stage with a <c>when</c>.</summary>
    Rounds = 1,

    /// <summary>A named signature with <c>delegateTo</c>.</summary>
    Delegation = 2,

    /// <summary>A named signature with <c>remindAfter</c> or <c>escalateAfter</c>.</summary>
    Notices = 4,

    /// <summary>A named signature with <c>reauthenticate</c>.</summary>
    Password = 8,
}

/// <summary>Which of the part 2 groups a gate uses, and the marks the approvals naming key hashes for them (signoff-design.md decision 32).</summary>
internal static class ApprovalGroupMarks
{
    /// <summary>The groups a gate with stages uses; <see cref="ApprovalGroups.None"/> for a gate without stages.</summary>
    /// <param name="gate">The gate or gate policy.</param>
    /// <returns>The groups.</returns>
    internal static ApprovalGroups Of(ProcessGate gate)
    {
        if (gate.Stages is not { Count: > 0 } stages)
            return ApprovalGroups.None;
        var groups = stages.Count > 1 || stages.Any(static s => s.When is not null) ? ApprovalGroups.Rounds : ApprovalGroups.None;
        foreach (var signature in gate.AllSignatures)
        {
            if (signature.DelegateTo.Count > 0)
                groups |= ApprovalGroups.Delegation;
            if (signature.RemindAfter is not null || signature.EscalateAfter is not null)
                groups |= ApprovalGroups.Notices;
            if (signature.Reauthenticate)
                groups |= ApprovalGroups.Password;
        }

        return groups;
    }

    /// <summary>The marks of the groups, in a fixed order: none for <see cref="ApprovalGroups.None"/>.</summary>
    /// <param name="groups">The groups.</param>
    /// <returns>The marks.</returns>
    internal static IEnumerable<string> Marks(ApprovalGroups groups)
    {
        if (groups.HasFlag(ApprovalGroups.Rounds))
            yield return "group:rounds";
        if (groups.HasFlag(ApprovalGroups.Delegation))
            yield return "group:delegation";
        if (groups.HasFlag(ApprovalGroups.Notices))
            yield return "group:notices";
        if (groups.HasFlag(ApprovalGroups.Password))
            yield return "group:password";
    }
}
