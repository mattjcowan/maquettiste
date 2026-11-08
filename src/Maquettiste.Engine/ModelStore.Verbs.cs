using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine;

/// <summary>
/// The verb operations of a batch (verbs-design.md decision 9): <c>rename-verb</c> renames the project's own verb and rewrites every use, or
/// merges it into another own verb or a built-in verb; <c>remove-verb</c> removes it and clears every use. Each expands into updates of the
/// verb vocabulary and of the processes, operations and named rights that name the verb, all or nothing, as <c>retag</c> does. A verb that
/// goes takes its translations with it, since the save removes its sub-element. A right whose last verb is removed goes (rights-design.md
/// section 8): an inline right leaves its lifecycle's rights, and a named right is deleted with its uses. A trigger point whose last verb is
/// removed leaves its lifecycle's trigger points (trigger-points-design.md section 9), and the scenario steps that assume its outcome or
/// expect it lose those entries. A named signature's <c>notBy</c> entry for the verb is renamed or dropped (signoff-design.md section 9). A
/// step's <c>response</c> names a built-in verb, which neither operation renames or removes, so it stays as it is.
/// </summary>
public sealed partial class ModelStore
{
    /// <summary>Whether an operation is a verb operation (<c>rename-verb</c>, <c>remove-verb</c>).</summary>
    /// <param name="op">The operation kind.</param>
    public static bool IsVerbOperation(BatchOp op) => op is BatchOp.RenameVerb or BatchOp.RemoveVerb;

