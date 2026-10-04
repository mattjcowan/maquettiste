using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Maquettiste.Engine;

/// <summary>A change the assistant proposes, checked by a dry run and pinned so applying it writes exactly what was previewed.</summary>
/// <param name="Summary">The one-line summary the model gave.</param>
/// <param name="Operations">The batch operations, in the <c>/api/model/batch</c> shape, as the editor applies them.</param>
/// <param name="Files">What applying them would create, change or delete (<see cref="ModelStore.PreviewBatchAsync"/>).</param>
public sealed record AssistantProposalDraft(string Summary, JsonArray Operations, IReadOnlyList<BatchPreviewFile> Files);

/// <summary>
/// The assistant's only write tool, <c>propose_changes</c> (erratum E44): the model sends batch operations and a one-line summary; the
/// batch is parsed and run as a dry run (<see cref="ModelStore.PreviewBatchAsync"/>), nothing is written, and the result is a proposal
/// the user reviews as a diff and applies through <c>/api/model/batch</c> (one undo step) or discards.
/// </summary>
public static class AssistantProposals
{
    /// <summary>The tool name.</summary>
    public const string ToolName = "propose_changes";

    /// <summary>The most operations one proposal holds.</summary>
    public const int MaxOperations = 100;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "summary": {
              "description": "One line saying what the change does, for the user, for example \"Add entity Shipment with a key and a relation to Order\"; required.",
              "type": "string"
            },
            "operations": {
              "description": "The batch operations, applied in order, all or nothing (the apply_batch shapes described above); required.",
              "type": "array",
              "items": { "type": "object" }
            }
          },
          "required": ["summary", "operations"]
        }
        """).RootElement.Clone();

    /// <summary>The tool's definition.</summary>
    public static AgentTool Tool { get; } = new(ToolName, "Propose changes",
        "Proposes a change to the model for the user to review. Nothing is written: the operations are checked as a dry run and shown to the "
        + "user as a diff with Apply and Discard. Operations are the batch shapes: {\"op\":\"create\",\"element\":{...}}, "
        + "{\"op\":\"update\",\"id\":...,\"element\":{whole document}} (expectedHash optional: the hash you read, else the current one), "
        + "{\"op\":\"delete\",\"id\":...,\"resolution\":\"refuse|remove-references|delete-dependents\"}, and the schema, materialize and process "
        + "operations. An element appears in one operation only. Missing ids of new elements and sub-elements (attributes, members, ends...) are added "
        + "for you. To refer to an element created in the same proposal, give it an id "
        + "yourself: 26 characters, Crockford base 32 uppercase (no I, L, O or U), first character 0 to 7. Read get_schema for a kind before "
        + "building its document. An invalid proposal is an error naming each failing operation and its diagnostics: fix them and call "
        + "again. Propose one coherent change per call and never repeat a proposal the user has not answered.",
        Schema);

    /// <summary>
    /// Checks the model's arguments and runs the batch as a dry run: a create without an id gets one, an update or delete without an
    /// expected hash gets the current one, and each created or updated element is replaced by the document the dry run would write (so
    /// sub-element ids are pinned too).
    /// </summary>
    /// <param name="store">The model store.</param>
    /// <param name="ids">The id generator (the store's).</param>
    /// <param name="arguments">The tool call's arguments.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The draft, or the error text to return to the model as the tool's result.</returns>
    public static async Task<(AssistantProposalDraft? Draft, string? Error)> PrepareAsync(ModelStore store, IIdGenerator ids, JsonElement arguments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ids);
        if (arguments.ValueKind != JsonValueKind.Object)
            return (null, Error("The arguments must be an object with summary and operations."));
        var summary = arguments.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()!.Trim() : "";
        if (summary.Length == 0)
            return (null, Error("summary is required: one line saying what the change does."));
        if (summary.Length > 300)
            summary = summary[..300];
        if (!arguments.TryGetProperty("operations", out var opsElement) || opsElement.ValueKind != JsonValueKind.Array)
            return (null, Error("operations is required: an array of batch operations."));
        var operations = JsonNode.Parse(opsElement.GetRawText())!.AsArray();
        if (operations.Count == 0)
            return (null, Error("operations is empty: propose at least one operation."));
        if (operations.Count > MaxOperations)
            return (null, Error($"A proposal holds at most {MaxOperations} operations; split the change."));

        var snapshot = await store.GetSnapshotAsync(ct).ConfigureAwait(false);
        foreach (var node in operations)
        {
            if (node is not JsonObject op)
                continue;
            var kind = op["op"]?.GetValueKind() == JsonValueKind.String ? op["op"]!.GetValue<string>() : null;
            if (kind == "create" && op["element"] is JsonObject element && element["id"] is null)
                element["id"] = ids.NewId();
            if (kind is "update" or "delete" && op["expectedHash"] is null && op["id"]?.GetValueKind() == JsonValueKind.String
                && snapshot.GetDocument(op["id"]!.GetValue<string>()) is { } current)
                op["expectedHash"] = current.Hash;
        }

        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes(new JsonObject { ["operations"] = operations.DeepClone() }.ToJsonString()));
        if (parsed.Batch is null)
        {
            return (null, Error("The operations do not form a valid batch; nothing was proposed.",
                new JsonObject { ["diagnostics"] = Diagnostics(parsed.Diagnostics) }));
        }

        var preview = await store.PreviewBatchAsync(parsed.Batch, ct).ConfigureAwait(false);
        // Sub-elements (attributes, members, ends, states...) need ids the model rarely has: add the ones the schemas ask for and try again.
        for (var round = 0; round < 4 && preview.Outcome == SaveOutcome.Invalid && FillMissingIds(operations, preview.Items, ids); round++)
        {
            parsed = store.ParseBatch(Encoding.UTF8.GetBytes(new JsonObject { ["operations"] = operations.DeepClone() }.ToJsonString()));
            if (parsed.Batch is null)
                break;
            preview = await store.PreviewBatchAsync(parsed.Batch, ct).ConfigureAwait(false);
        }

        if (parsed.Batch is null)
        {
            return (null, Error("The operations do not form a valid batch; nothing was proposed.",
                new JsonObject { ["diagnostics"] = Diagnostics(parsed.Diagnostics) }));
        }

        if (preview.Outcome != SaveOutcome.Saved)
        {
            var failures = new JsonArray();
            for (var i = 0; i < preview.Items.Count; i++)
            {
                var item = preview.Items[i];
                if (item.Outcome == SaveOutcome.Saved && item.Diagnostics.Count == 0)
                    continue;
                var failure = new JsonObject
                {
                    ["operation"] = i,
                    ["outcome"] = OutcomeName(item.Outcome),
                    ["id"] = item.Id,
                    ["diagnostics"] = Diagnostics(item.Diagnostics),
                };
                if (item.Referrers.Count > 0)
                    failure["referrers"] = new JsonArray([.. item.Referrers.Take(20).Select(r => (JsonNode?)JsonValue.Create(r.FromElementId))]);
                if (item.Outcome == SaveOutcome.Conflict && item.Hash is { } hash)
                    failure["currentHash"] = hash;
                failures.Add(failure);
            }

            return (null, Error($"The change would be refused ({OutcomeName(preview.Outcome)}); nothing was proposed. Fix the operations and call propose_changes again.",
                new JsonObject { ["failures"] = failures }));
        }

        // Pin what was previewed: the documents the dry run would write, ids of new sub-elements included.
        var written = preview.Files.Where(f => f.Action != "deleted" && f.Id is not null && f.After is not null && f.Path.EndsWith(".json", StringComparison.Ordinal))
            .GroupBy(f => f.Id!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().After!, StringComparer.Ordinal);
        foreach (var node in operations)
        {
            if (node is not JsonObject op || op["element"] is not JsonObject element || op["op"]?.GetValueKind() != JsonValueKind.String)
                continue;
            var id = op["op"]!.GetValue<string>() == "update" ? op["id"]?.GetValue<string>() : element["id"]?.GetValue<string>();
            if (id is not null && written.TryGetValue(id, out var after) && JsonNode.Parse(after) is JsonObject document)
            {
                document.Remove("$schema");
                op["element"] = document;
            }
        }

        return (new AssistantProposalDraft(summary, operations, preview.Files), null);
    }

    /// <summary>
    /// Gives an id to each sub-element a failed item's diagnostics say lacks one (MQ1002 "Required properties ["id"]" at a pointer into
    /// the operation's element); returns whether any was added.
    /// </summary>
    private static bool FillMissingIds(JsonArray operations, IReadOnlyList<SaveResult> items, IIdGenerator ids)
    {
        var added = false;
        for (var i = 0; i < items.Count && i < operations.Count; i++)
        {
            if (operations[i] is not JsonObject { } op || op["element"] is not JsonObject element)
                continue;
            foreach (var d in items[i].Diagnostics)
            {
                if (d.Rule != "MQ1002" || !d.Message.Contains("Required properties [\"id\"]", StringComparison.Ordinal) || d.JsonPointer is null)
                    continue;
                var pointer = d.JsonPointer;
                var prefix = "/operations/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/element";
                if (pointer.StartsWith(prefix, StringComparison.Ordinal))
                    pointer = pointer[prefix.Length..];
                if (Resolve(element, pointer) is JsonObject target && target["id"] is null)
                {
                    target["id"] = ids.NewId();
                    added = true;
                }
            }
        }

        return added;

        static JsonNode? Resolve(JsonNode root, string pointer)
        {
            var node = (JsonNode?)root;
            foreach (var raw in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                node = node switch
                {
                    JsonObject o => o[segment],
                    JsonArray a when int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) && index < a.Count => a[index],
                    _ => null,
                };
                if (node is null)
                    return null;
            }

            return node;
        }
    }

    /// <summary>The tool result for a stored proposal: its id and files, and what happens next.</summary>
    /// <param name="proposalId">The proposal id.</param>
    /// <param name="draft">The draft.</param>
    /// <returns>The JSON text.</returns>
    public static string Accepted(string proposalId, AssistantProposalDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var files = new JsonArray([.. draft.Files.Select(f => (JsonNode?)new JsonObject
        {
            ["path"] = f.Path,
            ["action"] = f.Action,
            ["kind"] = f.Kind,
            ["name"] = f.Name,
            ["id"] = f.Id,
        })]);
        return new JsonObject
        {
            ["proposal"] = proposalId,
            ["status"] = "pending-review",
            ["files"] = files,
            ["note"] = "Nothing is written yet. The user reviews the diff in the editor and applies or discards it; tell them what it does and do not propose it again.",
        }.ToJsonString();
    }

    private static string Error(string title, JsonObject? body = null)
    {
        var error = new JsonObject { ["code"] = "invalid", ["title"] = title };
        if (body is not null)
        {
            foreach (var (name, value) in body.ToList())
            {
                body.Remove(name);
                error[name] = value;
            }
        }

        return error.ToJsonString();
    }

    private static JsonArray Diagnostics(IEnumerable<Diagnostics.Diagnostic> diagnostics) =>
        new([.. diagnostics.Take(30).Select(d => (JsonNode?)new JsonObject
        {
            ["rule"] = d.Rule,
            ["severity"] = d.Severity switch { Engine.Diagnostics.DiagnosticSeverity.Error => "error", Engine.Diagnostics.DiagnosticSeverity.Warning => "warning", _ => "info" },
            ["message"] = d.Message,
            ["elementId"] = d.ElementId,
            ["pointer"] = d.JsonPointer,
        })]);

    private static string OutcomeName(SaveOutcome outcome) => outcome switch
    {
        SaveOutcome.Saved => "saved",
        SaveOutcome.Conflict => "conflict",
        SaveOutcome.Invalid => "invalid",
        SaveOutcome.NotFound => "not-found",
        SaveOutcome.Referenced => "referenced",
        _ => "failed",
    };
}
