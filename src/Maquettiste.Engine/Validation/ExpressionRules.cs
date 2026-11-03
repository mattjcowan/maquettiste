using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// One place an element holds an expression (next-phase-3-design.md section 3.1, "Schema and files"): its pointer, its tree (or
/// <see langword="null"/>), whether it also holds its raw form, and how its scope is built.
/// </summary>
/// <param name="Pointer">The pointer of the tree's key in the element's file.</param>
/// <param name="Tree">The tree, or <see langword="null"/>.</param>
/// <param name="Raw">Whether the site holds its raw form (SQL text, a derived value's string, a guard's JavaScript).</param>
/// <param name="SubId">The check, guard or attribute that holds the site, for diagnostics.</param>
/// <param name="Kind">The site's kind.</param>
/// <param name="Fill">Adds the site's members to its scope's root.</param>
internal sealed record ExprSite(string Pointer, Expr? Tree, bool Raw, string SubId, ExprSiteKind Kind, Action<ExprScope> Fill)
{
    /// <summary>The type a value site must have.</summary>
    public ExprType? ValueType { get; init; }

    /// <summary>Whether the site may hold neither form (a process guard without either is a stub).</summary>
    public bool MayBeEmpty { get; init; }

    /// <summary>The id a check must read (a column check's column, an entity check's attribute), for MQ3114.</summary>
    public string? Reads { get; init; }

    /// <summary>The constraint whose condition the site is.</summary>
    public Constraint? Constraint { get; init; }

    /// <summary>The ids of payload attributes, by the events of the process that carry them, for a process guard.</summary>
    public IReadOnlyList<HashSet<string>>? Payloads { get; init; }

    /// <summary>The ids of an operation subject's members, for MQ3111.</summary>
    public HashSet<string>? Subject { get; init; }
}

/// <summary>
/// The expression sites of the model and the scope each reads (next-phase-3-design.md section 3.1): a table's or stereotype's
/// columns at its checks; an entity's attributes (its bases' too, value-object members as <c>attribute.member</c>) at its checks and
/// derived values; an operation's input and its subject's attributes (under <c>subject</c>) at its guards; a process's context and
/// the payloads of the events that use a guard (under <c>event</c>) at the guard; a constraint's parameters at its condition.
/// </summary>
internal static class ExprSites
{
    /// <summary>The sites of an element, in document order.</summary>
    public static IReadOnlyList<ExprSite> Of(ModelSnapshot model, Element element)
    {
        var sites = new List<ExprSite>();
        switch (element)
        {
            case Table table:
                AddChecks(sites, table.Checks, s => Columns(s, model, table.Columns));
                break;
            case Stereotype stereotype:
                AddChecks(sites, stereotype.Checks, s => Columns(s, model, stereotype.Columns));
                break;
            case Entity entity:
                for (var i = 0; i < entity.Checks.Count; i++)
                {
                    var check = entity.Checks[i];
                    sites.Add(new ExprSite(Ptr.At("/checks", i) + "/condition", check.Condition, false, check.Id, ExprSiteKind.Check, s => Attributes(s, model, entity))
                    {
                        Reads = check.Attribute,
                    });
                }

                for (var i = 0; i < entity.Attributes.Count; i++)
                {
                    var attribute = entity.Attributes[i];
                    if (attribute.Derived is not { } derived)
                        continue;
                    sites.Add(new ExprSite(Ptr.At("/attributes", i) + "/derived/value", derived.Value, derived.Expression.Length > 0, attribute.Id, ExprSiteKind.Value,
                        s => Attributes(s, model, entity))
                    {
                        ValueType = TypeOf(model, attribute.Type),
                    });
                }

                break;
            case Operation operation:
                for (var i = 0; i < operation.Guards.Count; i++)
                {
                    var guard = operation.Guards[i];
                    var subject = operation.Subject is { } subjectId && model.Get<Entity>(subjectId) is { } entity
                        ? AttributesOf(model, entity).Select(a => a.Id).ToHashSet(StringComparer.Ordinal) : null;
                    sites.Add(new ExprSite(Ptr.At("/guards", i) + "/condition", guard.Condition, false, guard.Id, ExprSiteKind.Guard, s => Operation(s, model, operation))
                    {
                        Subject = subject,
                    });
                }

                break;
            case Process process:
                for (var i = 0; i < process.Guards.Count; i++)
                {
                    var guard = process.Guards[i];
                    var events = process.Transitions.Where(t => t.Guard == guard.Id && t.Event is not null).Select(t => t.Event!).Distinct(StringComparer.Ordinal)
                        .Select(id => process.Events.FirstOrDefault(e => e.Id == id)).OfType<ProcessEvent>().ToList();
                    sites.Add(new ExprSite(Ptr.At("/guards", i) + "/condition", guard.Condition, !string.IsNullOrEmpty(guard.Expression), guard.Id, ExprSiteKind.Guard,
                        s => Process(s, model, process, events))
                    {
                        MayBeEmpty = true,
                        Payloads = [.. events.Select(e => e.Payload.Select(a => a.Id).ToHashSet(StringComparer.Ordinal))],
                    });
                }

                break;
            case Constraint constraint:
                sites.Add(new ExprSite("/condition", constraint.Condition, false, constraint.Id, ExprSiteKind.Check, _ => { }) { Constraint = constraint });
                break;
        }

        return sites;

        static void AddChecks(List<ExprSite> sites, IReadOnlyList<CheckConstraint> checks, Action<ExprScope> fill)
        {
            for (var i = 0; i < checks.Count; i++)
            {
                var check = checks[i];
                sites.Add(new ExprSite(Ptr.At("/checks", i) + "/condition", check.Condition, check.Expression.Count > 0, check.Id, ExprSiteKind.Check, fill)
                {
                    Reads = check.Column,
                });
            }
        }
    }

