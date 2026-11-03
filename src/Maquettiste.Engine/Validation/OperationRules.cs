using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The rules on an operation's own structure (next-phase-2-design.md section 4.1): MQ3023 to MQ3028, MQ3030 and MQ3031, and on its
/// sub-elements the rules other kinds' parts take: names (MQ3001, MQ3018), member facets, defaults and validation rule ids (MQ3013,
/// MQ3019, MQ3017, MQ2007), and stereotypes and tags (MQ2003, MQ2004, MQ2006, MQ2008, MQ2013 to MQ2015). Every finding is about the
/// operation's own file. Pure functions of the context.
/// </summary>
internal static class OperationRules
{
    /// <summary>Runs the operation rules on one element file.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="report">The report of the document to validate.</param>
    public static void Validate(ValidationContext context, Report report)
    {
        if (report.Document.Element is Operation operation)
            Check(context, operation, report);
    }

    private static void Check(ValidationContext context, Operation operation, Report report)
    {
        if (operation.Name.Length > 0)
            BuiltinRules.CheckIdentifier(operation.Name, "operation", "/name", operation.Id, report);
        if (operation.Level == OperationLevel.Collection && operation.Subject is null)
        {
            report.Add("MQ3023", $"Operation '{operation.Name}' sets level collection without a subject, so the level is ignored; set a subject or remove level.",
                "/level");
        }

        CheckMembers(context, operation.Input, "/input", inReturns: false, report);
        for (var i = 0; i < operation.Outcomes.Count; i++)
        {
            var outcome = operation.Outcomes[i];
            var pointer = Ptr.At("/outcomes", i);
            BuiltinRules.CheckCommon(context, outcome, pointer, "outcome", report);
            if (outcome.Returns is not { } returns)
                continue;
            var forms = (returns.Members is not null ? 1 : 0) + (returns.Entity is not null ? 1 : 0);
            if (forms != 1)
            {
                report.Add("MQ3025", forms == 0
                        ? $"The returns of outcome '{outcome.Name}' states neither members nor entity; state one of them, or remove returns to return nothing."
                        : $"The returns of outcome '{outcome.Name}' states both members and entity; keep one of them.",
                    pointer + "/returns", outcome.Id);
            }

            if (returns.Members is { } members)
                CheckMembers(context, members, pointer + "/returns/members", inReturns: true, report);
        }

        Unique(operation.Outcomes.Select((o, i) => (o.Name, Ptr.At("/outcomes", i), o.Id)), "outcome", "operation '" + operation.Name + "'", report);
        CheckErrors(context, operation, report);
        CheckDefaultOutcome(context, operation, report);
        if (operation.Effect == OperationEffect.Read)
        {
            for (var i = 0; i < operation.Fires.Count; i++)
            {
                report.Add("MQ3030", $"Operation '{operation.Name}' only reads, but fires the event '{EventName(context, operation.Fires[i])}', and an event changes "
                    + "state; set effect to change, or drop the event.", Ptr.At("/fires", i));
            }
        }

        CheckRequires(context, operation, report);
    }

    // MQ3024 on each member, and the checks an attribute's facets take (MQ3013, MQ3019, MQ3017, MQ2007), names and marks.
    private static void CheckMembers(ValidationContext context, IReadOnlyList<OperationMember> members, string listPointer, bool inReturns, Report report)
    {
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            var pointer = Ptr.At(listPointer, i);
            var id = member.Id;
            BuiltinRules.CheckCommon(context, member, pointer, "operation-member", report);
            if (member.Type is null == member.KeyOf is null)
            {
                report.Add("MQ3024", member.Type is null
                        ? $"Member '{member.Name}' is neither a typed value nor a record key; state type or keyOf."
                        : $"Member '{member.Name}' states both type and keyOf; keep one form.",
                    member.Type is null ? pointer : pointer + "/keyOf", id);
            }

            if (member.KeyOf is not null && member.Type is null)
            {
                foreach (var (facet, set) in (ReadOnlySpan<(string, bool)>)
                    [("default", member.Default is not null), ("length", member.Length is not null), ("precision", member.Precision is not null),
                     ("scale", member.Scale is not null), ("validation", member.Validation is not null)])
                {
                    if (set)
                        report.Add("MQ3024", $"Member '{member.Name}' is a record key, which takes no {facet}; remove it, or make the member a typed value.", pointer + "/" + facet, id);
                }
            }

            if (inReturns && member.Default is not null)
                report.Add("MQ3024", $"Member '{member.Name}' of a returns sets default, which only an input member takes; remove it.", pointer + "/default", id);
            if (member.Type is { } typeRef)
                CheckTyped(context, member, typeRef, pointer, inReturns, report);
        }

