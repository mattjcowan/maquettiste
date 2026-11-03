using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Loading;

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
        + "operations. An element appears in one operation only. Send no ids for new elements and sub-elements (attributes, members, ends, "
        + "rows...): the engine mints them. Every id field (id, database, package, schema, target, the references inside an element) takes an "
        + "id or a qualified name, @kind:path with the name as stored (@entity:Billing.Invoice, @table:billing.notes, @column:billing.notes.body, "
        + "@attribute:Invoice.total); to refer to an element created in the same proposal, name it with \"as\":\"$name\" on its operation and "
        + "write $name where its id goes. When you re-send a document, keep the ids of its sub-elements. Read get_schema for a kind before "
        + "building its document. An invalid proposal is an error naming each failing operation and its diagnostics: fix them and call "
        + "again. Propose one coherent change per call and never repeat a proposal the user has not answered.",
        Schema);

    /// <summary>
    /// Checks the model's arguments and runs the batch as a dry run (next-phase-1-design.md section 5.1): an update's
    /// or delete's target given as a qualified name is resolved on the model as loaded and pinned to its id, an update
    /// or delete without an expected hash gets the current one, and the engine mints every missing id. After the dry
    /// run, an update's or delete's <c>id</c> stays the target pinned before it, and a create's becomes its element's
    /// id, else the operation's, else the root id the dry run minted for that operation (decision 15: the items do not
    /// line up with expanded operations, so the pin never reads them). Its other id fields are pinned to the ids the
    /// names resolved to, its element to the document the dry run would write (so minted ids are pinned too), and
    /// <c>as</c> is dropped: applying the proposal writes exactly what was previewed.
    /// </summary>
    /// <param name="store">The model store.</param>
    /// <param name="ids">The id generator (the store's); the store mints with its own.</param>
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
        // Update and delete targets given by name resolve on the model as loaded and are pinned before their hashes are read; a name
        // that does not resolve fails like a parse failure, with its MQ1012 to MQ1015 diagnostics.
        var targets = BatchNames.ResolveTargets(snapshot, new ModelBatch([.. operations.Select(TargetOf)]));
        var unresolved = targets.Diagnostics.SelectMany(d => d).ToList();
        if (unresolved.Count > 0)
        {
            return (null, Error("The operations name an element that cannot be found; nothing was proposed.",
                new JsonObject { ["diagnostics"] = Diagnostics(unresolved) }));
        }

        for (var i = 0; i < operations.Count; i++)
        {
            if (operations[i] is not JsonObject op || OpName(op) is not ("update" or "delete") || targets.Targets[i] is not { } target)
                continue;
            op["id"] = target;
            if (op["expectedHash"] is null && snapshot.GetDocument(target) is { } current)
                op["expectedHash"] = current.Hash;
        }

        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes(new JsonObject { ["operations"] = operations.DeepClone() }.ToJsonString()));
        if (parsed.Batch is null)
        {
            return (null, Error("The operations do not form a valid batch; nothing was proposed.",
                new JsonObject { ["diagnostics"] = Diagnostics(parsed.Diagnostics) }));
        }

        var preview = await store.PreviewBatchAsync(parsed.Batch, ct).ConfigureAwait(false);
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

        // Pin what was previewed: each operation's ids, and the documents the dry run would write with the ids it minted.
        var written = preview.Files.Where(f => f.Action != "deleted" && f.Id is not null && f.After is not null && f.Path.EndsWith(".json", StringComparison.Ordinal))
            .GroupBy(f => f.Id!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().After!, StringComparer.Ordinal);
        for (var i = 0; i < operations.Count; i++)
        {
            if (operations[i] is not JsonObject op)
                continue;
            op.Remove("as");
            var kind = OpName(op);
            PinNames(op, preview.Ids.Names);
            if (kind == "create" && CreatedId(op, i, preview.Ids) is { } created)
                op["id"] = created;
            if (kind is not ("create" or "update") || op["element"] is not JsonObject)
                continue;
            if (op["id"]?.GetValueKind() == JsonValueKind.String && written.TryGetValue(op["id"]!.GetValue<string>(), out var after)
                && JsonNode.Parse(after) is JsonObject document)
            {
                document.Remove("$schema");
                op["element"] = document;
            }
        }

        return (new AssistantProposalDraft(summary, operations, preview.Files), null);
    }

    /// <summary>The operation's <c>op</c>, when it is a string.</summary>
    private static string? OpName(JsonObject op) => op["op"]?.GetValueKind() == JsonValueKind.String ? op["op"]!.GetValue<string>() : null;

    /// <summary>
    /// What <see cref="BatchNames.ResolveTargets"/> reads of an operation: an update's or delete's <c>id</c>; anything else stands in as
    /// a create with no id, which it skips. The batch is not parsed yet, because an update without its expected hash does not parse.
    /// </summary>
    private static BatchOperation TargetOf(JsonNode? node)
    {
        var op = node as JsonObject;
        var kind = op is null ? null : OpName(op);
        var id = op?["id"]?.GetValueKind() == JsonValueKind.String ? op["id"]!.GetValue<string>() : null;
        return kind switch
        {
            "update" => new BatchOperation(BatchOp.Update, id, null, null),
            "delete" => new BatchOperation(BatchOp.Delete, id, null, null),
            _ => new BatchOperation(BatchOp.Create, null, null, null),
        };
    }

    /// <summary>
    /// The id of a create's element: the element's own id, else the operation's, else the root id the dry run minted for it. Read from
    /// the operation rather than from the items, which do not line up with the operations when the batch holds expanded operations.
    /// </summary>
    private static string? CreatedId(JsonObject op, int index, BatchIds ids)
    {
        if (op["element"] is JsonObject element && element["id"] is JsonValue own && own.TryGetValue<string>(out var given))
            return given;
        if (op["id"] is JsonValue opId && opId.TryGetValue<string>(out var operation))
            return operation;
        return ids.Minted.FirstOrDefault(m => m.Operation == index && m.Pointer.Length == 0)?.Id;
    }

    /// <summary>Replaces each name or placeholder in an operation's <c>batch.json</c> id fields by the id the dry run resolved it to.</summary>
    private static void PinNames(JsonObject op, IReadOnlyDictionary<string, string> names)
    {
        if (names.Count == 0)
            return;
        foreach (var field in new[] { "id", "database", "package", "schema", "target", "default" })
        {
            if (op[field] is JsonValue value && value.TryGetValue<string>(out var text) && names.TryGetValue(text, out var id))
                op[field] = id;
        }

        foreach (var field in new[] { "entities", "tables", "attributes" })
        {
            if (op[field] is not JsonArray items)
                continue;
            for (var j = 0; j < items.Count; j++)
            {
                if (items[j] is JsonValue item && item.TryGetValue<string>(out var text) && names.TryGetValue(text, out var id))
                    items[j] = id;
            }
        }

        if (op["expectedHashes"] is JsonObject hashes)
        {
            foreach (var (key, hash) in hashes.ToList())
            {
                if (!names.TryGetValue(key, out var id) || hashes.ContainsKey(id))
                    continue;
                hashes.Remove(key);
                hashes[id] = hash;
            }
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
