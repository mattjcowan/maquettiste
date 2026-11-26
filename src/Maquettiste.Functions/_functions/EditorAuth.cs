using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using StaticSiteHost.Functions;

namespace Maquettiste.Functions;

/// <summary>A signed-in caller.</summary>
/// <param name="Name">The user name (realtime presence and <c>PublishToUserAsync</c> use it).</param>
/// <param name="DisplayName">The name shown in the editor.</param>
/// <param name="Role"><c>viewer</c>, <c>editor</c>, <c>maintainer</c> or <c>admin</c>; phase 2 callers are all <c>admin</c>.</param>
/// <param name="Via">How the caller signed in: <c>local</c>, <c>token</c> or <c>cookie</c>.</param>
public sealed record EditorUser(string Name, string DisplayName, string Role, string Via)
{
    /// <summary>The claim type holding <see cref="Via"/>.</summary>
    public const string ViaClaim = "mq:via";

    /// <summary>The principal the sign-in gate puts on the request.</summary>
    /// <returns>The principal.</returns>
    public ClaimsPrincipal ToPrincipal() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, Name), new Claim(ClaimTypes.GivenName, DisplayName), new Claim(ClaimTypes.Role, Role), new Claim(ViaClaim, Via)],
        "maquettiste", ClaimTypes.Name, ClaimTypes.Role));

    /// <summary>Reads the user the sign-in gate put on the request.</summary>
    /// <param name="principal">The request's principal.</param>
    /// <returns>The user, or <see langword="null"/> when the request is anonymous.</returns>
    public static EditorUser? From(ClaimsPrincipal principal)
    {
        if (principal.Identity is not { IsAuthenticated: true } || principal.FindFirst(ClaimTypes.Name)?.Value is not { } name)
            return null;
        return new EditorUser(name, principal.FindFirst(ClaimTypes.GivenName)?.Value ?? name, principal.FindFirst(ClaimTypes.Role)?.Value ?? "viewer",
            principal.FindFirst(ViaClaim)?.Value ?? "token");
    }
}

/// <summary>
/// Who a request is (phase2-design.md section 3.3, PD9, PD11). In local mode without an editor token the editor is open: every request is
/// the local developer and nobody signs in. Once a token is set, a caller is a bearer of the token or the holder of a session cookie
/// issued for it; plus the failed sign-in throttle. Variables are read at call time, so setting, rotating or clearing the token takes
/// effect at once, and rotating it ends every cookie session.
/// </summary>
public sealed class EditorAuth
{
    /// <summary>The session cookie's name on a host without an explicit port (<see cref="CookieNameFor"/>).</summary>
    public const string CookieName = "mq_session";

