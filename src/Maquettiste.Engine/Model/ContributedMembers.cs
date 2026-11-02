namespace Maquettiste.Engine.Model;

/// <summary>One entry of a holder's redefines list (next-phase-2-design.md section 3.1).</summary>
internal interface IMemberRedefinition
{
    /// <summary>The id of the stereotype member the entry redefines.</summary>
    string Member { get; }

    /// <summary>The JSON names of the facets the entry states, in ordinal order (never "member").</summary>
    IReadOnlyList<string> Facets { get; }
}

/// <summary>A member a holder inherits from a stereotype of its effective set (next-phase-2-design.md section 3.1).</summary>
/// <typeparam name="TMember">The member's record type.</typeparam>
/// <param name="Key"><see cref="ContributedMembers.KeyOf"/>(holderKey, <paramref name="MemberId"/>).</param>
/// <param name="MemberId">The stereotype member's id.</param>
/// <param name="Member">The stereotype's member with the holder's redefinition applied.</param>
/// <param name="From">The effective-set entry that contributes it (its Source: own, implied or which default).</param>
/// <param name="Redefined">The redefinition's facets, or empty.</param>
internal sealed record InheritedMember<TMember>(string Key, string MemberId, TMember Member, AppliedStereotype From,
    IReadOnlyList<string> Redefined) where TMember : class;

/// <summary>What is wrong with one entry of a holder's <c>redefines</c> or <c>excludes</c> list, whatever the holder's kind.</summary>
internal enum MemberLinkProblem
{
    /// <summary>The entry names a member the holder does not inherit.</summary>
    NotInherited,

    /// <summary>A second <c>redefines</c> entry names the same member.</summary>
    RedefinedTwice,

    /// <summary>A <c>redefines</c> entry states no facet.</summary>
    NoFacet,

    /// <summary>A member is both excluded and redefined (reported at its <c>excludes</c> entry).</summary>
    ExcludedAndRedefined,
}

/// <summary>One kind-free link problem of a holder.</summary>
/// <param name="Problem">The problem.</param>
/// <param name="Member">The member id the entry names.</param>
/// <param name="Pointer">The entry's pointer from the holder's root: /redefines/&lt;n&gt;/member or /excludes/&lt;n&gt;.</param>
internal sealed record MemberLinkFinding(MemberLinkProblem Problem, string Member, string Pointer);

/// <summary>
/// The contributed-members mechanism (next-phase-2-design.md section 3.1): a holder (a table here, an operation's errors later) inherits
/// the members of the stereotypes of its effective set, each keyed by the holder's key and the member id, less what it excludes, with
/// the facets its redefinitions state. A holder kind supplies its member lists, its redefinition record and the function that applies
/// a redefinition; this class does the rest. Pure functions, no state.
/// </summary>
internal static class ContributedMembers
{
    /// <summary>The key of a member inherited by a holder: <c>holderKey + "." + memberId</c>.</summary>
    /// <param name="holderKey">The holder's key (a table key).</param>
    /// <param name="memberId">The stereotype member's id.</param>
    /// <returns>The keyed id.</returns>
    public static string KeyOf(string holderKey, string memberId) => holderKey + "." + memberId;

    /// <summary>
    /// Splits a keyed id at the last <c>.</c> when the rest is an id. Only splits: the caller confirms the holder key and that the member
    /// belongs to a stereotype (a relation end's key column has the same shape).
    /// </summary>
    /// <param name="key">The keyed id.</param>
    /// <param name="holderKey">The part before the last <c>.</c>, or <c>""</c>.</param>
    /// <param name="memberId">The id after the last <c>.</c>, or <c>""</c>.</param>
    /// <returns>Whether the key splits.</returns>
    public static bool TryParse(string key, out string holderKey, out string memberId)
    {
        ArgumentNullException.ThrowIfNull(key);
        var dot = key.LastIndexOf('.');
        if (dot > 0 && IdFormat.IsValid(key[(dot + 1)..]))
        {
            holderKey = key[..dot];
            memberId = key[(dot + 1)..];
            return true;
        }

        holderKey = "";
        memberId = "";
        return false;
    }

