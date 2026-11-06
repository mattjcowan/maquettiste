using System.Runtime.CompilerServices;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Processes;

/// <summary>
/// The memberships of a model's actors (actors-design.md decisions 3 to 5), for one snapshot (<see cref="Of"/>): an actor in a list
/// stands for itself and its members at any depth. Only a <c>memberOf</c> entry that names a role, group or organization other than the
/// actor itself counts, once (MQ9601 reports the others). The closures are computed only when some actor has a counted membership; a
/// cycle (MQ9602) never puts an actor among its own members. Immutable once built, so it is safe to share across threads.
/// </summary>
internal sealed class ActorMembership
{
    private static readonly ConditionalWeakTable<ModelSnapshot, ActorMembership> ByModel = new();
    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    private readonly Dictionary<string, List<string>> _memberOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _allMemberOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _allMembers = new(StringComparer.Ordinal);

    private ActorMembership(ModelSnapshot model)
    {
        string? system = null;
        var systems = 0;
        foreach (var actor in model.All<Actor>())
        {
            if (actor.Type == ActorType.System)
            {
                systems++;
                system = actor.Id;
            }

            foreach (var target in actor.MemberOf)
            {
                if (!Counts(model, actor, target))
                    continue;
                var list = _memberOf.TryGetValue(actor.Id, out var own) ? own : _memberOf[actor.Id] = [];
                if (list.Contains(target, StringComparer.Ordinal))
                    continue;
                list.Add(target);
                (_members.TryGetValue(target, out var members) ? members : _members[target] = []).Add(actor.Id);
            }
        }

        SystemActor = systems == 1 ? system : null;
        Any = _memberOf.Count > 0;
        if (!Any)
            return;
        foreach (var id in _memberOf.Keys)
        {
            var reached = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>(_memberOf[id]);
            while (stack.Count > 0)
            {
                var next = stack.Pop();
                if (string.Equals(next, id, StringComparison.Ordinal) || !reached.Add(next))
                    continue;
                foreach (var further in _memberOf.GetValueOrDefault(next, []))
                    stack.Push(further);
            }

            _allMemberOf[id] = reached;
            foreach (var holder in reached)
                (_allMembers.TryGetValue(holder, out var all) ? all : _allMembers[holder] = new HashSet<string>(StringComparer.Ordinal)).Add(id);
        }
    }

    /// <summary>Whether some actor has a membership that counts; without one every actor stands only for itself.</summary>
    public bool Any { get; }

    /// <summary>The id of the project's system actor (type <c>system</c>) when it has exactly one, else <see langword="null"/> (two are
    /// MQ9605).</summary>
    public string? SystemActor { get; }

    /// <summary>The memberships of a snapshot, built on first use and kept as long as the snapshot.</summary>
    /// <param name="model">The snapshot.</param>
    /// <returns>The memberships.</returns>
    public static ActorMembership Of(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return ByModel.GetValue(model, static m => new ActorMembership(m));
    }

    /// <summary>Whether a <c>memberOf</c> entry counts: it names an existing role, group or organization other than the actor itself.</summary>
    /// <param name="model">The model.</param>
    /// <param name="member">The actor whose entry it is.</param>
    /// <param name="target">The id the entry names.</param>
    /// <returns><see langword="true"/> when the entry makes the actor a member.</returns>
    public static bool Counts(ModelSnapshot model, Actor member, string target)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(member);
        return !string.Equals(target, member.Id, StringComparison.Ordinal) && model.Get<Actor>(target) is { } holder && ActorTypes.HoldsMembers(holder.Type);
    }

    /// <summary>The actors an actor is directly a member of, in file order, each once; only counted entries.</summary>
    /// <param name="id">The actor id.</param>
    /// <returns>The ids.</returns>
    public IReadOnlyList<string> MemberOf(string id) => _memberOf.TryGetValue(id, out var list) ? list : [];

    /// <summary>The actors directly members of an actor, in the model's actor order.</summary>
    /// <param name="id">The actor id.</param>
    /// <returns>The ids.</returns>
    public IReadOnlyList<string> Members(string id) => _members.TryGetValue(id, out var list) ? list : [];

    /// <summary>The actors an actor is a member of at any depth, never itself.</summary>
    /// <param name="id">The actor id.</param>
    /// <returns>The ids.</returns>
    public IReadOnlySet<string> AllMemberOf(string id) => _allMemberOf.TryGetValue(id, out var set) ? set : None;

    /// <summary>The members of an actor at any depth, never itself.</summary>
    /// <param name="id">The actor id.</param>
    /// <returns>The ids.</returns>
    public IReadOnlySet<string> AllMembers(string id) => _allMembers.TryGetValue(id, out var set) ? set : None;

    /// <summary>Whether an actor satisfies a listed actor: it is that actor or one of its members at any depth.</summary>
    /// <param name="listed">The actor a list names.</param>
    /// <param name="actor">The acting actor.</param>
    /// <returns><see langword="true"/> when it is accepted.</returns>
    public bool Accepts(string listed, string actor) =>
        string.Equals(listed, actor, StringComparison.Ordinal) || (Any && AllMemberOf(actor).Contains(listed));

    /// <summary>Whether an actor satisfies one of a list's actors (<see cref="Accepts"/>).</summary>
    /// <param name="list">The listed actors.</param>
    /// <param name="actor">The acting actor.</param>
    /// <returns><see langword="true"/> when it is accepted.</returns>
    public bool Allows(IReadOnlyList<string> list, string actor)
    {
        ArgumentNullException.ThrowIfNull(list);
        foreach (var listed in list)
        {
            if (Accepts(listed, actor))
                return true;
        }

        return false;
    }
}
