using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The approval rules (signoff-design.md section 6): MQ8201 on an approval's shape, MQ8202 on a named signature's and a stage's keys, MQ8203
/// on the stages' round order, and MQ8204 and MQ8205 on a signature's reminder and escalation, at a process's gates and a library's gate
/// policies, and at each use of a gate policy for the verbs only the using process can judge. The counting, actor and name rules that read a
/// gate's signatures stay with the process rules. Every finding needs a gate with stages or <c>allowSignerAcrossSignatures</c>, so a model
/// without either gives the same findings as before, and MQ8203 to MQ8205 and a stage's MQ8202 need a key of part 2.
/// </summary>
internal static class ApprovalRules
{
    /// <summary>Checks the approval rules of a document.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="element">The document's element.</param>
    /// <param name="report">The document's report.</param>
    public static void Check(ValidationContext context, Element element, Report report)
    {
        if (element is not Process process)
            return;
        if (process.Use == ProcessUse.Library)
        {
            for (var i = 0; i < process.Gates.Count; i++)
            {
                var gate = process.Gates[i];
                var name = $"gate '{gate.Name}' of library '{process.Name}'";
                var at = Ptr.At("/gates", i);
                CheckShape(gate, name, at, report);
                CheckRounds(gate, name, at, report);
                ForEachSignature(gate, at, (signature, pointer) =>
                {
                    CheckPolicySignature(signature, name, pointer, report);
                    CheckNotices(signature, name, pointer, report);
                });
                for (var j = 0; j < (gate.Stages?.Count ?? 0); j++)
                {
                    // A gate policy's stage takes no when, as its signatures take none (decision 33).
                    var stage = gate.Stages![j];
                    if (stage.When is not null)
                    {
                        report.Add("MQ8202", $"Stage '{stage.Name}' of {name} has a when, but a gate policy has no subject to read; remove the when, or put the gate on its transition.",
                            Ptr.At(at + "/stages", j) + "/when", stage.Id);
                    }
                }
            }

            return;
        }

        for (var i = 0; i < process.Transitions.Count; i++)
        {
            var t = process.Transitions[i];
            if (t.Gate is { } gate)
            {
                var name = $"gate '{gate.Name}' of process '{process.Name}'";
                var at = Ptr.At("/transitions", i) + "/gate";
                CheckShape(gate, name, at, report);
                CheckRounds(gate, name, at, report);
                ForEachSignature(gate, at, (signature, pointer) =>
                {
                    CheckSignature(context.Model, process, signature, name, pointer, report);
                    CheckNotices(signature, name, pointer, report);
                });
            }
            else if (t.GatePolicy is { } policy && ProcessLibraries.Owner(context.Model, policy, "gate") is { } library
                && library.Gates.FirstOrDefault(g => string.Equals(g.Id, policy, StringComparison.Ordinal)) is { UsesStages: true } used)
            {
                CheckPolicyUse(context.Model, process, library, used, t, Ptr.At("/transitions", i) + "/gatePolicy", report);
            }
        }
    }

    /// <summary>
    /// Whether a lifecycle uses an approval (signoff-design.md decision 29): one of its transitions carries a gate with stages, inline or
    /// through a gate policy.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process.</param>
    /// <returns>Whether it does.</returns>
    internal static bool UsesApproval(ModelSnapshot model, Process process) =>
        process.Use != ProcessUse.Library && process.Transitions.Any(t => GateOf(model, t) is { UsesStages: true });

    /// <summary>
    /// Whether a lifecycle uses delegation (signoff-design.md decision 20): one of its transitions carries a gate with stages, inline or through
    /// a gate policy, with a named signature that has <c>delegateTo</c>.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process.</param>
    /// <returns>Whether it does.</returns>
    internal static bool UsesDelegation(ModelSnapshot model, Process process) =>
        process.Use != ProcessUse.Library && process.Transitions.Any(t => GateOf(model, t) is { } gate && Delegable(gate));

    /// <summary>Whether a gate with stages has a named signature with <c>delegateTo</c> (signoff-design.md decision 20).</summary>
    /// <param name="gate">The gate.</param>
    /// <returns>Whether it has.</returns>
    internal static bool Delegable(ProcessGate gate) => gate.UsesStages && gate.AllSignatures.Any(static s => s.DelegateTo.Count > 0);

    /// <summary>
    /// The part 2 groups a lifecycle uses (signoff-design.md decision 32), on the gates of its transitions, inline or through a gate policy;
    /// <see cref="ApprovalGroups.None"/> for a library or a lifecycle that uses part 1 only.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process.</param>
    /// <returns>The groups.</returns>
    internal static ApprovalGroups GroupsOf(ModelSnapshot model, Process process) =>
        process.Use == ProcessUse.Library
            ? ApprovalGroups.None
            : process.Transitions.Select(t => GateOf(model, t)).OfType<ProcessGate>().Aggregate(ApprovalGroups.None, (groups, gate) => groups | ApprovalGroupMarks.Of(gate));

