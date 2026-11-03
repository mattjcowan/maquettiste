using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;

namespace Maquettiste.Engine;

/// <summary>
/// Patches (next-phase-1-design.md section 5.2): an RFC 6902 JSON Patch applied to one element's file document and saved as a
/// one-operation batch update, so an agent changes a field without sending the whole document back.
/// </summary>
public sealed partial class ModelStore
{
    /// <summary>The top-level members a patch keeps: an element's identity.</summary>
    private static readonly string[] PatchFixedMembers = ["id", "kind"];

    /// <summary>
    /// Applies an RFC 6902 patch to the element's file document (<c>$schema</c> included) and saves it as a one-operation batch update
    /// with <paramref name="expectedHash"/>: the update's conflict check, name resolution and minting (section 5.1), validation and
    /// migration facts all apply as for <see cref="ApplyBatchAsync"/>.
    /// </summary>
    /// <param name="id">The element id; a name or a sub-element id is not looked up.</param>
    /// <param name="patch">The patch: a UTF-8 JSON array of operations.</param>
    /// <param name="expectedHash">The hash the caller read.</param>
    /// <param name="source">What caused the change.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>
    /// The batch result with one item. <see cref="SaveOutcome.NotFound"/> when no element has the id;
    /// <see cref="SaveOutcome.Conflict"/> with the indexed document and hash when the hash differs; <see cref="SaveOutcome.Invalid"/> with
    /// one MQ1016 at <c>/&lt;operation index&gt;</c> when the patch cannot be applied (nothing is written); otherwise the update's result,
    /// with the facts it recorded on <see cref="BatchResult.Facts"/>. Every diagnostic but MQ1016 points into the document, never into
    /// the batch.
    /// </returns>
    public async Task<BatchResult> PatchAsync(string id, ReadOnlyMemory<byte> patch, string expectedHash, ChangeSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(expectedHash);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        if (snapshot.GetDocument(id) is not { } document || !string.Equals(document.Element.Id, id, StringComparison.Ordinal))
            return One(new SaveResult(SaveOutcome.NotFound, id, null, null, [], [], null));
        if (!string.Equals(expectedHash, document.Hash, StringComparison.Ordinal))
            return One(new SaveResult(SaveOutcome.Conflict, id, document.Hash, document, [], [], null));

        var file = JsonNode.Parse(document.Json.GetRawText());
        if (!JsonPatch.TryApply(file, patch.Span, PatchFixedMembers, id, out var patched, out var error))
            return One(new SaveResult(SaveOutcome.Invalid, id, null, null, [error], [], null));

        JsonElement element;
        using (var parsed = JsonDocument.Parse(Encoding.UTF8.GetBytes(patched!.ToJsonString())))
            element = parsed.RootElement.Clone();
        var result = await ApplyBatchCoreAsync(new ModelBatch([new BatchOperation(BatchOp.Update, id, expectedHash, element)]), source, null, ct).ConfigureAwait(false);
        return result with { Items = [.. result.Items.Select(item => item with { Diagnostics = [.. item.Diagnostics.Select(ToDocumentPointer)] })] };

        static BatchResult One(SaveResult item) => new(item.Outcome, [item], null);
    }

    /// <summary>
    /// A diagnostic of the one-operation batch with its pointer in the document: on a batch diagnostic (no file path),
    /// <c>/operations/0/element</c> is stripped and any other pointer into the operation becomes <c>""</c>, so a patch answers in two
    /// pointer spaces only, MQ1016 in the patch and the rest in the document.
    /// </summary>
    private static Diagnostic ToDocumentPointer(Diagnostic diagnostic)
    {
        const string Operation = "/operations/0";
        const string Element = Operation + "/element";
        if (diagnostic.FilePath is not null || diagnostic.JsonPointer is not { } pointer || !pointer.StartsWith(Operation, StringComparison.Ordinal)
            || (pointer.Length > Operation.Length && pointer[Operation.Length] != '/'))
            return diagnostic;
        if (pointer.StartsWith(Element, StringComparison.Ordinal) && (pointer.Length == Element.Length || pointer[Element.Length] == '/'))
            return diagnostic with { JsonPointer = pointer[Element.Length..] };
        return diagnostic with { JsonPointer = "" };
    }
}
