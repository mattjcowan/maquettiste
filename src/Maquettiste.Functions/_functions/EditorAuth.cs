using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
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
/// Who a request is (phase2-design.md section 3.3, PD9, PD11): the local developer, a bearer of the editor token, or the holder of a
/// session cookie issued for the current token; plus the failed sign-in throttle. Variables are read at call time, so rotating the
/// token takes effect at once and ends every cookie session.
/// </summary>
public sealed class EditorAuth
{
    /// <summary>The session cookie's name.</summary>
    public const string CookieName = "mq_session";

    /// <summary>How long a session cookie is honoured.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);

    /// <summary>Failed sign-ins an address may make per minute before it gets 429.</summary>
    public const int MaxFailuresPerMinute = 5;

    private static readonly string[] ForwardingHeaders = ["Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Real-IP"];

    private readonly ISiteVariables _variables;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<IPAddress, byte> _rejectedPeers = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _failures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedPeers = new(StringComparer.Ordinal);

    /// <summary>Creates the sign-in state.</summary>
    /// <param name="variables">The site's variables, used outside a request (a request reads its own).</param>
    /// <param name="protection">The host's data protection, for the session cookie.</param>
    /// <param name="time">The clock.</param>
    /// <param name="loggers">The loggers.</param>
    public EditorAuth(ISiteVariables variables, IDataProtectionProvider protection, TimeProvider time, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(loggers);
        _variables = variables ?? throw new ArgumentNullException(nameof(variables));
        _protector = protection.CreateProtector("Maquettiste.Session.v1");
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = loggers.CreateLogger("maquettiste.auth");
    }

    /// <summary>
    /// Who the caller is: the local developer (local trust), a bearer of the token, or a valid session cookie, in that order; a wrong
    /// bearer token identifies nobody, even from a local peer (<see cref="HasWrongBearer"/> says so).
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The user, or <see langword="null"/>.</returns>
    public EditorUser? Identify(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var variables = VariablesFor(context);
        var bearer = Bearer(context);
        if (bearer is not null && !TokenMatches(variables, bearer))
            return null;
        if (IsLocal(context, variables))
            return new EditorUser(LocalUserName(variables), "Local developer", "admin", "local");
        if (bearer is not null)
            return new EditorUser("token", "Token holder", "admin", "token");
        return FromCookie(context, variables);
    }

    /// <summary>Whether the request carries <c>Authorization: Bearer</c> with a token that is not the editor token (401 <c>bad-token</c>).</summary>
    /// <param name="context">The request.</param>
    /// <returns><see langword="true"/> for a wrong bearer token.</returns>
    public bool HasWrongBearer(HttpContext context)
    {
        var bearer = Bearer(context);
        return bearer is not null && !TokenMatches(VariablesFor(context), bearer);
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
        context.Response.Cookies.Append(CookieName, value, CookieOptions(context));
    }

    /// <summary>Clears the session cookie.</summary>
    /// <param name="context">The request.</param>
    public static void ClearCookie(HttpContext context) => context.Response.Cookies.Delete(CookieName, CookieOptions(context));

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

    /// <summary>
    /// Whether the request is the local developer: local mode with local trust on, a peer that is loopback or one of
    /// <c>MAQUETTISTE_LOCAL_PEERS</c> (both compared after <see cref="Normalize"/>), a local <c>Host</c>, and no forwarding header.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="variables">The variables in force.</param>
    /// <returns><see langword="true"/> for the local developer.</returns>
    public bool IsLocal(HttpContext context, ISiteVariables variables)
    {
        if (!EditorSettings.LocalTrustOn(variables))
            return false;
        var peer = Normalize(context.Connection.RemoteIpAddress);
        if (peer is null)
            return false;
        if (!IsLocalHost(context.Request.Host.Host))
            return false;
        if (!(IPAddress.IsLoopback(peer) || LocalPeers(variables).Contains(peer)))
        {
            NoteRejectedPeer(peer);
            return false;
        }

        foreach (var header in ForwardingHeaders)
        {
            if (context.Request.Headers.ContainsKey(header))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Logs, once per address (at most 16), a request for the local host name that came from an address that is not a local peer, so a
    /// developer on Docker Desktop or behind another network layer can find the value to put in <c>MAQUETTISTE_LOCAL_PEERS</c>.
    /// </summary>
    /// <param name="peer">The rejected peer.</param>
    private void NoteRejectedPeer(IPAddress peer)
    {
        if (_rejectedPeers.Count >= 16 || !_rejectedPeers.TryAdd(peer, 0))
            return;
        _logger.LogWarning(
            "maquettiste: a request for the local editor came from {Peer}, which is not a local peer, so it must sign in. If that is your browser, set MAQUETTISTE_LOCAL_PEERS={Peer} (never *) or set MAQUETTISTE_EDITOR_TOKEN and sign in.",
            peer, peer);
    }

    /// <summary>The parsed <c>MAQUETTISTE_LOCAL_PEERS</c>; an entry that is not an IP address (<c>*</c> included) is logged once and ignored.</summary>
    /// <param name="variables">The variables in force.</param>
    /// <returns>The normalised addresses.</returns>
    public IReadOnlyList<IPAddress> LocalPeers(ISiteVariables variables)
    {
        var peers = new List<IPAddress>();
        foreach (var entry in variables.Get(EditorSettings.LocalPeersVariable).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(entry, out var address) && entry.Any(c => c is '.' or ':'))
            {
                peers.Add(Normalize(address)!);
                continue;
            }

            bool first;
            lock (_gate)
                first = _reportedPeers.Add(entry);
            if (first)
                _logger.LogWarning("maquettiste: MAQUETTISTE_LOCAL_PEERS entry '{Entry}' is not an IP address and is ignored.", entry);
        }

        return peers;
    }

    /// <summary>Whether a host name (without its port) names this machine: <c>localhost</c>, a <c>*.localhost</c> name, <c>127.0.0.1</c> or <c>[::1]</c>.</summary>
    /// <param name="host">The host.</param>
    /// <returns><see langword="true"/> when it is local.</returns>
    public static bool IsLocalHost(string? host)
    {
        var name = (host ?? "").TrimEnd('.').ToLowerInvariant();
        return name is "localhost" or "127.0.0.1" or "[::1]" or "::1" || name.EndsWith(".localhost", StringComparison.Ordinal);
    }

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
        if (token.Length == 0 || !context.Request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrEmpty(value))
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