    /// <summary>
    /// The inherited members, in effective-set order, then each stereotype's member order. A member without an id, a member listed in
    /// excludes, and every member of a stereotype in skip are left out; a member a redefinition names is passed through apply (the
    /// first entry naming it, when two do).
    /// </summary>
    /// <typeparam name="TMember">The member's record type.</typeparam>
    /// <typeparam name="TRedefinition">The holder kind's redefinition record.</typeparam>
    /// <param name="holderKey">The holder's key.</param>
    /// <param name="effective">The holder's effective stereotype set.</param>
    /// <param name="membersOf">A stereotype's members of the kind.</param>
    /// <param name="idOf">A member's id, or <see langword="null"/>.</param>
    /// <param name="redefines">The holder's redefinitions.</param>
    /// <param name="excludes">The member ids the holder excludes.</param>
    /// <param name="skip">The keys of stereotypes whose members the holder does not inherit.</param>
    /// <param name="apply">Applies a redefinition's facets to a member.</param>
    /// <returns>The inherited members.</returns>
    public static IReadOnlyList<InheritedMember<TMember>> Inherit<TMember, TRedefinition>(
        string holderKey,
        IReadOnlyList<AppliedStereotype> effective,
        Func<Stereotype, IReadOnlyList<TMember>> membersOf,
        Func<TMember, string?> idOf,
        IReadOnlyList<TRedefinition> redefines,
        IReadOnlyList<string> excludes,
        IReadOnlySet<string> skip,
        Func<TMember, TRedefinition, TMember> apply)
        where TMember : class where TRedefinition : IMemberRedefinition
    {
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(membersOf);
        ArgumentNullException.ThrowIfNull(idOf);
        ArgumentNullException.ThrowIfNull(redefines);
        ArgumentNullException.ThrowIfNull(excludes);
        ArgumentNullException.ThrowIfNull(skip);
        ArgumentNullException.ThrowIfNull(apply);

        Dictionary<string, TRedefinition>? byMember = null;
        foreach (var redefinition in redefines)
        {
            byMember ??= new Dictionary<string, TRedefinition>(StringComparer.Ordinal);
            byMember.TryAdd(redefinition.Member, redefinition);
        }

        HashSet<string>? excluded = excludes.Count == 0 ? null : new HashSet<string>(excludes, StringComparer.Ordinal);
        var result = new List<InheritedMember<TMember>>();
        foreach (var applied in effective)
        {
            if (skip.Contains(applied.Stereotype.Key))
                continue;
            foreach (var member in membersOf(applied.Stereotype))
            {
                if (idOf(member) is not { Length: > 0 } id || (excluded is not null && excluded.Contains(id)))
                    continue;
                if (byMember is not null && byMember.TryGetValue(id, out var redefinition))
                    result.Add(new InheritedMember<TMember>(KeyOf(holderKey, id), id, apply(member, redefinition), applied, redefinition.Facets));
                else
                    result.Add(new InheritedMember<TMember>(KeyOf(holderKey, id), id, member, applied, []));
            }
        }

        return result;
    }

    /// <summary>
    /// The kind-free link problems: an entry naming a member outside inheritable, a member redefined twice, an entry with no facet, a
    /// member both excluded and redefined (reported at its excludes entry). Facet fit and the members a kind keeps together are the
    /// holder kind's checks. Findings come in list order: the redefinitions first, then the exclusions.
    /// </summary>
    /// <typeparam name="TRedefinition">The holder kind's redefinition record.</typeparam>
    /// <param name="inheritable">The member ids the holder inherits before excludes (after skip).</param>
    /// <param name="redefines">The holder's redefinitions.</param>
    /// <param name="excludes">The member ids the holder excludes.</param>
    /// <returns>The findings.</returns>
    public static IReadOnlyList<MemberLinkFinding> CheckLinks<TRedefinition>(IReadOnlySet<string> inheritable,
        IReadOnlyList<TRedefinition> redefines, IReadOnlyList<string> excludes)
        where TRedefinition : IMemberRedefinition
    {
        ArgumentNullException.ThrowIfNull(inheritable);
        ArgumentNullException.ThrowIfNull(redefines);
        ArgumentNullException.ThrowIfNull(excludes);

        var findings = new List<MemberLinkFinding>();
        var redefined = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < redefines.Count; i++)
        {
            var member = redefines[i].Member;
            var pointer = "/redefines/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/member";
            if (!inheritable.Contains(member))
                findings.Add(new MemberLinkFinding(MemberLinkProblem.NotInherited, member, pointer));
            else if (!redefined.Add(member))
                findings.Add(new MemberLinkFinding(MemberLinkProblem.RedefinedTwice, member, pointer));
            if (redefines[i].Facets.Count == 0)
                findings.Add(new MemberLinkFinding(MemberLinkProblem.NoFacet, member, pointer));
        }

        for (var i = 0; i < excludes.Count; i++)
        {
            var member = excludes[i];
            var pointer = "/excludes/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!inheritable.Contains(member))
                findings.Add(new MemberLinkFinding(MemberLinkProblem.NotInherited, member, pointer));
            else if (redefined.Contains(member))
                findings.Add(new MemberLinkFinding(MemberLinkProblem.ExcludedAndRedefined, member, pointer));
        }

        return findings;
    }
}
