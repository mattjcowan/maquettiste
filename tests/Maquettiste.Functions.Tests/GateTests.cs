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
    [InlineData("127.0.0.1", "maquettiste.localhost:8080", null, null)]
    [InlineData("::ffff:172.18.0.1", "maquettiste.localhost:8080", null, null)]
    [InlineData("192.168.65.1", "maquettiste.localhost:8080", null, null)]
    [InlineData("203.0.113.7", "editor.example.com", null, null)]
    [InlineData("127.0.0.1", "192.168.1.20:8080", null, null)]
    [InlineData("127.0.0.1", "maquettiste.localhost:8080", "X-Forwarded-For", "198.51.100.4")]
    [InlineData("127.0.0.1", "maquettiste.localhost:8080", "Forwarded", "for=198.51.100.4")]
    public async Task Without_a_token_every_request_is_the_local_admin(string remote, string hostHeader, string? header, string? value)
    {
        await using var host = EditorHost.Create();
        var request = TestRequest.Local("GET", "/api/session").With(r => { r.Remote = IPAddress.Parse(remote); r.Host = hostHeader; });
        if (header is not null)
            request.Header(header, value!);

        var response = await host.SendAsync(request);

        Assert.Equal(200, response.Status);
        Assert.Equal("local", response.Json["via"]!.GetValue<string>());
        Assert.Equal("local", response.Json["user"]!["name"]!.GetValue<string>());
        Assert.Equal("admin", response.Json["user"]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_a_token_a_bearer_is_ignored()
    {
        await using var host = EditorHost.Create();

        var any = await host.SendAsync(TestRequest.FromElsewhere("GET", Index).Bearer("anything"));
        var empty = await host.SendAsync(TestRequest.FromElsewhere("GET", Index).Bearer(""));

        Assert.Equal(200, any.Status);
        Assert.Equal(200, empty.Status);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/entities?sel=01J92P0V0FJ23CGSNKM7P1W5V7")]
    public async Task Without_a_token_a_browser_from_anywhere_gets_the_app_not_the_sign_in_page(string path)
    {
        await using var host = EditorHost.Create();

        var page = await host.SendAsync(TestRequest.FromElsewhere("GET", path));

        Assert.True(page.Routed);
        Assert.DoesNotContain("action=\"/api/session\"", page.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_token_signing_in_answers_the_local_developer_and_sets_no_cookie()
    {
        await using var host = EditorHost.Create();

        var json = await host.SendAsync(TestRequest.FromElsewhere("POST", "/api/session").WithJson(new { token = "anything" }));
        var form = await host.SendAsync(TestRequest.FromElsewhere("POST", "/api/session")
            .With(r => { r.Body = "token=&returnUrl=%2Fentities"u8.ToArray(); r.ContentType = "application/x-www-form-urlencoded"; }));

        Assert.Equal(200, json.Status);
        Assert.Equal("local", json.Json["via"]!.GetValue<string>());
        Assert.Empty(json.Headers.SetCookie.ToString());
        Assert.Equal(303, form.Status);
        Assert.Equal("/entities", form.Headers.Location.ToString());
        Assert.Empty(form.Headers.SetCookie.ToString());
    }

    [Theory]
    [InlineData("127.0.0.1", "maquettiste.localhost:8080")]
    [InlineData("::1", "localhost:8080")]
    [InlineData("203.0.113.7", "editor.example.com")]
    public async Task With_a_token_every_caller_signs_in_the_loopback_one_included(string remote, string hostHeader)
    {
        await using var host = EditorHost.Create(EditorHost.WithToken);
        TestRequest Request() => TestRequest.Local("GET", Index).With(r => { r.Remote = IPAddress.Parse(remote); r.Host = hostHeader; });

        var without = await host.SendAsync(Request());
        var with = await host.SendAsync(Request().Bearer(EditorHost.Token));

        Assert.Equal(401, without.Status);
        Assert.Equal("unauthenticated", without.ProblemCode);
        Contract.AssertResponse(without, Index);
        Assert.Equal(200, with.Status);
    }

    [Fact]
    public async Task Setting_or_clearing_the_token_takes_effect_at_once()
    {
        await using var host = EditorHost.Create();

        var open = await host.GetAsync(Index);
        host.Variables.Set("MAQUETTISTE_EDITOR_TOKEN", EditorHost.Token, isPublic: false);
        var closed = await host.GetAsync(Index);
        host.Variables.Set("MAQUETTISTE_EDITOR_TOKEN", "", isPublic: false);
        var openAgain = await host.GetAsync(Index);

        Assert.Equal(200, open.Status);
        Assert.Equal(401, closed.Status);
        Assert.Equal(200, openAgain.Status);
    }

    [Fact]
    public async Task Hosted_mode_is_never_open_and_answers_503_for_the_model()
    {
        await using var noToken = EditorHost.Create(v => v.Set("MAQUETTISTE_MODE", "hosted").Set("MAQUETTISTE_EDITOR_TOKEN", ""));
        await using var host = EditorHost.Create(v => EditorHost.WithToken(v.Set("MAQUETTISTE_MODE", "hosted")));

        var open = await noToken.GetAsync(Index);
        var noCredential = await host.GetAsync(Index);
        var bearer = await host.SendAsync(TestRequest.FromElsewhere("GET", Index).Bearer(EditorHost.Token));
        var health = await host.GetAsync("/api/health");

        Assert.Equal(401, open.Status);
        Assert.Equal(401, noCredential.Status);
        Assert.Equal(503, bearer.Status);
        Assert.Equal("model-unavailable", bearer.ProblemCode);
        Contract.AssertResponse(bearer, Index);
        Assert.Equal(200, health.Status);
    }

    [Fact]
    public async Task A_bearer_token_admits_a_caller_and_a_wrong_one_is_refused()
    {
        await using var host = EditorHost.Create(EditorHost.WithToken);

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
    public async Task Json_sign_in_sets_a_session_cookie_that_admits_a_remote_browser()
    {
        await using var host = EditorHost.Create(EditorHost.WithToken);

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
    public async Task Editors_on_two_ports_of_one_host_keep_their_own_session_cookies()
    {
        await using var host = EditorHost.Create(EditorHost.WithToken);
        TestRequest On(string port, string method, string path) => TestRequest.FromElsewhere(method, path).With(r => r.Host = "editor.example.com:" + port);

        // Browsers send every cookie of a host to each of its ports: a session on 8081 must not take the place of the one on 8080.
        var signIn = await host.SendAsync(On("8080", "POST", "/api/session").WithJson(new { token = EditorHost.Token }));
        var setCookie = signIn.Headers.SetCookie.ToString();
        var start = setCookie.IndexOf("mq_session_8080=", StringComparison.Ordinal) + "mq_session_8080=".Length;
        var end = setCookie.IndexOf(';', start);
        var cookie = setCookie[start..(end < 0 ? setCookie.Length : end)];
        var same = await host.SendAsync(On("8080", "GET", "/api/session").Header("Cookie", "mq_session_8080=" + cookie + "; mq_session_8081=other"));
        var other = await host.SendAsync(On("8081", "GET", "/api/session").Header("Cookie", "mq_session_8080=" + cookie));
        var signOut = await host.SendAsync(On("8080", "DELETE", "/api/session").Header("Cookie", "mq_session_8080=" + cookie));

        Assert.Equal(200, signIn.Status);
        Assert.StartsWith("mq_session_8080=", setCookie, StringComparison.Ordinal);
        Assert.Equal(200, same.Status);
        Assert.Equal("cookie", same.Json["via"]!.GetValue<string>());
        Assert.Equal(401, other.Status);
        Assert.Contains("mq_session_8080=;", signOut.Headers.SetCookie.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cookie_older_than_seven_days_is_refused()
    {
        await using var host = EditorHost.Create(EditorHost.WithToken);
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
        await using var host = EditorHost.Create(EditorHost.WithToken);
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
        await using var host = EditorHost.Create(EditorHost.WithToken);
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
        await using var host = EditorHost.Create(EditorHost.WithToken);
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
        await using var host = EditorHost.Create(EditorHost.WithToken);

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
        await using var host = EditorHost.Create(EditorHost.WithToken);

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
    public async Task A_foreign_origin_is_refused_for_state_changes_even_on_an_open_editor(string method)
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
    [InlineData(false, "203.0.113.7", "editor.example.com", null, null, "local")]
    [InlineData(false, "127.0.0.1", "maquettiste.localhost:8080", null, "http://evil.example", null)]
    [InlineData(false, "127.0.0.1", "maquettiste.localhost:8080", null, EditorHost.Origin, "local")]
    [InlineData(true, "127.0.0.1", "maquettiste.localhost:8080", null, null, null)]
    [InlineData(true, "203.0.113.7", "editor.example.com", null, null, null)]
    [InlineData(true, "203.0.113.7", "editor.example.com", EditorHost.Token, null, "token")]
    [InlineData(true, "127.0.0.1", "maquettiste.localhost:8080", "guess", null, null)]
    public async Task Connect_admits_the_callers_the_gate_admits(bool token, string remote, string hostHeader, string? bearer, string? origin, string? expected)
    {
        await using var host = EditorHost.Create(token ? EditorHost.WithToken : null);
        var request = TestRequest.Local("GET", "/_host/realtime").With(r => { r.Remote = IPAddress.Parse(remote); r.Host = hostHeader; });
        if (bearer is not null)
            request.Bearer(bearer);
        if (origin is not null)
            request.Header("Origin", origin);

        var user = RealtimeHooks.Connect(request.ToContext(host.Site, host.Services), host.Services.GetRequiredService<EditorAuth>());

        Assert.Equal(expected, user);
    }

    [Fact]
    public async Task Connect_follows_the_session_cookie_once_a_token_is_set()
    {
        await using var host = EditorHost.Create(EditorHost.WithToken);
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
        await using var host = EditorHost.Create(EditorHost.WithToken);
        var request = identified ? TestRequest.FromElsewhere("GET", "/_host/realtime").Bearer(EditorHost.Token) : TestRequest.FromElsewhere("GET", "/_host/realtime");

        var allowed = RealtimeHooks.Join(request.ToContext(host.Site, host.Services), host.Services.GetRequiredService<EditorAuth>(), group);

        Assert.Equal(expected, allowed);
    }

    [Fact]
    public async Task Ai_access_is_for_identified_callers_only()
    {
        await using var host = EditorHost.Create(EditorHost.WithToken);
        var auth = host.Services.GetRequiredService<EditorAuth>();

        Assert.True(RealtimeHooks.Ai(TestRequest.Local("POST", "/_host/ai/chat").Bearer(EditorHost.Token).ToContext(host.Site, host.Services), auth));
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
