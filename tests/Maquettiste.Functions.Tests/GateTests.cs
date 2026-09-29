using System.Net;
using Maquettiste.Functions.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Maquettiste.Functions.Tests;

/// <summary>The sign-in gate and the hooks (phase2-design.md sections 3.3 and 3.4, PD9, PD11).</summary>
public sealed class GateTests
{
    private const string Index = "/api/model/index";

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task A_loopback_peer_with_a_local_host_is_the_local_admin(string address)
    {
        await using var host = EditorHost.Create();

        var response = await host.SendAsync(TestRequest.Local("GET", "/api/session").With(r => r.Remote = IPAddress.Parse(address)));

        Assert.Equal(200, response.Status);
        Assert.Equal("local", response.Json["via"]!.GetValue<string>());
        Assert.Equal("local", response.Json["user"]!["name"]!.GetValue<string>());
        Assert.Equal("admin", response.Json["user"]!["role"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:8080")]
    [InlineData("maquettiste.localhost:8080")]
    [InlineData("127.0.0.1:8080")]
    [InlineData("[::1]:8080")]
    public async Task Every_local_host_name_is_accepted(string hostHeader)
    {
        await using var host = EditorHost.Create();

        var response = await host.SendAsync(TestRequest.Local("GET", Index).With(r => r.Host = hostHeader));

        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task The_ipv4_mapped_gateway_is_local_only_when_listed_in_local_peers()
    {
        await using var listed = EditorHost.Create(v => v.Set("MAQUETTISTE_LOCAL_PEERS", "172.18.0.1"));
        await using var unlisted = EditorHost.Create();
        static TestRequest Gateway() => TestRequest.Local("GET", Index).With(r => r.Remote = IPAddress.Parse("::ffff:172.18.0.1"));

        var admitted = await listed.SendAsync(Gateway());
        var refused = await unlisted.SendAsync(Gateway());
        var plain = await listed.SendAsync(TestRequest.Local("GET", Index).With(r => r.Remote = IPAddress.Parse("172.18.0.1")));

        Assert.Equal(200, admitted.Status);
        Assert.Equal(401, refused.Status);
        Assert.Equal("unauthenticated", refused.ProblemCode);
        Assert.Equal(200, plain.Status);
    }

    [Fact]
    public async Task A_mapped_local_peers_entry_is_normalised_too()
    {
        await using var host = EditorHost.Create(v => v.Set("MAQUETTISTE_LOCAL_PEERS", "::ffff:172.18.0.1"));

        var response = await host.SendAsync(TestRequest.Local("GET", Index).With(r => r.Remote = IPAddress.Parse("172.18.0.1")));

        Assert.Equal(200, response.Status);
    }

    [Theory]
    [InlineData("editor.example.com")]
    [InlineData("192.168.1.20:8080")]
    [InlineData("localhost.example.com")]
    public async Task A_local_peer_naming_another_host_is_not_local(string hostHeader)
    {
        await using var host = EditorHost.Create();

        var response = await host.SendAsync(TestRequest.Local("GET", Index).With(r => r.Host = hostHeader));

        Assert.Equal(401, response.Status);
        Assert.Equal("unauthenticated", response.ProblemCode);
        Contract.AssertResponse(response, Index);
    }

    [Theory]
    [InlineData("X-Forwarded-For", "198.51.100.4")]
    [InlineData("Forwarded", "for=198.51.100.4")]
    [InlineData("X-Forwarded-Host", "editor.example.com")]
    [InlineData("X-Real-IP", "198.51.100.4")]
    public async Task A_local_peer_with_a_forwarding_header_is_not_local(string header, string value)
    {
        await using var host = EditorHost.Create();

        var response = await host.SendAsync(TestRequest.Local("GET", Index).Header(header, value));

        Assert.Equal(401, response.Status);
    }

    [Fact]
    public async Task Local_trust_off_makes_every_caller_present_a_credential()
    {
        await using var host = EditorHost.Create(v => v.Set("MAQUETTISTE_LOCAL_TRUST", "off"));

        var local = await host.GetAsync(Index);
        var bearer = await host.SendAsync(TestRequest.Local("GET", Index).Bearer(EditorHost.Token));

        Assert.Equal(401, local.Status);
        Assert.Equal(200, bearer.Status);
    }

    [Fact]
    public async Task Hosted_mode_trusts_no_local_peer_and_answers_503_for_the_model()
    {
        await using var host = EditorHost.Create(v => v.Set("MAQUETTISTE_MODE", "hosted"));

        var local = await host.GetAsync(Index);
        var bearer = await host.SendAsync(TestRequest.FromElsewhere("GET", Index).Bearer(EditorHost.Token));
        var health = await host.GetAsync("/api/health");

        Assert.Equal(401, local.Status);
        Assert.Equal(503, bearer.Status);
        Assert.Equal("model-unavailable", bearer.ProblemCode);
        Contract.AssertResponse(bearer, Index);
        Assert.Equal(200, health.Status);
    }

    [Fact]
    public async Task Unparsable_local_peer_entries_are_ignored_and_logged_once()
    {
        await using var host = EditorHost.Create(v => v.Set("MAQUETTISTE_LOCAL_PEERS", "not-an-ip, *, 172.18.0.1"));
        static TestRequest Gateway() => TestRequest.Local("GET", Index).With(r => r.Remote = IPAddress.Parse("::ffff:172.18.0.1"));

        var first = await host.SendAsync(Gateway());
        var second = await host.SendAsync(Gateway());
        var stranger = await host.SendAsync(TestRequest.Local("GET", Index).With(r => r.Remote = IPAddress.Parse("10.9.8.7")));

        Assert.Equal(200, first.Status);
        Assert.Equal(200, second.Status);
        Assert.Equal(401, stranger.Status); // '*' trusts nobody (owner decision 3)
        var warnings = host.Logs.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("is not an IP address", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Single(warnings, w => w.Message.Contains("'not-an-ip'", StringComparison.Ordinal));
        Assert.Single(warnings, w => w.Message.Contains("'*'", StringComparison.Ordinal));
        Assert.Single(host.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("MAQUETTISTE_LOCAL_PEERS=10.9.8.7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_bearer_token_admits_a_remote_caller_and_a_wrong_one_is_refused_even_locally()
    {
        await using var host = EditorHost.Create();

        var remote = await host.SendAsync(TestRequest.FromElsewhere("GET", "/api/session").Bearer(EditorHost.Token));
        var wrongLocal = await host.SendAsync(TestRequest.Local("GET", Index).Bearer("guess"));
        var wrongRemote = await host.SendAsync(TestRequest.FromElsewhere("GET", Index).Bearer("guess"));

        Assert.Equal(200, remote.Status);
        Assert.Equal("token", remote.Json["via"]!.GetValue<string>());
        Assert.Equal(401, wrongLocal.Status);
        Assert.Equal("bad-token", wrongLocal.ProblemCode);
        Contract.AssertResponse(wrongLocal, Index);
        Assert.Equal(401, wrongRemote.Status);
        Assert.Equal("bad-token", wrongRemote.ProblemCode);
    }

    [Fact]
    public async Task Without_a_configured_token_no_bearer_is_accepted()
    {
        await using var host = EditorHost.Create(v => v.Set("MAQUETTISTE_EDITOR_TOKEN", ""));

        var response = await host.SendAsync(TestRequest.FromElsewhere("GET", Index).Bearer(""));
        var any = await host.SendAsync(TestRequest.FromElsewhere("GET", Index).Bearer("anything"));

        Assert.Equal(401, response.Status);
        Assert.Equal(401, any.Status);
        Assert.Equal("bad-token", any.ProblemCode);
    }

    [Fact]
    public async Task Json_sign_in_sets_a_session_cookie_that_admits_a_remote_browser()
    {
        await using var host = EditorHost.Create();

        var signIn = await host.SendAsync(TestRequest.FromElsewhere("POST", "/api/session").WithJson(new { token = EditorHost.Token }));
        var cookie = Cookie(signIn);
        var withCookie = await host.SendAsync(TestRequest.FromElsewhere("GET", "/api/session").Header("Cookie", "mq_session=" + cookie));

        Assert.Equal(200, signIn.Status);
        Contract.AssertResponse(signIn, "/api/session");
        var setCookie = signIn.Headers.SetCookie.ToString();
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(200, withCookie.Status);
        Assert.Equal("cookie", withCookie.Json["via"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_cookie_older_than_seven_days_is_refused()
    {
        await using var host = EditorHost.Create();
        var cookie = Cookie(await host.SendAsync(TestRequest.FromElsewhere("POST", "/api/session").WithJson(new { token = EditorHost.Token })));

        host.Clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1));
        var fresh = await host.SendAsync(TestRequest.FromElsewhere("GET", Index).Header("Cookie", "mq_session=" + cookie));
        host.Clock.Advance(TimeSpan.FromMinutes(2));
        var expired = await host.SendAsync(TestRequest.FromElsewhere("GET", Index).Header("Cookie", "mq_session=" + cookie));

        Assert.Equal(200, fresh.Status);
        Assert.Equal(401, expired.Status);
    }

    [Fact]
    public async Task Rotating_the_token_ends_every_cookie_session()
    {
        await using var host = EditorHost.Create();
        var cookie = Cookie(await host.SendAsync(TestRequest.FromElsewhere("POST", "/api/session").WithJson(new { token = EditorHost.Token })));

        host.Variables.Set("MAQUETTISTE_EDITOR_TOKEN", "a-new-token");
        var rotated = await host.SendAsync(TestRequest.FromElsewhere("GET", Index).Header("Cookie", "mq_session=" + cookie));
        var tampered = await host.SendAsync(TestRequest.FromElsewhere("GET", Index).Header("Cookie", "mq_session=" + cookie[..^4] + "AAAA"));

        Assert.Equal(401, rotated.Status);
        Assert.Equal(401, tampered.Status);
    }

    [Fact]
    public async Task A_wrong_token_is_401_and_five_failures_a_minute_are_429()
    {
        await using var host = EditorHost.Create();
        TestRequest Attempt() => TestRequest.FromElsewhere("POST", "/api/session").WithJson(new { token = "guess" });

        var failures = new List<TestResponse>();
        for (var i = 0; i < 5; i++)
            failures.Add(await host.SendAsync(Attempt()));
        var throttled = await host.SendAsync(Attempt());
        var rightButThrottled = await host.SendAsync(TestRequest.FromElsewhere("POST", "/api/session").WithJson(new { token = EditorHost.Token }));
        host.Clock.Advance(TimeSpan.FromSeconds(61));
        var later = await host.SendAsync(TestRequest.FromElsewhere("POST", "/api/session").WithJson(new { token = EditorHost.Token }));

        Assert.All(failures, f =>
        {
            Assert.Equal(401, f.Status);
            Assert.Equal("bad-token", f.ProblemCode);
            Contract.AssertResponse(f, "/api/session");
        });
        Assert.Equal(429, throttled.Status);
        Assert.Equal("too-many-attempts", throttled.ProblemCode);
        Contract.AssertResponse(throttled, "/api/session");
        Assert.Equal(429, rightButThrottled.Status);
        Assert.Equal(200, later.Status);
    }

    [Fact]
    public async Task The_sign_in_form_redirects_to_a_local_return_url_or_shows_the_page_again()
    {
        await using var host = EditorHost.Create();
        static TestRequest Form(string body) => TestRequest.FromElsewhere("POST", "/api/session")
            .With(r => { r.Body = System.Text.Encoding.UTF8.GetBytes(body); r.ContentType = "application/x-www-form-urlencoded"; });

        var ok = await host.SendAsync(Form("token=" + EditorHost.Token + "&returnUrl=%2Fentities%3Fsel%3D1"));
        var offsite = await host.SendAsync(Form("token=" + EditorHost.Token + "&returnUrl=%2F%2Fevil.example"));
        var wrong = await host.SendAsync(Form("token=guess&returnUrl=%2F"));

        Assert.Equal(303, ok.Status);
        Assert.Equal("/entities?sel=1", ok.Headers.Location.ToString());
        Assert.Contains("mq_session=", ok.Headers.SetCookie.ToString(), StringComparison.Ordinal);
        Contract.AssertResponse(ok, "/api/session");
        Assert.Equal(303, offsite.Status);
        Assert.Equal("/", offsite.Headers.Location.ToString());
        Assert.Equal(401, wrong.Status);
        Assert.StartsWith("text/html", wrong.ContentType, StringComparison.Ordinal);
        Assert.Contains("not the editor token", wrong.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unauthenticated_browser_gets_the_sign_in_page_and_other_requests_401()
    {
        await using var host = EditorHost.Create();

        var page = await host.SendAsync(TestRequest.FromElsewhere("GET", "/entities?sel=01J92P0V0FJ23CGSNKM7P1W5V7"));
        var head = await host.SendAsync(TestRequest.FromElsewhere("HEAD", "/"));
        var post = await host.SendAsync(TestRequest.FromElsewhere("POST", "/upload"));
        var api = await host.SendAsync(TestRequest.FromElsewhere("GET", Index));

        Assert.Equal(200, page.Status);
        Assert.False(page.Routed);
        Assert.StartsWith("text/html", page.ContentType, StringComparison.Ordinal);
        Assert.Equal("default-src 'none'; style-src 'unsafe-inline'; img-src 'self'; form-action 'self'", page.Headers.ContentSecurityPolicy.ToString());
        Assert.Contains("action=\"/api/session\"", page.Text, StringComparison.Ordinal);
        Assert.Contains("value=\"/entities?sel=01J92P0V0FJ23CGSNKM7P1W5V7\"", page.Text, StringComparison.Ordinal);
        Assert.Equal(200, head.Status);
        Assert.Empty(head.Body);
        Assert.Equal(401, post.Status);
        Assert.Equal(401, api.Status);
        Assert.False(api.Routed);
    }

    [Fact]
    public async Task Anonymous_paths_reach_their_handlers_without_a_user()
    {
        await using var host = EditorHost.Create();

        var health = await host.SendAsync(TestRequest.FromElsewhere("GET", "/api/health"));
        var session = await host.SendAsync(TestRequest.FromElsewhere("GET", "/api/session"));
        var signIn = await host.SendAsync(TestRequest.FromElsewhere("POST", "/api/session").WithJson(new { token = "guess" }));
        var healthWithWrongBearer = await host.SendAsync(TestRequest.FromElsewhere("GET", "/api/health").Bearer("guess"));

        Assert.True(health.Routed);
        Assert.Equal(200, health.Status);
        Assert.True(session.Routed);
        Assert.True(signIn.Routed);
        Assert.Equal(200, healthWithWrongBearer.Status);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task A_foreign_origin_is_refused_for_state_changes_even_from_the_local_peer(string method)
    {
        await using var host = EditorHost.Create();
        var path = method == "POST" ? "/api/model/elements" : "/api/model/elements/" + EditorHost.InvoiceId;

        var foreign = await host.SendAsync(TestRequest.Local(method, path).WithJson("{}").Header("Origin", "http://evil.example"));
        var nullOrigin = await host.SendAsync(TestRequest.Local(method, path).WithJson("{}").Header("Origin", "null"));

        Assert.Equal(403, foreign.Status);
        Assert.Equal("forbidden-origin", foreign.ProblemCode);
        Assert.False(foreign.Routed);
        Assert.Equal(403, nullOrigin.Status);
    }

    [Fact]
    public async Task The_site_own_origin_passes()
    {
        await using var host = EditorHost.Create();

        var response = await host.SendAsync(TestRequest.Local("POST", "/api/validate").WithJson("{}").Header("Origin", EditorHost.Origin));

        Assert.Equal(200, response.Status);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data; boundary=x")]
    [InlineData(null)]
    public async Task A_post_or_put_under_api_that_is_not_json_is_415(string? contentType)
    {
        await using var host = EditorHost.Create();

        var post = await host.SendAsync(TestRequest.Local("POST", "/api/generate/plan").With(r => { r.Body = "{}"u8.ToArray(); r.ContentType = contentType; }));
        var put = await host.SendAsync(TestRequest.Local("PUT", "/api/presence").With(r => { r.Body = "{}"u8.ToArray(); r.ContentType = contentType; }));

        Assert.Equal(415, post.Status);
        Assert.Equal("unsupported-media-type", post.ProblemCode);
        Assert.False(post.Routed);
        Assert.Equal(415, put.Status);
    }

    [Fact]
    public async Task Json_with_a_charset_is_json()
    {
        await using var host = EditorHost.Create();

        var response = await host.SendAsync(TestRequest.Local("POST", "/api/validate").With(r => { r.Body = "{}"u8.ToArray(); r.ContentType = "application/json; charset=utf-8"; }));

        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task An_unexpected_failure_in_a_handler_is_a_500_problem_with_the_trace_id()
    {
        await using var host = EditorHost.Create();
        await host.Store.DisposeAsync(); // every store call now throws ObjectDisposedException inside the handler

        var response = await host.GetAsync(Index);

        Assert.Equal(500, response.Status);
        Assert.Empty(host.HostFailures); // the handler answered; the host's text/plain 500 did not
        Assert.StartsWith("application/problem+json", response.ContentType, StringComparison.Ordinal);
        var body = response.Json;
        Assert.Equal("internal", body["code"]!.GetValue<string>());
        Assert.Equal("test-trace", body["traceId"]!.GetValue<string>());
        Assert.Contains(host.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("test-trace", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_io_failure_in_a_handler_is_a_503_model_unavailable_problem()
    {
        await using var host = EditorHost.Create();
        var context = TestRequest.Local("GET", Index).ToContext(host.Site, host.Services);

        var result = await Api.GuardAsync(context, () => throw new IOException("disk gone"));
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);

        Assert.Equal(503, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = System.Text.Json.Nodes.JsonNode.Parse(context.Response.Body)!;
        Assert.Equal("model-unavailable", body["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_local_host_from_an_unknown_peer_is_refused_and_the_peer_is_logged_once()
    {
        await using var host = EditorHost.Create();

        var first = await host.SendAsync(TestRequest.Local("GET", Index).With(r => r.Remote = System.Net.IPAddress.Parse("192.168.65.1")));
        await host.SendAsync(TestRequest.Local("GET", Index).With(r => r.Remote = System.Net.IPAddress.Parse("192.168.65.1")));

        Assert.Equal(401, first.Status);
        Assert.Single(host.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("MAQUETTISTE_LOCAL_PEERS=192.168.65.1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_host_paths_pass_through_untouched()
    {
        await using var host = EditorHost.Create();
        var auth = host.Services.GetRequiredService<EditorAuth>();
        foreach (var path in new[] { "/_host/site.js", "/healthz" })
        {
            var context = TestRequest.FromElsewhere("GET", path).ToContext(host.Site, host.Services);
            var called = false;
            await SignInGate.Run(context, () => { called = true; return Task.CompletedTask; }, auth, host.Logs.CreateLogger("t"));
            Assert.True(called, path);
        }
    }

    // ---------------------------------------------------------------- hooks

    [Theory]
    [InlineData("127.0.0.1", "maquettiste.localhost:8080", null, null, "local")]
    [InlineData("::ffff:172.18.0.1", "maquettiste.localhost:8080", null, null, "local")]
    [InlineData("203.0.113.7", "editor.example.com", null, null, null)]
    [InlineData("203.0.113.7", "editor.example.com", EditorHost.Token, null, "token")]
    [InlineData("127.0.0.1", "maquettiste.localhost:8080", "guess", null, null)]
    [InlineData("127.0.0.1", "maquettiste.localhost:8080", null, "http://evil.example", null)]
    [InlineData("127.0.0.1", "maquettiste.localhost:8080", null, EditorHost.Origin, "local")]
    public async Task Connect_admits_the_callers_the_gate_admits(string remote, string hostHeader, string? bearer, string? origin, string? expected)
    {
        await using var host = EditorHost.Create(v => v.Set("MAQUETTISTE_LOCAL_PEERS", "172.18.0.1"));
        var request = TestRequest.Local("GET", "/_host/realtime").With(r => { r.Remote = IPAddress.Parse(remote); r.Host = hostHeader; });
        if (bearer is not null)
            request.Bearer(bearer);
        if (origin is not null)
            request.Header("Origin", origin);

        var user = RealtimeHooks.Connect(request.ToContext(host.Site, host.Services), host.Services.GetRequiredService<EditorAuth>());

        Assert.Equal(expected, user);
    }

    [Fact]
    public async Task Connect_follows_local_trust_off_and_the_session_cookie()
    {
        await using var host = EditorHost.Create(v => v.Set("MAQUETTISTE_LOCAL_TRUST", "off"));
        var auth = host.Services.GetRequiredService<EditorAuth>();
        var cookie = Cookie(await host.SendAsync(TestRequest.FromElsewhere("POST", "/api/session").WithJson(new { token = EditorHost.Token })));

        var local = RealtimeHooks.Connect(TestRequest.Local("GET", "/_host/realtime").ToContext(host.Site, host.Services), auth);
        var withCookie = RealtimeHooks.Connect(TestRequest.FromElsewhere("GET", "/_host/realtime").Header("Cookie", "mq_session=" + cookie)
            .ToContext(host.Site, host.Services), auth);

        Assert.Null(local);
        Assert.Equal("token", withCookie);
    }

    [Theory]
    [InlineData("editors", true, true)]
    [InlineData("job:01M3MNY0HTVJQSH4BX78RWN8F7", true, true)]
    [InlineData("job:not-a-ulid", true, false)]
    [InlineData("job:01m3mny0htvjqsh4bx78rwn8f7", true, false)]
    [InlineData("admins", true, false)]
    [InlineData("editors", false, false)]
    [InlineData("job:01M3MNY0HTVJQSH4BX78RWN8F7", false, false)]
    public async Task Join_admits_identified_callers_to_editors_and_job_groups_only(string group, bool identified, bool expected)
    {
        await using var host = EditorHost.Create();
        var request = identified ? TestRequest.Local("GET", "/_host/realtime") : TestRequest.FromElsewhere("GET", "/_host/realtime");

        var allowed = RealtimeHooks.Join(request.ToContext(host.Site, host.Services), host.Services.GetRequiredService<EditorAuth>(), group);

        Assert.Equal(expected, allowed);
    }

    [Fact]
    public async Task Ai_access_is_for_identified_callers_only()
    {
        await using var host = EditorHost.Create();
        var auth = host.Services.GetRequiredService<EditorAuth>();

        Assert.True(RealtimeHooks.Ai(TestRequest.Local("POST", "/_host/ai/chat").ToContext(host.Site, host.Services), auth));
        Assert.True(RealtimeHooks.Ai(TestRequest.FromElsewhere("POST", "/_host/ai/chat").Bearer(EditorHost.Token).ToContext(host.Site, host.Services), auth));
        Assert.False(RealtimeHooks.Ai(TestRequest.FromElsewhere("POST", "/_host/ai/chat").ToContext(host.Site, host.Services), auth));
        Assert.False(RealtimeHooks.Ai(TestRequest.Local("POST", "/_host/ai/chat").Bearer("guess").ToContext(host.Site, host.Services), auth));
    }

    [Fact]
    public async Task Hooks_register_nothing_on_the_request()
    {
        await using var host = EditorHost.Create();
        var auth = host.Services.GetRequiredService<EditorAuth>();
        var context = TestRequest.Local("GET", "/_host/realtime").ToContext(host.Site, host.Services);
        var items = context.Items.Count;

        RealtimeHooks.Connect(context, auth);
        RealtimeHooks.Join(context, auth, "editors");
        RealtimeHooks.Ai(context, auth);

        Assert.Equal(items, context.Items.Count);
        Assert.False(context.User.Identity?.IsAuthenticated ?? false);
        Assert.False(context.Response.HasStarted);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private static string Cookie(TestResponse signIn)
    {
        var header = signIn.Headers.SetCookie.ToString();
        var start = header.IndexOf("mq_session=", StringComparison.Ordinal) + "mq_session=".Length;
        var end = header.IndexOf(';', start);
        return header[start..(end < 0 ? header.Length : end)];
    }
}
