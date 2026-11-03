using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Rendering;

namespace Maquettiste.Engine;

/// <summary>A pack's export records for the elements asked (<c>get_pack_exports</c>, <c>pack exports</c>).</summary>
/// <param name="Pack">The pack.</param>
/// <param name="Elements">One entry per element asked, in the order asked; the model-scope record alone when none was asked.</param>
/// <param name="Diagnostics">Why a record failed, or why the pack does not load.</param>
public sealed record PackExportsResult(string Pack, IReadOnlyList<ElementExports> Elements, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>One pack's merged export record for one element.</summary>
/// <param name="ElementId">The element, or null for model scope.</param>
/// <param name="Name">The element's name, or null.</param>
/// <param name="Kind">The element's kind, or null.</param>
/// <param name="Values">The merged record, or null when no unit exports for it (or it failed).</param>
/// <param name="Units">The keys of the units whose exports it merges, ordinal.</param>
public sealed record ElementExports(string? ElementId, string? Name, string? Kind, JsonElement? Values, IReadOnlyList<string> Units);

public sealed partial class GenerationService
{
    /// <summary>
    /// A pack's export records for at most <see cref="MaxRenderedPaths"/> elements, or its model-scope record when
    /// <paramref name="elementIds"/> is empty (next-phase-2-design.md section 5.3); evaluated now, under the preview deadline, from the
    /// pack and its uses closure. Only the records asked, and the records their export templates read, are evaluated. Nothing is written.
    /// </summary>
    /// <param name="pack">The pack name.</param>
    /// <param name="elementIds">The element ids, or none for the model-scope record.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The records.</returns>
    /// <exception cref="ArgumentException">More than <see cref="MaxRenderedPaths"/> elements.</exception>
    public async Task<PackExportsResult> GetPackExportsAsync(string pack, IReadOnlyList<string>? elementIds, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(pack);
        var ids = (elementIds ?? []).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count > MaxRenderedPaths)
            throw new ArgumentException($"At most {MaxRenderedPaths} elements, not {ids.Count}.", nameof(elementIds));
        var deadline = PreviewDeadlineMs();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(deadline);
        IDisposable? slot = null;
        try
        {
            slot = await EnterPreviewAsync(timeout.Token).ConfigureAwait(false);
            using var held = slot;
            var session = await PrepareSessionAsync(pack, timeout.Token).ConfigureAwait(false);
            if (session.Pack is not { } loaded || session.Resolved is not { } resolved)
                return new PackExportsResult(pack, [], session.Failure);
            var packs = new PackSet([loaded], []) { Used = session.Used };
            using var table = ExportTable.ForPreview(resolved, packs, _services.Scripts, _services.CreateRenderer() as Renderer, timeout.Token);
            table.Bind(new RenderContext(resolved, packs, session.SchemaDiffs, _services.Scripts, PreviewHasher.Instance, 1) { Exports = table });
            var elements = new List<ElementExports>();
            var diagnostics = new List<Diagnostic>();
            foreach (var id in ids.Count == 0 ? [null] : ids.Cast<string?>())
            {
                var element = id is null ? null : resolved.Find(id);
                var record = id is not null && element is null ? null : await Task.Run(() => table.Record(loaded.Name, id), timeout.Token).ConfigureAwait(false);
                if (record is { Failed: true })
                    diagnostics.AddRange(record.Diagnostics);
                elements.Add(new ElementExports(id, element is null ? null : NameOf(element), element?.Kind, record?.Values, record?.Units ?? []));
            }

            return new PackExportsResult(pack, elements, Outcomes.Sort(diagnostics));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PackExportsResult(pack, [], [RuleCatalog.Create("MQ6007",
                $"The exports of '{pack}' did not finish within {deadline} ms ({PreviewDeadlineText}).")]);
        }
    }
}
