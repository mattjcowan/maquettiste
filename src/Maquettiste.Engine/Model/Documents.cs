using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>One loaded model file.</summary>
/// <param name="Element">The deserialized element.</param>
/// <param name="Path">The repo-relative path with <c>/</c> separators, for example <c>.maquettiste/model/entities/invoice.json</c>.</param>
/// <param name="Hash">The SHA-256 of the file bytes, lowercase hex: the element's ETag (host-contracts requirement 11).</param>
/// <param name="DependencyHash"><c>H(Hash, sidecar hash)</c>: what <c>e:&lt;id&gt;</c> dependency keys hash to.</param>
/// <param name="Json">The parsed file, as read.</param>
/// <param name="SidecarText">The sidecar description text, when the description is a file reference that was found.</param>
public sealed record ElementDocument(Element Element, string Path, string Hash, string DependencyHash, JsonElement Json, string? SidecarText);

/// <summary>An entry in the id index: an element or a sub-element.</summary>
/// <param name="Id">The id.</param>
/// <param name="OwnerId">The id of the top-level element whose file holds it (equal to <paramref name="Id"/> for an element).</param>
/// <param name="Kind">An element kind name, or <c>attribute</c>, <c>enum-member</c>, <c>end</c>, <c>column</c>, <c>category</c>, <c>schema</c> or <c>key</c>.</param>
/// <param name="JsonPointer">The pointer to the object in its owner's file (empty for an element).</param>
public sealed record IndexEntry(string Id, string OwnerId, string Kind, string JsonPointer);

/// <summary>One reference from a model file to an id.</summary>
/// <param name="FromElementId">The id of the top-level element whose file holds the reference.</param>
/// <param name="FromId">The id of the innermost element or sub-element holding the reference.</param>
/// <param name="JsonPointer">The pointer to the reference value (including the list index for list references).</param>
/// <param name="Field">The JSON property name holding the reference.</param>
/// <param name="ToId">The referenced id.</param>
/// <param name="Owning">Whether the referrer belongs to the referenced element (a seed's <c>target</c>): an owning reference never
/// refuses the target's delete, which deletes the referrer with it (reference-types-seeds-localization.md section 2.7). Left out of
/// the JSON when false.</param>
/// <param name="Optional">For a value an <c>x-ref</c> types (next-phase-1-design.md section 4.1): whether every schema that types it
/// says <c>optional</c>, so <c>remove-references</c> may clear it. Left out of the JSON when false.</param>
/// <param name="Schema">For a value an <c>x-ref</c> types: the schema that types it, <c>extension:&lt;name&gt;</c>,
/// <c>stereotype:&lt;key&gt;</c> or, for a custom element's content, <c>custom-kind:&lt;type&gt;</c> (next-phase-2-design.md section 4.2,
/// with the top-level content key as <paramref name="Field"/> and the innermost member that holds the value, else the custom element,
/// as <paramref name="FromId"/>); <see langword="null"/> (and left out of the JSON) for every other reference.</param>
public sealed record ReferenceInfo(string FromElementId, string FromId, string JsonPointer, string Field, string ToId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Owning = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Optional = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Schema = null);

