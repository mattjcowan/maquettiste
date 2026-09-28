using Maquettiste.Engine.Generation;

namespace Maquettiste.Engine.Tests.Generation;

public sealed class WatchPathsTests
{
    [Fact]
    public void Paths_reported_through_a_symbolic_link_map_onto_the_configured_root()
    {
        var real = Path.Combine(Path.GetTempPath(), "maquettiste-tests", "wp-real-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(Path.GetTempPath(), "maquettiste-tests", "wp-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(real, "repo", ".maquettiste", "manifest"));
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, real);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Assert.Skip("symbolic links cannot be created here: " + e.Message);
                return;
            }

            var givenRoot = Path.Combine(link, "repo");
            var realRoot = WatchPaths.ResolveLinks(givenRoot);
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.Combine(WatchPaths.ResolveLinks(real), "repo")), realRoot);
            Assert.NotEqual(givenRoot, realRoot);

            var reported = Path.Combine(realRoot, ".maquettiste", "manifest", "sql-ddl.json");
            Assert.Equal(Path.Combine(givenRoot, ".maquettiste", "manifest", "sql-ddl.json"), WatchPaths.Map(reported, givenRoot, realRoot));
            Assert.Equal(givenRoot, WatchPaths.Map(realRoot, givenRoot, realRoot));

            var elsewhere = Path.Combine(Path.GetTempPath(), "other", "x.json");
            Assert.Equal(elsewhere, WatchPaths.Map(elsewhere, givenRoot, realRoot));
            Assert.Equal(reported, WatchPaths.Map(reported, realRoot, realRoot));
        }
        finally
        {
            try { Directory.Delete(link); } catch (IOException) { }
            try { Directory.Delete(real, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Resolving_links_on_a_plain_directory_returns_its_full_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maquettiste-tests", "wp-plain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(WatchPaths.ResolveLinks(Path.GetTempPath()) + Path.DirectorySeparatorChar + Path.GetRelativePath(Path.GetTempPath(), dir),
                WatchPaths.ResolveLinks(dir + Path.DirectorySeparatorChar));
        }
        finally
        {
            Directory.Delete(dir);
        }
    }
}
