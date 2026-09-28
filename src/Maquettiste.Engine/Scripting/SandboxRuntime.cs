using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Jint;
using Jint.Native.Temporal;
using Jint.Runtime;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Scripting;

/// <summary>
/// A clock fixed at 2000-01-01T00:00:00Z in UTC, so <c>Date.now()</c>, <c>new Date()</c> and <c>Temporal.Now</c> are constant
/// (engine-design.md section 10).
/// </summary>
internal sealed class FixedTimeSystem : ITimeSystem
{
    private static readonly DateTimeOffset Epoch = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly DefaultTimeSystem _parser = new(TimeZoneInfo.Utc, CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public TimeZoneInfo DefaultTimeZone => TimeZoneInfo.Utc;

    /// <inheritdoc/>
    public DateTimeOffset GetUtcNow() => Epoch;

    /// <inheritdoc/>
    public TimeSpan GetUtcOffset(long epochMilliseconds) => TimeSpan.Zero;

    /// <inheritdoc/>
    public bool TryParse(string date, out long epochMilliseconds) => _parser.TryParse(date, out epochMilliseconds);
}

/// <summary>
/// The Temporal and Intl time zone provider: Jint's default provider, except that the default ("system") time zone is always
/// UTC, so <c>Temporal.Now.*</c> and <c>Intl.DateTimeFormat()</c> do not depend on the host's time zone (engine-design.md
/// section 10). Named zones a script asks for explicitly still resolve through the host's time zone data.
/// </summary>
internal sealed class UtcDefaultTimeZoneProvider : ITimeZoneProvider
{
    private const string Utc = "UTC";

    /// <summary>The shared instance (stateless).</summary>
    public static UtcDefaultTimeZoneProvider Instance { get; } = new();

    private static ITimeZoneProvider Inner => DefaultTimeZoneProvider.Instance;

    /// <inheritdoc/>
    public long GetOffsetNanosecondsFor(string timeZoneId, BigInteger epochNanoseconds) => Inner.GetOffsetNanosecondsFor(timeZoneId, epochNanoseconds);

    /// <inheritdoc/>
    public BigInteger[] GetPossibleInstantsFor(string timeZoneId, int year, int month, int day, int hour, int minute, int second, int millisecond, int microsecond, int nanosecond) =>
        Inner.GetPossibleInstantsFor(timeZoneId, year, month, day, hour, minute, second, millisecond, microsecond, nanosecond);

    /// <inheritdoc/>
    public BigInteger? GetNextTransition(string timeZoneId, BigInteger epochNanoseconds) => Inner.GetNextTransition(timeZoneId, epochNanoseconds);

    /// <inheritdoc/>
    public BigInteger? GetPreviousTransition(string timeZoneId, BigInteger epochNanoseconds) => Inner.GetPreviousTransition(timeZoneId, epochNanoseconds);

    /// <inheritdoc/>
    public bool IsValidTimeZone(string timeZoneId) => Inner.IsValidTimeZone(timeZoneId);

    /// <inheritdoc/>
    public string? CanonicalizeTimeZone(string timeZoneId) => Inner.CanonicalizeTimeZone(timeZoneId);

    /// <inheritdoc/>
    public IReadOnlyCollection<string> GetAvailableTimeZones() => Inner.GetAvailableTimeZones();

    /// <inheritdoc/>
    public string GetDefaultTimeZone() => Utc;

    /// <inheritdoc/>
    public string? GetPrimaryTimeZoneIdentifier(string timeZoneId) => Inner.GetPrimaryTimeZoneIdentifier(timeZoneId);
}

/// <summary>The state of the top-level call a sandbox is running; read by the proxies, the constraint and the globals.</summary>
internal sealed class CallState
{
    /// <summary>The unit's read recorder, or <see langword="null"/>.</summary>
    public IReadRecorder? Reads { get; private set; }

    /// <summary>The call's cancellation token.</summary>
    public CancellationToken Token { get; private set; }

    /// <summary>The call's pack parameters.</summary>
    public IReadOnlyDictionary<string, object?>? Parameters { get; private set; }

    /// <summary>Whether a top-level call is running.</summary>
    public bool Active { get; private set; }

