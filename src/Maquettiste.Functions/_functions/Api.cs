using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StaticSiteHost.Functions;

namespace Maquettiste.Functions;

/// <summary>
/// What every handler shares (phase2-design.md section 3.7): the JSON options engine records are written with, problem documents,
/// ETags and <c>If-Match</c>, the request body reader and the outcome-to-status map.
/// </summary>
public static class Api
{
    /// <summary>
    /// The options every body is serialized with: <see cref="JsonSerializerDefaults.Web"/> and nothing else (host-contracts requirement 5),
    /// held here so their type cache goes with the functions when they are unloaded.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The largest request body a handler reads: 4 MB.</summary>
    public const int MaxBodyBytes = 4 * 1024 * 1024;

    /// <summary>The largest realtime payload the functions build before cutting it: 200 KB, under the hub's 256 KB.</summary>
    public const int MaxEventBytes = 200 * 1024;

    private static readonly string[] Roles = ["viewer", "editor", "maintainer", "admin"];

    /// <summary>A JSON body serialized with <see cref="JsonOptions"/>.</summary>
    /// <typeparam name="T">The body type (serialized as declared).</typeparam>
    /// <param name="value">The body.</param>
    /// <param name="status">The status code.</param>
    /// <returns>The result.</returns>
    public static IResult Json<T>(T value, int status = StatusCodes.Status200OK) => Results.Json(value, JsonOptions, statusCode: status);

    /// <summary>An RFC 9457 problem document (<c>application/problem+json</c>) with a stable <c>code</c> (PD12).</summary>
    /// <param name="code">The code, one of the <c>Problem.code</c> values of the contract.</param>
    /// <param name="title">A sentence for people.</param>
    /// <param name="status">The status code.</param>
    /// <param name="detail">More detail, when there is any.</param>
    /// <param name="traceId">The request's trace id, for <c>internal</c> problems.</param>
    /// <returns>The result.</returns>
    public static IResult Problem(string code, string title, int status, string? detail = null, string? traceId = null)
    {
        var body = new JsonObject { ["type"] = "about:blank", ["title"] = title, ["status"] = status, ["code"] = code };
        if (detail is not null)
            body["detail"] = detail;
        if (traceId is not null)
            body["traceId"] = traceId;
        return Results.Json(body, JsonOptions, "application/problem+json", status);
    }

    /// <summary>The 404 problem for an id nothing has.</summary>
    /// <param name="what">What was looked for (<c>element</c>, <c>plan</c>, <c>job</c>…).</param>
    /// <param name="id">The id.</param>
    /// <param name="code">The code (<c>not-found</c> unless a more precise one applies).</param>
    /// <returns>The result.</returns>
    public static IResult NotFound(string what, string id, string code = "not-found") =>
        Problem(code, $"No {what} has the id {id}.", StatusCodes.Status404NotFound);

    /// <summary>The 400 problem for a body that is not what the operation takes.</summary>
    /// <param name="detail">What is wrong.</param>
    /// <returns>The result.</returns>
    public static IResult BadRequest(string detail) => Problem("bad-request", "The request is not valid.", StatusCodes.Status400BadRequest, detail);

    /// <summary>A strong ETag for a hash: <c>"&lt;hash&gt;"</c>.</summary>
    /// <param name="hash">The lowercase hex hash.</param>
    /// <returns>The header value.</returns>
    public static string ETag(string hash) => "\"" + hash + "\"";

    /// <summary>Sets <c>ETag</c> on the response.</summary>
    /// <param name="context">The request.</param>
    /// <param name="hash">The hash, or <see langword="null"/> to set nothing.</param>
    public static void SetETag(HttpContext context, string? hash)
    {
        if (hash is not null)
            context.Response.Headers.ETag = ETag(hash);
    }

    /// <summary>Reads <c>If-Match</c>: <c>"&lt;hash&gt;"</c>, <c>W/"&lt;hash&gt;"</c> or the bare hash.</summary>
    /// <param name="request">The request.</param>
    /// <param name="hash">The hash, without quotes or the weak prefix.</param>
    /// <returns><see langword="false"/> when the header is missing or empty (428).</returns>
    public static bool TryGetIfMatch(HttpRequest request, out string hash) => TryReadTag(request.Headers.IfMatch.ToString(), out hash);

    /// <summary>Reads an entity tag in any of the forms <see cref="TryGetIfMatch"/> accepts.</summary>
    /// <param name="value">The header value.</param>
    /// <param name="hash">The bare tag.</param>
    /// <returns><see langword="false"/> when the value is empty.</returns>
    public static bool TryReadTag(string? value, out string hash)
    {
        var tag = (value ?? "").Trim();
        if (tag.StartsWith("W/", StringComparison.Ordinal))
            tag = tag[2..];
        hash = tag.Trim('"');
        return hash.Length > 0;
    }

    /// <summary>The 428 problem for a save or delete without <c>If-Match</c>.</summary>
    /// <returns>The result.</returns>
    public static IResult PreconditionRequired() =>
        Problem("precondition-required", "Send the hash you loaded in If-Match.", StatusCodes.Status428PreconditionRequired);

