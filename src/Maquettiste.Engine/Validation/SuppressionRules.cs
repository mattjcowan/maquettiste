using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The rules on an element's <c>suppressions</c> (next-phase-2-design.md section 5.6), judged per element so saves see them: MQ5018 for a
/// rule that cannot be suppressed, MQ5019 for a rule nothing defines, a member the element does not hold or an entry that repeats
/// another's rule and member. An element without suppressions costs nothing.
/// </summary>
internal static class SuppressionRules
{
    /// <summary>Whether findings of a rule may be suppressed: every rule but MQ1xxx (files that do not load), MQ7012 and MQ5018 to MQ5020.</summary>
    /// <param name="rule">The rule id.</param>
    /// <returns><see langword="true"/> when an entry may name it.</returns>
    public static bool CanBeSuppressed(string rule) =>
        !(rule.Length == 6 && rule.StartsWith("MQ1", StringComparison.Ordinal)) && rule is not ("MQ7012" or "MQ5018" or "MQ5019" or "MQ5020");

    /// <summary>Checks one element's suppressions.</summary>
    /// <param name="context">The validation context (its <see cref="ValidationContext.RuleNames"/> judge <c>x/</c> ids).</param>
    /// <param name="report">The element's report.</param>
    public static void Validate(ValidationContext context, Report report)
    {
        var element = report.Document.Element;
        var entries = element.Suppressions;
        if (entries.Count == 0)
            return;

        var seen = new HashSet<(string Rule, string? Member)>();
        HashSet<string>? held = null;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var at = Ptr.At("/suppressions", i);
            if (!seen.Add((entry.Rule, entry.Member)))
            {
                report.Add("MQ5019", $"The suppression repeats an earlier entry for {entry.Rule}{(entry.Member is null ? "" : " on " + entry.Member)}.", at);
                continue;
            }

            if (!CanBeSuppressed(entry.Rule))
                report.Add("MQ5018", $"Findings of {entry.Rule} cannot be suppressed.", at + "/rule");
            else if (Defined(context, entry.Rule) == false)
                report.Add("MQ5019", $"The suppression names the rule '{entry.Rule}', which nothing defines.", at + "/rule");

            if (entry.Member is { } member && !Owns(context.Model, element, member) && !(held ??= Members(context.Model, element)).Contains(member))
                report.Add("MQ5019", $"The suppression names the member '{member}', which {element.Name} does not hold.", at + "/member");
        }
    }

    /// <summary>Whether the index lists an id as a part in the element's file.</summary>
    internal static bool Owns(ModelSnapshot model, Element element, string id) =>
        model.TryGetEntry(id, out var entry) && entry.OwnerId == element.Id && entry.Id != element.Id;

    /// <summary>
    /// Whether a rule id is defined: built-in ids by the catalog; <c>x/</c> ids by the rule scripts and the declarations, known only when
    /// every rule script loaded and every declaration file fits its schema (<see langword="null"/> otherwise).
    /// </summary>
    private static bool? Defined(ValidationContext context, string rule)
    {
        if (!rule.StartsWith("x/", StringComparison.Ordinal))
            return RuleCatalog.TryGet(rule, out _);
        var model = context.Model;
        if (context.RuleNames is not { } scripts || model.LoadDiagnostics.Any(d => d.Rule == "MQ5014"))
            return null;
        var bare = rule[2..];
        return scripts.Contains(bare) || model.DeclaredRules.Any(d => d.Rules.Rules.Any(r => string.Equals(r.Id, bare, StringComparison.Ordinal)));
    }

    /// <summary>
    /// The member ids an element holds: the parts in its file (the index's entries it owns), the attributes it holds through a base or a
    /// stereotype, and for a table the keyed ids of the members it inherits (next-phase-2-design.md section 3.1).
    /// </summary>
    internal static HashSet<string> Members(ModelSnapshot model, Element element)
    {
        var held = new HashSet<string>(ModelValidator.SubElementIds(element, model), StringComparer.Ordinal);
        switch (element)
        {
            case Entity entity:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var e = entity; e is not null && seen.Add(e.Id); e = e.Base is { } b ? model.Get<Entity>(b) : null)
                {
                    if (!ReferenceEquals(e, entity))
                        held.UnionWith(ModelValidator.SubElementIds(e));
                    foreach (var applied in model.EffectiveStereotypes(e))
                        held.UnionWith(applied.Stereotype.Attributes.Select(a => a.Id));
                }

                break;
            case ValueObject or Relation:
                foreach (var applied in model.EffectiveStereotypes(element))
                    held.UnionWith(applied.Stereotype.Attributes.Select(a => a.Id));
                break;
            case Table table:
                var set = TableMembers.Of(model, table);
                held.UnionWith(set.Columns.Select(m => m.Key));
                held.UnionWith(set.PrimaryKeys.Select(m => m.Key));
                held.UnionWith(set.Uniques.Select(m => m.Key));
                held.UnionWith(set.ForeignKeys.Select(m => m.Key));
                held.UnionWith(set.Indexes.Select(m => m.Key));
                held.UnionWith(set.Checks.Select(m => m.Key));
                break;
        }

        return held;
    }
}
