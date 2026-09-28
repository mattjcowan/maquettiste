using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using StaticSiteHost.Functions;

namespace Maquettiste.Functions;

/// <summary>
/// The one middleware (phase2-design.md section 3.3): identifies the caller, refuses cross-site forgery, lets the anonymous paths
/// through, answers everyone else with 401 or the sign-in page, and turns an unexpected failure under <c>/api/</c> into a 500 problem.
/// </summary>
public static class SignInGate
{
    private const string PageSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'";

    /// <summary>Runs on every request to the site except <c>/_host/*</c> and <c>/healthz</c>, which the host answers first.</summary>
    /// <param name="context">The request.</param>
    /// <param name="next">The rest of the site: handlers, then files.</param>
    /// <param name="auth">The sign-in state.</param>
    /// <param name="logger">The functions' logger.</param>
    /// <returns>A task.</returns>
    [Middleware(Order = 0)]
    public static async Task Run(HttpContext context, Func<Task> next, EditorAuth auth, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(auth);
        var request = context.Request;
        var path = request.Path;
        if (path.StartsWithSegments("/_host", StringComparison.OrdinalIgnoreCase) || path.Equals("/healthz", StringComparison.OrdinalIgnoreCase))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var api = path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
        var method = request.Method;

        // 1. Identify.
        var wrongBearer = auth.HasWrongBearer(context);
        var user = wrongBearer ? null : auth.Identify(context);

        // 2. Forgery checks: a foreign Origin, and a POST or PUT under /api/ that is not JSON (the sign-in form excepted).
        if (HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method))
        {
            if (request.Headers.Origin is { Count: > 0 } origin && !SameOrigin(origin.ToString(), request))
            {
                await Write(context, Api.Problem("forbidden-origin", "Requests from another site are refused.", StatusCodes.Status403Forbidden)).ConfigureAwait(false);
                return;
            }

            var signInForm = HttpMethods.IsPost(method) && path.Equals("/api/session", StringComparison.OrdinalIgnoreCase) && IsForm(request.ContentType);
            if (api && (HttpMethods.IsPost(method) || HttpMethods.IsPut(method)) && !IsJson(request.ContentType) && !signInForm)
            {
                await Write(context, Api.Problem("unsupported-media-type", "Send the body as application/json.", StatusCodes.Status415UnsupportedMediaType))
                    .ConfigureAwait(false);
                return;
            }
        }

        // 3. Anonymous paths go on without a user; a wrong bearer token is refused everywhere else, even from a local peer.
        var anonymous = (HttpMethods.IsGet(method) && path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
            || ((HttpMethods.IsGet(method) || HttpMethods.IsPost(method)) && path.Equals("/api/session", StringComparison.OrdinalIgnoreCase));
        if (wrongBearer && !anonymous)
        {
            await Write(context, Api.Problem("bad-token", "The editor token is not valid.", StatusCodes.Status401Unauthorized)).ConfigureAwait(false);
            return;
        }

        // 4. Unauthenticated.
        if (user is null && !anonymous)
        {
            if (!api && (HttpMethods.IsGet(method) || HttpMethods.IsHead(method)))
            {
                await WriteSignInPage(context, StatusCodes.Status200OK, null).ConfigureAwait(false);
                return;
            }

            await Write(context, Api.Problem("unauthenticated", "Sign in to use the editor.", StatusCodes.Status401Unauthorized)).ConfigureAwait(false);
            return;
        }

        // 5. Admit.
        if (user is not null)
            context.User = user.ToPrincipal();
        if (api && !anonymous && EditorSettings.ModeOf(auth.VariablesFor(context)) == "hosted")
        {
            await Write(context, Api.Problem("model-unavailable", "Hosted mode arrives in phase 4; this editor serves local mode only.",
                StatusCodes.Status503ServiceUnavailable)).ConfigureAwait(false);
            return;
        }

        try
        {
            await next().ConfigureAwait(false);
        }
        catch (Exception ex) when (api && !context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            var unavailable = ex is IOException or UnauthorizedAccessException;
            logger.LogError(ex, "maquettiste: {Method} {Path} failed ({TraceId})", method, path.Value, context.TraceIdentifier);
            context.Response.Clear();
            await Write(context, unavailable
                ? Api.Problem("model-unavailable", "The model folder cannot be read.", StatusCodes.Status503ServiceUnavailable, ex.Message, context.TraceIdentifier)
                : Api.Problem("internal", "The editor failed to answer this request.", StatusCodes.Status500InternalServerError, null, context.TraceIdentifier))
                .ConfigureAwait(false);
        }
    }

