using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maquettiste.Functions;

/// <summary>Who the caller is.</summary>
/// <param name="User">The user.</param>
/// <param name="Via"><c>local</c>, <c>token</c> or <c>cookie</c>.</param>
/// <param name="Mode"><c>local</c> or <c>hosted</c>.</param>
public sealed record SessionInfo(SessionUser User, string Via, string Mode);

/// <summary>A signed-in user.</summary>
/// <param name="Name">The user name.</param>
/// <param name="DisplayName">The name shown.</param>
/// <param name="Role">The role.</param>
public sealed record SessionUser(string Name, string DisplayName, string Role);

/// <summary>The sign-in body (JSON, or the sign-in page's form).</summary>
/// <param name="Token">The editor token.</param>
/// <param name="ReturnUrl">Form posts only: a local path to redirect to.</param>
public sealed record SignInRequest(string? Token, string? ReturnUrl);

/// <summary><c>GET</c>, <c>POST</c> and <c>DELETE /api/session</c>: who the caller is, and token sign-in for non-local browsers (PD11).</summary>
public static class SessionEndpoints
{
    /// <summary>Who the caller is.</summary>
    /// <param name="context">The request.</param>
    /// <param name="auth">The sign-in state.</param>
    /// <returns>200 with <see cref="SessionInfo"/>, or 401.</returns>
    [HttpGet("/api/session")]
    public static IResult Get(HttpContext context, EditorAuth auth) => Api.Guard(context, () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(auth);
        return EditorUser.From(context.User) is { } user
            ? Api.Json(Info(user, context, auth))
            : Api.Problem("unauthenticated", "Sign in to use the editor.", StatusCodes.Status401Unauthorized);
    });

    /// <summary>
    /// Signs in with the editor token and sets the <c>mq_session</c> cookie, bound to the token's fingerprint. A JSON body answers 200 with
    /// the session; the sign-in page's form answers 303 to its <c>returnUrl</c> (a local path) or <c>/</c>. Five failures a minute from one
    /// address answer 429.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="auth">The sign-in state.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 303, 401 or 429.</returns>
    [HttpPost("/api/session")]
    public static Task<IResult> SignIn(HttpContext context, EditorAuth auth, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(auth);
        var address = EditorAuth.AddressOf(context);
        if (auth.IsThrottled(address))
            return Api.Problem("too-many-attempts", "Too many failed sign-ins; wait a minute.", StatusCodes.Status429TooManyRequests);

        var form = SignInGate.IsForm(context.Request.ContentType);
        SignInRequest? body;
        if (form)
        {
            var fields = await context.Request.ReadFormAsync(ct).ConfigureAwait(false);
            body = new SignInRequest(fields["token"].ToString(), fields["returnUrl"].ToString());
        }
        else
        {
            var (json, error) = await Api.ReadBodyAsync(context.Request, ct).ConfigureAwait(false);
            if (error is not null)
                return error;
            try
            {
                body = JsonSerializer.Deserialize<SignInRequest>(json, Api.JsonOptions);
            }
            catch (JsonException)
            {
                body = null;
            }
        }

        if (!auth.CheckToken(context, body?.Token))
        {
            auth.RecordFailure(address);
            if (form)
                return new SignInPage("That is not the editor token.");
            return Api.Problem("bad-token", "The editor token is not valid.", StatusCodes.Status401Unauthorized);
        }

        var user = new EditorUser("token", "Token holder", "admin", "cookie");
        auth.IssueCookie(context, user);
        if (form)
            return new SeeOther(LocalPath(body!.ReturnUrl));
        return Api.Json(Info(user, context, auth));
    });

    /// <summary>Clears the session cookie. A local caller stays signed in, since local trust needs no cookie.</summary>
    /// <param name="context">The request.</param>
    /// <returns>204.</returns>
    [HttpDelete("/api/session")]
    public static IResult SignOut(HttpContext context) => Api.Guard(context, () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        EditorAuth.ClearCookie(context);
        return Results.NoContent();
    });

    /// <summary>A redirect target that stays on this site: a path starting with one <c>/</c>, else <c>/</c>.</summary>
    /// <param name="returnUrl">The requested target.</param>
    /// <returns>The target.</returns>
    public static string LocalPath(string? returnUrl) =>
        returnUrl is { Length: > 0 } url && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\')) ? url : "/";

    private static SessionInfo Info(EditorUser user, HttpContext context, EditorAuth auth) =>
        new(new SessionUser(user.Name, user.DisplayName, user.Role), user.Via, EditorSettings.ModeOf(auth.VariablesFor(context)));

    /// <summary>303 See Other to a local path, after a form sign-in.</summary>
    private sealed class SeeOther(string location) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
            httpContext.Response.Headers.Location = location;
            return Task.CompletedTask;
        }
    }

    /// <summary>The sign-in page again, with a message, after a failed form sign-in (401).</summary>
    private sealed class SignInPage(string error) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => SignInGate.WriteSignInPage(httpContext, StatusCodes.Status401Unauthorized, error);
    }
}
