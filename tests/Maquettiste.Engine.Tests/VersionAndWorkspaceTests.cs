namespace Maquettiste.Engine.Tests;

/// <summary>
/// The release next to the engine contract. The workspace name is plain data the hosts detect
/// (<c>Maquettiste.Functions.Tests.WorkspaceDetectionTests</c>); the engine reads no version control.
/// </summary>
public sealed class VersionAndWorkspaceTests
{
    [Theory]
    [InlineData("0.5.3", "0.5.3", "0.5.3")]
    [InlineData("0.5.3-b14a8131cfe39", "0.5.3", "0.5.3-b14a8131cfe39")]
    [InlineData("0.6.0-rc.1.b3f9c2a1", "0.6.0-rc.1", "0.6.0-rc.1.b3f9c2a1")]
    [InlineData("0.5.3+0e35bb0aa11", "0.5.3", "0.5.3")]
    [InlineData("0.5.3-b14a8131cfe39+0e35bb0", "0.5.3", "0.5.3-b14a8131cfe39")]
    [InlineData("0.5.3-beta", "0.5.3-beta", "0.5.3-beta")] // not a build marker: not hex
    [InlineData("0.5.3-bad", "0.5.3-bad", "0.5.3-bad")] // hex, but too short to be one
    public void Product_drops_the_metadata_and_the_build_marker_build_only_the_metadata(string informational, string product, string build)
    {
        Assert.Equal(product, EngineVersion.ProductOf(informational));
        Assert.Equal(build, EngineVersion.BuildOf(informational));
    }

    [Fact]
    public void The_running_engine_reports_a_release_apart_from_the_contract()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", EngineVersion.Product);
        Assert.DoesNotContain("+", EngineVersion.Build, StringComparison.Ordinal);
        Assert.StartsWith(EngineVersion.Product, EngineVersion.Build, StringComparison.Ordinal);
        Assert.Equal("1.0.0", EngineVersion.Value);
    }

    [Fact]
    public void Nothing_known_is_four_nulls() => Assert.Equal(new WorkspaceInfo(null, null, null, null), WorkspaceInfo.None);
}
