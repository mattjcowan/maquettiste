using Maquettiste.Engine.Model;
using System.Diagnostics;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Rendering;

/// <summary>
/// Cancellation in the middle of a unit, time-zone independent date builtins, bounded regular expressions and bounded
/// <c>indent</c>.
/// </summary>
public sealed class RobustnessTests
{
    // Each iteration copies 20 000 characters twice; 900 000 iterations stay under the loop limit but take seconds.
    private const string SlowTemplate = "{{ for i in 1..900000 }}{{ s = 'x' | string.pad_left 20000 | string.replace 'x' 'y' }}{{ end }}done";

    [Fact]
    public async Task Cancelling_render_one_in_the_middle_of_a_unit_throws()
    {
        var model = await BillingModel.GetAsync();
        using var pack = new TempPack("adhoc", new Dictionary<string, string> { ["main.scriban"] = SlowTemplate });
        var packUnit = new PackUnit { Id = "main", Template = "main.scriban", For = "model", Output = "out.txt" };
        var loaded = pack.Load(RenderKit.Manifest("adhoc", packUnit));
        var planned = new PlannedUnit("adhoc/main", loaded, packUnit, null, "static:adhoc/main");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(200);
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RenderKit.NewRenderer().RenderOneAsync(planned, RenderKit.Context(model, [loaded], 1), cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "Cancellation took " + watch.Elapsed);
    }

    [Fact]
    public async Task Cancelling_the_stream_in_the_middle_of_a_unit_throws()
    {
        var model = await BillingModel.GetAsync();
        using var pack = new TempPack("adhoc", new Dictionary<string, string> { ["main.scriban"] = SlowTemplate });
        var packUnit = new PackUnit { Id = "main", Template = "main.scriban", For = "model", Output = "out.txt" };
        var loaded = pack.Load(RenderKit.Manifest("adhoc", packUnit));
        var planned = new PlannedUnit("adhoc/main", loaded, packUnit, null, "static:adhoc/main");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(200);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var unit in RenderKit.NewRenderer().RenderAsync([planned], RenderKit.Context(model, [loaded], 1), null, cts.Token))
                Assert.Fail("A unit was yielded: " + string.Join("; ", unit.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        });
    }

    [Theory]
    [InlineData("{{ date.parse '2020-01-01T00:00:00+02:00' | date.to_string '%F %T %Z' }}", "2019-12-31 22:00:00 +00:00")]
    [InlineData("{{ date.parse '2020-01-01T00:00:00Z' | date.to_string '%F %T %Z' }}", "2020-01-01 00:00:00 +00:00")]
    [InlineData("{{ date.parse '2020-01-01' | date.to_string '%F %T %Z' }}", "2020-01-01 00:00:00 +00:00")]
    [InlineData("{{ date.parse '20/01/2022 08:32:48 +02:00' culture:'en-GB' | date.to_string '%F %T' }}", "2022-01-20 06:32:48")]
    [InlineData("{{ date.parse '2018--06--17 10:11:12' '%Y--%m--%d %T' | date.to_string '%F %T %Z' }}", "2018-06-17 10:11:12 +00:00")]
    [InlineData("{{ date.parse_to_string '2020-01-01T00:00:00+02:00' '%F %T %Z' }}", "2019-12-31 22:00:00 +00:00")]
    [InlineData("{{ '03 14, 2016' | date.parse_to_string '%F' input_pattern: '%m %d, %Y' }}", "2016-03-14")]
    [InlineData("{{ date.parse '2020-01-01' | date.add_days 1 | date.to_string '%F %Z' }}", "2020-01-02 +00:00")]
    public async Task Dates_are_utc_whatever_the_machine_time_zone(string template, string expected)
    {
        Assert.Equal(expected, (await Adhoc.RenderAsync(template)).Text());
    }

    [Theory]
    [InlineData("{{ date.parse '2018-06-17 +02:00' '%Y-%m-%d %Z' }}")]
    [InlineData("{{ '2018-06-17 +02:00' | date.parse_to_string '%F' input_pattern: '%Y-%m-%d %Z' }}")]
    public async Task A_date_pattern_that_reads_an_offset_is_refused(string template)
    {
        var error = (await Adhoc.RenderAsync(template)).Error();
        Assert.Equal("MQ6006", error.Rule);
        Assert.Contains("UTC offset", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_catastrophic_regular_expression_fails_fast_with_MQ6007()
    {
        var watch = Stopwatch.StartNew();
        var error = (await Adhoc.RenderAsync("{{ s = ('a' * 30) + '!' }}{{ for i in 1..50 }}{{ s | regex.match '^(a+)+$' }}{{ end }}")).Error();
        Assert.Equal("MQ6007", error.Rule);
        Assert.Equal(".maquettiste/templates/adhoc/main.scriban", error.FilePath);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "The regex ran " + watch.Elapsed);
    }

    [Fact]
    public async Task Indent_is_bounded()
    {
        var count = (await Adhoc.RenderAsync("{{ indent 'a' 300000000 }}")).Error();
        Assert.Equal("MQ6006", count.Rule);

        var size = (await Adhoc.RenderAsync("{{ text = \"a\\n\" * 100000 }}{{ indent text ('x' * 1000) | string.size }}")).Error();
        Assert.Equal("MQ6007", size.Rule);

        Assert.Equal("    a\n\n    b", (await Adhoc.RenderAsync("{{ indent \"a\\n\\nb\" 4 }}")).Text());
    }
}
