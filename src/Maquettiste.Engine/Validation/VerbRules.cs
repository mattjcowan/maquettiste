using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The verb rules (verbs-design.md section 5): MQ9702, MQ9703 and MQ9706 on the verb vocabulary; MQ9701, MQ9704 and MQ9705 at each event,
/// transition and operation that names a verb. Every rule needs a verb vocabulary or a <c>verb</c> key, so a model without either gives the
/// same findings as before. MQ9707 compares a save with the stored model (<see cref="SaveRules"/>).
/// </summary>
internal static class VerbRules
{
    /// <summary>Checks the verb rules of a document.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="element">The document's element.</param>
    /// <param name="report">The document's report.</param>
    public static void Check(ValidationContext context, Element element, Report report)
    {
        switch (element)
        {
            case VerbVocabulary vocabulary:
                CheckVocabulary(vocabulary, report);
                break;
            case Process process:
                for (var i = 0; i < process.Events.Count; i++)
                {
                    if (process.Events[i] is { Verb: { } verb } e)
                        CheckUse(context.Model, verb, Ptr.At("/events", i) + "/verb", e.Id, $"Event '{e.Name}'", report);
                }

                for (var i = 0; i < process.Transitions.Count; i++)
                {
                    var t = process.Transitions[i];
                    if (t.Verb is not { } verb)
                        continue;
                    var pointer = Ptr.At("/transitions", i) + "/verb";
                    if (t.Trigger == TransitionTrigger.Event)
                    {
                        // Rights are checked on the event before a guard picks a transition, so the verb comes from the event alone (decision 12).
                        var eventName = t.Event is { } eventId ? process.Events.FirstOrDefault(e => e.Id == eventId) is { } named ? $" '{named.Name}'" : "" : "";
                        report.Add("MQ9705", $"This transition is triggered by event{eventName} and names verb '{verb}' of its own; it takes its event's verb. Move the verb to the event, or clear it.",
                            pointer, t.Id);
                        continue;
                    }

                    CheckUse(context.Model, verb, pointer, t.Id, "This transition", report);
                }

                break;
            case Operation { Verb: { } operationVerb } operation:
                CheckUse(context.Model, operationVerb, "/verb", operation.Id, $"Operation '{operation.Name}'", report);
                break;
        }
    }

    /// <summary>MQ9701 for a key neither built in nor the project's own; MQ9704 for a hidden built-in verb.</summary>
    private static void CheckUse(ModelSnapshot model, string key, string pointer, string id, string who, Report report)
    {
        if (BuiltInVerbs.IsBuiltIn(key))
        {
            if (model.Verbs is { } vocabulary && vocabulary.Hidden.Contains(key, StringComparer.Ordinal))
                report.Add("MQ9704", $"{who} names verb '{key}', which the project hides; show the verb again, or name another verb.", pointer, id);
            return;
        }

        if (model.OwnVerb(key) is null)
            report.Add("MQ9701", $"{who} names verb '{key}', which is neither a built-in verb nor one of the project's own; add it to the project's verbs, or clear it.", pointer, id);
    }

    private static void CheckVocabulary(VerbVocabulary vocabulary, Report report)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        // Keys without their hyphens: x-1 and x1 would get one name in generated code (one enum member twice).
        var compact = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < vocabulary.Verbs.Count; i++)
        {
            var verb = vocabulary.Verbs[i];
            var pointer = Ptr.At("/verbs", i) + "/key";
            if (BuiltInVerbs.IsBuiltIn(verb.Key))
            {
                report.Add("MQ9702", $"Own verb '{verb.Key}' has the key of a built-in verb; use the built-in verb (rename-verb to '{verb.Key}' adopts it), or give this verb another key.",
                    pointer, verb.Id);
            }
            else if (!seen.TryAdd(verb.Key, i))
            {
                report.Add("MQ9702", $"Own verb '{verb.Key}' is already declared at index {Ptr.N(seen[verb.Key])}; merge the two (rename-verb), or give this verb another key.",
                    pointer, verb.Id);
            }
            else if (!compact.TryAdd(BuiltInVerbs.Compact(verb.Key), i))
            {
                var first = vocabulary.Verbs[compact[BuiltInVerbs.Compact(verb.Key)]];
                report.Add("MQ9702", $"Own verbs '{first.Key}' and '{verb.Key}' are one key once hyphens are dropped, so generated code would give them one name; merge the two (rename-verb), or give this verb another key.",
                    pointer, verb.Id);
            }
            else if (BuiltInVerbs.LookAlike(verb.Key) is { } builtIn)
            {
                report.Add("MQ9706", $"Own verb '{verb.Key}' is built-in verb '{builtIn.Key}' without its hyphens; use the built-in verb (rename-verb to '{builtIn.Key}').",
                    pointer, verb.Id);
            }
        }

        for (var i = 0; i < vocabulary.Hidden.Count; i++)
        {
            if (!BuiltInVerbs.IsBuiltIn(vocabulary.Hidden[i]))
                report.Add("MQ9703", $"'{vocabulary.Hidden[i]}' is not a built-in verb, so it cannot be hidden; remove the entry.", Ptr.At("/hidden", i));
        }
    }
}
