using Maquettiste.Engine;
using Maquettiste.Engine.Branding;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maquettiste.Functions;

/// <summary>The body of <c>POST /api/project/branding/icon</c>.</summary>
/// <param name="ContentType"><c>image/svg+xml</c> or <c>image/png</c>.</param>
/// <param name="Data">The file, base64.</param>
public sealed record BrandingIconUpload(string? ContentType, string? Data);

/// <summary>The answer to an icon upload.</summary>
/// <param name="Icon">The model-relative path to save as <c>branding.icon</c>.</param>
/// <param name="ContentType">The stored file's media type.</param>
/// <param name="Hash">The stored file's content hash.</param>
/// <param name="Removed">What was removed from an SVG before it was stored.</param>
public sealed record BrandingIconSaved(string Icon, string ContentType, string Hash, IReadOnlyList<string> Removed);

/// <summary><c>POST</c> and <c>GET /api/project/branding/icon</c>: the project icon (settings <c>branding.icon</c>).</summary>
public static class BrandingEndpoints
{
    /// <summary>The icon's own policy: an SVG served from the site may not run scripts or load anything.</summary>
    private const string IconSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; sandbox";

    /// <summary>Stores an SVG (sanitized) or PNG icon of at most 512 KB under <c>.maquettiste/branding/</c>; admin.</summary>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with the path to save, 400, 413 or 422.</returns>
    [HttpPost("/api/project/branding/icon")]
    public static Task<IResult> Upload(HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        if (Api.Require(context, "admin") is { } forbidden)
            return forbidden;
        var (body, error) = await Api.ReadJsonAsync<BrandingIconUpload>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        var contentType = body?.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
        if (contentType is not (BrandingIcons.Svg or BrandingIcons.Png))
            return Api.BadRequest("contentType must be image/svg+xml or image/png.");
        if (string.IsNullOrEmpty(body!.Data))
            return Api.BadRequest("data must hold the file, base64.");
        if (body.Data.Length > (BrandingIcons.MaxBytes + 2) / 3 * 4 + 4)
            return TooLarge();
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(body.Data);
        }
        catch (FormatException)
        {
            return Api.BadRequest("data is not base64.");
        }

        var result = await store.SaveBrandingIconAsync(contentType, bytes, ct).ConfigureAwait(false);
        if (result.TooLarge)
            return TooLarge();
        if (!result.Saved)
            return Api.Problem("invalid-icon", "The icon cannot be used.", StatusCodes.Status422UnprocessableEntity, result.Problem);
        return Api.Json(new BrandingIconSaved(result.Icon!, contentType, result.Hash!, result.Removed));
    });

    /// <summary>The icon named by <c>branding.icon</c> (an SVG without unsafe parts); anonymous, for the sign-in page and the tab.</summary>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with the file, 304, or 404 when the project has no usable icon.</returns>
    [HttpGet("/api/project/branding/icon")]
    public static Task<IResult> Read(HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        var icon = await store.ReadBrandingIconAsync(ct).ConfigureAwait(false);
        if (icon is null)
            return Api.Problem("not-found", "The project has no icon.", StatusCodes.Status404NotFound);
        var headers = context.Response.Headers;
        Api.SetETag(context, icon.Hash);
        headers.CacheControl = "no-cache";
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = IconSecurityPolicy;
        if (Api.TryReadTag(context.Request.Headers.IfNoneMatch.ToString(), out var tag) && string.Equals(tag, icon.Hash, StringComparison.Ordinal))
            return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Bytes(icon.Bytes, icon.ContentType);
    });

    private static IResult TooLarge() =>
        Api.Problem("too-large", "The icon is too large.", StatusCodes.Status413PayloadTooLarge, $"An icon may be at most {BrandingIcons.MaxBytes / 1024} KB.");
}
