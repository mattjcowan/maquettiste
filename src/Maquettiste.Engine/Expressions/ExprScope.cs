using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Expressions;

/// <summary>
/// A member an expression can read (next-phase-3-design.md section 3.1): an attribute, a value object member, a column, an input
/// member, a context or payload attribute, a subject's attribute or a constraint parameter. The text names it by
/// <see cref="Prefix"/> and <see cref="Name"/> (<c>event.amount</c>, <c>subject.total</c>, <c>total.amount</c>); the tree by
/// <see cref="Id"/>, a keyed id for an inherited or contributed member and <c>attributeId.memberId</c> for a value object member.
/// </summary>
/// <param name="Id">The id a path holds.</param>
/// <param name="Name">The member's own name: an attribute name, or a column's physical name.</param>
/// <param name="Type">Its type; a kind the core does not have (<c>float</c>, <c>value-object</c>…) is refused where it is read.</param>
/// <param name="Nullable">Whether the value may be missing: optional, nullable, or a payload member not every event carries.</param>
public sealed record ExprMember(string Id, string Name, ExprType Type, bool Nullable)
{
    /// <summary>The dotted identifiers the text writes before <see cref="Name"/>, or <see langword="null"/>.</summary>
    public string? Prefix { get; init; }

    /// <summary>Whether the member is a collection, which no path reads (MQ3102).</summary>
    public bool Collection { get; init; }

    /// <summary>Whether the value is a fixed-length string: read without its trailing spaces.</summary>
    public bool FixedLength { get; init; }

    /// <summary>The declared scale of a decimal, or <see langword="null"/>.</summary>
    public int? Scale { get; init; }

    /// <summary>What the member stands for in the caller's model (a resolved attribute, a column…), or <see langword="null"/>.</summary>
    public object? Target { get; init; }
}

/// <summary>A navigation an expression can follow at a guard site: a relation end, or a value it leads to.</summary>
/// <param name="Id">The relation end id a path holds.</param>
/// <param name="Name">The navigation's name.</param>
/// <param name="Target">The members and navigations of what it leads to.</param>
/// <param name="ToOne">Whether it leads to at most one record; a to-many navigation is refused (MQ3102).</param>
/// <param name="Optional">Whether it may lead to nothing, which makes every value read through it nullable.</param>
public sealed record ExprNavigation(string Id, string Name, ExprContainer Target, bool ToOne, bool Optional)
{
    /// <summary>The dotted identifiers the text writes before <see cref="Name"/>, or <see langword="null"/>.</summary>
    public string? Prefix { get; init; }

    /// <summary>
    /// The id of the entity it leads to, which <c>in_state</c> asks a lifecycle about, or <see langword="null"/>: also for a relation end with
    /// several types, whose types the engine reads from the end <see cref="Id"/> names (several-types-design.md decision 15).
    /// </summary>
    public string? Entity { get; init; }
}

/// <summary>
/// A state <c>in_state</c> can name (object-effects-design.md decision 7): text writes its lifecycle's name, then its path of state names.
/// </summary>
/// <param name="Id">The state's id.</param>
/// <param name="Lifecycle">The id of the process that holds it.</param>
/// <param name="LifecycleName">The process's name.</param>
/// <param name="Path">The state's names from the root's child down to it.</param>
/// <param name="IsState">Whether an object can be in it: not a history or choice state.</param>
/// <param name="Inside">The state and every state inside it, in document order: <c>in_state</c> holds when any of them is active.</param>
public sealed record ExprState(string Id, string Lifecycle, string LifecycleName, IReadOnlyList<string> Path, bool IsState, IReadOnlyList<string> Inside);

/// <summary>
/// What <c>in_state</c> reads at a guard or set site (object-effects-design.md decisions 7 and 8), which the engine gives per site: the
/// states by id and by name, the process the site belongs to, and whether a lifecycle can govern the object a path reaches.
/// </summary>
internal interface IExprLifecycles
{
    /// <summary>The id of the process whose guard or set the site is, or <see langword="null"/> at an operation guard.</summary>
    string? Own { get; }

    /// <summary>The state with an id, or <see langword="null"/>.</summary>
    ExprState? State(string id);

