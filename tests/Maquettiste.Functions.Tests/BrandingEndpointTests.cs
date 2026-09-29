using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Branding;
using Maquettiste.Functions.Tests.Support;

namespace Maquettiste.Functions.Tests;

/// <summary><c>POST</c> and <c>GET /api/project/branding/icon</c>, the settings' <c>branding</c> and the sign-in page's branding.</summary>
public sealed class BrandingEndpointTests
{
    private const string Icon = "/api/project/branding/icon";

    private const string Svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" onload="alert(1)"><script>alert(2)</script><circle cx="8" cy="8" r="6"/></svg>""";

    private static object Upload(string contentType, byte[] bytes) => new { contentType, data = Convert.ToBase64String(bytes) };

    private static byte[] Png(int length)
    {
        var bytes = new byte[length];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    private static async Task<TestResponse> SaveBrandingAsync(EditorHost host, JsonObject branding, string? name = null)
    {
        var document = await host.GetAsync("/api/project/settings");
        var json = document.Json["json"]!.AsObject().DeepClone().AsObject();
        json["branding"] = branding;
        if (name is not null)
            json["name"] = name;
        return await host.SendJsonAsync("PUT", "/api/project/settings", json, r => r.IfMatch(document.Json["hash"]!.GetValue<string>()));
    }

    [Fact]
    public async Task An_svg_upload_is_sanitized_stored_under_branding_and_served_once_the_settings_name_it()
    {
        await using var host = EditorHost.Create();

        var none = await host.GetAsync(Icon);
        var uploaded = await host.SendJsonAsync("POST", Icon, Upload("image/svg+xml", Encoding.UTF8.GetBytes(Svg)));

        Assert.Equal(404, none.Status);
        Assert.Equal(200, uploaded.Status);
        Contract.AssertResponse(uploaded, Icon);
        var iconPath = uploaded.Json["icon"]!.GetValue<string>();
        Assert.True(BrandingIcons.IsUploadPath(iconPath), iconPath);
        Assert.NotEmpty(uploaded.Json["removed"]!.AsArray());
        var stored = File.ReadAllText(Path.Combine(host.ModelRoot, iconPath));
        Assert.DoesNotContain("<script", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("onload", stored, StringComparison.Ordinal);

        var saved = await SaveBrandingAsync(host, new JsonObject
        {
            ["icon"] = iconPath,
            ["colors"] = new JsonObject { ["light"] = "#7a1fa2", ["dark"] = "#e0a0ff" },
        }, "Partner app");
        Assert.Equal(200, saved.Status);

        var project = await host.GetAsync("/api/project");
        Contract.AssertResponse(project, "/api/project");
        Assert.Equal("Partner app", project.Json["name"]!.GetValue<string>());
        Assert.Equal("#7a1fa2", project.Json["settings"]!["branding"]!["colors"]!["light"]!.GetValue<string>());
        var hash = project.Json["iconHash"]!.GetValue<string>();
        Assert.Equal(uploaded.Json["hash"]!.GetValue<string>(), hash);

        // Anonymous: a caller from elsewhere without a token still gets the icon (the sign-in page and the tab need it).
        var served = await host.SendAsync(TestRequest.FromElsewhere("GET", Icon));
        Assert.Equal(200, served.Status);
        Assert.Equal("image/svg+xml", served.ContentType);
        Assert.Equal("nosniff", served.Headers.XContentTypeOptions.ToString());
        Assert.Contains("sandbox", served.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
        Assert.Equal(stored, served.Text);
        var cached = await host.GetAsync(Icon, r => r.Header("If-None-Match", "\"" + hash + "\""));
        Assert.Equal(304, cached.Status);

        // The sign-in page shows the icon and the colors.
        var page = await host.SendAsync(TestRequest.FromElsewhere("GET", "/"));
        Assert.Contains($"<img class=\"icon\" src=\"{Icon}?v={hash[..12]}\"", page.Text, StringComparison.Ordinal);
        Assert.Contains("--accent: #7a1fa2", page.Text, StringComparison.Ordinal);
        Assert.Contains("--accent: #e0a0ff; --on-accent: #0f1115", page.Text, StringComparison.Ordinal);
        Assert.Contains("img-src 'self'", page.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uploads_are_limited_by_type_and_size()
    {
        await using var host = EditorHost.Create();

        var png = await host.SendJsonAsync("POST", Icon, Upload("image/png", Png(2048)));
        var large = await host.SendJsonAsync("POST", Icon, Upload("image/png", Png(512 * 1024 + 1)));
        var gif = await host.SendJsonAsync("POST", Icon, Upload("image/gif", Png(16)));
        var fake = await host.SendJsonAsync("POST", Icon, Upload("image/png", Encoding.UTF8.GetBytes("<svg/>")));
        var html = await host.SendJsonAsync("POST", Icon, Upload("image/svg+xml", Encoding.UTF8.GetBytes("<html/>")));
        var notBase64 = await host.SendJsonAsync("POST", Icon, new { contentType = "image/png", data = "***" });
        var plain = await host.SendJsonAsync("POST", Icon, Upload("image/png", Png(16)), r => r.ContentType = "text/plain");

        Assert.Equal(200, png.Status);
        Assert.EndsWith(".png", png.Json["icon"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(BrandingIcons.IsUploadPath(png.Json["icon"]!.GetValue<string>()));
        Assert.Empty(png.Json["removed"]!.AsArray());
        Assert.Equal(413, large.Status);
        Contract.AssertResponse(large, Icon);
        Assert.Equal(400, gif.Status);
        Assert.Equal(422, fake.Status);
        Assert.Equal(422, html.Status);
        Contract.AssertResponse(html, Icon);
        Assert.Equal(400, notBase64.Status);
        Assert.Equal(415, plain.Status);
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(host.ModelRoot, "branding")), f => f.EndsWith(".svg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bad_branding_settings_are_refused_with_their_rules()
    {
        await using var host = EditorHost.Create();

        var color = await SaveBrandingAsync(host, new JsonObject { ["colors"] = new JsonObject { ["dark"] = "rebeccapurple" } });
        var missing = await SaveBrandingAsync(host, new JsonObject { ["icon"] = "branding/nothing.png" });

        Assert.Equal(422, color.Status);
        Assert.Equal("MQ8001", color.Json["diagnostics"]![0]!["rule"]!.GetValue<string>());
        Assert.Equal(422, missing.Status);
        Assert.Equal("MQ8002", missing.Json["diagnostics"]![0]!["rule"]!.GetValue<string>());
        var page = await host.SendAsync(TestRequest.FromElsewhere("GET", "/"));
        Assert.DoesNotContain("<img", page.Text, StringComparison.Ordinal);
    }
}
