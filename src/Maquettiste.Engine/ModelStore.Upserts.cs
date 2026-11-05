using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine;

public sealed partial class ModelStore
{
    /// <summary>
    /// Turns each <c>upsert</c> of a batch into the create or update it stands for, on the model as loaded: a script that re-runs against
    /// another source sends one batch, whether or not the element exists yet. The name finds the element, as an update's qualified name
    /// does, and it must be the name the element states, so that the next run finds what this one creates. An update replaces the whole
    /// document and, as any update does, keeps a sub-element's id only when the element sends it.
    /// </summary>
    /// <param name="loaded">The model as loaded.</param>
    /// <param name="batch">The batch.</param>
    /// <param name="failures">Per operation, why its upsert is refused.</param>
    /// <returns>The batch with no upsert left.</returns>
    private static ModelBatch ExpandUpserts(ModelSnapshot loaded, ModelBatch batch, SaveResult?[] failures)
    {
        var operations = new BatchOperation[batch.Operations.Count];
        for (var i = 0; i < operations.Length; i++)
        {
            var o = batch.Operations[i];
            operations[i] = o;
            if (o.Op != BatchOp.Upsert)
                continue;
            var pointer = "/operations/" + i.ToString(CultureInfo.InvariantCulture);
            if (Refusal(o, out var name) is { } refusal)
            {
                failures[i] = Refused("MQ1015", refusal, o.Id, pointer + "/id");
                continue;
            }

            var candidates = loaded.Names(name!.Kind).Find(name.Path);
            if (candidates.Count > 1)
            {
                failures[i] = Refused("MQ1013", $"'{o.Id}' names {candidates.Count.ToString(CultureInfo.InvariantCulture)} {name.Kind} elements: "
                    + string.Join(", ", candidates.Select(c => c.Longest + " (" + c.Id + ")")) + ". Use a longer form.", o.Id, pointer + "/id");
                continue;
            }

            operations[i] = candidates.Count == 0
                ? o with { Op = BatchOp.Create, Id = null }
                : o with { Op = BatchOp.Update, Id = candidates[0].Id, ExpectedHash = o.ExpectedHash ?? loaded.GetDocument(candidates[0].Id)?.Hash };
        }

        return new ModelBatch(operations);
    }

    /// <summary>Why an upsert cannot name its element, or <see langword="null"/> with the name it uses.</summary>
    private static string? Refusal(BatchOperation o, out QualifiedName? name)
    {
        name = null;
        if (o.Element is not { ValueKind: JsonValueKind.Object } element)
            return "An upsert needs an element.";
        if (o.Id is not { Length: > 0 } id || id[0] != '@')
            return $"An upsert's id must be a qualified name (@kind:path), not '{o.Id}': it finds the element by name.";
        if (!QualifiedName.TryParse(id, out var parsed, out var error))
            return $"'{id}' is not a qualified name: {error}.";
        if (!KindInfo.TryGet(parsed.Kind, out _))
            return $"An upsert writes a whole element, and '{id}' names a {parsed.Kind}. Update the element that holds it.";
        var kind = element.TryGetProperty("kind", out var kindValue) && kindValue.ValueKind == JsonValueKind.String ? kindValue.GetString() : null;
        if (!string.Equals(kind, parsed.Kind, StringComparison.Ordinal))
            return $"'{id}' names a {parsed.Kind}, and the element is a {kind ?? "document with no kind"}.";
        var key = kind == "stereotype" ? "key" : "name";
        if (element.TryGetProperty(key, out var stated) && stated.ValueKind == JsonValueKind.String
            && !string.Equals(stated.GetString(), parsed.Segments[^1], StringComparison.Ordinal))
        {
            return $"'{id}' ends in '{parsed.Segments[^1]}', and the element's {key} is '{stated.GetString()}'. An upsert does not rename: the next run would not find it.";
        }

        name = parsed;
        return null;
    }

    private static SaveResult Refused(string rule, string message, string? id, string pointer) =>
        new(SaveOutcome.Invalid, id, null, null, [RuleCatalog.Create(rule, message, null, null, pointer)], [], null);
}