    /// <summary>The id of the state text names: a lifecycle's name, then a path of state names; <see langword="null"/> when none.</summary>
    string? Find(IReadOnlyList<string> segments);

    /// <summary>
    /// Why the lifecycle cannot govern the object reached through the navigations (none: the object itself), or <see langword="null"/>
    /// when it can: the entity reached, a base, a stereotype it carries or a derived entity is one of its subjects (MQ3116).
    /// </summary>
    string? CannotGovern(string lifecycle, IReadOnlyList<ExprNavigation> through);
}

/// <summary>A named constraint as an expression sees it: its typed parameters and its condition.</summary>
/// <param name="Id">The constraint's id.</param>
/// <param name="Name">The name the text writes after <c>satisfies</c>.</param>
/// <param name="Parameters">The parameters, in order; a path in the condition names one by its id.</param>
/// <param name="Condition">The condition.</param>
public sealed record ExprConstraint(string Id, string Name, IReadOnlyList<ExprMember> Parameters, Expr Condition);

/// <summary>
/// The members and navigations of one record, context or row. A container a navigation leads to may be filled on its first read, so
/// a scope follows only the relations an expression names rather than every record reachable from its site. Reads are thread-safe once
/// the container is built: a resolved condition's scope is read by every thread that prints its text, and the first read fills it once,
/// under a lock the reads after it do not take.
/// </summary>
public sealed class ExprContainer
{
    private readonly Dictionary<string, ExprMember> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExprNavigation> _navigations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _byName = new(StringComparer.Ordinal);
    private readonly Lock? _gate;
    private Action<ExprContainer>? _fill;

    /// <summary>An empty container.</summary>
    public ExprContainer()
    {
    }

    /// <summary>A container that <paramref name="fill"/> fills on its first read; the fill adds members and navigations, never reads.</summary>
    /// <param name="fill">Adds the members and navigations.</param>
    public ExprContainer(Action<ExprContainer> fill)
    {
        _fill = fill;
        _gate = new Lock();
    }

    /// <summary>Adds a member; a second member with the same id or name is ignored.</summary>
    /// <param name="member">The member.</param>
    /// <returns>This container.</returns>
    public ExprContainer Add(ExprMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (_members.TryAdd(member.Id, member))
            _byName.TryAdd(Key(member.Prefix, member.Name), member);
        return this;
    }

    /// <summary>
    /// Adds another name the text may write for a member already added (an overlay entry's own name for its attribute's column): text
    /// by either name reads the member, which prints by its own name. Ignored when the name is taken, so an alias never hides another
    /// member's name and the first name added wins, or when no member has the id. Like <see cref="Add(ExprMember)"/>, called while the
    /// container is built: by its fill, which holds the lock, or before it is shared.
    /// </summary>
    /// <param name="id">The member's id.</param>
    /// <param name="name">The other name, written without a prefix.</param>
    /// <returns>This container.</returns>
    internal ExprContainer Alias(string id, string name)
    {
        if (_members.TryGetValue(id, out var member))
            _byName.TryAdd(Key(null, name), member);
        return this;
    }

    /// <summary>Adds a navigation; a second navigation with the same id or name is ignored.</summary>
    /// <param name="navigation">The navigation.</param>
    /// <returns>This container.</returns>
    public ExprContainer Add(ExprNavigation navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        if (_navigations.TryAdd(navigation.Id, navigation))
            _byName.TryAdd(Key(navigation.Prefix, navigation.Name), navigation);
        return this;
    }

    /// <summary>The members, in the order they were added.</summary>
    public IEnumerable<ExprMember> Members => Filled()._members.Values;

    /// <summary>The member with an id, or <see langword="null"/>.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The member.</returns>
    public ExprMember? Member(string id) => Filled()._members.GetValueOrDefault(id);

    /// <summary>The navigation with an id, or <see langword="null"/>.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The navigation.</returns>
    public ExprNavigation? Navigation(string id) => Filled()._navigations.GetValueOrDefault(id);

    /// <summary>The member or navigation the text names with these segments, or <see langword="null"/>.</summary>
    internal object? Find(IEnumerable<string> segments) => Filled()._byName.GetValueOrDefault(string.Join('\u0001', segments));