    /// <summary>
    /// Plans a verb operation: adds the updates to <paramref name="changes"/>; returns why it is refused, or <see langword="null"/>. Uses are
    /// found by key in every event, transition, operation, right, trigger point, named signature's notBy and scenario verb step. <c>rename-verb</c> and <c>remove-verb</c> of an own verb whose key a built-in
    /// verb has (MQ9702) leave the uses, which name the built-in verb: they write only the vocabulary.
    /// </summary>
    private string? VerbOperation(ModelSnapshot snapshot, BatchOperation o, List<PlannedChange> changes)
    {
        var opName = o.Op == BatchOp.RenameVerb ? "rename-verb" : "remove-verb";
        if (o.Verb is not { Length: > 0 } key)
            return $"{opName} names the project's own verb by its key (verb).";
        var vocabulary = snapshot.Verbs;
        var index = -1;
        for (var i = 0; vocabulary is not null && i < vocabulary.Verbs.Count; i++)
        {
            if (string.Equals(vocabulary.Verbs[i].Key, key, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (vocabulary is null || index < 0 || snapshot.GetDocument(vocabulary.Id) is not { } vocabularyDocument)
        {
            return BuiltInVerbs.IsBuiltIn(key)
                ? $"'{key}' is a built-in verb, which cannot be renamed or removed; hide it in the verb vocabulary instead."
                : $"No own verb has the key '{key}'.";
        }

        string? to = null;
        var merge = true;
        if (o.Op == BatchOp.RenameVerb)
        {
            to = o.To;
            if (!BuiltInVerbs.IsKey(to))
                return $"'{to}' is not a verb key: rename-verb needs the new key (to), in kebab-case.";
            if (string.Equals(to, key, StringComparison.Ordinal) && !BuiltInVerbs.IsBuiltIn(key))
                return $"Own verb '{key}' already has the key '{to}'.";
            // To a built-in key (adopting it when it is the verb's own) or another own verb's, the two merge and the definition goes.
            var target = to!;
            merge = BuiltInVerbs.IsBuiltIn(target) || vocabulary.Verbs.Where((v, i) => i != index).Any(v => string.Equals(v.Key, target, StringComparison.Ordinal));
        }

        var touched = new Dictionary<string, (string Hash, JsonObject Node)>(StringComparer.Ordinal);
        JsonObject Working(ElementDocument document)
        {
            if (!touched.TryGetValue(document.Element.Id, out var working))
                touched[document.Element.Id] = working = (document.Hash, (JsonObject)JsonNode.Parse(document.Json.GetRawText())!);
            return working.Node;
        }

        void Rewrite(JsonObject holder)
        {
            if (to is null)
                holder.Remove("verb");
            else
                holder["verb"] = to;
        }

        // A right's verbs (rights-design.md section 8): the key is renamed in place, or dropped when the list already names the new key; on
        // a remove it is dropped. Whether the list keeps a verb.
        bool RewriteList(JsonObject holder)
        {
            if (holder["verbs"] is not JsonArray verbs)
                return true;
            for (var i = verbs.Count - 1; i >= 0; i--)
            {
                if (verbs[i] is not JsonValue value || !value.TryGetValue<string>(out var named) || !string.Equals(named, key, StringComparison.Ordinal))
                    continue;
                if (to is null || verbs.Any(v => v is JsonValue other && other.TryGetValue<string>(out var listed) && string.Equals(listed, to, StringComparison.Ordinal)))
                    verbs.RemoveAt(i);
                else
                    verbs[i] = to;
            }

            return verbs.Count > 0;
        }

        // Whether a gate's named signatures name the verb in a notBy entry.
        bool NotByNames(ProcessGate? gate) =>
            gate is not null && gate.AllSignatures.Any(s => s.NotBy.Any(e => string.Equals(e.Verb, key, StringComparison.Ordinal)));

        // A named signature's notBy (signoff-design.md section 9): a verb entry is renamed in place, or dropped when the list already names the
        // new key; on a remove it is dropped. A list left empty goes, as an empty notBy excludes nobody. Transition entries stay.
        void RewriteNotBy(JsonObject gate)
        {
            foreach (var signature in (gate["stages"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(s => (s["signatures"] as JsonArray ?? []).OfType<JsonObject>()))
            {
                if (signature["notBy"] is not JsonArray entries)
                    continue;
                for (var i = entries.Count - 1; i >= 0; i--)
                {
                    if (entries[i] is not JsonObject entry || !NamesVerb(entry, key))
                        continue;
                    if (to is not { } renamed || entries.Any(e => e is JsonObject other && NamesVerb(other, renamed)))
                        entries.RemoveAt(i);
                    else
                        entry["verb"] = renamed;
                }

                if (entries.Count == 0)
                    signature.Remove("notBy");
            }
        }

        static bool NamesVerb(JsonObject entry, string verb) =>
            entry["verb"] is JsonValue value && value.TryGetValue<string>(out var named) && string.Equals(named, verb, StringComparison.Ordinal);

        // An own verb that shadows a built-in key (MQ9702) has no uses: they name the built-in verb, so a rename or a remove leaves them, and
        // adopting the built-in (to the same key) writes only the vocabulary. A write that would change nothing is skipped.
        var rewriteUses = !BuiltInVerbs.IsBuiltIn(key) && !string.Equals(to, key, StringComparison.Ordinal);
        // The named rights and named trigger points a remove leaves with no verb: each is deleted, and a named right's uses leave their
        // lifecycles' rights.
        var goneRights = new SortedSet<string>(StringComparer.Ordinal);
        if (rewriteUses && to is null)
        {
            foreach (var named in snapshot.All<Right>())
            {
                if (named.Verbs.Count > 0 && named.Verbs.All(v => string.Equals(v, key, StringComparison.Ordinal)))
                    goneRights.Add(named.Id);
            }
        }
        // The trigger points a remove leaves with no verb: each leaves its lifecycle, and its word entries in assume and its ids in the
        // expected check warnings and trigger points go with it.
        var gonePoints = new HashSet<string>(StringComparer.Ordinal);
        if (rewriteUses && to is null)
        {
            foreach (var process in snapshot.All<Process>())
            {
                foreach (var point in process.TriggerPoints)
                {
                    if (point.Verbs.Count > 0 && point.Verbs.All(v => string.Equals(v, key, StringComparison.Ordinal)))
                        gonePoints.Add(point.Id);
                }
            }

            // A named trigger point left with no verb is deleted (trigger-points-design.md section 9, part 2).
            foreach (var named in snapshot.All<TriggerPoint>())
            {
                if (named.Verbs.Count > 0 && named.Verbs.All(v => string.Equals(v, key, StringComparison.Ordinal)))
                {
                    gonePoints.Add(named.Id);
                    goneRights.Add(named.Id);
                }
            }
        }

        foreach (var document in snapshot.Documents)
        {
            if (!rewriteUses || !ReferenceEquals(snapshot.GetDocument(document.Element.Id), document))
                continue;
            switch (document.Element)
            {
                case Process process:
                    for (var i = 0; i < process.Events.Count; i++)
                    {
                        if (string.Equals(process.Events[i].Verb, key, StringComparison.Ordinal) && Working(document)["events"]?[i] is JsonObject e)
                            Rewrite(e);
                    }

                    for (var i = 0; i < process.Transitions.Count; i++)
                    {
                        if (string.Equals(process.Transitions[i].Verb, key, StringComparison.Ordinal) && Working(document)["transitions"]?[i] is JsonObject t)
                            Rewrite(t);
                    }

                    // From the last entry, so a removed one leaves the indexes of those before it.
                    for (var i = process.Rights.Count - 1; i >= 0; i--)
                    {
                        var entry = process.Rights[i];
                        var useGoes = entry.Right is { } used && goneRights.Contains(used);
                        if (!useGoes && !entry.Verbs.Contains(key, StringComparer.Ordinal))
                            continue;
                        if (Working(document)["rights"] is JsonArray rights && rights[i] is JsonObject r && (useGoes || !RewriteList(r)))
                            rights.RemoveAt(i);
                    }

                    // A trigger point's verbs, as a right's: one left with no verb goes.
                    for (var i = process.TriggerPoints.Count - 1; i >= 0; i--)
                    {
                        if (!process.TriggerPoints[i].Verbs.Contains(key, StringComparer.Ordinal))
                            continue;
                        if (Working(document)["triggerPoints"] is JsonArray points && points[i] is JsonObject point && !RewriteList(point))
                            points.RemoveAt(i);
                    }

                    // A named signature's notBy, on an inline gate or a gate policy (signoff-design.md section 9).
                    for (var i = 0; i < process.Transitions.Count; i++)
                    {
                        if (NotByNames(process.Transitions[i].Gate) && Working(document)["transitions"]?[i]?["gate"] is JsonObject gate)
                            RewriteNotBy(gate);
                    }

                    for (var i = 0; i < process.Gates.Count; i++)
                    {
                        if (NotByNames(process.Gates[i]) && Working(document)["gates"]?[i] is JsonObject policy)
                            RewriteNotBy(policy);
                    }

                    break;
                case Operation operation when string.Equals(operation.Verb, key, StringComparison.Ordinal):
                    Rewrite(Working(document));
                    break;
                case Right named when !goneRights.Contains(named.Id) && named.Verbs.Contains(key, StringComparer.Ordinal):
                    RewriteList(Working(document));
                    break;
                case TriggerPoint namedPoint when !goneRights.Contains(namedPoint.Id) && namedPoint.Verbs.Contains(key, StringComparer.Ordinal):
                    RewriteList(Working(document));
                    break;
                case Scenario scenario:
                    // First, while the steps keep their places: a step's entries for a trigger point that goes.
                    if (gonePoints.Count > 0)
                        ForgetPoints(document, scenario);
                    // A verb step (rights-design.md section 8): renamed in place; on a remove the step goes, since a verb step names one verb.
                    for (var i = scenario.Steps.Count - 1; i >= 0; i--)
                    {
                        if (scenario.Steps[i].Input != StepInput.Verb || !string.Equals(scenario.Steps[i].Verb, key, StringComparison.Ordinal)
                            || Working(document)["steps"] is not JsonArray steps || steps[i] is not JsonObject step)
                            continue;
                        if (to is null)
                            steps.RemoveAt(i);
                        else
                            step["verb"] = to;
                    }

                    break;
            }
        }

        // A step's word entries for a trigger point that goes, and its id in the expected check warnings and trigger points.
        void ForgetPoints(ElementDocument document, Scenario scenario)
        {
            for (var i = 0; i < scenario.Steps.Count; i++)
            {
                var step = scenario.Steps[i];
                var named = step.Assume.Keys.Any(gonePoints.Contains)
                    || (step.Expect?.CheckWarnings?.Any(gonePoints.Contains) ?? false) || (step.Expect?.TriggerPoints?.Any(gonePoints.Contains) ?? false);
                if (!named || Working(document)["steps"] is not JsonArray steps || i >= steps.Count || steps[i] is not JsonObject node)
                    continue;
                if (node["assume"] is JsonObject assume)
                {
                    foreach (var id in assume.Select(p => p.Key).Where(gonePoints.Contains).ToList())
                        assume.Remove(id);
                }

                if (node["expect"] is JsonObject expect)
                {
                    foreach (var list in new[] { "checkWarnings", "triggerPoints" })
                    {
                        if (expect[list] is not JsonArray ids)
                            continue;
                        for (var j = ids.Count - 1; j >= 0; j--)
                        {
                            if (ids[j] is JsonValue value && value.TryGetValue<string>(out var id) && gonePoints.Contains(id))
                                ids.RemoveAt(j);
                        }
                    }
                }
            }
        }

        var verbs = Working(vocabularyDocument)["verbs"] as JsonArray;
        if (verbs?[index] is not JsonObject definition)
            return $"The verb vocabulary does not hold own verb '{key}' where it was read.";
        if (merge)
            verbs.RemoveAt(index);
        else
            definition["key"] = to;

        foreach (var (id, (hash, node)) in touched.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!TryParseRequest(Encoding.UTF8.GetBytes(node.ToJsonString()), id, out var parsed, out var failure))
                return failure.Diagnostics.FirstOrDefault()?.Message ?? $"The rewritten '{id}' is not valid.";
            var expected = o.ExpectedHashes is { } read ? read.GetValueOrDefault(id) ?? "unread" : hash;
            changes.Add(new PlannedChange(BatchOp.Update, id, expected, parsed, DeleteResolution.Refuse));
        }

        foreach (var id in goneRights)
        {
            var hash = snapshot.GetDocument(id)!.Hash;
            var expected = o.ExpectedHashes is { } read ? read.GetValueOrDefault(id) ?? "unread" : hash;
            changes.Add(new PlannedChange(BatchOp.Delete, id, expected, null, DeleteResolution.Refuse));
        }

        return null;
    }
}
