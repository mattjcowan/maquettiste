using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;

namespace Maquettiste.Engine.Loading;

/// <summary>
/// Parses an untrusted batch (editor, refactoring or AI proposal) against <c>batch.json</c> with no disk access (host-contracts
/// requirement 16). Diagnostics carry the pointer, line and column in the batch text and no file path. Element contents are
/// validated when the batch is applied, against their kind's schema.
/// </summary>
/// <param name="schemas">The schema registry.</param>
/// <param name="json">The canonical writer (unused by parsing; the reader shares it).</param>
internal sealed class BatchParser(ISchemaRegistry schemas, ICanonicalJson json)
{
    private readonly DocumentReader _reader = new(schemas, json);

    /// <summary>Parses a batch.</summary>
    /// <param name="utf8">The batch JSON.</param>
    /// <returns>The batch, or diagnostics.</returns>
    public BatchParseResult Parse(ReadOnlySpan<byte> utf8)
    {
        var bytes = utf8.ToArray();
        if (!_reader.TryParse(bytes, null, "MQ1001", out var document, out var error))
            return new BatchParseResult(null, [error]);

        using (document)
        {
            var root = document.RootElement;
            var diagnostics = schemas.Evaluate("batch.json", root, "")
                .Select(d => _reader.Locate(d with { FilePath = null, ElementId = null }, bytes))
                .Order(Diagnostic.Order)
                .ToList();
            if (diagnostics.Count > 0)
                return new BatchParseResult(null, diagnostics);

            var translate = root.GetProperty("operations").EnumerateArray()
                .Select((item, index) => (item, index))
                .Where(p => p.item.GetProperty("op").ValueEquals("translate"))
                .Select(p => RuleCatalog.Create("MQ1002", $"/operations/{p.index}/op The translate operation is declared in the contract; its handler lands in a later step.", null, null, $"/operations/{p.index}/op"))
                .ToList();
            if (translate.Count > 0)
                return new BatchParseResult(null, translate);

            var operations = new List<BatchOperation>();
            foreach (var item in root.GetProperty("operations").EnumerateArray())
            {
                var op = item.GetProperty("op").GetString() switch
                {
                    "create" => BatchOp.Create,
                    "update" => BatchOp.Update,
                    "add-schema" => BatchOp.AddSchema,
                    "rename-schema" => BatchOp.RenameSchema,
                    "remove-schema" => BatchOp.RemoveSchema,
                    "set-default-schema" => BatchOp.SetDefaultSchema,
                    "sync-enum" => BatchOp.SyncEnum,
                    "set-lifecycle" => BatchOp.SetLifecycle,
                    "set-initial" => BatchOp.SetInitial,
                    "refresh-scenario" => BatchOp.RefreshScenario,
                    _ => BatchOp.Delete,
                };
                operations.Add(new BatchOperation(
                    op,
                    item.TryGetProperty("id", out var id) ? id.GetString() : null,
                    item.TryGetProperty("expectedHash", out var hash) ? hash.GetString() : null,
                    item.TryGetProperty("element", out var element) ? element.Clone() : null,
                    Schema: Text(item, "schema"),
                    Name: Text(item, "name"),
                    Target: Text(item, "target"),
                    Default: Text(item, "default")));
            }

            return new BatchParseResult(new ModelBatch(operations), []);
        }
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