    // The fill runs once, under the lock, and the dictionaries are published by the write that clears it: a reader that sees no fill
    // sees them whole, and a reader that comes while another fills waits for it.
    private ExprContainer Filled()
    {
        if (Volatile.Read(ref _fill) is null)
            return this;
        lock (_gate!)
        {
            if (_fill is { } fill)
            {
                fill(this);
                Volatile.Write(ref _fill, null);
            }
        }

        return this;
    }

    private static string Key(string? prefix, string name) =>
        prefix is null ? name : string.Join('\u0001', prefix.Split('.').Append(name));
}

/// <summary>
/// Where a scope built for text finds what the text names (next-phase-3-design.md section 3.1): the scope asks once per name, on the
/// first lookup that needs it, and the source adds what it finds, so a parse reads only what the text names rather than every enum,
/// reference type, seed and constraint of the model.
/// </summary>
internal interface IExprNames
{
    /// <summary>Adds the enum, else the reference type, that text names <paramref name="name"/> (<c>Status</c> in <c>Status.Paid</c>).</summary>
    /// <param name="scope">The scope to add it to.</param>
    /// <param name="name">The name.</param>
    /// <returns>Its id, or <see langword="null"/> when nothing has the name.</returns>
    string? Set(ExprScope scope, string name);

    /// <summary>Adds the constraint named <paramref name="name"/>, with what its condition names by id.</summary>
    /// <param name="scope">The scope to add it to.</param>
    /// <param name="name">The name.</param>
    /// <returns>Its id, or <see langword="null"/> when no constraint has the name.</returns>
    string? Constraint(ExprScope scope, string name);
}

/// <summary>
/// What an expression at one site can read (next-phase-3-design.md section 3.1), built by the engine per site: the site's kind, the
/// members and navigations, the enums, reference types and constraints its names resolve to, the type a value site needs and where
/// the expression sits for diagnostics.
/// </summary>
public sealed class ExprScope
{
    private readonly Dictionary<string, (string Id, string Name, bool Code, Dictionary<string, string> ByName)> _sets;
    private readonly Dictionary<string, (string Owner, string Name)> _refs;
    private readonly Dictionary<string, string> _setsByName;
    private readonly Dictionary<string, ExprConstraint> _constraints;
    private readonly Dictionary<string, ExprConstraint> _constraintsByName;
    private readonly Dictionary<string, string?> _namedSets;
    private readonly Dictionary<string, string?> _namedConstraints;

    /// <summary>Creates an empty scope for a site.</summary>
    /// <param name="site">The site's kind.</param>
    public ExprScope(ExprSiteKind site)
    {
        Site = site;
        AllowsNow = site is ExprSiteKind.Guard or ExprSiteKind.Set or ExprSiteKind.Right or ExprSiteKind.Trigger or ExprSiteKind.TriggerSet or ExprSiteKind.Approval;
        AllowsNavigation = site is ExprSiteKind.Guard or ExprSiteKind.Set or ExprSiteKind.Right or ExprSiteKind.Trigger or ExprSiteKind.TriggerSet or ExprSiteKind.Approval;
        AllowsActor = site is ExprSiteKind.Guard or ExprSiteKind.Set or ExprSiteKind.Right or ExprSiteKind.Trigger or ExprSiteKind.TriggerSet;
        AllowsState = AllowsActor || site == ExprSiteKind.Approval;
        Old = site is ExprSiteKind.Trigger or ExprSiteKind.TriggerSet ? new ExprContainer() : null;
        _sets = new(StringComparer.Ordinal);
        _refs = new(StringComparer.Ordinal);
        _setsByName = new(StringComparer.Ordinal);
        _constraints = new(StringComparer.Ordinal);
        _constraintsByName = new(StringComparer.Ordinal);
        _namedSets = new(StringComparer.Ordinal);
        _namedConstraints = new(StringComparer.Ordinal);
    }