    /// <summary>Writes the self-contained sign-in page (one token field posting to <c>/api/session</c>).</summary>
    /// <param name="context">The request.</param>
    /// <param name="status">The status code.</param>
    /// <param name="error">A message to show, or <see langword="null"/>.</param>
    /// <returns>A task.</returns>
    public static Task WriteSignInPage(HttpContext context, int status, string? error)
    {
        ArgumentNullException.ThrowIfNull(context);
        var returnUrl = context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ? "/" : context.Request.Path + context.Request.QueryString;
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.ContentSecurityPolicy = PageSecurityPolicy;
        context.Response.Headers.CacheControl = "no-store";
        var message = error is null ? "" : $"<p class=\"error\">{WebUtility.HtmlEncode(error)}</p>";
        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Sign in to Maquettiste</title>
            <style>
            :root { color-scheme: light dark; --bg: #f6f7f9; --panel: #ffffff; --text: #1a1d23; --muted: #5b6472; --border: #7d8694; --accent: #2f6fdb; }
            @media (prefers-color-scheme: dark) { :root { --bg: #0f1115; --panel: #171a21; --text: #e6e8ec; --muted: #a1a9b6; --border: #6b7482; --accent: #4c8df6; } }
            body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: var(--bg); color: var(--text); font: 14px/1.5 system-ui, sans-serif; }
            form { width: min(360px, calc(100vw - 32px)); background: var(--panel); border: 1px solid var(--border); border-radius: 8px; padding: 24px; }
            h1 { font-size: 20px; margin: 0 0 8px; }
            p { color: var(--muted); margin: 0 0 16px; }
            label { display: block; font-weight: 600; margin-bottom: 4px; }
            input { box-sizing: border-box; width: 100%; padding: 8px; border: 1px solid var(--border); border-radius: 6px; background: transparent; color: inherit; font: inherit; }
            button { margin-top: 16px; width: 100%; padding: 8px; border: 0; border-radius: 6px; background: var(--accent); color: #fff; font: inherit; font-weight: 600; }
            .error { color: #c0392b; }
            </style>
            </head>
            <body>
            <form method="post" action="/api/session">
            <h1>Sign in to Maquettiste</h1>
            <p>This editor is not on your machine's loopback address, so it needs the editor token (MAQUETTISTE_EDITOR_TOKEN).</p>
            {{message}}
            <label for="token">Editor token</label>
            <input id="token" name="token" type="password" autocomplete="current-password" required autofocus>
            <input name="returnUrl" type="hidden" value="{{WebUtility.HtmlEncode(returnUrl)}}">
            <button type="submit">Sign in</button>
            </form>
            </body>
            </html>
            """;
        return HttpMethods.IsHead(context.Request.Method) ? Task.CompletedTask : context.Response.WriteAsync(html, context.RequestAborted);
    }

    /// <summary>Whether an <c>Origin</c> header names this request's own origin, <c>&lt;scheme&gt;://&lt;Host&gt;</c>.</summary>
    /// <param name="origin">The header value.</param>
    /// <param name="request">The request.</param>
    /// <returns><see langword="true"/> when it is the site itself.</returns>
    public static bool SameOrigin(string origin, HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return string.Equals(origin.Trim().TrimEnd('/'), request.Scheme + "://" + request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a content type is <c>application/json</c> (parameters such as <c>charset</c> allowed).</summary>
    /// <param name="contentType">The header value.</param>
    /// <returns><see langword="true"/> for JSON.</returns>
    public static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var media) && string.Equals(media.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a content type is <c>application/x-www-form-urlencoded</c>.</summary>
    /// <param name="contentType">The header value.</param>
    /// <returns><see langword="true"/> for a form post.</returns>
    public static bool IsForm(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var media)
        && string.Equals(media.MediaType.Value, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);

    private static Task Write(HttpContext context, IResult result) => result.ExecuteAsync(context);
}
