using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The rules on an operation's structure (next-phase-2-design.md section 4.1): MQ3023 to MQ3028, MQ3030 and MQ3031, counting the
/// errors its stereotypes contribute (MQ3026, MQ3027) and judging its <c>redefines</c> and <c>excludes</c> entries (MQ2027, MQ2028,
/// through <see cref="ContributedMembers.CheckLinks"/>); on its sub-elements the rules other kinds' parts take: names (MQ3001, MQ3018),
/// member facets, defaults and validation rule ids (MQ3013, MQ3019, MQ3017, MQ2007), and stereotypes and tags (MQ2003, MQ2004, MQ2006,
/// MQ2008, MQ2013 to MQ2015). On a stereotype: MQ3029, MQ3027 and the same name and mark rules on its errors. A guard
/// (next-phase-3-design.md section 3.1) takes the name and mark rules here; its condition and its error (MQ31xx, MQ3111) are judged by
/// <see cref="ExpressionRules"/>. Every finding is about the document's own file. Pure functions of the context.
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
        else if (report.Document.Element is Stereotype { Errors.Count: > 0 } stereotype)
            CheckStereotype(context, stereotype, report);
    }

    // MQ3029 when the stereotype does not apply to operations; MQ3027 on an error with an input; names, codes and marks of its errors.
    private static void CheckStereotype(ValidationContext context, Stereotype stereotype, Report report)
    {
        if (stereotype.AppliesTo.Count > 0 && !stereotype.AppliesTo.Contains("operation", StringComparer.Ordinal))
        {
            report.Add("MQ3029", $"Stereotype '{stereotype.Key}' declares errors but does not apply to operations, so no operation can inherit them; add operation to appliesTo.",
                "/appliesTo");
        }

        for (var i = 0; i < stereotype.Errors.Count; i++)
        {
            var error = stereotype.Errors[i];
            var pointer = Ptr.At("/errors", i);
            BuiltinRules.CheckCommon(context, error, pointer, "error", report);
            if (error.Input is not null)
            {
                report.Add("MQ3027", $"Error '{error.Name}' of stereotype '{stereotype.Key}' points at an input member, but a stereotype's error has none; remove input, and "
                    + "let each operation that carries the stereotype redefine the error's input.", pointer + "/input", error.Id);
            }
        }

        Unique(stereotype.Errors.Select((e, i) => (e.Name, Ptr.At("/errors", i), e.Id)), "error", "stereotype '" + stereotype.Key + "'", report);
        var codes = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < stereotype.Errors.Count; i++)
        {
            var error = stereotype.Errors[i];
            if (error.Code is { Length: > 0 } code && !codes.TryAdd(code, Ptr.At("/errors", i)))
            {
                report.Add("MQ3026", $"The error code '{code}' is already used at {codes[code]} in stereotype '{stereotype.Key}'; clients of the operations that carry it could not tell the errors apart.",
                    Ptr.At("/errors", i) + "/code", error.Id);
            }
        }
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
            CheckReturns(outcome, returns, pointer + "/returns", report);

            if (returns.Members is { } members)
                CheckMembers(context, members, pointer + "/returns/members", inReturns: true, report);
        }

        Unique(operation.Outcomes.Select((o, i) => (o.Name, Ptr.At("/outcomes", i), o.Id)), "outcome", "operation '" + operation.Name + "'", report);
        CheckErrors(context, operation, report);
        for (var i = 0; i < operation.Guards.Count; i++)
            BuiltinRules.CheckCommon(context, operation.Guards[i], Ptr.At("/guards", i), "guard", report);
        Unique(operation.Guards.Select((g, i) => (g.Name, Ptr.At("/guards", i), g.Id)), "guard", "operation '" + operation.Name + "'", report);
        CheckDefaultOutcome(context, operation, report);
        if (operation.Effect == OperationEffect.Read)
        {
            for (var i = 0; i < operation.Fires.Count; i++)
            {
                report.Add("MQ3030", $"Operation '{operation.Name}' only reads, but fires the event '{EventName(context, operation.Fires[i])}', and an event changes "
                    + "state; set effect to change, or drop the event.", Ptr.At("/fires", i));
            }
        }

        CheckFires(context.Model, operation, report);
        CheckRequires(context, operation, report);
    }

    // MQ3032 and MQ3044 on each fired event (processes-operations-design.md decisions 2 and 3), at /fires/<i>; a repeated id is judged once, at
    // its first entry. An id that names no event is MQ2001 or MQ2002.
    private static void CheckFires(ModelSnapshot model, Operation operation, Report report)
    {
        if (operation.Fires.Count == 0)
            return;
        var subject = operation.Subject is { } subjectId ? model.Get<Entity>(subjectId) : null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < operation.Fires.Count; i++)
        {
            var id = operation.Fires[i];
            if (!seen.Add(id) || !model.TryGetEntry(id, out var entry) || entry.Kind != "event" || model.Get<Process>(entry.OwnerId) is not { } process
                || process.Events.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal)) is not { } fired)
                continue;
            var at = Ptr.At("/fires", i);
            // The level doesn't matter here: a collection operation may fire a lifecycle event of the record it makes.
            if (subject is not null && process.Subjects.Count > 0 && !OperationLinks.LinesUp(model, process, subject))
            {
                report.Add("MQ3032", $"Operation '{operation.Name}' acts on '{subject.Name}' and fires event '{fired.Name}', but process '{process.Name}' is about "
                    + $"{OperationLinks.Subjects(model, process)}, and governs neither '{subject.Name}' nor an entity derived from it; the model can't say which instance gets "
                    + "the event. Fire an event of a process about the operation's subject, or route it in your system and turn the rule off.", at);
            }

            foreach (var attribute in fired.Payload)
            {
                if (Unsupplied(model, operation, fired, attribute) is { } message)
                    report.Add("MQ3044", message, at);
            }
        }
    }

    // Why an operation's input can't supply a payload attribute of an event it fires (decision 3), or null: the member of the same name must
    // be a typed value of the same type, or a record key of an entity whose one key attribute has that type, and a list exactly when the
    // attribute is one. An optional attribute, or one with a default, needs no member. The implicit record key is not payload.
    private static string? Unsupplied(ModelSnapshot model, Operation operation, ProcessEvent fired, ModelAttribute attribute)
    {
        var type = OperationLinks.TypeText(model, attribute.Type) + (attribute.Collection ? " list" : "");
        var member = operation.Input.FirstOrDefault(m => string.Equals(m.Name, attribute.Name, StringComparison.Ordinal));
        if (member is null)
        {
            return attribute.Required && attribute.Default is null && attribute.DefaultExpression is null
                ? $"Event '{fired.Name}' needs '{attribute.Name}' ({type}, required); operation '{operation.Name}' has no input of that name. Add the input, or lower the rule "
                    + "if your system fills it."
                : null;
        }

        var fits = !member.File && member.Collection == attribute.Collection && (member.KeyOf is { } entityId
            ? model.Get<Entity>(entityId) is { } entity && ExprSites.KeyAttribute(model, entity) is { } key && OperationLinks.SameType(key.Type, attribute.Type)
            : OperationLinks.SameType(member.Type, attribute.Type));
        return fits ? null
            : $"Event '{fired.Name}' carries '{attribute.Name}' as {type}, but input '{member.Name}' of operation '{operation.Name}' is {OperationLinks.MemberText(model, member)}; "
                + "give the input the attribute's type and form, or lower the rule if your system converts it.";
    }

    // MQ3025: one form of members, entity and file; mediaTypes only beside file, list never beside it; paging only on a list, and with
    // maxPageSize.
    private static void CheckReturns(OperationOutcome outcome, OperationReturns returns, string pointer, Report report)
    {
        var id = outcome.Id;
        var forms = (returns.Members is not null ? 1 : 0) + (returns.Entity is not null ? 1 : 0) + (returns.File ? 1 : 0);
        if (forms != 1)
        {
            report.Add("MQ3025", forms == 0
                    ? $"The returns of outcome '{outcome.Name}' states neither members, entity nor file; state one of them, or remove returns to return nothing."
                    : $"The returns of outcome '{outcome.Name}' states more than one of members, entity and file; keep one of them.",
                pointer, id);
        }

        if (returns.MediaTypes.Count > 0 && !returns.File)
            report.Add("MQ3025", $"The returns of outcome '{outcome.Name}' lists mediaTypes, which only a file result takes; remove them, or return a file.", pointer + "/mediaTypes", id);
        if (returns.File && returns.List)
            report.Add("MQ3025", $"The returns of outcome '{outcome.Name}' is a file and a list; a file result is one file, so remove list.", pointer + "/list", id);
        if (returns.Paging is not null && !returns.List)
            report.Add("MQ3025", $"The returns of outcome '{outcome.Name}' sets paging without list; only a list is paged, so set list or remove paging.", pointer + "/paging", id);
        if (returns.Paging is null != returns.MaxPageSize is null)
        {
            report.Add("MQ3025", returns.Paging is null
                    ? $"The returns of outcome '{outcome.Name}' sets maxPageSize without paging; set paging (page or cursor) with it, or remove it."
                    : $"The returns of outcome '{outcome.Name}' sets paging without maxPageSize; state the largest page a caller may ask for.",
                pointer + (returns.Paging is null ? "/maxPageSize" : "/paging"), id);
        }
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
            var forms = (member.Type is not null ? 1 : 0) + (member.KeyOf is not null ? 1 : 0) + (member.File ? 1 : 0);
            if (forms != 1)
            {
                report.Add("MQ3024", forms == 0
                        ? $"Member '{member.Name}' is neither a typed value, a record key nor a file; state type, keyOf or file."
                        : $"Member '{member.Name}' states more than one of type, keyOf and file; keep one form.",
                    forms == 0 ? pointer : pointer + (member.File ? "/file" : "/keyOf"), id);
            }

            if (member.MediaTypes.Count > 0 && !member.File)
                report.Add("MQ3024", $"Member '{member.Name}' lists mediaTypes, which only a file member takes; remove them, or make the member a file.", pointer + "/mediaTypes", id);
            if (member.Type is null && (member.KeyOf is not null || member.File))
            {
                var what = member.KeyOf is not null ? "a record key" : "a file";
                foreach (var (facet, set) in (ReadOnlySpan<(string, bool)>)
                    [("default", member.Default is not null), ("length", member.KeyOf is not null && member.Length is not null), ("precision", member.Precision is not null),
                     ("scale", member.Scale is not null), ("validation", member.Validation is not null)])
                {
                    if (set)
                        report.Add("MQ3024", $"Member '{member.Name}' is {what}, which takes no {facet}; remove it, or make the member a typed value.", pointer + "/" + facet, id);
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

    // MQ3026 on errors sharing a name or a code (the code, else the name), own and inherited counted, and on two inherited errors of one
    // key (two stereotypes giving an error of one id); MQ3027 on an input, own or
    // redefined, that is not an input member of the operation; MQ3018 on names and the marks of each own error; MQ2027 and MQ2028 on the
    // redefines and excludes entries.
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

            if (NotOwnInput(context, operation, error.Input) is { } where)
            {
                report.Add("MQ3027", $"Error '{error.Name}' points at {where}, not at an input member of operation '{operation.Name}'; pick one of its input members.",
                    pointer + "/input", error.Id);
            }
        }

        for (var i = 0; i < operation.Redefines.Count; i++)
        {
            var redefinition = operation.Redefines[i];
            if (NotOwnInput(context, operation, redefinition.Input) is { } where)
            {
                report.Add("MQ3027", $"The redefinition of error '{redefinition.Member}' points at {where}, not at an input member of operation '{operation.Name}'; pick one of its input members.",
                    Ptr.At("/redefines", i) + "/input");
            }
        }

        if (operation.Redefines.Count == 0 && operation.Excludes.Count == 0 && !HasStereotypeErrors(context, operation))
            return;
        CheckLinks(context, operation, report);
        var keys = new Dictionary<string, AppliedStereotype>(StringComparer.Ordinal);
        foreach (var inherited in OperationErrors.Of(context.Model, operation))
        {
            var error = inherited.Member;
            if (!keys.TryAdd(inherited.Key, inherited.From))
            {
                // Two stereotypes give an error of one id: resolution keeps the first, so the second's name and code are not counted.
                report.Add("MQ3026", $"Stereotypes '{keys[inherited.Key].Stereotype.Key}' and '{inherited.From.Stereotype.Key}' both contribute an error with id {inherited.MemberId} to operation '{operation.Name}', which keeps the one of '{keys[inherited.Key].Stereotype.Key}'; give the other error a new id, or carry only one of the stereotypes.",
                    inherited.From.Entry >= 0 ? Ptr.At("/stereotypes", inherited.From.Entry) : "/stereotypes");
                continue;
            }

            var at = PointerOf(operation, inherited.MemberId, inherited.From);
            var from = $"error '{error.Name}' that stereotype '{inherited.From.Stereotype.Key}' contributes";
            var nameTaken = false;
            if (error.Name.Length > 0 && (nameTaken = !names.TryAdd(error.Name, at)))
            {
                report.Add("MQ3026", $"The {from} has the name of the error at {names[error.Name]} in operation '{operation.Name}'; rename the own error, or exclude the inherited one.",
                    at);
            }

            var code = error.Code ?? error.Name;
            if (code.Length > 0 && !codes.TryAdd(code, at) && !(error.Code is null && nameTaken))
            {
                report.Add("MQ3026", $"The {from} has the code '{code}' of the error at {codes[code]} in operation '{operation.Name}'; clients could not tell the errors apart, so exclude one of them.",
                    at);
            }
        }
    }

    // Why an input is not one of the operation's input members ("a member of one of its returns", "a member of another operation"), or null.
    private static string? NotOwnInput(ValidationContext context, Operation operation, string? input) =>
        input is not null && !operation.Input.Any(m => string.Equals(m.Id, input, StringComparison.Ordinal))
            && context.Model.TryGetEntry(input, out var entry) && entry.Kind == "operation-member"
            ? string.Equals(entry.OwnerId, operation.Id, StringComparison.Ordinal) ? "a member of one of its returns" : "a member of another operation"
            : null;

    private static bool HasStereotypeErrors(ValidationContext context, Operation operation) =>
        context.Model.EffectiveStereotypes(operation).Any(s => s.Stereotype.Errors.Count > 0);

    // MQ2027 and MQ2028 on the redefines and excludes entries, as section 3.1 words them for any holder.
    private static void CheckLinks(ValidationContext context, Operation operation, Report report)
    {
        if (operation.Redefines.Count == 0 && operation.Excludes.Count == 0)
            return;
        var inheritable = OperationErrors.Inheritable(context.Model, operation).Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var finding in ContributedMembers.CheckLinks(inheritable, operation.Redefines, operation.Excludes))
        {
            var excludesEntry = finding.Pointer.StartsWith("/excludes/", StringComparison.Ordinal);
            switch (finding.Problem)
            {
                case MemberLinkProblem.NotInherited when excludesEntry:
                    report.Add("MQ2028", $"Exclusion names '{finding.Member}', which is not an error operation '{operation.Name}' inherits from its stereotypes; remove the entry.", finding.Pointer);
                    break;
                case MemberLinkProblem.NotInherited:
                    report.Add("MQ2027", $"Redefinition names '{finding.Member}', which is not an error operation '{operation.Name}' inherits from its stereotypes; fix or remove the entry.", finding.Pointer);
                    break;
                case MemberLinkProblem.RedefinedTwice:
                    report.Add("MQ2027", $"Error '{finding.Member}' is redefined twice; state its facets in one entry.", finding.Pointer);
                    break;
                case MemberLinkProblem.NoFacet:
                    report.Add("MQ2027", $"Redefinition of error '{finding.Member}' states no facet; state the facets it changes (input, displayName, description), or remove the entry.", finding.Pointer);
                    break;
                case MemberLinkProblem.ExcludedAndRedefined:
                    report.Add("MQ2028", $"Error '{finding.Member}' is both excluded and redefined; keep one of the two entries.", finding.Pointer);
                    break;
            }
        }
    }

    // Where a finding about an inherited error goes: its redefines entry, else the contributing stereotype's entry, else /stereotypes
    // (a package default).
    private static string PointerOf(Operation operation, string memberId, AppliedStereotype from)
    {
        for (var i = 0; i < operation.Redefines.Count; i++)
        {
            if (string.Equals(operation.Redefines[i].Member, memberId, StringComparison.Ordinal))
                return Ptr.At("/redefines", i);
        }

        return from.Entry >= 0 ? Ptr.At("/stereotypes", from.Entry) : "/stereotypes";
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
        var own = ModelValidator.SubElementIds(operation).Prepend(operation.Id).Concat(operation.Guards.Select(g => g.Id)).ToHashSet(StringComparer.Ordinal);
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