    private ExprScope(ExprScope parent, ExprConstraint constraint)
        : this(parent.Site)
    {
        _sets = parent._sets;
        _refs = parent._refs;
        _setsByName = parent._setsByName;
        _constraints = parent._constraints;
        _constraintsByName = parent._constraintsByName;
        _namedSets = parent._namedSets;
        _namedConstraints = parent._namedConstraints;
        Names = parent.Names;
        AllowsNow = true;
        AllowsNavigation = false;
        AllowsActor = false;
        AllowsState = false;
        ConstraintId = constraint.Id;
        ElementId = constraint.Id;
        Pointer = "/condition";
        foreach (var parameter in constraint.Parameters)
            Root.Add(parameter);
    }

    /// <summary>The site's kind.</summary>
    public ExprSiteKind Site { get; }

    /// <summary>Whether <c>now</c> may be read; by default at guard, set and right sites only (MQ3104).</summary>
    public bool AllowsNow { get; init; }

    /// <summary>Whether a path may follow navigations; by default at guard, set and right sites only (MQ3102).</summary>
    public bool AllowsNavigation { get; init; }

    /// <summary>
    /// Whether <c>current_actor()</c> and <c>in_state</c> may be read; by default at guard, set and right sites only, never in a constraint's own condition
    /// (MQ3104, object-effects-design.md decisions 5 and 9).
    /// </summary>
    public bool AllowsActor { get; init; }

    /// <summary>
    /// Whether <c>in_state</c> may be read: wherever <c>current_actor()</c> may, and at a named signature's <c>when</c>, which reads a
    /// lifecycle's state but not whoever acts (signoff-design.md decision 7).
    /// </summary>
    public bool AllowsState { get; init; }

    /// <summary>What <c>in_state</c> reads at the site; <see langword="null"/> where no lifecycle can be read.</summary>
    internal IExprLifecycles? Lifecycles { get; init; }

    /// <summary>Whether an approval site is a stage's <c>when</c> rather than a named signature's (signoff-design.md decision 6), for messages.</summary>
    internal bool StageCondition { get; init; }

    /// <summary>
    /// Whether a <see cref="ExprSiteKind.Set"/> site is a call input's value rather than a set's (processes-operations-design.md decision 6),
    /// for messages: it reads and types the same.
    /// </summary>
    internal bool CallInput { get; init; }

    /// <summary>The type a value or set site's result must have (a derived or set attribute's type), or <see langword="null"/> at other sites.</summary>
    public ExprType? ValueType { get; init; }

    /// <summary>The id of the element that holds the expression, for diagnostics.</summary>
    public string? ElementId { get; init; }

    /// <summary>The path of the file that holds the expression, for diagnostics.</summary>
    public string? FilePath { get; init; }

    /// <summary>The JSON pointer of the expression in its file (<c>/checks/0/condition</c>); node pointers extend it.</summary>
    public string Pointer { get; init; } = "";

    /// <summary>
    /// At a rule condition, what a <c>member</c> node reads: the type of the selected object's member at a path, or
    /// <see langword="null"/> when there is none (MQ3102).
    /// </summary>
    public Func<string, ExprMember?>? MemberPaths { get; init; }

    /// <summary>The members and navigations a bare name reaches.</summary>
    public ExprContainer Root { get; } = new();

    /// <summary>
    /// At a trigger point's site, what <c>old.</c> reads: the object's values before the verb, by the same ids as under <c>subject</c>
    /// (trigger-points-design.md decision 14). <see langword="null"/> at every other site, where text keeps the meaning it has and the tree's
    /// <c>old</c> flag is MQ3104.
    /// </summary>
    public ExprContainer? Old { get; }

    /// <summary>The constraint whose own condition this scope types, or <see langword="null"/>.</summary>
    public string? ConstraintId { get; }

    /// <summary>
    /// For a scope built for text: where the names the text writes before a member or code, or after <c>satisfies</c>, are looked up, once
    /// each, on the first parse that needs them; <see langword="null"/> when the scope already holds what it can name. What a lookup adds
    /// is shared with <see cref="ForConstraint"/>'s scopes. Lookups happen while text is parsed, which the scope's builder does before
    /// sharing the scope; printing and typing a parsed tree read ids only.
    /// </summary>
    internal IExprNames? Names { get; init; }