    /// <summary>
    /// The scope of a site: its members, then the enums, reference types and constraints its tree names (every one of the model
    /// with <paramref name="everything"/>, or when the tree, or a constraint it cites, holds text, since text can name any of them).
    /// </summary>
    public static ExprScope Scope(ModelSnapshot model, Element element, ElementDocument? document, ExprSite site, bool everything)
    {
        var scope = new ExprScope(site.Kind) { ValueType = site.ValueType, ElementId = site.SubId, FilePath = document?.Path, Pointer = site.Pointer };
        site.Fill(scope);
        var constraints = new Dictionary<string, Constraint>(StringComparer.Ordinal);
        var refs = new HashSet<string>(StringComparer.Ordinal);
        if (site.Constraint is { } own)
            constraints[own.Id] = own;
        var pending = new Stack<Expr>();
        if (!everything && site.Tree is { } tree)
            pending.Push(tree);
        while (pending.Count > 0)
        {
            var e = pending.Pop();
            if (e.Text is not null)
            {
                everything = true;
                break;
            }

            if (e.Ref is { } r)
                refs.Add(r);
            if (e.Satisfies is { } cited && !constraints.ContainsKey(cited) && model.Get<Constraint>(cited) is { } found)
            {
                constraints[cited] = found;
                pending.Push(found.Condition);
            }

            foreach (var child in Children(e))
                pending.Push(child);
        }

        if (everything)
        {
            foreach (var e in model.All<EnumType>())
                AddEnum(scope, e);
            foreach (var type in model.All<ReferenceType>())
                AddCodes(scope, model, type);
            foreach (var c in model.All<Constraint>())
                constraints[c.Id] = c;
        }
        else
        {
            var sets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in refs)
            {
                if (!model.TryGetEntry(r, out var entry) || !sets.Add(entry.OwnerId))
                    continue;
                if (model.Get<EnumType>(entry.OwnerId) is { } e)
                    AddEnum(scope, e);
                else if (model.Get<Seed>(entry.OwnerId) is { } seed && model.Get<ReferenceType>(seed.Target) is { } type && sets.Add(type.Id))
                    AddCodes(scope, model, type);
            }
        }

