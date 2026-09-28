using System.Globalization;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>The billing fixture's resolved model against a golden text rendering (set MAQUETTISTE_UPDATE_GOLDEN=1 to rewrite it).</summary>
public sealed class GoldenTests
{
    private static string Expected => Path.Combine(Fixtures.RepoRoot, "tests", "Maquettiste.Engine.Tests", "Resolution", "Golden", "billing");

    [Fact]
    public void Billing_resolves_to_the_golden_model()
    {
        var text = ResolutionKit.Dump(ResolutionKit.Resolve(BillingFixture.Create()));
        var actual = Directory.CreateTempSubdirectory("mq-resolve-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(actual, "resolved.txt"), text);
            Golden.AssertMatches(Expected, actual);
        }
        finally
        {
            Directory.Delete(actual, recursive: true);
        }
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    [InlineData("")]
    public void Resolution_is_identical_under_any_culture_and_run(string culture)
    {
        var reference = ResolutionKit.Dump(ResolutionKit.Resolve(BillingFixture.Create()));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Equal(reference, ResolutionKit.Dump(ResolutionKit.Resolve(BillingFixture.Create())));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