    /// <summary>Adds an enum, so <c>Status.Paid</c> names a member.</summary>
    /// <param name="id">The enum's id.</param>
    /// <param name="name">The name the text writes.</param>
    /// <param name="members">Its members' ids and names.</param>
    /// <returns>This scope.</returns>
    public ExprScope AddEnum(string id, string name, IEnumerable<(string Id, string Name)> members) => AddSet(id, name, false, members);

    /// <summary>Adds a reference type, so <c>PaymentTerm.NET30</c> names a code.</summary>
    /// <param name="id">The reference type's id.</param>
    /// <param name="name">The name the text writes.</param>
    /// <param name="codes">Its codes' row ids and code values.</param>
    /// <returns>This scope.</returns>
    public ExprScope AddReferenceType(string id, string name, IEnumerable<(string Id, string Code)> codes) => AddSet(id, name, true, codes);

    /// <summary>Adds a constraint, so <c>satisfies Name(…)</c> cites it.</summary>
    /// <param name="constraint">The constraint.</param>
    /// <returns>This scope.</returns>
    public ExprScope AddConstraint(ExprConstraint constraint)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        if (_constraints.TryAdd(constraint.Id, constraint))
            _constraintsByName.TryAdd(constraint.Name, constraint);
        return this;
    }

    /// <summary>
    /// The scope a constraint's own condition is typed in: its parameters, the same enums, reference types and constraints, <c>now</c>
    /// allowed (a site that cites it decides) and no navigation.
    /// </summary>
    /// <param name="constraint">The constraint.</param>
    /// <returns>The scope.</returns>
    public ExprScope ForConstraint(ExprConstraint constraint)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        return new ExprScope(this, constraint);
    }

    /// <summary>The constraint with an id, or <see langword="null"/>.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The constraint.</returns>
    public ExprConstraint? Constraint(string id) => _constraints.GetValueOrDefault(id);

    /// <summary>The type a <c>ref</c> to an enum member or a reference-type code has, or <see langword="null"/> for an unknown id.</summary>
    /// <param name="id">The member's or code's id.</param>
    /// <returns>The type.</returns>
    public ExprType? RefType(string id) =>
        _refs.TryGetValue(id, out var r) ? (_sets[r.Owner].Code ? ExprType.Code(r.Owner) : ExprType.Enum(r.Owner)) : null;

    /// <summary>The text of a <c>ref</c>: the enum's or reference type's name, then the member's name or the code.</summary>
    internal (string Set, string Name)? RefNames(string id) =>
        _refs.TryGetValue(id, out var r) ? (_sets[r.Owner].Name, r.Name) : null;

    /// <summary>The id of the member or code a set's name and a member name or code name.</summary>
    internal string? FindRef(string set, string name)
    {
        var owner = Names is { } names ? Named(_namedSets, set, names.Set) : _setsByName.GetValueOrDefault(set);
        return owner is not null && _sets.TryGetValue(owner, out var found) && found.ByName.TryGetValue(name, out var id) ? id : null;
    }

    /// <summary>The constraint the text names, or <see langword="null"/>.</summary>
    internal ExprConstraint? FindConstraint(string name) =>
        Names is { } names
            ? Named(_namedConstraints, name, names.Constraint) is { } id ? _constraints.GetValueOrDefault(id) : null
            : _constraintsByName.GetValueOrDefault(name);

    /// <summary>The id a name stands for, looked up through <see cref="Names"/> on its first use.</summary>
    private string? Named(Dictionary<string, string?> found, string name, Func<ExprScope, string, string?> lookUp)
    {
        if (!found.TryGetValue(name, out var id))
            found[name] = id = lookUp(this, name);
        return id;
    }

    private ExprScope AddSet(string id, string name, bool code, IEnumerable<(string Id, string Name)> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (_sets.ContainsKey(id))
            return this;
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        _sets[id] = (id, name, code, byName);
        _setsByName.TryAdd(name, id);
        foreach (var (memberId, memberName) in members)
        {
            if (_refs.TryAdd(memberId, (id, memberName)))
                byName.TryAdd(memberName, memberId);
        }

        return this;
    }
}
