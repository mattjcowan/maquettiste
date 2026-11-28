using System.Collections.Concurrent;
using System.Text;

namespace Maquettiste.Engine.Text;

/// <summary>
/// One string instance per distinct value (ordinal) among the strings a load and its index keep: element and sub-element ids, the ids
/// references hold, JSON pointers and schema references. A model of thousands of files repeats the same pointers in every file
/// (<c>/attributes/0</c>) and each id in every file that refers to it; without a pool every copy is a string of its own. A store's loader
/// owns one pool and carries it across loads, so an edit's files take the instances the previous snapshot holds; the owner bounds it
/// (<see cref="Count"/>, <see cref="Reset"/>). Thread-safe: parallel readers and index walks intern at once, and a lookup that finds the
/// value allocates nothing. Pointers and schema references are few and very frequent, so they sit in a map read without a lock;
/// everything else (ids, hundreds of thousands, spread evenly) in striped sets, which cost less memory per value. Interning never changes a
/// value, so nothing that reads the strings can tell.
/// </summary>
internal sealed class StringPool
{
    private const int StripeCount = 32; // a power of two

    private readonly Stripe[] _stripes = new Stripe[StripeCount];

    private readonly ConcurrentDictionary<string, string> _pointers = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _pointerLookup;

    /// <summary>Creates an empty pool.</summary>
    public StringPool()
    {
        for (var i = 0; i < _stripes.Length; i++)
            _stripes[i] = new Stripe();
        _pointerLookup = _pointers.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>How many distinct strings the pool holds.</summary>
    public int Count
    {
        get
        {
            var count = _pointers.Count;
            foreach (var stripe in _stripes)
            {
                lock (stripe.Gate)
                    count += stripe.Set.Count;
            }

            return count;
        }
    }

    /// <summary>The pool's instance of a value, adding this one when the pool has none.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The pooled instance (<paramref name="value"/> itself the first time).</returns>
    public string Intern(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
            return string.Empty;
        if (!IsIdLike(value))
            return _pointers.TryGetValue(value, out var pointer) ? pointer : _pointers.GetOrAdd(value, value);
        var stripe = StripeOf(value);
        lock (stripe.Gate)
        {
            if (stripe.Set.TryGetValue(value, out var held))
                return held;
            stripe.Set.Add(value);
            return value;
        }
    }

    /// <summary>The pool's instance of a value given as characters; a new string is allocated only for a value the pool lacks.</summary>
    /// <param name="value">The characters.</param>
    /// <returns>The pooled instance.</returns>
    public string Intern(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return string.Empty;
        if (!IsIdLike(value))
        {
            if (_pointerLookup.TryGetValue(value, out var pointer))
                return pointer;
            var text = value.ToString();
            return _pointers.GetOrAdd(text, text);
        }

        var stripe = StripeOf(value);
        lock (stripe.Gate)
        {
            var lookup = stripe.Set.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(value, out var held))
                return held;
            var created = value.ToString();
            stripe.Set.Add(created);
            return created;
        }
    }

    /// <summary>
    /// The pool's instance of an ASCII value given as UTF-8 bytes (an id as JSON holds it), or <see langword="null"/> when the bytes are not
    /// ASCII or longer than 128 bytes (the caller reads the string itself).
    /// </summary>
    /// <param name="utf8">The bytes.</param>
    /// <returns>The pooled instance, or <see langword="null"/>.</returns>
    public string? InternAscii(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > 128 || !Ascii.IsValid(utf8))
            return null;
        Span<char> chars = stackalloc char[utf8.Length];
        Ascii.ToUtf16(utf8, chars, out var written);
        return Intern(chars[..written]);
    }

    /// <summary>Empties the pool (its owner refills it from what it still holds).</summary>
    public void Reset()
    {
        _pointers.Clear();
        foreach (var stripe in _stripes)
        {
            lock (stripe.Gate)
            {
                stripe.Set.Clear();
                stripe.Set.TrimExcess();
            }
        }
    }

    /// <summary>Whether a value has an id's length and first character: those go to the striped sets, everything else to the shared map.</summary>
    private static bool IsIdLike(ReadOnlySpan<char> value) => value.Length == IdFormat.Length && value[0] is >= '0' and <= '7';

    /// <summary>
    /// The stripe of an id: chosen by its last two characters, which are random in an id (no second hash of the whole value; the set
    /// hashes it once).
    /// </summary>
    private Stripe StripeOf(ReadOnlySpan<char> value) => _stripes[((value[^1] * 31) + value[^2]) & (StripeCount - 1)];

    private sealed class Stripe
    {
        public readonly Lock Gate = new();
        public readonly HashSet<string> Set = new(StringComparer.Ordinal);
    }
}
