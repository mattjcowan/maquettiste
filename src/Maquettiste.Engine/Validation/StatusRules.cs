using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// MQ2022 (next-phase-1-design.md section 4.5): a reference to a retired element or sub-element, reported in the referring file at
/// the reference, so retiring something finds its last users. Excepted: a referrer that is itself retired (the element or the
/// sub-element holding the reference), membership (<c>package</c> and <c>parent</c>, so retiring a package does not warn on each
/// member), owning references (a migration fact or other child of a retired database) and diagrams, which only show. A
/// <c>deprecated</c> target does not warn.
/// </summary>
internal static class StatusRules
{
    public static void Validate(ValidationContext context, Report report)
    {
        var model = context.Model;
        var element = report.Document.Element;
        if (element is Diagram || model.IsRetired(element.Id))
            return;
        foreach (var reference in model.ReferencesFrom(element.Id))
        {
            if (!model.IsRetired(reference.ToId) || reference.Owning || reference.Field is "package" or "parent" || model.IsRetired(reference.FromId))
                continue;
            report.Add(
                "MQ2022",
                $"'{reference.Field}' references {ChangePlanner.Describe(model, reference.ToId)}, which is retired: repoint the reference, or retire this element.",
                reference.JsonPointer,
                reference.FromId);
        }
    }
}
