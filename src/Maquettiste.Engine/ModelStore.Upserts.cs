using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine;

public sealed partial class ModelStore
{
    /// <summary>
    /// Turns each <c>upsert</c> of a batch into the create or update it stands for, on the model as loaded: a script that re-runs against
    /// another source sends one batch, whether or not the element exists yet. The name finds the element, as an update's qualified name
    /// does. What the name must also be true of the document, once it is resolved, is <see cref="BatchNames.CheckUpserts"/>'s.
    /// </summary>
    /// <param name="loaded">The model as loaded.</param>
    /// <param name="batch">The batch.</param>
    /// <param name="failures">Per operation, why its upsert is refused.</param>
    /// <param name="upserts">The name of each operation that was an upsert, by its index.</param>
    /// <returns>The batch with no upsert left.</returns>
    private static ModelBatch ExpandUpserts(ModelSnapshot loaded, ModelBatch batch, SaveResult?[] failures, out Dictionary<int, QualifiedName> upserts)
    {
        upserts = [];
        var seen = new HashSet<QualifiedName>();
        var operations = new BatchOperation[batch.Operations.Count];
        for (var i = 0; i < operations.Length; i++)
        {
            var o = batch.Operations[i];
            operations[i] = o;
            if (o.Op != BatchOp.Upsert)
                continue;
            var pointer = "/operations/" + i.ToString(CultureInfo.InvariantCulture) + "/id";
            if (UpsertRefusal(o, out var name) is { } refusal)
            {
                failures[i] = BatchNames.Refused("MQ1015", refusal, o.Id, pointer);
                continue;
            }

            if (!seen.Add(name!))
            {
                failures[i] = BatchNames.Refused("MQ1014", $"'{o.Id}' is upserted twice in this batch: an element appears in one operation only.", o.Id, pointer);
                continue;
            }

            var candidates = loaded.Names(name!.Kind).Find(name.Path);
            if (candidates.Count > 1)
            {
                failures[i] = BatchNames.Refused("MQ1013", $"'{o.Id}' names {candidates.Count.ToString(CultureInfo.InvariantCulture)} {name.Kind} elements: "
                    + string.Join(", ", candidates.Select(c => c.Longest + " (" + c.Id + ")")) + ". Use a longer form, or an update with the id when no form tells them apart.", o.Id, pointer);
                continue;
            }

            if (candidates.Count == 0 && o.ExpectedHash is not null)
            {
                failures[i] = BatchNames.Refused("MQ1012", $"No {name.Kind} is named '{o.Id}', and expectedHash says it exists: it was renamed or deleted since you read it.", o.Id, pointer);
                continue;
            }

            upserts[i] = name;
            operations[i] = candidates.Count == 0
                ? o with { Op = BatchOp.Create, Id = null }
                : o with { Op = BatchOp.Update, ExpectedHash = o.ExpectedHash ?? loaded.GetDocument(candidates[0].Id)?.Hash };
        }

        return new ModelBatch(operations);
    }

    /// <summary>Why an upsert cannot name its element, or <see langword="null"/> with the name it uses.</summary>
    private static string? UpsertRefusal(BatchOperation o, out QualifiedName? name)
    {
        name = null;
        if (o.Element is not { ValueKind: JsonValueKind.Object } element)
            return "An upsert needs an element.";
        if (o.Id is not { Length: > 0 } id || id[0] != '@')
            return $"An upsert's id must be a qualified name (@kind:path), not '{o.Id}': it finds the element by name.";
        if (!QualifiedName.TryParse(id, out var parsed, out var error))
            return $"'{id}' is not a qualified name: {error}.";
        if (!KindInfo.TryGet(parsed.Kind, out _))
            return $"An upsert writes a whole element, and '{id}' is of kind {parsed.Kind}. Update the element that holds it.";
        var kind = element.TryGetProperty("kind", out var kindValue) && kindValue.ValueKind == JsonValueKind.String ? kindValue.GetString() : null;
        if (!string.Equals(kind, parsed.Kind, StringComparison.Ordinal))
            return $"'{id}' is of kind {parsed.Kind}, and the element is of kind {kind ?? "(none)"}.";
        name = parsed;
        return null;
    }

    /// <summary>The batch's names with each upsert's own, which a script reads back to learn the id it created or updated.</summary>
    private static Dictionary<string, string> WithUpsertNames(IReadOnlyDictionary<string, string> names, Dictionary<int, QualifiedName> upserts, JsonNode?[] nodes)
    {
        var all = names.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var (i, name) in upserts)
        {
            if (nodes[i] is JsonObject node && node["id"] is JsonValue id && id.TryGetValue<string>(out var value))
                all[name.Text] = value;
        }

        return all;
    }
}