        foreach (var c in constraints.Values.OrderBy(c => c.Id, StringComparer.Ordinal))
            scope.AddConstraint(ToExpr(model, c));
        return site.Constraint is { } constraint ? scope.ForConstraint(scope.Constraint(constraint.Id)!) : scope;
    }

    /// <summary>A constraint as the typer reads it.</summary>
    public static ExprConstraint ToExpr(ModelSnapshot model, Constraint c) =>
        new(c.Id, c.Name, [.. c.Parameters.Select(p => new ExprMember(p.Id, p.Name, TypeOf(model, p.Type), true))], c.Condition);

    /// <summary>The children of a node.</summary>
    public static IEnumerable<Expr> Children(Expr e)
    {
        foreach (var arg in e.Args ?? [])
            yield return arg;
        foreach (var when in e.Case ?? [])
        {
            yield return when.When;
            yield return when.Then;
        }

        if (e.Else is { } otherwise)
            yield return otherwise;
        foreach (var argument in e.With ?? [])
            yield return argument.Value;
    }

    /// <summary>Every node of a tree, depth first.</summary>
    public static IEnumerable<Expr> Nodes(Expr e)
    {
        yield return e;
        foreach (var child in Children(e))
        {
            foreach (var node in Nodes(child))
                yield return node;
        }
    }

    /// <summary>The ids each path of a tree names, every segment.</summary>
    public static IEnumerable<string> PathIds(Expr e) => Nodes(e).SelectMany(n => n.Path ?? []);

    /// <summary>Whether a tree holds a <c>text</c> node anywhere.</summary>
    public static bool HasText(Expr? e) => e is not null && Nodes(e).Any(n => n.Text is not null);

    /// <summary>The expression type of a type reference: a built-in, an enum, a reference type's code, a scalar type's base.</summary>
    public static ExprType TypeOf(ModelSnapshot model, TypeRef? type)
    {
        if (type?.Builtin is { } builtin)
            return ExprType.FromBuiltin(builtin);
        if (type?.Ref is not { } id)
            return new ExprType("unknown", null);
        if (model.Get<EnumType>(id) is not null)
            return ExprType.Enum(id);
        if (model.Get<ReferenceType>(id) is not null)
            return ExprType.Code(id);
        if (model.Get<ScalarType>(id) is { } scalar)
            return ExprType.FromBuiltin(scalar.Base);
        return new ExprType("value-object", id);
    }

    private static void AddEnum(ExprScope scope, EnumType e) => scope.AddEnum(e.Id, e.Name, e.Members.Select(m => (m.Id, m.Name)));

    /// <summary>A reference type's codes: the code cell of each row of each seed of the type.</summary>
    private static void AddCodes(ExprScope scope, ModelSnapshot model, ReferenceType type)
    {
        var codes = new List<(string Id, string Code)>();
        foreach (var seed in model.All<Seed>().Where(s => s.Target == type.Id))
        {
            var at = seed.Columns.ToList().IndexOf(type.Code.Id);
            if (at < 0)
                continue;
            foreach (var row in seed.Rows)
            {
                if (at < row.Values.Count && row.Values[at] is { ValueKind: JsonValueKind.String or JsonValueKind.Number } code)
                    codes.Add((row.Id, code.ValueKind == JsonValueKind.String ? code.GetString()! : code.GetRawText()));
            }
        }

        scope.AddReferenceType(type.Id, type.Name, codes);
    }

    /// <summary>A table's or stereotype's columns; one that states no type and whose attribute gives none is unknown (MQ3103 where read).</summary>
    private static void Columns(ExprScope scope, ModelSnapshot model, IReadOnlyList<Column> columns)
    {
        foreach (var column in columns)
        {
            var type = column.TypeRef is { } typeRef ? TypeOf(model, typeRef)
                : column.Type is { } keyword ? ExprType.FromBuiltin(keyword)
                : column.Attribute is { } attribute && AttributeOf(model, attribute) is { } a ? TypeOf(model, a.Type)
                : new ExprType("unknown", null);
            var fixedLength = column.FixedLength || (column.NativeType?.StartsWith("char(", StringComparison.OrdinalIgnoreCase) ?? false);
            scope.Root.Add(new ExprMember(column.Id, column.Name, type, column.Nullable ?? true) { FixedLength = fixedLength, Scale = column.Scale });
        }
    }

    private static ModelAttribute? AttributeOf(ModelSnapshot model, string keyed)
    {
        var id = keyed.Split('.', '@')[^1];
        return model.TryGetEntry(id, out var entry) && model.Get<Entity>(entry.OwnerId) is { } owner ? owner.Attributes.FirstOrDefault(a => a.Id == id) : null;
    }

    /// <summary>An entity's attributes and its bases', nearest first.</summary>
    public static IEnumerable<ModelAttribute> AttributesOf(ModelSnapshot model, Entity entity)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = entity; current is not null && seen.Add(current.Id); current = current.Base is { } b ? model.Get<Entity>(b) : null)
        {
            foreach (var attribute in current.Attributes)
                yield return attribute;
        }
    }

    private static void Attributes(ExprScope scope, ModelSnapshot model, Entity entity) => AddAttributes(scope, model, AttributesOf(model, entity), null);

    private static void AddAttributes(ExprScope scope, ModelSnapshot model, IEnumerable<ModelAttribute> attributes, string? prefix)
    {
        foreach (var attribute in attributes)
        {
            var type = TypeOf(model, attribute.Type);
            scope.Root.Add(new ExprMember(attribute.Id, attribute.Name, type, !attribute.Required)
            {
                Prefix = prefix,
                Collection = attribute.Collection,
                Scale = attribute.Scale,
            });
            if (type.Kind != "value-object" || model.Get<ValueObject>(type.Of!) is not { } valueObject)
                continue;
            foreach (var member in valueObject.Attributes)
            {
                scope.Root.Add(new ExprMember(attribute.Id + "." + member.Id, member.Name, TypeOf(model, member.Type), !attribute.Required || !member.Required)
                {
                    Prefix = prefix is null ? attribute.Name : prefix + "." + attribute.Name,
                    Collection = member.Collection,
                    Scale = member.Scale,
                });
            }
        }
    }

    private static void Operation(ExprScope scope, ModelSnapshot model, Operation operation)
    {
        foreach (var member in operation.Input)
        {
            var type = member.KeyOf is not null ? ExprType.Uuid : TypeOf(model, member.Type);
            scope.Root.Add(new ExprMember(member.Id, member.Name, type, !member.Required) { Collection = member.Collection, Scale = member.Scale });
        }

        if (operation.Subject is { } subject && model.Get<Entity>(subject) is { } entity)
            AddAttributes(scope, model, AttributesOf(model, entity), "subject");
    }

    private static void Process(ExprScope scope, ModelSnapshot model, Process process, IReadOnlyList<ProcessEvent> events)
    {
        AddAttributes(scope, model, process.Context, null);
        foreach (var e in events)
            AddAttributes(scope, model, e.Payload, "event");
    }
}

