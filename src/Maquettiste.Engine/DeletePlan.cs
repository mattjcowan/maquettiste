using System.Text.Json.Serialization;

namespace Maquettiste.Engine;

/// <summary>
/// What a delete would do, read before it happens (engine-design.md section 15.1): <c>GET /api/model/elements/{id}/delete-plan</c>,
/// the MCP <c>delete_element</c> dry run and <c>maquettiste model delete --dry-run</c>. Nothing is written to compute it.
/// </summary>
/// <param name="Ids">The elements the delete names.</param>
/// <param name="Resolution">The resolution the plan was computed for.</param>
/// <param name="Outcome">What the delete would return now: <c>saved</c>, <c>referenced</c> (refuse), <c>invalid</c> (a reference
/// that cannot be resolved, or an error the change would introduce), <c>not-found</c> or <c>conflict</c>.</param>
/// <param name="Deletes">Every other element deleted with them, in the order the cascade reached it.</param>
/// <param name="Clears">References cleared from elements that stay.</param>
/// <param name="Removes">Parts removed from elements that stay (an attribute whose type is deleted, a diagram member, a key).</param>
/// <param name="Refused">What blocks the delete: references that cannot be resolved with this resolution, and errors the change would
/// introduce. Empty when <paramref name="Outcome"/> is <c>saved</c>.</param>
/// <param name="Settings">Entries of <c>maquettiste.json</c> removed in the same change (the conventions of a deleted database).</param>
/// <param name="Warnings">What the delete leaves as it is but stops working (a pack unit that filters on a deleted database's name).</param>
public sealed record DeletePlan(
    IReadOnlyList<string> Ids,
    DeleteResolution Resolution,
    SaveOutcome Outcome,
    IReadOnlyList<DeletePlanDelete> Deletes,
    IReadOnlyList<DeletePlanClear> Clears,
    IReadOnlyList<DeletePlanRemove> Removes,
    IReadOnlyList<DeletePlanRefusal> Refused,
    IReadOnlyList<DeletePlanSetting> Settings,
    IReadOnlyList<DeletePlanWarning> Warnings);

/// <summary>An element a delete takes with it.</summary>
/// <param name="Id">The element id.</param>
/// <param name="Kind">The kind name.</param>
/// <param name="Name">A readable name (a synthesized table: its entity's name and "table").</param>
/// <param name="Path">The repo-relative file path.</param>
/// <param name="Because">Why, for example "needs database main" or "belongs to entity Invoice".</param>
public sealed record DeletePlanDelete(string Id, string Kind, string Name, string Path, string Because);

/// <summary>A reference cleared from an element that stays.</summary>
/// <param name="Id">The referring element's id.</param>
/// <param name="Kind">Its kind name.</param>
/// <param name="Name">Its readable name.</param>
/// <param name="Pointer">The reference's JSON pointer in the element's file.</param>
/// <param name="Field">The property holding the reference.</param>
/// <param name="Target">The deleted id it pointed to.</param>
/// <param name="Because">What it pointed to, for example "pointed to package Catalog".</param>
public sealed record DeletePlanClear(string Id, string Kind, string Name, string Pointer, string Field, string Target, string Because);

/// <summary>A part removed from an element that stays.</summary>
/// <param name="Id">The element's id.</param>
/// <param name="Kind">Its kind name.</param>
/// <param name="Name">Its readable name.</param>
/// <param name="Pointer">The removed part's JSON pointer in the element's file.</param>
/// <param name="What">The part, for example "attribute unitPrice", "member Product" or "key".</param>
/// <param name="SubId">The removed sub-element's id, when the part is one.</param>
/// <param name="SubKind">The removed part's kind (<c>attribute</c>, <c>end</c>, <c>member</c>, ...), when known.</param>
/// <param name="Because">Why, for example "needs scalar type Money".</param>
public sealed record DeletePlanRemove(
    string Id, string Kind, string Name, string Pointer, string What,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SubId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SubKind,
    string Because);

/// <summary>Something that blocks the delete.</summary>
/// <param name="Id">The element concerned, when there is one.</param>
/// <param name="Kind">Its kind name.</param>
/// <param name="Name">Its readable name.</param>
/// <param name="Pointer">Where in its file.</param>
/// <param name="Why">The reason, in words.</param>
/// <param name="Rule">The rule id (MQ2001 for a reference that cannot be resolved).</param>
public sealed record DeletePlanRefusal(string? Id, string? Kind, string? Name, string? Pointer, string Why, string Rule);

/// <summary>An entry of <c>maquettiste.json</c> the delete removes.</summary>
/// <param name="Pointer">Its JSON pointer, for example <c>/databases/main</c>.</param>
/// <param name="What">What it is, in words.</param>
public sealed record DeletePlanSetting(string Pointer, string What);

/// <summary>Something the delete leaves as it is that stops matching anything.</summary>
/// <param name="Message">The warning, in words.</param>
/// <param name="Pack">The pack, when it is a pack unit.</param>
/// <param name="Unit">The unit id.</param>
public sealed record DeletePlanWarning(
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Pack,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unit);
