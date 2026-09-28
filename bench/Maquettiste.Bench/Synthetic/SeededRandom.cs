namespace Maquettiste.Bench.Synthetic;

/// <summary>
/// A SplitMix64 generator: the synthetic model's only source of randomness, so the same seed gives the same model on every
/// machine, runtime and culture (<see cref="System.Random"/>'s algorithm is not a documented contract).
/// </summary>
internal sealed class SeededRandom(ulong seed)
{
    private ulong _state = seed;

    /// <summary>Returns the next 64 random bits.</summary>
    /// <returns>The bits.</returns>
    public ulong NextULong()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Returns an integer in <c>[0, max)</c>.</summary>
    /// <param name="max">The exclusive upper bound, positive.</param>
    /// <returns>The integer.</returns>
    public int Next(int max) => max <= 1 ? 0 : (int)(NextULong() % (ulong)max);

    /// <summary>Returns an integer in <c>[min, max]</c>.</summary>
    /// <param name="min">The inclusive lower bound.</param>
    /// <param name="max">The inclusive upper bound.</param>
    /// <returns>The integer.</returns>
    public int Between(int min, int max) => min + Next(max - min + 1);

    /// <summary>Returns a number in <c>[0, 1)</c> with 53 random bits.</summary>
    /// <returns>The number.</returns>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Returns <see langword="true"/> with the given probability.</summary>
    /// <param name="probability">The probability.</param>
    /// <returns>The draw.</returns>
    public bool Chance(double probability) => NextDouble() < probability;
}

/// <summary>
/// Deterministic element ids: valid uppercase Crockford ULIDs (engine-design.md section 2.1) made of a seed-derived time part, a
/// counter (unique) and seeded random bits. No clock, no <see cref="Guid"/>.
/// </summary>
internal sealed class SyntheticIds
{
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private readonly SeededRandom _random;
    private readonly string _prefix;
    private long _counter;

    /// <summary>Creates the generator.</summary>
    /// <param name="seed">The model seed.</param>
    public SyntheticIds(int seed)
    {
        _random = new SeededRandom(0xA5A5_0000_0000_0000UL ^ (ulong)(uint)seed);
        _prefix = "01J" + Encode(_random.NextULong(), 7);
    }

    /// <summary>Returns the next id.</summary>
    /// <returns>A 26-character ULID string.</returns>
    public string Next()
    {
        _counter++;
        return _prefix + Encode((ulong)_counter, 7) + Encode(_random.NextULong(), 9);
    }

    private static string Encode(ulong value, int length)
    {
        Span<char> chars = stackalloc char[length];
        for (var i = length - 1; i >= 0; i--)
        {
            chars[i] = Crockford[(int)(value & 31)];
            value >>= 5;
        }

        return new string(chars);
    }
}
