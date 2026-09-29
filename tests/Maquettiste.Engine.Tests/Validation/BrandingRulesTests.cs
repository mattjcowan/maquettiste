using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Branding;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Tests.Loading;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>Settings <c>branding</c>: MQ8001 to MQ8003, the icon's safety check, and the store's icon write and read.</summary>
public sealed class BrandingRulesTests
{
    private const string Clean = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16"><defs><linearGradient id="g"/></defs><rect width="16" height="16" fill="url(#g)"/></svg>""";

    private const string Unsafe = """
        <?xml-stylesheet href="https://example.invalid/x.css"?>
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" onload="alert(1)" viewBox="0 0 16 16">
          <script>alert(2)</script>
          <style>@import url(https://example.invalid/a.css);</style>
          <foreignObject><div xmlns="http://www.w3.org/1999/xhtml">x</div></foreignObject>
          <image xlink:href="https://example.invalid/track.png" width="4" height="4"/>
          <image href="data:image/png;base64,iVBORw0KGgo=" width="4" height="4"/>
          <a href="javascript:alert(3)"><rect width="8" height="8" style="fill: url('https://example.invalid/p.svg#x')"/></a>
          <use href="#keep"/>
          <set attributeName="href" to="javascript:alert(4)"/>
          <circle id="keep" cx="8" cy="8" r="4" fill="red"/>
        </svg>
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_clean_svg_is_kept_byte_for_byte()
    {
        var bytes = Encoding.UTF8.GetBytes(Clean);

        var result = BrandingIcons.Inspect(BrandingIcons.Svg, bytes);

        Assert.True(result.Clean);
        Assert.Equal(bytes, result.Safe);
    }

    [Fact]
    public void An_unsafe_svg_loses_scripts_handlers_and_external_references_deterministically()
    {
        var bytes = Encoding.UTF8.GetBytes(Unsafe);

        var result = BrandingIcons.Inspect(BrandingIcons.Svg, bytes);
        var again = BrandingIcons.Inspect(BrandingIcons.Svg, bytes);
        var safe = Encoding.UTF8.GetString(result.Safe!);

        Assert.True(result.Usable);
        Assert.False(result.Clean);
        Assert.Equal(result.Safe, again.Safe);
        foreach (var gone in new[] { "<script", "onload", "@import", "foreignObject", "example.invalid", "javascript:", "<set", "xml-stylesheet" })
            Assert.DoesNotContain(gone, safe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data:image/png;base64", safe, StringComparison.Ordinal);
        Assert.Contains("href=\"#keep\"", safe, StringComparison.Ordinal);
        Assert.Contains("<circle", safe, StringComparison.Ordinal);
        Assert.True(BrandingIcons.Inspect(BrandingIcons.Svg, result.Safe).Clean);
        Assert.Contains(result.Problems, p => p.Contains("<script>", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Contains("onload", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("image/svg+xml", "<html><body/></html>", "not <svg>")]
    [InlineData("image/svg+xml", "<svg viewBox='0 0 1 1'/>", "not <svg>")]
    [InlineData("image/svg+xml", "<svg xmlns='http://www.w3.org/2000/svg'>", "not well-formed")]
    [InlineData("image/svg+xml", "<!DOCTYPE svg [<!ENTITY x \"boom\">]><svg xmlns='http://www.w3.org/2000/svg'>&x;</svg>", "not well-formed")]
    [InlineData("image/png", "GIF89a", "not a PNG")]
    [InlineData("image/gif", "GIF89a", "SVG or a PNG")]
    public void Files_that_are_not_an_svg_or_a_png_are_not_usable(string contentType, string text, string reason)
    {
        var result = BrandingIcons.Inspect(contentType, Encoding.UTF8.GetBytes(text));

        Assert.False(result.Usable);
        Assert.Null(result.Safe);
        Assert.Contains(reason, Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_png_is_checked_by_signature_and_size()
    {
        var png = Png(1024);
        var large = Png(BrandingIcons.MaxBytes + 1);

        Assert.True(BrandingIcons.Inspect(BrandingIcons.Png, png).Clean);
        Assert.False(BrandingIcons.Inspect(BrandingIcons.Png, large).Usable);
        Assert.Contains("512 KB", BrandingIcons.Inspect(BrandingIcons.Png, large).Problems[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("branding/icon.svg", true)]
    [InlineData("branding/Logo_2.png", true)]
    [InlineData("branding/.hidden.svg", false)]
    [InlineData("branding/../model/x.svg", false)]
    [InlineData("branding/sub/icon.svg", false)]
    [InlineData("icon.svg", false)]
    [InlineData("branding/icon.gif", false)]
    [InlineData("/etc/icon.svg", false)]
    public void Icon_paths_are_files_directly_under_branding(string path, bool valid) => Assert.Equal(valid, BrandingIcons.IsIconPath(path));

    [Fact]
    public async Task A_bad_color_is_MQ8001_and_refuses_the_settings_save()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var result = await SaveBrandingAsync(store, new JsonObject { ["colors"] = new JsonObject { ["light"] = "blue", ["dark"] = "#12345" } });

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        Assert.Equal(["/branding/colors/dark", "/branding/colors/light"],
            result.Diagnostics.Where(d => d.Rule == "MQ8001").Select(d => d.JsonPointer!).Order(StringComparer.Ordinal));
        Assert.Equal(SaveOutcome.Saved, (await SaveBrandingAsync(store, new JsonObject { ["colors"] = new JsonObject { ["light"] = "#0B5CD5", ["dark"] = "#abc" } })).Outcome);
        Assert.Equal("#0B5CD5", store.Current!.Settings.Branding.Colors.Light);
    }

    [Theory]
    [InlineData("branding/missing.svg")]
    [InlineData("../outside.svg")]
    [InlineData("branding/icon.gif")]
    public async Task An_icon_that_names_no_file_under_branding_is_MQ8002(string icon)
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var result = await SaveBrandingAsync(store, new JsonObject { ["icon"] = icon });

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Rule == "MQ8002");
        Assert.Equal("/branding/icon", diagnostic.JsonPointer);
        Assert.Equal(".maquettiste/maquettiste.json", diagnostic.FilePath);
    }

    [Fact]
    public async Task An_unsafe_svg_on_disk_is_MQ8003_and_is_served_without_its_unsafe_parts()
    {
        var (h, store) = await OpenAsync(harness =>
        {
            Directory.CreateDirectory(harness.Model("branding"));
            File.WriteAllText(harness.Model("branding/hand.svg"), Unsafe);
            File.WriteAllText(harness.Model("branding/clean.svg"), Clean);
        });
        using var _ = h;
        await using var __ = store;

        var unsafeSave = await SaveBrandingAsync(store, new JsonObject { ["icon"] = "branding/hand.svg" });
        var cleanSave = await SaveBrandingAsync(store, new JsonObject { ["icon"] = "branding/clean.svg" });
        var served = await store.ReadBrandingIconAsync(Ct);

        Assert.Equal(SaveOutcome.Invalid, unsafeSave.Outcome);
        Assert.Contains("onload", Assert.Single(unsafeSave.Diagnostics, d => d.Rule == "MQ8003").Message, StringComparison.Ordinal);
        Assert.Equal(SaveOutcome.Saved, cleanSave.Outcome);
        Assert.Equal("branding/clean.svg", served!.Icon);
        Assert.Equal(Clean, Encoding.UTF8.GetString(served.Bytes));

        // A hand edit to an unsafe file after the save: the rule reports it and the read serves the cleaned file.
        File.WriteAllText(h.Model("branding/clean.svg"), Unsafe);
        var report = await store.ValidateAsync(ValidationScope.All, Ct);
        var again = await store.ReadBrandingIconAsync(Ct);
        Assert.Single(report.Diagnostics, d => d.Rule == "MQ8003");
        Assert.DoesNotContain("<script", Encoding.UTF8.GetString(again!.Bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_store_writes_the_uploaded_icon_under_branding_and_the_settings_name_it()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var refused = await store.SaveBrandingIconAsync(BrandingIcons.Png, Png(BrandingIcons.MaxBytes + 1), Ct);
        var written = await store.SaveBrandingIconAsync(BrandingIcons.Svg, Encoding.UTF8.GetBytes(Unsafe), Ct);

        Assert.False(refused.Saved);
        Assert.True(refused.TooLarge);
        Assert.True(written.Saved);
        Assert.True(BrandingIcons.IsUploadPath(written.Icon));
        Assert.Equal(BrandingIcons.PathFor(BrandingIcons.Svg, written.Hash!), written.Icon);
        Assert.NotEmpty(written.Removed);
        var stored = File.ReadAllText(h.Model(written.Icon!));
        Assert.DoesNotContain("<script", stored, StringComparison.Ordinal);
        Assert.Null(await store.ReadBrandingIconAsync(Ct)); // not named by the settings yet

        var saved = await SaveBrandingAsync(store, new JsonObject { ["icon"] = written.Icon, ["colors"] = new JsonObject { ["light"] = null } });
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.Equal(written.Hash, (await store.ReadBrandingIconAsync(Ct))!.Hash);
        var file = File.ReadAllText(h.Model("maquettiste.json"));
        Assert.Contains("\"branding\": {\n    \"icon\": \"" + written.Icon + "\"\n  }", file, StringComparison.Ordinal); // nulls and empty colors omitted
        Assert.True(file.IndexOf("\"name\"", StringComparison.Ordinal) < file.IndexOf("\"branding\"", StringComparison.Ordinal)
            || !file.Contains("\"name\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""<!DOCTYPE svg [<!ATTLIST svg onload CDATA "alert(1)">]><svg xmlns="http://www.w3.org/2000/svg"><rect/></svg>""", "DOCTYPE")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><rect style="fill:u\72l(http://evil.example/a.svg#p)"/></svg>""", "evil.example")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><style>rect{background-image:image-set("http://evil.example/x.png" 1x)}</style><rect/></svg>""", "evil.example")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><style>@\69mport "http://evil.example/x.css";</style><rect/></svg>""", "evil.example")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><style>@font-face{font-family:x;src:"//evil.example/f.woff"}</style><rect/></svg>""", "evil.example")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><set attributeName="href " to="http://evil.example"/><rect/></svg>""", "evil.example")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><set attributeName=" ONLOAD" to="alert(1)"/><rect/></svg>""", "alert(1)")]
    public void MQ8003_hidden_scripts_and_external_references_are_removed(string svg, string gone)
    {
        var result = BrandingIcons.Inspect(BrandingIcons.Svg, Encoding.UTF8.GetBytes(svg));
        var safe = Encoding.UTF8.GetString(result.Safe!);

        Assert.True(result.Usable);
        Assert.False(result.Clean);
        Assert.DoesNotContain(gone, safe, StringComparison.Ordinal);
        Assert.DoesNotContain("onload", safe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<rect", safe, StringComparison.Ordinal);
        Assert.True(BrandingIcons.Inspect(BrandingIcons.Svg, result.Safe).Clean);
    }

    [Fact]
    public async Task An_upload_never_replaces_the_saved_icon_and_the_save_removes_unnamed_uploads()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var first = await store.SaveBrandingIconAsync(BrandingIcons.Svg, Encoding.UTF8.GetBytes(Clean), Ct);
        Assert.Equal(SaveOutcome.Saved, (await SaveBrandingAsync(store, new JsonObject { ["icon"] = first.Icon })).Outcome);

        var second = await store.SaveBrandingIconAsync(BrandingIcons.Png, Png(64), Ct);

        Assert.NotEqual(first.Icon, second.Icon);
        Assert.Equal(first.Hash, (await store.ReadBrandingIconAsync(Ct))!.Hash); // the live icon is unchanged until the save
        Assert.True(File.Exists(h.Model(second.Icon!)));

        Assert.Equal(SaveOutcome.Saved, (await SaveBrandingAsync(store, new JsonObject { ["icon"] = second.Icon })).Outcome);
        Assert.Equal(second.Hash, (await store.ReadBrandingIconAsync(Ct))!.Hash);
        Assert.False(File.Exists(h.Model(first.Icon!)));
        Assert.True(File.Exists(h.Model(second.Icon!)));
    }

    private static byte[] Png(int length)
    {
        var bytes = new byte[length];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    private static async Task<SettingsSaveResult> SaveBrandingAsync(ModelStore store, JsonObject branding)
    {
        var document = await store.GetSettingsAsync(Ct);
        var json = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        json["branding"] = branding;
        return await store.SaveSettingsAsync(Encoding.UTF8.GetBytes(json.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
    }

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(Action<LoaderHarness>? prepare = null)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "reference-data");
        prepare?.Invoke(harness);
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        return (harness, store);
    }
}
