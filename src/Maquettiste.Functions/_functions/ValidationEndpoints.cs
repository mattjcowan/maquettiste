using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maquettiste.Functions;

/// <summary><c>POST /api/validate</c>.</summary>
public static class ValidationEndpoints
{
    /// <summary>Validates the whole model (an empty body or <c>{}</c>) or a scope; load diagnostics included, never truncated.</summary>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with the report, or 400.</returns>
    [HttpPost("/api/validate")]
    public static Task<IResult> Validate(HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        var (scope, error) = await Api.ReadJsonAsync(context.Request, ValidationScope.All, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        if (scope!.ElementIds is { } ids && ids.Any(id => !Api.IsUlid(id)))
            return Api.BadRequest("elementIds must be element ids (uppercase ULIDs).");
        return Api.Json(await store.ValidateAsync(scope, ct).ConfigureAwait(false));
    });
}
