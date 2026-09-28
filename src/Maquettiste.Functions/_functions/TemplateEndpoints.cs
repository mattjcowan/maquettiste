using Maquettiste.Engine;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maquettiste.Functions;

/// <summary>The body of <c>POST /api/templates/preview</c>.</summary>
/// <param name="Pack">The pack name.</param>
/// <param name="Unit">The unit id.</param>
/// <param name="ElementId">An element id or a resolved table key; <see langword="null"/> for a <c>model</c> unit.</param>
public sealed record PreviewRequest(string? Pack, string? Unit, string? ElementId);

/// <summary><c>POST /api/templates/preview</c>: render one unit against one element with no writes (the DDL preview).</summary>
public static class TemplateEndpoints
{
    /// <summary>Renders the unit even when its <c>where</c> filter would not plan it; a model with errors returns the errors and no files.</summary>
    /// <param name="context">The request.</param>
    /// <param name="generation">The generation service.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with the rendered files and diagnostics, or 400.</returns>
    [HttpPost("/api/templates/preview")]
    public static Task<IResult> Preview(HttpContext context, GenerationService generation, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(generation);
        var (request, error) = await Api.ReadJsonAsync<PreviewRequest>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        if (string.IsNullOrEmpty(request!.Pack) || string.IsNullOrEmpty(request.Unit))
            return Api.BadRequest("pack and unit are required.");
        return Api.Json(await generation.PreviewAsync(request.Pack, request.Unit, request.ElementId, ct).ConfigureAwait(false));
    });
}