/// <summary>
/// The expression rules (next-phase-3-design.md section 3.1, "Rules"): each tree is typed in its site's scope (MQ3101 to MQ3106,
/// MQ3108, MQ3109, MQ3113 come from the typer), and the sites, bindings and guards are checked around it (MQ3107, MQ3110, MQ3111,
/// MQ3114, MQ3115). An element without expression sites costs one type test.
/// </summary>
internal static class ExpressionRules
{
    /// <summary>Checks the expression sites of the report's element.</summary>
    public static void Validate(ValidationContext context, Report report)
    {
        var element = report.Document.Element;
        if (element is not (Table or Stereotype or Entity or Operation or Process or Constraint))
            return;
        var model = context.Model;
        var sites = ExprSites.Of(model, element);
        if (sites.Count == 0 && !(element is Entity { Bindings.Count: > 0 } bound && bound.Bindings.Any(b => b.Checks.Count > 0)))
            return;
        var trees = new Dictionary<string, Expr>(StringComparer.Ordinal);
        foreach (var site in sites)
        {
            if (site.Raw && site.Tree is not null)
            {
                report.Add("MQ3107", "This site holds both its raw form and an expression tree; keep one: the tree, or the text it replaces.", site.Pointer, site.SubId);
            }
            else if (!site.Raw && site.Tree is null && !site.MayBeEmpty)
            {
                report.Add("MQ3107", "This site holds neither its raw form nor an expression tree; write a condition or value.", site.Pointer, site.SubId);
            }

            if (site.Tree is not { } tree)
                continue;
            trees[site.SubId] = tree;
            var scope = ExprSites.Scope(model, element, report.Document, site, everything: false);
            var problems = new List<Diagnostic>();
            var typed = ExprTyper.Type(tree, scope, problems);
            foreach (var problem in problems)
                report.Add(problem);
            if (typed is null)
                continue;
            var read = ExprSites.PathIds(tree).ToHashSet(StringComparer.Ordinal);
            if (site.Reads is { } wanted && !read.Contains(wanted) && !read.Contains(wanted.Split('.', '@', ':')[^1]))
            {
                report.Add("MQ3114", element is Entity
                    ? "The entity check does not read its attribute, so a failure points at a member the condition does not judge; point attribute at a member it reads."
                    : "The column check does not read its column; move it to the table's checks or point column at a member it reads.", site.Pointer, site.SubId);
            }

            if (site.Payloads is { Count: > 0 } payloads)
            {
                foreach (var id in read.Where(id => payloads.Any(p => p.Contains(id))).Order(StringComparer.Ordinal))
                {
                    if (payloads.Any(p => !p.Contains(id)))
                    {
                        report.Add("MQ3102", $"The guard reads the payload attribute '{id}', which some event that uses the guard does not carry; read a context attribute, "
                            + "or use the guard only on transitions of events that carry it.", site.Pointer, site.SubId);
                    }
                }
            }

            if (element is Operation operation)
                CheckOperationGuard(model, operation, site, read, report);
        }

        if (element is Entity entity)
            CheckBindings(model, entity, trees, report);
    }