/// <summary>A summary of one element, for explorers and indexes.</summary>
/// <param name="Id">The element id.</param>
/// <param name="Kind">The kind name.</param>
/// <param name="Name">The element name.</param>
/// <param name="Package">The id of the owning (or, for a package, parent) package, when it has one.</param>
/// <param name="Tags">The element's tag keys.</param>
/// <param name="Hash">The file hash (ETag).</param>
/// <param name="Path">The repo-relative file path.</param>
/// <param name="Category">The id of the element's category-tree node, when it has one (E4: the explorer's category filter).</param>
/// <param name="Stereotypes">The element's stereotype keys, in application order (E4: the explorer's stereotype filter).</param>
/// <param name="DisplayName">The element's display name, when it has one (E5: search and the explorer's label).</param>
/// <param name="Database">The owning database's id, on table, view, sequence and mapping rows (E5), and on the rows of the other database objects and migration facts.</param>
/// <param name="Entity">The entity a mapping maps or a table overlay applies to, when it has one (E5).</param>
/// <param name="MemberCount">The number of members, on diagram rows (E5) and on custom element rows (next-phase-2-design.md section 4.2).</param>
/// <param name="Ends">The relation's ends in document order, on relation rows (E5).</param>
/// <param name="Target">The id of the entity, relation or reference type a seed holds rows for, on seed rows.</param>
/// <param name="RowCount">The number of rows, on seed rows.</param>
/// <param name="FieldCount">The number of user fields, on reference type rows.</param>
/// <param name="Base">The base entity's id, on entity rows that have one (E5h: base and derived entities in related-element highlighting).</param>
/// <param name="Use">The process's use, <c>lifecycle</c> or <c>orchestration</c>, on process rows (phase-3-design.md section 2.1).</param>
/// <param name="Subject">The entity a process governs, on process rows with exactly one subject in either form; the entity an operation
/// acts on, on operation rows that have one.</param>
/// <param name="StateCount">The number of states at every depth, on process rows.</param>
/// <param name="ActorType">The actor's type, <c>person</c>, <c>role</c>, <c>group</c>, <c>organization</c>, <c>external-system</c> or
/// <c>system</c>, on actor rows.</param>
/// <param name="Process">The process a scenario runs, on scenario rows; the process a diagram is the statechart of, on process diagram rows.</param>
/// <param name="StepCount">The number of steps, on scenario rows.</param>
/// <param name="Effect">The operation's effect, <c>read</c> or <c>change</c>, on operation rows (next-phase-2-design.md section 4.1).</param>
/// <param name="Type">The custom kind's type, on custom element rows (next-phase-2-design.md section 4.2).</param>
/// <param name="Subjects">The entities a process's entity entries name, in entry order, on process rows with two or more entity entries; such a
/// row has no <paramref name="Subject"/> (lifecycle-design.md decision 23). Stereotype entries are not counted, but a row with one lists
/// its entity entries from one on (stereotype-sets-design.md section 8).</param>
/// <param name="Actors">The actors a process names anywhere in its document (events, gates, invokes, custom properties), as ids sorted
/// ordinally, on process rows (libraries too) that name at least one. Only ids whose element is an actor count, so the row can change
/// when an actor file appears or goes; that file's own row changes with it, so the index tag still follows.</param>
/// <param name="MemberOf">The roles, groups and organizations an actor is a member of, as ids in file order, on actor rows that name at
/// least one (actors-design.md decision 3).</param>
/// <param name="Relative">How an actor from the object is worked out, <c>owner</c>, <c>path</c>, <c>members-of</c> or <c>none</c>
/// (actors-design.md decision 9), on actor rows whose file has <c>relative</c>.</param>
/// <param name="Participants">The number of participants, on scenario rows that have at least one (actors-design.md decision 14).</param>
/// <param name="SubjectStereotypes">The ids of the stereotypes a process's subject entries name, in entry order, on process rows that have such
/// an entry (stereotype-sets-design.md section 8); an unknown key is left out. Such a row has no <paramref name="Subject"/>.</param>
/// <param name="Covers">The ids of the carriers a process's stereotype entries govern (decision 4), each entry's by qualified name, in entry
/// order, on process rows that have such an entry and a carrier. Worked out per snapshot, since package defaults, implied stereotypes and
/// bases decide them.</param>
/// <param name="Verb">The verb an operation names, as its key, on operation rows that name one (verbs-design.md section 7).</param>
/// <param name="Verbs">The distinct verb keys a process's events, transitions, inline rights and trigger points name, sorted ordinally, on
/// process rows that name at least one (verbs-design.md section 7, rights-design.md section 8, trigger-points-design.md section 9); a named
/// right's verbs, on right rows.</param>
/// <param name="Rights">The number of entries in a lifecycle's rights, inline rights and uses, on process rows that have at least one
/// (rights-design.md section 8).</param>
/// <param name="Access">Whether a named right allows or denies, <c>allow</c> or <c>deny</c>, on right rows (rights-design.md section 8).</param>
/// <param name="TriggerPoints">The number of a process's trigger points, on process rows that have at least one (trigger-points-design.md
/// section 9).</param>
/// <param name="TriggerVerbs">The distinct verb keys a process's trigger points name, sorted ordinally, on process rows that have a trigger
/// point (trigger-points-design.md section 9).</param>
/// <param name="Replaces">The distinct verb keys a process's replaces name, sorted ordinally, on process rows that have a replace
/// (trigger-points-design.md section 9).</param>
/// <param name="Approvals">The number of a process's gates that have stages, inline on its transitions or as gate policies of a library, on
/// process rows that have at least one (signoff-design.md section 9).</param>
/// <param name="Signatures">The number of named signatures across those gates' stages, on process rows that have at least one
/// (signoff-design.md section 9).</param>
/// <remarks>
/// The E5 members are left out of the JSON when they are <see langword="null"/>, so rows of other kinds cost nothing. Equality compares
/// the lists by their items, so two indexes built from the same documents are equal whatever lists they hold.
/// </remarks>
public sealed record ElementSummary(string Id, string Kind, string Name, string? Package, IReadOnlyList<string> Tags, string Hash, string Path,
    string? Category, IReadOnlyList<string> Stereotypes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Database = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Entity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MemberCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RelationEndSummary>? Ends = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Target = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RowCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FieldCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Base = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Use = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Subject = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StateCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActorType = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Process = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StepCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Effect = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Subjects = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Actors = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? MemberOf = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Relative = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Participants = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? SubjectStereotypes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Covers = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Verb = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Verbs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Rights = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Access = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TriggerPoints = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? TriggerVerbs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Replaces = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Approvals = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Signatures = null)
{
    /// <summary>
    /// A process row's <c>rightsDefault</c> as its file writes it, <c>allow</c> or <c>deny</c> (rights-design.md section 6); <see langword="null"/>
    /// when it writes none. For the rights naming key only: never serialized.
    /// </summary>
    [JsonIgnore]
    internal string? RightsDefault { get; init; }

    /// <summary>
    /// The distinct verb keys a process's inline deny rights name, sorted ordinally (rights-design.md section 6); a named right's row gives its
    /// own access and verbs. <see langword="null"/> when none. For the rights naming key only: never serialized.
    /// </summary>
    [JsonIgnore]
    internal IReadOnlyList<string>? DenyVerbs { get; init; }

    /// <summary>
    /// The distinct ids of the named rights a process's rights use, sorted ordinally (rights-design.md section 6); <see langword="null"/> when
    /// none. For the rights naming key only: never serialized.
    /// </summary>
    [JsonIgnore]
    internal IReadOnlyList<string>? RightUses { get; init; }

    /// <summary>
    /// Whether a named right's row has <c>whileFrozen</c>, or a process row has an inline right with it (rights-design.md decision 28). For the
    /// freezing naming key only: never serialized.
    /// </summary>
    [JsonIgnore]
    internal bool WhileFrozen { get; init; }

    /// <summary>
    /// Whether the row is a lifecycle that writes <c>rights</c> or <c>rightsDefault</c>, so the project uses rights (rights-design.md decision
    /// 28). An orchestration that still writes them (MQ9901) uses none, so demoting a lifecycle changes the rights naming key.
    /// </summary>
    internal bool UsesRights => string.Equals(Use, "lifecycle", StringComparison.Ordinal) && (Rights is > 0 || RightsDefault is not null);

    /// <summary>
    /// A process row's replaces for the trigger points naming key (trigger-points-design.md section 7): one entry per replace, each its verbs
    /// and the entities it narrows to, both sorted ordinally, as <c>verb,verb|entity,entity</c> (no entity: every entity it covers), the
    /// entries sorted ordinally. <see langword="null"/> when none. For the trigger points naming key only: never serialized.
    /// </summary>
    [JsonIgnore]
    internal IReadOnlyList<string>? ReplaceScopes { get; init; }

    /// <summary>
    /// Whether the row is a lifecycle that has <c>triggerPoints</c>, so the project uses trigger points (trigger-points-design.md decision 29).
    /// An orchestration that still writes them (MQ8101) uses none. The trigger points naming key, <c>tn:trigger-points</c>, hashes each such
    /// row by id: its <see cref="TriggerVerbs"/>, then its <see cref="ReplaceScopes"/>.
    /// </summary>
    internal bool UsesTriggerPoints => string.Equals(Use, "lifecycle", StringComparison.Ordinal) && TriggerPoints is > 0;

    /// <summary>
    /// Whether a process row has a gate with stages on one of its transitions (signoff-design.md section 7). For the approvals naming key
    /// only: never serialized.
    /// </summary>
    [JsonIgnore]
    internal bool InlineApproval { get; init; }

    /// <summary>
    /// The distinct ids of the gate policies a process row's transitions use, sorted ordinally, whether or not they have stages
    /// (signoff-design.md section 7); <see langword="null"/> when none. For the approvals naming key only: never serialized.
    /// </summary>
    [JsonIgnore]
    internal IReadOnlyList<string>? GatePolicyUses { get; init; }

    /// <summary>
    /// The ids of a library row's gates that have stages, sorted ordinally (signoff-design.md section 7); <see langword="null"/> when none.
    /// For the approvals naming key only: never serialized.
    /// </summary>
    [JsonIgnore]
    internal IReadOnlyList<string>? ApprovalGatePolicies { get; init; }

    /// <inheritdoc/>
    public bool Equals(ElementSummary? other) =>
        other is not null
        && string.Equals(Id, other.Id, StringComparison.Ordinal)
        && string.Equals(Kind, other.Kind, StringComparison.Ordinal)
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Package, other.Package, StringComparison.Ordinal)
        && Tags.SequenceEqual(other.Tags, StringComparer.Ordinal)
        && string.Equals(Hash, other.Hash, StringComparison.Ordinal)
        && string.Equals(Path, other.Path, StringComparison.Ordinal)
        && string.Equals(Category, other.Category, StringComparison.Ordinal)
        && Stereotypes.SequenceEqual(other.Stereotypes, StringComparer.Ordinal)
        && string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal)
        && string.Equals(Database, other.Database, StringComparison.Ordinal)
        && string.Equals(Entity, other.Entity, StringComparison.Ordinal)
        && MemberCount == other.MemberCount
        && string.Equals(Target, other.Target, StringComparison.Ordinal)
        && RowCount == other.RowCount
        && FieldCount == other.FieldCount
        && string.Equals(Base, other.Base, StringComparison.Ordinal)
        && string.Equals(Use, other.Use, StringComparison.Ordinal)
        && string.Equals(Subject, other.Subject, StringComparison.Ordinal)
        && StateCount == other.StateCount
        && string.Equals(ActorType, other.ActorType, StringComparison.Ordinal)
        && string.Equals(Process, other.Process, StringComparison.Ordinal)
        && StepCount == other.StepCount
        && string.Equals(Effect, other.Effect, StringComparison.Ordinal)
        && string.Equals(Type, other.Type, StringComparison.Ordinal)
        && (ReferenceEquals(Ends, other.Ends) || (Ends is not null && other.Ends is not null && Ends.SequenceEqual(other.Ends)))
        && (ReferenceEquals(Subjects, other.Subjects) || (Subjects is not null && other.Subjects is not null && Subjects.SequenceEqual(other.Subjects, StringComparer.Ordinal)))
        && (ReferenceEquals(Actors, other.Actors) || (Actors is not null && other.Actors is not null && Actors.SequenceEqual(other.Actors, StringComparer.Ordinal)))
        && (ReferenceEquals(MemberOf, other.MemberOf) || (MemberOf is not null && other.MemberOf is not null && MemberOf.SequenceEqual(other.MemberOf, StringComparer.Ordinal)))
        && string.Equals(Relative, other.Relative, StringComparison.Ordinal)
        && Participants == other.Participants
        && (ReferenceEquals(SubjectStereotypes, other.SubjectStereotypes)
            || (SubjectStereotypes is not null && other.SubjectStereotypes is not null && SubjectStereotypes.SequenceEqual(other.SubjectStereotypes, StringComparer.Ordinal)))
        && (ReferenceEquals(Covers, other.Covers) || (Covers is not null && other.Covers is not null && Covers.SequenceEqual(other.Covers, StringComparer.Ordinal)))
        && string.Equals(Verb, other.Verb, StringComparison.Ordinal)
        && (ReferenceEquals(Verbs, other.Verbs) || (Verbs is not null && other.Verbs is not null && Verbs.SequenceEqual(other.Verbs, StringComparer.Ordinal)))
        && Rights == other.Rights
        && string.Equals(Access, other.Access, StringComparison.Ordinal)
        && string.Equals(RightsDefault, other.RightsDefault, StringComparison.Ordinal)
        && (ReferenceEquals(DenyVerbs, other.DenyVerbs) || (DenyVerbs is not null && other.DenyVerbs is not null && DenyVerbs.SequenceEqual(other.DenyVerbs, StringComparer.Ordinal)))
        && (ReferenceEquals(RightUses, other.RightUses) || (RightUses is not null && other.RightUses is not null && RightUses.SequenceEqual(other.RightUses, StringComparer.Ordinal)))
        && WhileFrozen == other.WhileFrozen
        && TriggerPoints == other.TriggerPoints
        && (ReferenceEquals(TriggerVerbs, other.TriggerVerbs) || (TriggerVerbs is not null && other.TriggerVerbs is not null && TriggerVerbs.SequenceEqual(other.TriggerVerbs, StringComparer.Ordinal)))
        && (ReferenceEquals(Replaces, other.Replaces) || (Replaces is not null && other.Replaces is not null && Replaces.SequenceEqual(other.Replaces, StringComparer.Ordinal)))
        && Approvals == other.Approvals
        && Signatures == other.Signatures
        && (ReferenceEquals(ReplaceScopes, other.ReplaceScopes)
            || (ReplaceScopes is not null && other.ReplaceScopes is not null && ReplaceScopes.SequenceEqual(other.ReplaceScopes, StringComparer.Ordinal)))
        && InlineApproval == other.InlineApproval
        && (ReferenceEquals(GatePolicyUses, other.GatePolicyUses)
            || (GatePolicyUses is not null && other.GatePolicyUses is not null && GatePolicyUses.SequenceEqual(other.GatePolicyUses, StringComparer.Ordinal)))
        && (ReferenceEquals(ApprovalGatePolicies, other.ApprovalGatePolicies)
            || (ApprovalGatePolicies is not null && other.ApprovalGatePolicies is not null && ApprovalGatePolicies.SequenceEqual(other.ApprovalGatePolicies, StringComparer.Ordinal)));

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(Id), StringComparer.Ordinal.GetHashCode(Hash), StringComparer.Ordinal.GetHashCode(Path));
}

/// <summary>One end of a relation, on the relation's index row (E5).</summary>
/// <param name="Entity">The id of the entity at this end.</param>
/// <param name="Role">The end's role name.</param>
public sealed record RelationEndSummary(string Entity, string Role);

/// <summary>A loaded extension schema file.</summary>
/// <param name="Schema">The extension.</param>
/// <param name="Path">The repo-relative file path.</param>
/// <param name="Hash">The file hash.</param>
public sealed record ExtensionDocument(ExtensionSchema Schema, string Path, string Hash);
