using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine;

/// <summary>
/// Expressions written as text (next-phase-3-design.md section 3.1, "Schema and files"): a write may hold <c>{ "text": … }</c> where a
/// tree goes, at a site or inside a tree, and the store parses it in the site's scope and stores the tree, so renames follow it; text
/// that does not parse refuses the write (MQ3101). <c>text-to-trees</c> does the same for files that already hold text (MQ3108). A
/// body without a text node is never read twice.
/// </summary>
public sealed partial class ModelStore
{
    /// <summary>Whether a request body holds a text node anywhere: an object whose one key is <c>text</c>, holding a string.</summary>
    private static bool HasTextNode(JsonNode? node) => node switch
    {
        JsonObject obj when obj.Count == 1 && obj["text"] is JsonValue value && value.GetValueKind() == JsonValueKind.String => true,
        JsonObject obj => obj.Any(p => HasTextNode(p.Value)),
        JsonArray array => array.Any(HasTextNode),
        _ => false,
    };

    /// <summary>
    /// Turns the text nodes of a create's or save's body into trees, once the body has the ids the planner would mint (a create's own,
    /// every sub-element's), so that text names the columns and attributes the write adds; the problems that refuse it, or
    /// <see langword="null"/>. <paramref name="minted"/> receives the pointers of the ids minted, which a save's planner needs.
    /// </summary>
    private async Task<SaveResult?> ExpressionInputAsync(JsonNode? node, string? id, List<string> minted, CancellationToken ct)
    {
        if (!HasTextNode(node))
            return null;
        var loaded = await LoadedAsync(ct).ConfigureAwait(false);
        MintIds(node!, id, loaded, minted);
        var problems = ExpressionInput(node!, loaded);
        return problems.Count == 0 ? null : new SaveResult(SaveOutcome.Invalid, id ?? (node!["id"] as JsonValue)?.ToString(), null, null, [.. problems.Order(Diagnostic.Order)], [], null);
    }

    /// <summary>
    /// Gives a body the ids <see cref="ChangePlanner"/> would: a create's root (a save's is its target's), then every sub-element
    /// without one, in the same order; a body without a known kind is left to the planner to refuse.
    /// </summary>
    private void MintIds(JsonNode node, string? id, ModelSnapshot snapshot, List<string> minted)
    {
        if (node is not JsonObject root || root["kind"] is not JsonValue kind || !kind.TryGetValue<string>(out var name) || !KindInfo.TryGet(name, out var info))
            return;
        var ids = _options.EffectiveIdGenerator;
        if (root["id"] is null)
            root["id"] = id ?? ids.NewId();
        var found = new List<MintedId>();
        IdMinter.Fill(root, _services.Schemas.GetLayout(info.SchemaFile), ids, found, snapshot.PropertySchemas);
        minted.AddRange(found.Select(m => m.Pointer));
    }

    /// <summary>Turns the text nodes of a body into trees in place, read against the model as loaded.</summary>
    /// <returns>The MQ3101 problems of text that does not parse; empty when every text became a tree or there was none.</returns>
    private static List<Diagnostic> ExpressionInput(JsonNode node, ModelSnapshot snapshot)
    {
        var problems = new List<Diagnostic>();
        if (!HasTextNode(node))
            return problems;
        Element element;
        try
        {
            element = ElementReader.ReadElement(JsonSerializer.SerializeToElement(node, EngineJson.Options));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return problems; // the save's own checks report the body
        }

        ConvertTexts(snapshot, element, null, node, problems);
        return problems;
    }

    /// <summary>Replaces each site's text nodes with trees in a document node; true when it changed anything.</summary>
    private static bool ConvertTexts(ModelSnapshot snapshot, Element element, ElementDocument? document, JsonNode root, List<Diagnostic> problems)
    {
        var changed = false;
        foreach (var site in ExprSites.Of(snapshot, element))
        {
            if (!ExprSites.HasText(site.Tree))
                continue;
            var scope = ExprSites.Scope(snapshot, element, document, site, text: true);
            var before = problems.Count;
            var tree = ExprSites.ParseTexts(site.Tree!, scope, problems);
            if (problems.Count > before)
                continue;
            SetValue(root, site.Pointer, JsonSerializer.SerializeToNode(tree, EngineJson.Options)!);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// <c>text-to-trees</c>: in the elements <c>elementIds</c> lists, or in every document, each expression held as text becomes its
    /// tree. Text that does not parse refuses the operation with its MQ3101 at the file and pointer. Adds the updates to
    /// <paramref name="changes"/>; nothing to convert adds none.
    /// </summary>
    /// <returns>The diagnostics that refuse the operation, or <see langword="null"/>.</returns>
    private IReadOnlyList<Diagnostic>? TextToTrees(ModelSnapshot snapshot, BatchOperation o, List<PlannedChange> changes, string pointer)
    {
        List<ElementDocument> documents;
        if (o.ElementIds is { } ids)
        {
            documents = [];
            for (var k = 0; k < ids.Count; k++)
            {
                if (snapshot.Get<Element>(ids[k]) is null || snapshot.GetDocument(ids[k]) is not { } document)
                {
                    return [RuleCatalog.Create("MQ1002", $"'{ids[k]}' is not an element: elementIds lists the elements whose expressions become trees.", o.Id, null,
                        pointer + "/elementIds/" + k.ToString(CultureInfo.InvariantCulture))];
                }

                if (documents.All(d => d.Element.Id != document.Element.Id))
                    documents.Add(document);
            }
        }
        else
        {
            documents = ModelValidator.ActiveDocuments(snapshot);
        }

        var problems = new List<Diagnostic>();
        var edits = new List<(ElementDocument Document, JsonNode Node)>();
        foreach (var document in documents)
        {
            if (document.Element is not (Table or Stereotype or Entity or Operation or Process or Constraint))
                continue;
            if (!ExprSites.Of(snapshot, document.Element).Any(s => ExprSites.HasText(s.Tree)))
                continue;
            var node = JsonNode.Parse(document.Json.GetRawText())!;
            if (ConvertTexts(snapshot, document.Element, document, node, problems))
                edits.Add((document, node));
        }

        if (problems.Count > 0)
            return problems;
        foreach (var (document, node) in edits)
        {
            var elementId = document.Element.Id;
            if (!TryParseRequest(Encoding.UTF8.GetBytes(node.ToJsonString()), elementId, out var parsed, out var failure))
                return failure.Diagnostics.Count > 0 ? failure.Diagnostics : [RuleCatalog.Create("MQ1002", $"The rewritten '{elementId}' is not valid.", elementId, document.Path, null)];
            var expected = o.ExpectedHashes is { } read ? read.GetValueOrDefault(elementId) ?? "unread" : document.Hash;
            changes.Add(new PlannedChange(BatchOp.Update, elementId, expected, parsed, DeleteResolution.Refuse));
        }

        return null;
    }
}