    /// <summary>The gate a transition carries: its own, else the gate policy it names; <see langword="null"/> for none or a policy no library holds.</summary>
    /// <param name="model">The model.</param>
    /// <param name="transition">The transition.</param>
    /// <returns>The gate.</returns>
    internal static ProcessGate? GateOf(ModelSnapshot model, ProcessTransition transition) =>
        transition.Gate ?? (transition.GatePolicy is { } policy && ProcessLibraries.Owner(model, policy, "gate") is { } library
            ? library.Gates.FirstOrDefault(g => string.Equals(g.Id, policy, StringComparison.Ordinal))
            : null);

    /// <summary>Calls <paramref name="visit"/> on each named signature of a gate with its pointer, stage by stage, in file order.</summary>
    internal static void ForEachSignature(ProcessGate gate, string gatePointer, Action<NamedSignature, string> visit)
    {
        if (gate.Stages is not { } stages)
            return;
        for (var j = 0; j < stages.Count; j++)
        {
            for (var k = 0; k < stages[j].Signatures.Count; k++)
                visit(stages[j].Signatures[k], Ptr.At(Ptr.At(gatePointer + "/stages", j) + "/signatures", k));
        }
    }

    // ---- MQ8201 ----

    private static void CheckShape(ProcessGate gate, string name, string at, Report report)
    {
        if (!gate.UsesStages)
        {
            if (gate.AllowSignerAcrossSignatures)
            {
                report.Add("MQ8201", $"{Upper(name)} has no stages, so it has one list of signers and allowSignerAcrossSignatures does nothing; remove it.",
                    at + "/allowSignerAcrossSignatures", gate.Id);
            }

            return;
        }

        if (gate.HasRequired)
            report.Add("MQ8201", $"{Upper(name)} has stages, so its named signatures carry the count; remove required from the gate.", at + "/required", gate.Id);
        if (gate.Signers.Count > 0)
            report.Add("MQ8201", $"{Upper(name)} has stages, so its named signatures name the signers; remove signers from the gate.", at + "/signers", gate.Id);
        if (gate.RequiredActors.Count > 0)
        {
            report.Add("MQ8201", $"{Upper(name)} has stages, so its named signatures name the required actors; remove requiredActors from the gate.",
                at + "/requiredActors", gate.Id);
        }

        if (gate.AllowRepeatSigner)
        {
            report.Add("MQ8201", $"{Upper(name)} has stages, so each named signature says whether one person may count twice; remove allowRepeatSigner from the gate.",
                at + "/allowRepeatSigner", gate.Id);
        }
    }

    // ---- MQ8203 ----

    // Stages are listed in round order (decision 5): each stage's round is at least the round of every stage before it.
    private static void CheckRounds(ProcessGate gate, string name, string at, Report report)
    {
        if (gate.Stages is not { Count: > 1 } stages)
            return;
        var highest = stages[0].Round;
        for (var j = 1; j < stages.Count; j++)
        {
            var stage = stages[j];
            if (stage.Round < highest)
            {
                report.Add("MQ8203", $"Stage '{stage.Name}' of {name} runs in round {Ptr.N(stage.Round)}, but a stage before it runs in round {Ptr.N(highest)}; "
                    + "list the stages in round order, so the file reads from top to bottom.", Ptr.At(at + "/stages", j) + "/round", stage.Id);
            }

            highest = Math.Max(highest, stage.Round);
        }
    }

    // ---- MQ8204 and MQ8205 ----

    // A signature's reminder and escalation (decisions 21 and 35): escalateTo and escalateAfter go together, each delay is a positive ISO 8601
    // duration read as an after transition's is, and an escalation falls due after the reminder.
    private static void CheckNotices(NamedSignature signature, string name, string at, Report report)
    {
        if (signature.EscalateTo.Count > 0 && signature.EscalateAfter is null)
        {
            report.Add("MQ8204", $"Signature '{signature.Name}' of {name} names escalateTo but no escalateAfter, so no escalation ever falls due; add escalateAfter, or remove escalateTo.",
                at + "/escalateTo", signature.Id);
        }

        if (signature.EscalateAfter is not null && signature.EscalateTo.Count == 0)
        {
            report.Add("MQ8204", $"Signature '{signature.Name}' of {name} has escalateAfter but no escalateTo, so an escalation would go to nobody; add escalateTo, or remove escalateAfter.",
                at + "/escalateAfter", signature.Id);
        }

        var remind = Delay(signature, signature.RemindAfter, "remindAfter", name, at, report);
        var escalate = Delay(signature, signature.EscalateAfter, "escalateAfter", name, at, report);
        if (remind is { } r && escalate is { } e && e <= r)
        {
            report.Add("MQ8205", $"Signature '{signature.Name}' of {name} escalates after {signature.EscalateAfter}, no later than its reminder after {signature.RemindAfter}, "
                + "so the reminder never comes first; lengthen escalateAfter, or shorten remindAfter.", at + "/escalateAfter", signature.Id);
        }
    }