    /// <summary>Reads the whole request body, refusing more than <see cref="MaxBodyBytes"/>.</summary>
    /// <param name="request">The request.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The bytes, or the 400 <c>too-large</c> problem.</returns>
    public static async Task<(byte[]? Body, IResult? Error)> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxBodyBytes)
            return (null, TooLarge());
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
                return (null, TooLarge());
            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), null);
    }

    /// <summary>Reads an optional JSON body into <typeparamref name="T"/>: an empty body is <paramref name="empty"/>.</summary>
    /// <typeparam name="T">The body type.</typeparam>
    /// <param name="request">The request.</param>
    /// <param name="empty">The value of an empty body, or <see langword="null"/> when a body is required.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The value, or a 400 problem.</returns>
    public static async Task<(T? Value, IResult? Error)> ReadJsonAsync<T>(HttpRequest request, T? empty, CancellationToken ct) where T : class
    {
        var (body, error) = await ReadBodyAsync(request, ct).ConfigureAwait(false);
        if (error is not null)
            return (null, error);
        if (body!.AsSpan().Trim(" \t\r\n"u8).Length == 0)
            return empty is null ? (null, BadRequest("The request body is empty.")) : (empty, null);
        try
        {
            var value = JsonSerializer.Deserialize<T>(body, JsonOptions);
            return value is null ? (null, BadRequest("The request body is null.")) : (value, null);
        }
        catch (JsonException ex)
        {
            return (null, BadRequest(ex.Message));
        }
    }

    /// <summary>The status an engine outcome maps to: saved 200 (201 on create), conflict 409, invalid 422, not-found 404, referenced 409.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <param name="created">Whether the operation created the element.</param>
    /// <returns>The status code.</returns>
    public static int StatusOf(SaveOutcome outcome, bool created = false) => outcome switch
    {
        SaveOutcome.Saved => created ? StatusCodes.Status201Created : StatusCodes.Status200OK,
        SaveOutcome.Conflict or SaveOutcome.Referenced => StatusCodes.Status409Conflict,
        SaveOutcome.Invalid => StatusCodes.Status422UnprocessableEntity,
        SaveOutcome.NotFound => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>
    /// The 403 problem when the caller's role is below <paramref name="role"/> (viewer &lt; editor &lt; maintainer &lt; admin), else
    /// <see langword="null"/>. Phase 2 callers are all admin, so the check never refuses yet.
    /// </summary>
    /// <param name="context">The request, whose user the sign-in gate set.</param>
    /// <param name="role">The least role the operation needs.</param>
    /// <returns>The problem, or <see langword="null"/>.</returns>
    public static IResult? Require(HttpContext context, string role)
    {
        var have = Array.IndexOf(Roles, context.User.FindFirst(ClaimTypes.Role)?.Value);
        return have >= Array.IndexOf(Roles, role) && have >= 0
            ? null
            : Problem("forbidden", $"This needs the {role} role.", StatusCodes.Status403Forbidden);
    }

    /// <summary>Whether a string is an uppercase Crockford ULID (engine-design.md D1).</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when it is one.</returns>
    public static bool IsUlid(string? value)
    {
        if (value is not { Length: 26 } || value[0] > '7' || value[0] < '0')
            return false;
        foreach (var c in value)
        {
            if (!(c is >= '0' and <= '9' || c is >= 'A' and <= 'Z') || c is 'I' or 'L' or 'O' or 'U')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Runs a handler body and turns an exception it throws into the contract's problem (phase2-design.md section 3.7): an
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> is 503 <c>model-unavailable</c>, anything else is
    /// 500 <c>internal</c> with the trace id. The host catches handler exceptions itself and answers <c>500 text/plain</c>, so
    /// the mapping has to happen inside each handler; the sign-in gate never sees them.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="body">The handler body.</param>
    /// <returns>The body's result, or the problem.</returns>
    public static async Task<IResult> GuardAsync(HttpContext context, Func<Task<IResult>> body)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(body);
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Failure(context, ex);
        }
    }

    /// <summary>The synchronous form of <see cref="GuardAsync"/>.</summary>
    /// <param name="context">The request.</param>
    /// <param name="body">The handler body.</param>
    /// <returns>The body's result, or the problem.</returns>
    public static IResult Guard(HttpContext context, Func<IResult> body)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(body);
        try
        {
            return body();
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Failure(context, ex);
        }
    }

    /// <summary>Logs a handler failure and returns its problem.</summary>
    /// <param name="context">The request.</param>
    /// <param name="ex">The failure.</param>
    /// <returns>503 <c>model-unavailable</c> or 500 <c>internal</c>.</returns>
    public static IResult Failure(HttpContext context, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ex);
        var logger = (context.TryGetSite(out var site) ? site.Services.GetService<ILogger>() : null)
            ?? context.RequestServices?.GetService<ILogger>() ?? NullLogger.Instance;
        logger.LogError(ex, "maquettiste: {Method} {Path} failed ({TraceId})", context.Request.Method, context.Request.Path.Value, context.TraceIdentifier);
        return ex is IOException or UnauthorizedAccessException
            ? Problem("model-unavailable", "The model folder cannot be read.", StatusCodes.Status503ServiceUnavailable, ex.Message, context.TraceIdentifier)
            : Problem("internal", "The editor failed to answer this request.", StatusCodes.Status500InternalServerError, null, context.TraceIdentifier);
    }

    private static IResult TooLarge() =>
        Problem("too-large", "The request body is larger than 4 MB.", StatusCodes.Status400BadRequest);
}
