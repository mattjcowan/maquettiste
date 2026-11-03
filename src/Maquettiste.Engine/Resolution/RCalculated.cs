using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// The named values the project's calculation files compute for one resolved object (next-phase-2-design.md section 5.5). Nothing is
/// evaluated until a value is read; the first read evaluates it and later reads in the same resolved model reuse it.
/// </summary>
public sealed class RCalculated
{
    private readonly CalculationHost? _host;

    internal RCalculated(CalculationHost? host, RAnnotated? owner)
    {
        _host = host;
        Owner = owner;
    }

    /// <summary>The view of an object whose kind declares no calculated value (and of every object of a project that declares none).</summary>
    public static RCalculated Empty { get; } = new(null, null);

    /// <summary>The names declared for the object's kind, ordinal.</summary>
    public IReadOnlyList<string> Names => ReadNames(null);

    /// <summary>Whether the object's kind declares a calculated value of that name. Evaluates nothing.</summary>
    /// <param name="name">The name.</param>
    /// <returns><see langword="true"/> when declared.</returns>
    public bool Contains(string name) => Declares(name, null);

    /// <summary>
    /// The value, evaluated on first read. Throws <see cref="CalculatedValueException"/> when the value failed or the kind does not
    /// declare the name.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>Null, bool, long, double, string, or a list or map of them.</returns>
    public object? this[string name] => Read(name, null, CancellationToken.None);

    /// <summary>The object the values belong to; <see langword="null"/> for <see cref="Empty"/>.</summary>
    internal RAnnotated? Owner { get; }

    /// <summary>The host that evaluates the values; <see langword="null"/> when the project declares none.</summary>
    internal CalculationHost? Host => _host;

    /// <summary>The view of an object: its own, or one without a host that still knows its kind (so an undeclared read names it).</summary>
    /// <param name="owner">The object.</param>
    /// <returns>The view.</returns>
    internal static RCalculated Of(RAnnotated owner) => owner.Calculated.Owner is not null ? owner.Calculated : new RCalculated(null, owner);

    /// <summary>The names declared for the object's kind, ordinal; records <c>cv:&lt;kind&gt;</c>.</summary>
    /// <param name="reads">The reader's recorder, or <see langword="null"/>.</param>
    /// <returns>The names.</returns>
    internal IReadOnlyList<string> ReadNames(IReadRecorder? reads)
    {
        if (Owner is null)
            return [];
        reads?.Record(CalculationHost.DeclarationKey(Owner.Kind));
        return _host?.NamesOf(Owner.Kind) ?? [];
    }

    /// <summary>Whether the object's kind declares a name; records <c>cv:&lt;kind&gt;</c>, evaluates nothing.</summary>
    /// <param name="name">The name.</param>
    /// <param name="reads">The reader's recorder, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when declared.</returns>
    internal bool Declares(string name, IReadRecorder? reads)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Owner is null)
            return false;
        reads?.Record(CalculationHost.DeclarationKey(Owner.Kind));
        return _host?.Declares(Owner.Kind, name) ?? false;
    }

    /// <summary>
    /// Reads a value: evaluated on first read, else reused; the keys it read and its calculation file's <c>x:</c> key go to
    /// <paramref name="reads"/>.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="reads">The reader's recorder, or <see langword="null"/>.</param>
    /// <param name="ct">The reader's cancellation.</param>
    /// <returns>The value.</returns>
    /// <exception cref="CalculatedValueException">The value failed (MQ5010, MQ5011, MQ5013, or a failure it read), or the kind does not
    /// declare the name (MQ5012).</exception>
    internal object? Read(string name, IReadRecorder? reads, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_host is not null && Owner is not null)
            return _host.Read(Owner, name, reads, ct);
        if (Owner is not null)
            reads?.Record(CalculationHost.DeclarationKey(Owner.Kind));
        var kind = Owner?.Kind ?? "this object";
        throw new CalculatedValueException(RuleCatalog.Create("MQ5012",
            $"The calculated value '{name}' is not declared for {kind}: {kind} declares no calculated value.", Owner?.Id, null));
    }
}

/// <summary>A calculated value that failed, or a read of one its object's kind does not declare (next-phase-2-design.md section 5.5).</summary>
public sealed class CalculatedValueException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="diagnostic">The failure: MQ5010, MQ5011, MQ5012 or MQ5013.</param>
    /// <param name="inner">The underlying exception, if any.</param>
    public CalculatedValueException(Diagnostic diagnostic, Exception? inner = null)
        : base(diagnostic?.Message, inner)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        Diagnostic = diagnostic;
    }

    /// <summary>The failure.</summary>
    public Diagnostic Diagnostic { get; }
}