    // A delay read as an after transition's is (MQ9008); null, after MQ8204, when it is not a positive duration.
    private static TimeSpan? Delay(NamedSignature signature, string? value, string key, string name, string at, Report report)
    {
        if (value is null)
            return null;
        if (ProcessRules.IsPositiveDuration(value) && StatechartModel.ParseDuration(value) is { } span && span > TimeSpan.Zero)
            return span;
        report.Add("MQ8204", $"Signature '{signature.Name}' of {name} has {key} '{value}', which is not a positive ISO 8601 duration; use a form such as P2D or PT30M.",
            at + "/" + key, signature.Id);
        return null;
    }

    // ---- MQ8202 ----

    // A signature of a gate on one of the process's own transitions: its notBy transitions are the process's, and its notBy verbs are ones
    // the process's runs see (decision 15).
    private static void CheckSignature(ModelSnapshot model, Process process, NamedSignature signature, string name, string at, Report report)
    {
        for (var k = 0; k < signature.NotBy.Count; k++)
        {
            var entry = signature.NotBy[k];
            var pointer = Ptr.At(at + "/notBy", k);
            if (entry.Transition is { } transition && !process.Transitions.Any(t => string.Equals(t.Id, transition, StringComparison.Ordinal)))
            {
                report.Add("MQ8202", $"Signature '{signature.Name}' of {name} names '{transition}' in notBy, which is not a transition of process '{process.Name}'; remove the entry.",
                    pointer + "/transition", signature.Id);
            }

            if (entry.Verb is { } verb && !Seen(model, process, verb))
            {
                report.Add("MQ8202", $"Signature '{signature.Name}' of {name} names verb '{verb}' in notBy, which the runs of process '{process.Name}' never see "
                    + "(only move verbs, signature verbs, complete and verbs its events name); remove the entry.", pointer + "/verb", signature.Id);
            }
        }
    }

    // A signature of a gate policy: no transition and no when, since a library belongs to no single lifecycle and has no subject; a built-in
    // verb no run sees. An own verb is judged at each use, against the using process's events.
    private static void CheckPolicySignature(NamedSignature signature, string name, string at, Report report)
    {
        if (signature.When is not null)
        {
            report.Add("MQ8202", $"Signature '{signature.Name}' of {name} has a when, but a gate policy has no subject to read; remove the when, or put the gate on its transition.",
                at + "/when", signature.Id);
        }

        for (var k = 0; k < signature.NotBy.Count; k++)
        {
            var entry = signature.NotBy[k];
            var pointer = Ptr.At(at + "/notBy", k);
            if (entry.Transition is not null)
            {
                report.Add("MQ8202", $"Signature '{signature.Name}' of {name} names a transition in notBy, but a gate policy belongs to no single lifecycle; name a verb, or remove the entry.",
                    pointer + "/transition", signature.Id);
            }

            if (entry.Verb is { } verb && BuiltInVerbs.IsBuiltIn(verb) && !SeenBuiltIn(verb))
            {
                report.Add("MQ8202", $"Signature '{signature.Name}' of {name} names verb '{verb}' in notBy, which no run sees (only move verbs, signature verbs, complete "
                    + "and verbs a using lifecycle's events name); remove the entry.", pointer + "/verb", signature.Id);
            }
        }
    }

    // A gate policy with stages used on a transition: each own verb in a signature's notBy must be one the using process's runs see.
    private static void CheckPolicyUse(ModelSnapshot model, Process process, Process library, ProcessGate gate, ProcessTransition t, string pointer, Report report)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var signature in gate.AllSignatures)
        {
            foreach (var verb in signature.NotBy.Select(e => e.Verb).OfType<string>())
            {
                if (BuiltInVerbs.IsBuiltIn(verb) || Seen(model, process, verb) || !reported.Add(signature.Id + "|" + verb))
                    continue;
                report.Add("MQ8202", $"Signature '{signature.Name}' of gate '{gate.Name}' of library '{library.Name}', used by process '{process.Name}', names verb '{verb}' "
                    + "in notBy, which no event of the process names, so its runs never see it; remove the entry, or name the verb on an event.", pointer, t.Id);
            }
        }
    }

    // Whether a process's runs see a verb (decision 15): a move verb, a signature verb, complete, or a verb its events or transitions name. A
    // verb that is neither built in nor the project's own is MQ9701's, not this rule's.
    private static bool Seen(ModelSnapshot model, Process process, string verb) =>
        (BuiltInVerbs.IsBuiltIn(verb) ? SeenBuiltIn(verb) : model.OwnVerb(verb) is null)
        || process.Events.Any(e => e.Verb == verb) || process.Transitions.Any(t => t.Verb == verb);

    private static bool SeenBuiltIn(string verb) => BuiltInVerbs.Get(verb)?.Group is "move" or "signature" || verb == "complete";

    private static string Upper(string text) => char.ToUpperInvariant(text[0]) + text[1..];
}
