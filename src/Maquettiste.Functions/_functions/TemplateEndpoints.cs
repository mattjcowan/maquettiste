using System.Text.Json;
using Maquettiste.Engine;
using Maquettiste.Engine.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maquettiste.Functions;

/// <summary>The body of <c>POST /api/templates/preview</c>.</summary>
/// <param name="Pack">The pack name.</param>
/// <param name="Unit">The unit id.</param>
/// <param name="ElementId">An element id or a resolved table key; <see langword="null"/> for a <c>model</c> unit.</param>
/// <param name="UnitOverride">The unsaved unit used instead of the saved one; its id must equal <paramref name="Unit"/> (maintainer).</param>
/// <param name="Overlay">Pack-relative path to unsaved text of templates, partials and scripts (maintainer).</param>
/// <param name="Parameters">Effective parameter values to use (maintainer).</param>
public sealed record PreviewRequest(string? Pack, string? Unit, string? ElementId, PackUnit? UnitOverride = null,
    IReadOnlyDictionary<string, string>? Overlay = null, IReadOnlyDictionary<string, JsonElement>? Parameters = null);

/// <summary>The body of <c>POST /api/templates/paths</c>.</summary>
/// <param name="Pack">The pack.</param>
/// <param name="Unit">The unit id.</param>
/// <param name="ElementIds">Only these elements; every planned element when absent.</param>
/// <param name="Limit">How many elements to render (default 200, at most 2000); the count is always the whole scope.</param>
/// <param name="UnitOverride">An unsaved unit (maintainer).</param>
/// <param name="Overlay">Unsaved pack files (maintainer).</param>
/// <param name="Parameters">Unsaved parameter values (maintainer).</param>
public sealed record PathsRequest(string? Pack, string? Unit, IReadOnlyList<string>? ElementIds = null, int? Limit = null, PackUnit? UnitOverride = null,
    IReadOnlyDictionary<string, string>? Overlay = null, IReadOnlyDictionary<string, JsonElement>? Parameters = null);

/// <summary><c>POST /api/templates/preview</c>: render one unit against one element with no writes (the DDL preview).</summary>
public static class TemplateEndpoints
{
    /// <summary>
    /// The caller's key, the <c>X-Maquettiste-Client</c> header (at most 64 characters), or <see langword="null"/>: without it nothing
    /// is superseded, since one editor tab sends several path listings at once and HTTP/2 multiplexes them on one connection.
    /// </summary>
    private static string? ClientKey(HttpContext context) =>
        context.Request.Headers["X-Maquettiste-Client"].ToString() is { Length: > 0 and <= 64 } client ? client : null;

    /// <summary>409 <c>superseded</c>: a newer preview or paths request on the same connection cancelled this one.</summary>
    private static IResult Superseded() => Api.Problem("superseded", "A newer request on this connection replaced this one.", StatusCodes.Status409Conflict);

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
        // One request in flight per client key and operation: a newer one cancels the older on the server (generation-ui.md section 5.2).
        var options = new PreviewOptions
        {
            UnitOverride = request.UnitOverride,
            Overlay = request.Overlay,
            Parameters = request.Parameters,
            Connection = ClientKey(context) is { } client ? "preview:" + client : null,
        };
        // Unsaved text is code the server renders or runs (generation-ui.md section 5.1): it needs the role that may save it.
        if (options.CarriesUnsavedText && Api.Require(context, "maintainer") is { } forbidden)
            return forbidden;
        try
        {
            return Api.Json(await generation.PreviewAsync(request.Pack, request.Unit, request.ElementId, options, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Superseded();
        }
        catch (ArgumentException ex)
        {
            return Api.BadRequest(ex.Message);
        }
    });

    /// <summary>A unit's rendered output paths over its scope or the listed elements, with the count, collisions and root checks.</summary>
    /// <param name="context">The request.</param>
    /// <param name="generation">The generation service.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 or 400.</returns>
    [HttpPost("/api/templates/paths")]
    public static Task<IResult> Paths(HttpContext context, GenerationService generation, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(generation);
        var (request, error) = await Api.ReadJsonAsync<PathsRequest>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        if (string.IsNullOrEmpty(request!.Pack) || string.IsNullOrEmpty(request.Unit))
            return Api.BadRequest("pack and unit are required.");
        // One request in flight per client key and operation: a newer one cancels the older on the server (generation-ui.md section 5.2).
        var options = new PreviewOptions
        {
            UnitOverride = request.UnitOverride,
            Overlay = request.Overlay,
            Parameters = request.Parameters,
            Connection = ClientKey(context) is { } client ? "paths:" + client : null,
        };
        if (options.CarriesUnsavedText && Api.Require(context, "maintainer") is { } forbidden)
            return forbidden;
        try
        {
            return Api.Json(await generation.PathsAsync(request.Pack, request.Unit, request.ElementIds, options, request.Limit ?? 200, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Superseded();
        }
        catch (ArgumentException ex)
        {
            return Api.BadRequest(ex.Message);
        }
    });

    /// <summary>Completion data for a unit's templates: variables, record members and helpers.</summary>
    /// <param name="context">The request.</param>
    /// <param name="pack">The pack.</param>
    /// <param name="unit">The unit id.</param>
    /// <param name="generation">The generation service.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 400 or 404.</returns>
    [HttpGet("/api/templates/context")]
    public static Task<IResult> Context(HttpContext context, string? pack, string? unit, GenerationService generation, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (string.IsNullOrEmpty(pack) || string.IsNullOrEmpty(unit))
            return Api.BadRequest("pack and unit are required.");
        try
        {
            var result = await generation.GetTemplateContextAsync(pack, unit, ct).ConfigureAwait(false);
            return result is null ? Api.NotFound("unit", pack + "/" + unit) : Api.Json(result);
        }
        catch (ArgumentException ex)
        {
            return Api.BadRequest(ex.Message);
        }
    });
}