    /// <summary>
    /// The session cookie's name for a request: <c>mq_session</c>, or <c>mq_session_&lt;port&gt;</c> when the <c>Host</c> header names a
    /// port. Browsers keep one cookie jar per host whatever the port, so two editors on <c>localhost:8080</c> and <c>localhost:8081</c>
    /// would otherwise overwrite each other's session (and each, unable to read the other's, would sign the user out).
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The cookie name.</returns>
    public static string CookieNameFor(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Request.Host.Port is { } port ? CookieName + "_" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) : CookieName;
    }

    /// <summary>How long a session cookie is honoured.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);

    /// <summary>Failed sign-ins an address may make per minute before it gets 429.</summary>
    public const int MaxFailuresPerMinute = 5;

    private readonly ISiteVariables _variables;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _failures = new(StringComparer.Ordinal);

    /// <summary>Creates the sign-in state.</summary>
    /// <param name="variables">The site's variables, used outside a request (a request reads its own).</param>
    /// <param name="protection">The host's data protection, for the session cookie.</param>
    /// <param name="time">The clock.</param>
    public EditorAuth(ISiteVariables variables, IDataProtectionProvider protection, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(protection);
        _variables = variables ?? throw new ArgumentNullException(nameof(variables));
        _protector = protection.CreateProtector("Maquettiste.Session.v1");
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>
    /// Who the caller is. An open editor (<see cref="IsOpen"/>) answers the local developer for every request, whatever its address, host
    /// name or headers. Otherwise: a bearer of the token, or a valid session cookie; a wrong bearer token identifies nobody
    /// (<see cref="HasWrongBearer"/> says so).
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The user, or <see langword="null"/>.</returns>
    public EditorUser? Identify(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var variables = VariablesFor(context);
        if (IsOpen(variables))
            return new EditorUser(LocalUserName(variables), "Local developer", "admin", "local");
        if (Bearer(context) is { } bearer)
            return TokenMatches(variables, bearer) ? new EditorUser("token", "Token holder", "admin", "token") : null;
        return FromCookie(context, variables);
    }

    /// <summary>Whether the editor is open: local mode and no editor token set. Nobody signs in, and every request is the local developer.</summary>
    /// <param name="variables">The variables in force.</param>
    /// <returns><see langword="true"/> when open.</returns>
    public static bool IsOpen(ISiteVariables variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        return EditorSettings.ModeOf(variables) == "local" && variables.Get(EditorSettings.TokenVariable).Length == 0;
    }

    /// <summary>
    /// Whether the request carries <c>Authorization: Bearer</c> with a token that is not the editor token (401 <c>bad-token</c>); never on an
    /// open editor, which ignores credentials.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns><see langword="true"/> for a wrong bearer token.</returns>
    public bool HasWrongBearer(HttpContext context)
    {
        var variables = VariablesFor(context);
        return Bearer(context) is { } bearer && !IsOpen(variables) && !TokenMatches(variables, bearer);
    }

    /// <summary>Whether a presented token is the editor token (compared in fixed time; never when no token is set).</summary>
    /// <param name="context">The request (its variables are read now).</param>
    /// <param name="token">The presented token.</param>
    /// <returns><see langword="true"/> when it matches.</returns>
    public bool CheckToken(HttpContext context, string? token) => token is { Length: > 0 } && TokenMatches(VariablesFor(context), token);

    /// <summary>Sets the session cookie for a user, bound to the current token's fingerprint.</summary>
    /// <param name="context">The request.</param>
    /// <param name="user">The user.</param>
    public void IssueCookie(HttpContext context, EditorUser user)
    {
        var payload = new CookiePayload(user.Name, user.DisplayName, user.Role, _time.GetUtcNow().ToUnixTimeSeconds(),
            Fingerprint(VariablesFor(context).Get(EditorSettings.TokenVariable)));
        var value = _protector.Protect(JsonSerializer.Serialize(payload, Api.JsonOptions));
        context.Response.Cookies.Append(CookieNameFor(context), value, CookieOptions(context));
    }

    /// <summary>Clears the session cookie.</summary>
    /// <param name="context">The request.</param>
    public static void ClearCookie(HttpContext context) => context.Response.Cookies.Delete(CookieNameFor(context), CookieOptions(context));

    /// <summary>Whether an address has used up its failed sign-ins for the minute (429).</summary>
    /// <param name="address">The caller's address.</param>
    /// <returns><see langword="true"/> when throttled.</returns>
    public bool IsThrottled(string address)
    {
        lock (_gate)
            return Recent(address) >= MaxFailuresPerMinute;
    }

    /// <summary>Records a failed sign-in from an address.</summary>
    /// <param name="address">The caller's address.</param>
    public void RecordFailure(string address)
    {
        lock (_gate)
        {
            Recent(address);
            if (!_failures.TryGetValue(address, out var queue))
                _failures[address] = queue = new Queue<DateTimeOffset>();
            queue.Enqueue(_time.GetUtcNow());
        }
    }

    /// <summary>The caller's address, IPv4-mapped addresses normalised, as a throttle key.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The address, or <c>unknown</c>.</returns>
    public static string AddressOf(HttpContext context) => Normalize(context.Connection.RemoteIpAddress)?.ToString() ?? "unknown";

    /// <summary>An address with the IPv4-mapped form Kestrel's dual-stack socket reports (<c>::ffff:172.17.0.1</c>) made IPv4.</summary>
    /// <param name="address">The address.</param>
    /// <returns>The normalised address.</returns>
    public static IPAddress? Normalize(IPAddress? address) => address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;

    /// <summary>The first 16 hex of the SHA-256 of a token: what a session cookie is bound to.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The fingerprint.</returns>
    public static string Fingerprint(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16];

    /// <summary>The variables in force for a request: the request's own site's, else the ones this service was built with.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The variables.</returns>
    public ISiteVariables VariablesFor(HttpContext context) => context.TryGetSite(out var site) && site is not null ? site.Variables : _variables;

    private static string LocalUserName(ISiteVariables variables) => variables.Get(EditorSettings.LocalUserVariable, "local").Trim() is { Length: > 0 } name ? name : "local";

    private static string? Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
    }

    private static bool TokenMatches(ISiteVariables variables, string presented)
    {
        var token = variables.Get(EditorSettings.TokenVariable);
        if (token.Length == 0)
            return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), SHA256.HashData(Encoding.UTF8.GetBytes(presented)));
    }

    private EditorUser? FromCookie(HttpContext context, ISiteVariables variables)
    {
        var token = variables.Get(EditorSettings.TokenVariable);
        if (token.Length == 0 || !context.Request.Cookies.TryGetValue(CookieNameFor(context), out var value) || string.IsNullOrEmpty(value))
            return null;
        CookiePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CookiePayload>(_protector.Unprotect(value), Api.JsonOptions);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null;
        }

        if (payload is null || !string.Equals(payload.Fingerprint, Fingerprint(token), StringComparison.Ordinal))
            return null;
        var age = _time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(payload.IssuedUnix);
        if (age >= SessionLifetime || age < TimeSpan.FromMinutes(-5))
            return null;
        return new EditorUser(payload.Name, payload.DisplayName, payload.Role, "cookie");
    }

    private int Recent(string address)
    {
        if (!_failures.TryGetValue(address, out var queue))
            return 0;
        var cutoff = _time.GetUtcNow().AddMinutes(-1);
        while (queue.Count > 0 && queue.Peek() <= cutoff)
            queue.Dequeue();
        if (queue.Count == 0)
            _failures.Remove(address);
        return queue.Count;
    }

    private static CookieOptions CookieOptions(HttpContext context) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Secure = context.Request.IsHttps,
        IsEssential = true,
    };

    private sealed record CookiePayload(string Name, string DisplayName, string Role, long IssuedUnix, string Fingerprint);
}