        Unique(members.Select((m, i) => (m.Name, Ptr.At(listPointer, i), m.Id)), "member",
            inReturns ? "the members of this returns" : "the input", report);
    }

    private static void CheckTyped(ValidationContext context, OperationMember member, TypeRef typeRef, string pointer, bool inReturns, Report report)
    {
        var id = member.Id;
        if (typeRef.Ref is null && !BuiltinTypes.IsBuiltin(typeRef.Builtin))
        {
            report.Add("MQ3013", $"Member '{member.Name}' has the unknown type keyword '{typeRef.Builtin}'.", pointer + "/type", id);
            return;
        }

        var type = AttributeRules.Resolve(context.Model, typeRef);
        AttributeRules.CheckFacets(type, member.Length, member.Precision, member.Scale, member.Validation, "Member '" + member.Name + "'", pointer, id, report);
        if (member.Default is { } literal && !inReturns)
        {
            var facets = new ModelAttribute
            {
                Id = id, Name = member.Name, Type = typeRef, Length = member.Length, Precision = member.Precision, Scale = member.Scale,
                Required = member.Required, Collection = member.Collection, Sensitive = member.Sensitive, Validation = member.Validation,
            };
            AttributeRules.CheckDefault(type, facets, literal, "member", pointer, report, suggestExpression: false);
        }

        AttributeRules.CheckRuleIds(context, member.Validation, pointer + "/validation/rules", id, report);
    }

    // MQ3026 on own errors sharing a name or a code (the code, else the name), MQ3027 on an input that is not an input member of the
    // operation, MQ3018 on names and the marks of each error.
    private static void CheckErrors(ValidationContext context, Operation operation, Report report)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var codes = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < operation.Errors.Count; i++)
        {
            var error = operation.Errors[i];
            var pointer = Ptr.At("/errors", i);
            BuiltinRules.CheckCommon(context, error, pointer, "error", report);
            var nameTaken = false;
            if (error.Name.Length > 0)
            {
                BuiltinRules.CheckIdentifier(error.Name, "error", pointer + "/name", error.Id, report);
                if (nameTaken = !names.TryAdd(error.Name, pointer))
                {
                    report.Add("MQ3026", $"The error name '{error.Name}' is already used at {names[error.Name]} in operation '{operation.Name}'; rename one of them.",
                        pointer + "/name", error.Id);
                }
            }

            // Without a code the name is the code: a repeated name is reported once, as a name.
            var code = error.Code ?? error.Name;
            if (code.Length > 0 && !codes.TryAdd(code, pointer) && !(error.Code is null && nameTaken))
            {
                report.Add("MQ3026", $"The error code '{code}' is already used at {codes[code]} in operation '{operation.Name}'; clients could not tell the errors apart.",
                    pointer + (error.Code is not null ? "/code" : "/name"), error.Id);
            }

            if (error.Input is { } input && !operation.Input.Any(m => string.Equals(m.Id, input, StringComparison.Ordinal))
                && context.Model.TryGetEntry(input, out var entry) && entry.Kind == "operation-member")
            {
                var where = string.Equals(entry.OwnerId, operation.Id, StringComparison.Ordinal) ? "a member of one of its returns" : "a member of another operation";
                report.Add("MQ3027", $"Error '{error.Name}' points at {where}, not at an input member of operation '{operation.Name}'; pick one of its input members.",
                    pointer + "/input", error.Id);
            }
        }
    }

    private static void CheckDefaultOutcome(ValidationContext context, Operation operation, Report report)
    {
        if (operation.DefaultOutcome is not { } id || operation.Outcomes.Any(o => string.Equals(o.Id, id, StringComparison.Ordinal)))
            return;
        if (context.Model.TryGetEntry(id, out var entry) && entry.Kind == "outcome")
        {
            report.Add("MQ3028", $"defaultOutcome names an outcome of another operation, not one of operation '{operation.Name}'s own; pick one of its outcomes.",
                "/defaultOutcome");
        }
    }

    // MQ3031: requires names the operation itself, one of its own sub-elements, a diagram or a migration fact.
    private static void CheckRequires(ValidationContext context, Operation operation, Report report)
    {
        if (operation.Requires.Count == 0)
            return;
        var own = ModelValidator.SubElementIds(operation).Prepend(operation.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < operation.Requires.Count; i++)
        {
            var id = operation.Requires[i];
            var what = own.Contains(id)
                ? string.Equals(id, operation.Id, StringComparison.Ordinal) ? "the operation itself" : "one of the operation's own parts"
                : context.Model.Get<Element>(id) switch
                {
                    Diagram diagram => "the diagram '" + diagram.Name + "'",
                    Migration => "a migration fact",
                    _ => null,
                };
            if (what is not null)
                report.Add("MQ3031", $"requires names {what}, which cannot stand for what operation '{operation.Name}' requires; remove the entry.", Ptr.At("/requires", i));
        }
    }

    // MQ3001 and MQ3018 on a list of sub-elements that share one namespace.
    private static void Unique(IEnumerable<(string Name, string Pointer, string Id)> items, string what, string scope, Report report)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, pointer, id) in items)
        {
            if (name.Length == 0)
                continue;
            BuiltinRules.CheckIdentifier(name, what, pointer + "/name", id, report);
            if (!seen.TryAdd(name, pointer))
                report.Add("MQ3001", $"The {what} name '{name}' is already used at {seen[name]} in {scope}; rename one of them.", pointer + "/name", id);
        }
    }

    private static string EventName(ValidationContext context, string id) =>
        context.Model.TryGetEntry(id, out var entry) && context.Model.Get<Process>(entry.OwnerId) is { } process
            && process.Events.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal)) is { } e
            ? process.Name + "." + e.Name
            : id;
}