    /// <summary>Counts top-level calls (and the loading phase, as 0), so per-call caches know when a new call started.</summary>
    public long CallNumber { get; private set; }

    /// <summary>Starts a call.</summary>
    /// <param name="ctx">The call context.</param>
    public void Begin(ScriptCallContext ctx)
    {
        _recordedLists.Clear();
        Reads = ctx.Reads;
        Token = ctx.CancellationToken;
        Parameters = ctx.Parameters;
        Active = true;
        CallNumber++;
    }

    /// <summary>Ends a call.</summary>
    public void End()
    {
        _recordedLists.Clear();
        Reads = null;
        Token = CancellationToken.None;
        Parameters = null;
        Active = false;
        CallNumber++;
    }

    /// <summary>Records one dependency key.</summary>
    /// <param name="key">The key.</param>
    public void Record(string key) => Reads?.Record(key);

    /// <summary>Records dependency keys, once per key list and call.</summary>
    /// <param name="keys">The keys.</param>
    /// <remarks>
    /// Recorded keys form a set (engine-design.md section 11), so a list recorded again in the same call adds nothing; skipping it
    /// keeps a script that walks a large list cheap (every item and length read records the list's membership keys, and a
    /// database's table list has one per model file).
    /// </remarks>
    public void Record(IReadOnlyList<string> keys)
    {
        var reads = Reads;
        if (reads is null || !_recordedLists.Add(keys))
            return;
        for (var i = 0; i < keys.Count; i++)
            reads.Record(keys[i]);
    }

    private readonly HashSet<IReadOnlyList<string>> _recordedLists = new(ReferenceEqualityComparer.Instance);
}

/// <summary>
/// Stops a running script when the current call's <see cref="ScriptCallContext.CancellationToken"/> is cancelled; the pool's run
/// token is wired separately through <c>Options.CancellationToken</c>.
/// </summary>
internal sealed class CallCancellationConstraint(CallState state) : Constraint
{
    /// <inheritdoc/>
    public override void Check()
    {
        if (state.Token.IsCancellationRequested)
            throw new ExecutionCanceledException();
    }

    /// <inheritdoc/>
    public override void Reset()
    {
    }
}

/// <summary>The deterministic <c>Math.random</c>: xorshift128+ seeded from a string (the unit key or element id).</summary>
internal sealed class SeededRandom
{
    private ulong _s0;
    private ulong _s1;

    /// <summary>Creates a generator seeded with the empty string.</summary>
    public SeededRandom() => Seed("");

    /// <summary>Re-seeds the generator: the state is the first 16 bytes of SHA-256 over the UTF-8 seed.</summary>
    /// <param name="seed">The seed.</param>
    public void Seed(string seed)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(seed), hash);
        _s0 = BinaryPrimitives.ReadUInt64LittleEndian(hash);
        _s1 = BinaryPrimitives.ReadUInt64LittleEndian(hash[8..]);
        if (_s0 == 0 && _s1 == 0)
            _s1 = 0x9E3779B97F4A7C15UL;
    }

    /// <summary>The next value in [0, 1).</summary>
    /// <returns>The value.</returns>
    public double NextDouble()
    {
        var s1 = _s0;
        var s0 = _s1;
        _s0 = s0;
        s1 ^= s1 << 23;
        _s1 = s1 ^ s0 ^ (s1 >> 17) ^ (s0 >> 26);
        var result = _s1 + s0;
        return (result >> 11) * (1.0 / (1UL << 53));
    }
}

/// <summary>A script failed (MQ6016 while rendering, MQ5002 for validation rule scripts) and the call produced no result.</summary>
internal sealed class ScriptErrorException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="diagnostic">The diagnostic, with the script path and line when known.</param>
    /// <param name="inner">The underlying exception.</param>
    public ScriptErrorException(Diagnostic diagnostic, Exception? inner = null)
        : base(diagnostic.Message, inner)
    {
        Diagnostic = diagnostic;
    }

    /// <summary>The diagnostic.</summary>
    public Diagnostic Diagnostic { get; }
}

/// <summary>A value could not cross the sandbox boundary; mapped to a script error by the call wrapper.</summary>
internal sealed class ScriptValueException(string message) : Exception(message);
