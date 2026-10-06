using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Maquettiste.Testing;

namespace Maquettiste.Functions.Tests;

/// <summary>
/// The site zip's <c>_functions/</c> (phase2-design.md section 3.1): within the host's limits (50 files, 1 MB each, 4 MB in all) and the
/// budget (28 files since snapshots added SnapshotEndpoints.cs, 40 KB each, 400 KB in all); directives only in <c>Directives.cs</c>, pinning the version <c>Directory.Build.props</c>
/// builds; no <c>#:project</c>; class names unique across files; and <c>_variables.json</c> declaring what the functions read.
/// </summary>
public sealed partial class BundleTests
{
    private static readonly string Folder = Path.Combine(Fixtures.RepoRoot, "src", "Maquettiste.Functions", "_functions");

    private static IReadOnlyList<FileInfo> Files => [.. new DirectoryInfo(Folder).EnumerateFiles().OrderBy(f => f.Name, StringComparer.Ordinal)];

    [Fact]
    public void The_functions_fit_the_host_limits_and_the_budget()
    {
        var files = Files;

        Assert.All(files, f => Assert.Equal(".cs", f.Extension));
        Assert.InRange(files.Count, 1, 28);
        Assert.All(files, f => Assert.True(f.Length <= 40 * 1024, $"{f.Name} is {f.Length} bytes, over the 40 KB budget."));
        Assert.True(files.Sum(f => f.Length) <= 400 * 1024, $"_functions/ is {files.Sum(f => f.Length)} bytes, over the 400 KB budget.");
        Assert.True(files.Count <= 50 && files.All(f => f.Length <= 1024 * 1024) && files.Sum(f => f.Length) <= 4 * 1024 * 1024);
    }

    [Fact]
    public void Only_directives_cs_has_directives_and_it_pins_the_built_engine_version()
    {
        var props = XDocument.Load(Path.Combine(Fixtures.RepoRoot, "Directory.Build.props"));
        var prefix = props.Descendants("VersionPrefix").Single().Value;
        var suffix = props.Descendants("VersionSuffix").SingleOrDefault()?.Value;
        var version = string.IsNullOrEmpty(suffix) ? prefix : prefix + "-" + suffix;

        foreach (var file in Files.Where(f => f.Name != "Directives.cs"))
            Assert.DoesNotContain(File.ReadAllLines(file.FullName), l => l.TrimStart().StartsWith("#:", StringComparison.Ordinal));
        var directives = File.ReadAllLines(Path.Combine(Folder, "Directives.cs")).Where(l => l.StartsWith("#:", StringComparison.Ordinal)).ToList();
        Assert.Equal(["#:package Maquettiste.Engine@" + version, "#:package StaticSiteHost.Abstractions@*"], directives);
        Assert.All(File.ReadAllLines(Path.Combine(Folder, "Directives.cs")), l => Assert.True(l.Length == 0 || l.StartsWith("//", StringComparison.Ordinal) || l.StartsWith("#:", StringComparison.Ordinal), l));
    }

    [Fact]
    public void The_image_version_label_defaults_to_the_built_version()
    {
        var props = XDocument.Load(Path.Combine(Fixtures.RepoRoot, "Directory.Build.props"));
        var prefix = props.Descendants("VersionPrefix").Single().Value;
        var suffix = props.Descendants("VersionSuffix").SingleOrDefault()?.Value;
        var version = string.IsNullOrEmpty(suffix) ? prefix : prefix + "-" + suffix;

        var dockerfile = File.ReadAllLines(Path.Combine(Fixtures.RepoRoot, "docker", "Dockerfile"));
        Assert.Contains("ARG MAQUETTISTE_VERSION=" + version, dockerfile);
        Assert.Contains(dockerfile, l => l.Contains("org.opencontainers.image.version=\"${MAQUETTISTE_VERSION}\"", StringComparison.Ordinal));
    }

    [Fact]
    public void No_file_references_a_project()
    {
        Assert.All(Files, f => Assert.DoesNotContain("#:project", File.ReadAllText(f.FullName), StringComparison.Ordinal));
    }

    [Fact]
    public void Type_names_are_unique_across_files_and_every_file_has_its_own_usings()
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Files.Where(f => f.Name != "Directives.cs"))
        {
            var text = File.ReadAllText(file.FullName);
            foreach (Match match in TypeDeclaration().Matches(text))
            {
                var name = match.Groups["name"].Value;
                Assert.False(owners.TryGetValue(name, out var other), $"{name} is declared in {other} and {file.Name} (CS0101).");
                owners[name] = file.Name;
            }

            Assert.Contains("namespace Maquettiste.Functions;", text, StringComparison.Ordinal);
        }

        Assert.Contains("EditorSetup", owners.Keys);
        Assert.Contains("JobCompletedEvent", owners.Keys);
    }

    [Fact]
    public void Variables_json_declares_every_variable_the_functions_read()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Fixtures.RepoRoot, "src", "Maquettiste.Functions", "_variables.json")));
        var declared = document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        var read = typeof(EditorSettings).GetFields().Where(f => f.IsLiteral && f.Name.EndsWith("Variable", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(read, declared);
        Assert.True(document.RootElement.GetProperty("MAQUETTISTE_EDITOR_TOKEN").GetProperty("secret").GetBoolean());
        Assert.Equal("${env:MAQUETTISTE_REPO_ROOT}", document.RootElement.GetProperty("MAQUETTISTE_REPO_ROOT").GetProperty("default").GetString());
    }

    [GeneratedRegex(@"^(public|internal)\s+(sealed\s+|static\s+|abstract\s+|partial\s+)*(class|record|struct|interface|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Multiline)]
    private static partial Regex TypeDeclaration();
}
