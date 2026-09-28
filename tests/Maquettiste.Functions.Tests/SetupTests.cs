using System.Security.Cryptography;
using System.Text;
using Maquettiste.Engine;
using Maquettiste.Functions.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StaticSiteHost.Functions.Testing;

namespace Maquettiste.Functions.Tests;

/// <summary><see cref="EditorSetup.Configure"/> and <see cref="EditorSettings.From"/> (phase2-design.md sections 3.2 and 3.5).</summary>
public sealed class SetupTests
{
    [Fact]
    public void Settings_map_the_variables_to_engine_options_without_io()
    {
        var data = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "never-created-" + Guid.NewGuid().ToString("N"), "data"));
        var variables = new FakeSiteVariables().Set("MAQUETTISTE_REPO_ROOT", "/repo").Set("MAQUETTISTE_CACHE_DIR", "/data/maquettiste/cache");

        var settings = EditorSettings.From(variables, data);

        Assert.False(data.Exists);
        Assert.Equal(Path.GetFullPath("/repo"), settings.Engine.RepoRoot);
        Assert.Equal(data.FullName, settings.Engine.ModelRoot);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath("/repo"))))[..16];
        Assert.Equal(Path.Combine("/data/maquettiste/cache", key), settings.Engine.CacheDirectory);
        Assert.Equal(0, settings.Engine.MaxDegreeOfParallelism);
        Assert.True(settings.RepoRootConfigured);
    }

    [Fact]
    public void Without_variables_the_repo_is_the_data_folder_parent_and_the_cache_is_under_temp()
    {
        var data = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "x", ".maquettiste"));

        var settings = EditorSettings.From(new FakeSiteVariables(), data);

        Assert.Equal(Path.Combine(Path.GetTempPath(), "x").TrimEnd(Path.DirectorySeparatorChar), settings.Engine.RepoRoot);
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "maquettiste-cache"), settings.Engine.CacheDirectory, StringComparison.Ordinal);
        Assert.False(settings.RepoRootConfigured);
    }

    [Theory]
    [InlineData("", "local")]
    [InlineData("local", "local")]
    [InlineData("hosted", "hosted")]
    [InlineData("HOSTED", "hosted")]
    [InlineData("something", "local")]
    public void The_mode_is_local_unless_hosted(string value, string mode) =>
        Assert.Equal(mode, EditorSettings.ModeOf(new FakeSiteVariables().Set("MAQUETTISTE_MODE", value)));

    [Fact]
    public async Task Configure_registers_singletons_that_build_with_scope_validation()
    {
        await using var host = EditorHost.Create();

        Assert.Same(host.Services.GetRequiredService<ModelStore>(), host.Services.GetRequiredService<ModelStore>());
        Assert.Same(host.Services.GetRequiredService<EngineOptions>(), host.Services.GetRequiredService<EditorSettings>().Engine);
        Assert.NotNull(host.Services.GetRequiredService<GenerationService>());
        Assert.NotNull(host.Services.GetRequiredService<JobQueue>());
        Assert.NotNull(host.Services.GetRequiredService<GitStatusReader>());
        Assert.Null(host.Store.Current); // no I/O at registration: the model loads on first use
    }

    [Fact]
    public void Configure_warns_once_when_a_token_is_set_while_local_trust_is_on()
    {
        var logs = new CapturingLoggers();
        var data = new DirectoryInfo(Path.GetTempPath());

        EditorSetup.Configure(new ServiceCollection(), new FakeSiteVariables().Set("MAQUETTISTE_EDITOR_TOKEN", "t"), data, logs.CreateLogger("f"));
        EditorSetup.Configure(new ServiceCollection(), new FakeSiteVariables().Set("MAQUETTISTE_EDITOR_TOKEN", "t").Set("MAQUETTISTE_LOCAL_TRUST", "off"), data, logs.CreateLogger("f"));
        EditorSetup.Configure(new ServiceCollection(), new FakeSiteVariables(), data, logs.CreateLogger("f"));

        Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("MAQUETTISTE_LOCAL_TRUST=off", StringComparison.Ordinal));
    }

    [Fact]
    public void Git_status_output_is_parsed_into_branch_head_and_changed_files()
    {
        var clean = GitStatusReader.Parse("## main...origin/main [ahead 1]\n", "99b67a4\n");
        var dirty = GitStatusReader.Parse("## phase-1\n M .maquettiste/model/entities/invoice.json\n?? .maquettiste/model/entities/refund.json\n", "f22a8b7");
        var detached = GitStatusReader.Parse("## HEAD (no branch)\n", "abc1234");
        var fresh = GitStatusReader.Parse("## No commits yet on main\n", null);

        Assert.Equal(new GitSummary("main", "99b67a4", 0), clean);
        Assert.Equal(new GitSummary("phase-1", "f22a8b7", 2), dirty);
        Assert.Equal(new GitSummary(null, "abc1234", 0), detached);
        Assert.Equal(new GitSummary("main", null, 0), fresh);
    }

    [Fact]
    public async Task Git_status_of_a_real_checkout_counts_model_changes()
    {
        await using var host = EditorHost.Create();
        if (!Git(host.RepoRoot, "init", "-q", "-b", "main") || !Git(host.RepoRoot, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "add", ".")
            || !Git(host.RepoRoot, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "commit", "-q", "-m", "fixture"))
        {
            Assert.Fail("git is required on the machine that runs these tests (CI images have it).");
        }

        File.AppendAllText(host.PathOf(".maquettiste/model/entities/invoice.md"), "More.\n");
        var status = await host.Services.GetRequiredService<GitStatusReader>().ReadAsync(EditorHost.Ct);

        Assert.NotNull(status);
        Assert.Equal("main", status.Branch);
        Assert.Matches("^[0-9a-f]{7,}$", status.Head!);
        Assert.Equal(1, status.ChangedModelFiles);
        var project = await host.GetAsync("/api/project");
        Contract.AssertResponse(project, "/api/project");
        Assert.Equal("main", project.Json["git"]!["branch"]!.GetValue<string>());
    }

    private static bool Git(string repo, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(repo);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        return process.ExitCode == 0;
    }
}
