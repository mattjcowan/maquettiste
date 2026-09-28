using System.Text;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>The resolver's memos: rendered-name keys never collide, and the inflector carried across runs stays bounded.</summary>
public sealed class ResolverMemoTests
{
    [Fact]
    public void Rendered_name_keys_differ_when_a_literal_looks_like_a_length_prefixed_value()
    {
        // {a} with a = "X|1:Y" against the pattern "{a}|5:X" with a = "Y": without the pattern's own length prefix both keys are
        // "{a}|5:X|1:Y".
        var key = new StringBuilder();
        var first = DatabaseRun.MemoKey(key, "{a}", [("a", null)], [("a", "X|1:Y")]);
        var second = DatabaseRun.MemoKey(key, "{a}|5:X", [("a", null), (null, ["5", "x"])], [("a", "Y")]);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Rendered_name_keys_are_equal_for_the_same_pattern_and_token_values()
    {
        var key = new StringBuilder();
        List<(string? Token, IReadOnlyList<string>? Literal)> segments = [("attribute", null), (null, ["x"]), ("member", null)];
        var first = DatabaseRun.MemoKey(key, "{attribute}_{member}", segments, [("member", "b"), ("attribute", "a"), ("unused", "z")]);
        var second = DatabaseRun.MemoKey(key, "{attribute}_{member}", segments, [("attribute", "a"), ("member", "b")]);
        var missing = DatabaseRun.MemoKey(key, "{attribute}_{member}", segments, [("attribute", "a")]);
        Assert.Equal(first, second);
        Assert.NotEqual(first, missing);
    }

    [Fact]
    public async Task The_inflector_is_reused_across_runs_until_its_memo_passes_the_limit()
    {
        var b = new ModelBuilder(5);
        b.Database("main", Dialect.PostgreSql);
        b.Entity("Invoice").Key("id", "uuid").Attr("lines", "string", a => a.Collection());
        var model = b.Build();
        var resolver = new ModelResolver(ResolutionKit.Options);
        var ct = TestContext.Current.CancellationToken;

        var before = await resolver.ResolveAsync(model, null, ct);
        var inflector = resolver.LastInflector;
        Assert.NotNull(inflector);
        await resolver.ResolveAsync(model, null, ct);
        Assert.Same(inflector, resolver.LastInflector);

        // Names of earlier runs (renames in a long-lived host) fill the memo past the limit: the next run starts a fresh one.
        var limit = ModelResolver.MaxCarriedWords(model.Documents.Count);
        for (var i = 0; inflector.MemoizedWords <= limit; i++)
            inflector.Pluralize("Word" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var after = await resolver.ResolveAsync(model, null, ct);
        Assert.NotSame(inflector, resolver.LastInflector);
        Assert.True(resolver.LastInflector!.MemoizedWords <= limit);
        Assert.Equal(ResolutionKit.Dump(before), ResolutionKit.Dump(after));
    }
}
