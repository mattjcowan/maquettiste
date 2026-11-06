namespace Maquettiste.Engine.Resolution;

/// <summary>
/// A resolved right (rights-design.md section 6): a named right (kind <c>right</c>, an element of <c>model/rights/</c> that many lifecycles
/// use) or an inline right (kind <c>process-right</c>, an entry of one lifecycle's rights, which shares its id with its use). It allows or
/// denies its actors, less those in <see cref="Except"/>, its verbs, under an optional condition; where it holds is each use's.
/// </summary>
public sealed class RRight : RObject
{
    /// <inheritdoc/>
    public override string Kind => Named ? "right" : "process-right";

    /// <summary>The name: unique among the lifecycle's inline rights, or among the named rights of its package.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The display name, falling back to the name.</summary>
    public string DisplayName { get; internal set; } = "";

    /// <summary>Why the right is there, shown with every refusal; <see langword="null"/> when there is none.</summary>
    public string? Description { get; internal set; }

    /// <summary><c>allow</c> or <c>deny</c>.</summary>
    public string Access { get; internal set; } = "";

    /// <summary>The actors it applies to, with their members at any depth, in file order; empty means everyone (<see cref="Everyone"/>).</summary>
    public RList<RActor> Actors { get; internal set; } = RList<RActor>.Empty;

    /// <summary>Whether the right applies to everyone: the file lists no actors.</summary>
    public bool Everyone { get; internal set; }

    /// <summary>The actors it does not apply to, in file order; empty when none. It never grants: other rights and the default still answer.</summary>
    public RList<RActor> Except { get; internal set; } = RList<RActor>.Empty;

    /// <summary>The verbs it speaks of, in file order: built-in verbs or the project's own.</summary>
    public RList<RVerb> Verbs { get; internal set; } = RList<RVerb>.Empty;

    /// <summary>The relation ends of the subject whose links it speaks of, in file order; empty when it speaks of the object.</summary>
    public RList<REnd> Ends { get; internal set; } = RList<REnd>.Empty;

    /// <summary>
    /// The condition as text, printed from the current names: an inline right's in its lifecycle's scope, a named right's in the scope of
    /// all its uses; the stored text for a named right whose condition is still text. <see langword="null"/> when the right has none.
    /// </summary>
    public string? ConditionText { get; internal set; }

    /// <summary>Whether it is a named right, an element, rather than an entry of one lifecycle's rights.</summary>
    public bool Named { get; internal set; }

    /// <summary>A named right's package, or <see langword="null"/> for the root and for an inline right.</summary>
    public RPackage? Package { get; internal set; }

    /// <summary>
    /// Where it holds: an inline right's one use; a named right's uses, in <see cref="ResolvedModel.Processes"/> order then entry order,
    /// empty while no lifecycle uses it.
    /// </summary>
    public RList<RRightUse> Uses { get; internal set; } = RList<RRightUse>.Empty;
}

/// <summary>
/// One entry of a lifecycle's rights (kind <c>process-right</c>, rights-design.md section 6): an inline right, which shares the entry's id, or
/// a use of a named right, with the states where it holds and its condition typed for this lifecycle's subjects.
/// </summary>
public sealed class RRightUse : RObject
{
    /// <inheritdoc/>
    public override string Kind => "process-right";

    /// <summary>The right that holds here: the named right the entry uses, or the inline right the entry states.</summary>
    public RRight Right { get; internal set; } = null!;

    /// <summary>The lifecycle whose rights list the entry.</summary>
    public RProcess Process { get; internal set; } = null!;

    /// <summary>The states of the lifecycle where the right holds, in file order; empty means every state.</summary>
    public RList<RState> States { get; internal set; } = RList<RState>.Empty;

    /// <summary>
    /// The right's condition typed for this lifecycle's subjects, its paths ending at the declaring entity's attributes, as a guard's that
    /// reads the object; <see langword="null"/> when the right has none, or when it does not type here (validation says why). Its
    /// JavaScript is <see langword="null"/> when it reads <c>current_actor()</c> or <c>in_state</c>.
    /// </summary>
    public RExpression? Condition { get; internal set; }
}

/// <summary>
/// A lifecycle, other than the process itself, that governs one of the process's subjects and could say no to an object, link or own verb
/// the process's events or rights name (rights-design.md section 6): it has a deny right naming the verb, or it denies by default.
/// </summary>
public sealed class RMayRefuse : IResolvedObject
{
    /// <summary>The synthesized id, <c>&lt;process id&gt;/may-refuse/&lt;lifecycle id&gt;</c>: an entry is no element of the model.</summary>
    public string Id { get; internal set; } = "";

    /// <inheritdoc/>
    public string Kind => "may-refuse";

    /// <summary>The keys of the process's <see cref="RProcess.MayRefuse"/> list, which decide the entry too.</summary>
    public IReadOnlyList<string> Dependencies { get; internal set; } = [];

    /// <summary>The lifecycle that could say no.</summary>
    public RProcess Lifecycle { get; internal set; } = null!;

    /// <summary>The verbs it could say no to, ordinal by key.</summary>
    public RList<RVerb> Verbs { get; internal set; } = RList<RVerb>.Empty;

    /// <summary>Whether an event of the process names one of <see cref="Verbs"/>, so a generated event check needs that lifecycle's states.</summary>
    public bool ByEvents { get; internal set; }
}
