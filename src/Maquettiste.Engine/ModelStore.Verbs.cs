using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine;

/// <summary>
/// The verb operations of a batch (verbs-design.md decision 9): <c>rename-verb</c> renames the project's own verb and rewrites every use, or
/// merges it into another own verb or a built-in verb; <c>remove-verb</c> removes it and clears every use. Each expands into updates of the
/// verb vocabulary and of the processes and operations that name the verb, all or nothing, as <c>retag</c> does. A verb that goes takes its
/// translations with it, since the save removes its sub-element.
/// </summary>
public sealed partial class ModelStore
{
    /// <summary>Whether an operation is a verb operation (<c>rename-verb</c>, <c>remove-verb</c>).</summary>
    /// <param name="op">The operation kind.</param>
    public static bool IsVerbOperation(BatchOp op) => op is BatchOp.RenameVerb or BatchOp.RemoveVerb;

    /// <summary>
    /// Plans a verb operation: adds the updates to <paramref name="changes"/>; returns why it is refused, or <see langword="null"/>. Uses are
    /// found by key in every event, transition and operation. <c>rename-verb</c> and <c>remove-verb</c> of an own verb whose key a built-in
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

        // An own verb that shadows a built-in key (MQ9702) has no uses: they name the built-in verb, so a rename or a remove leaves them, and
        // adopting the built-in (to the same key) writes only the vocabulary. A write that would change nothing is skipped.
        var rewriteUses = !BuiltInVerbs.IsBuiltIn(key) && !string.Equals(to, key, StringComparison.Ordinal);
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

                    break;
                case Operation operation when string.Equals(operation.Verb, key, StringComparison.Ordinal):
                    Rewrite(Working(document));
                    break;
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

        return null;
    }
}