    private static void CheckOperationGuard(ModelSnapshot model, Operation operation, ExprSite site, HashSet<string> read, Report report)
    {
        var index = operation.Guards.ToList().FindIndex(g => g.Id == site.SubId);
        var guard = operation.Guards[index];
        if (guard.Error is { } error)
        {
            var id = error.Split(':', '.')[^1];
            var errors = operation.Errors.Select(e => e.Id)
                .Concat(model.EffectiveStereotypes(operation).SelectMany(s => s.Stereotype.Errors).Select(e => e.Id));
            if (!errors.Contains(id, StringComparer.Ordinal))
            {
                report.Add("MQ3111", $"The guard's error '{error}' is not an error of the operation; name one of its own or inherited errors.",
                    Ptr.At("/guards", index) + "/error", guard.Id);
            }
        }

        if (operation.Level != OperationLevel.Record && site.Subject is { } subject && read.Any(subject.Contains))
        {
            report.Add("MQ3111", "The guard reads the subject, but the operation is not on one record, so there is no subject to read; read its input instead.",
                site.Pointer, guard.Id);
        }
    }

    /// <summary>MQ3110 on the checks a binding lists; MQ3115 on an entity check no binding lists though one could enforce it.</summary>
    private static void CheckBindings(ModelSnapshot model, Entity entity, Dictionary<string, Expr> trees, Report report)
    {
        if (entity.Checks.Count == 0 && entity.Bindings.All(b => b.Checks.Count == 0))
            return;
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var enforceable = new HashSet<string>(StringComparer.Ordinal);
        for (var j = 0; j < entity.Bindings.Count; j++)
        {
            var binding = entity.Bindings[j];
            var table = WriteTable(model, binding);
            foreach (var check in entity.Checks)
            {
                if (table is not null && trees.TryGetValue(check.Id, out var tree) && Unmapped(model, binding, table, tree) is null)
                    enforceable.Add(check.Id);
            }

            for (var k = 0; k < binding.Checks.Count; k++)
            {
                var id = binding.Checks[k];
                var at = Ptr.At(Ptr.At("/bindings", j) + "/checks", k);
                if (entity.Checks.All(c => c.Id != id))
                {
                    report.Add("MQ3110", $"The binding lists '{id}', which is not a check of this entity; list one of the entity's checks.", at, binding.Id);
                    continue;
                }

                if (table is null)
                {
                    report.Add("MQ3110", "The binding lists a check but writes no table, so no database could enforce it; give the binding a write table or remove the check.", at, binding.Id);
                    continue;
                }

                listed.Add(id);
                if (trees.TryGetValue(id, out var tree) && Unmapped(model, binding, table, tree) is { } problem)
                    report.Add("MQ3110", problem, at, binding.Id);
            }
        }

        for (var i = 0; i < entity.Checks.Count; i++)
        {
            var check = entity.Checks[i];
            if (!listed.Contains(check.Id) && enforceable.Contains(check.Id))
            {
                report.Add("MQ3115", "No binding lists this check, though a binding with a write table maps every attribute it reads, so no database enforces it; "
                    + "list it in that binding's checks.", Ptr.At("/checks", i), check.Id);
            }
        }
    }

    /// <summary>The table a binding writes: its write table, else its source when that is a table; none when it never writes.</summary>
    private static Table? WriteTable(ModelSnapshot model, EntityBinding binding)
    {
        if (binding.Write is { None: true })
            return null;
        var id = binding.Write?.Table ?? binding.Source;
        return model.Get<Table>(id);
    }

    /// <summary>Why a binding cannot enforce a check: an attribute it reads that the binding does not map to a varying column of the table.</summary>
    private static string? Unmapped(ModelSnapshot model, EntityBinding binding, Table table, Expr tree)
    {
        foreach (var path in ExprSites.Nodes(tree).Where(n => n.Path is { Count: > 0 }).Select(n => n.Path![0]).Distinct(StringComparer.Ordinal))
        {
            var field = binding.Fields.FirstOrDefault(f => f.Attribute == path || f.Attribute.Split('.')[0] == path.Split('.')[0]);
            if (field is null)
                return $"The binding lists a check that reads '{path}', which the binding does not map to its write table; map the attribute, or remove the check from the binding.";
            var columnId = field.Column.Split('.', '@')[^1];
            var column = table.Columns.FirstOrDefault(c => c.Id == columnId || c.Id == field.Column);
            if (column is not null && (column.FixedLength || (column.NativeType?.StartsWith("char(", StringComparison.OrdinalIgnoreCase) ?? false)))
                return $"The binding lists a check that reads '{path}', which it maps to the fixed-length column '{column.Name}'; map the attribute to a varying column, or remove the check from the binding.";
        }

        _ = model;
        return null;
    }

    /// <summary>The position text of an index, for messages.</summary>
    internal static string Index(int i) => i.ToString(CultureInfo.InvariantCulture);
}
