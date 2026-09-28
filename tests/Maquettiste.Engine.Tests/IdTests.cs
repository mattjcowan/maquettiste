using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests;

public sealed class IdTests
{
    [Fact]
    public void Ulid_generator_creates_valid_unique_ids()
    {
        var generator = new UlidIdGenerator();

        var ids = Enumerable.Range(0, 10_000).Select(_ => generator.NewId()).ToList();

        Assert.All(ids, id => Assert.True(IdFormat.IsValid(id), id));
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Ulid_generator_encodes_the_clock_in_the_first_ten_characters()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
        var generator = new UlidIdGenerator(clock);

        var a = generator.NewId();
        var b = generator.NewId();
        clock.Now = clock.Now.AddMilliseconds(1);
        var c = generator.NewId();

        Assert.Equal(a[..10], b[..10]);
        Assert.NotEqual(a[..10], c[..10]);
        Assert.True(string.CompareOrdinal(a[..10], c[..10]) < 0);
        Assert.Equal(Ulid.NewUlid(clock.Now.AddMilliseconds(-1), new byte[10]).ToString()[..10], a[..10]);
    }

    [Theory]
    [InlineData("01JAX3K9V2Q7M4T8W1Z5C6B0DE", true)]
    [InlineData("7ZZZZZZZZZZZZZZZZZZZZZZZZZ", true)]
    [InlineData("01jax3k9v2q7m4t8w1z5c6b0de", false)]
    [InlineData("81JAX3K9V2Q7M4T8W1Z5C6B0DE", false)]
    [InlineData("01JAX3K9V2Q7M4T8W1Z5C6B0D", false)]
    [InlineData("01JAX3K9V2Q7M4T8W1Z5C6B0DEF", false)]
    [InlineData("01JAX3K9V2Q7M4T8W1Z5C6B0DI", false)]
    [InlineData("01JAX3K9V2Q7M4T8W1Z5C6B0DL", false)]
    [InlineData("01JAX3K9V2Q7M4T8W1Z5C6B0DO", false)]
    [InlineData("01JAX3K9V2Q7M4T8W1Z5C6B0DU", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Id_format_is_strict(string? id, bool valid) => Assert.Equal(valid, IdFormat.IsValid(id));

    [Fact]
    public void Id_format_matches_its_regular_expression()
    {
        var regex = new System.Text.RegularExpressions.Regex(IdFormat.Pattern);
        var generator = new UlidIdGenerator();
        for (var i = 0; i < 100; i++)
            Assert.Matches(regex, generator.NewId());
    }

    [Fact]
    public void Format_places_time_then_randomness()
    {
        var id = IdFormat.Format(0, 0, 1);

        Assert.Equal("00000000000000000000000001", id);
        Assert.True(IdFormat.IsValid(IdFormat.Format((1L << 48) - 1, ushort.MaxValue, ulong.MaxValue)));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdFormat.Format(1L << 48, 0, 0));
    }

    [Fact]
    public void Sequential_generator_is_deterministic_per_seed_and_sorted()
    {
        var a = new SequentialIdGenerator(3);
        var b = new SequentialIdGenerator(3);
        var c = new SequentialIdGenerator(4);

        var first = Enumerable.Range(0, 50).Select(_ => a.NewId()).ToList();
        var second = Enumerable.Range(0, 50).Select(_ => b.NewId()).ToList();
        var other = c.NewId();

        Assert.Equal(first, second);
        Assert.DoesNotContain(other, first);
        Assert.Equal(first.Order(StringComparer.Ordinal), first);
        Assert.All(first, id => Assert.True(IdFormat.IsValid(id)));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
