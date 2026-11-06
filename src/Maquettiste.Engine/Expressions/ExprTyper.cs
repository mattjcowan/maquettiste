using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Expressions;

/// <summary>
/// A typed node (next-phase-3-design.md section 3.1, typing): the node, its pointer, its type, whether it may be null and its typed
/// operands. A <c>case</c> lists its operands as when, then, when, then… and the else last when there is one; a <c>satisfies</c> lists
/// its argument values in the constraint's parameter order and carries the constraint's typed condition in <see cref="Body"/>; a
/// <c>text</c> node is the typed tree its text parses to.
/// </summary>
/// <param name="Node">The node; for a <c>text</c> node, the tree it parses to.</param>
/// <param name="Pointer">The node's JSON pointer in its file.</param>
/// <param name="Type">The node's type.</param>
/// <param name="Nullable">Whether the node may be null.</param>
/// <param name="Args">The typed operands.</param>
public sealed record TypedExpr(Expr Node, string Pointer, ExprType Type, bool Nullable, IReadOnlyList<TypedExpr> Args)
{
    /// <summary>For a path: the member it ends at.</summary>
    public ExprMember? Member { get; init; }

    /// <summary>For a path: the navigations it follows, in order.</summary>
    public IReadOnlyList<ExprNavigation> Through { get; init; } = [];

    /// <summary>For a <c>satisfies</c>: the constraint.</summary>
    public ExprConstraint? Constraint { get; init; }

    /// <summary>For a <c>satisfies</c>: the constraint's condition, typed against its parameters; its paths name parameter ids, which
    /// lowering replaces with <see cref="Args"/>.</summary>
    public TypedExpr? Body { get; init; }

    /// <summary>For an <c>in_state</c>: the id of the lifecycle (process) whose states it names. Its <see cref="Through"/> is its
    /// <c>of</c> path.</summary>
    public string? Lifecycle { get; init; }

    /// <summary>For an <c>in_state</c>: the states it names, in its order.</summary>
    public IReadOnlyList<ExprState> States { get; init; } = [];

    /// <summary>For an <c>in_state</c>: the states it names and every state inside them, each once, in the lifecycle's document order:
    /// it holds when any of them is active.</summary>
    public IReadOnlyList<string> Inside { get; init; } = [];

    /// <summary>For an <c>in_state</c>: whether it reads the running chart, the process's own lifecycle on the object itself
    /// (object-effects-design.md decision 8), rather than an object's given states.</summary>
    public bool Running { get; init; }
}

/// <summary>
/// Types an expression in its site's scope (next-phase-3-design.md section 3.1, typing): every node gets a type and whether it may be
/// null, and every misfit is a diagnostic at the node's pointer: MQ3101 (a malformed node), MQ3102 (a path the site cannot read),
/// MQ3103 (types that do not fit), MQ3104 (<c>now</c> outside a guard), MQ3105 (a pattern outside the portable subset), MQ3106 (an
/// argument that must be a literal), MQ3108 (text in a file), MQ3109 (a <c>satisfies</c> that does not bind, or cites a constraint
/// that does not type) and MQ3113 (size).
/// </summary>
public static class ExprTyper
{
    /// <summary>Types an expression.</summary>
    /// <param name="e">The expression.</param>
    /// <param name="scope">The site's scope.</param>
    /// <param name="problems">Receives the diagnostics.</param>
    /// <returns>The typed tree, or <see langword="null"/> when it does not type.</returns>
    public static TypedExpr? Type(Expr e, ExprScope scope, ICollection<Diagnostic> problems)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(problems);
        var run = new Run(scope, problems);
        if (scope.ConstraintId is { } own)
            run.Citing.Add(own);
        if (!run.WithinSize(e, scope.Pointer))
            return null;
        var typed = run.Node(e, scope.Pointer, flat: false);
        if (typed is null)
            return null;

        if (scope.ConstraintId is not null || scope.Site is not (ExprSiteKind.Value or ExprSiteKind.Set))
        {
            if (!Fits(ExprType.Bool, typed.Type))
            {
                run.Report("MQ3103", typed.Pointer, "The expression is " + typed.Type + "; a " + SiteName(scope) + " must be bool.");
                return null;
            }
        }
        else if (scope.ValueType is { } wanted)
        {
            // actor() takes the type of the attribute it is set to (object-effects-design.md decision 5).
            if (scope.Site == ExprSiteKind.Set && typed.Node.Actor == true && wanted.IsAvailable)
                typed = typed with { Type = wanted };
            if (!Fits(wanted, typed.Type))
            {
                run.Report("MQ3103", typed.Pointer, "The expression is " + typed.Type + "; the " + (scope.Site == ExprSiteKind.Set ? "set's value" : "derived value")
                    + " must be " + wanted + ".");
                return null;
            }
        }

        return typed;
    }

    /// <summary>Whether a value of one type fits where another is wanted: the same type (an <c>int</c> of any width), an <c>int</c>
    /// where a <c>decimal</c> is wanted, a <c>datetime</c> where a <c>datetimeoffset</c> is, or the null literal anywhere.</summary>
    /// <param name="wanted">The type wanted.</param>
    /// <param name="actual">The value's type.</param>
    /// <returns><see langword="true"/> when it fits.</returns>
    public static bool Fits(ExprType wanted, ExprType actual)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(actual);
        return actual.Kind == "null" || (Unify(wanted, actual) is { } common && common.Kind == wanted.Kind && common.Of == wanted.Of);
    }

    /// <summary>The type two operands compare or compute in, or <see langword="null"/> when they do not fit together.</summary>
    internal static ExprType? Unify(ExprType a, ExprType b)
    {
        if (a.Kind == "null")
            return b;
        if (b.Kind == "null")
            return a;
        if (a.Kind == b.Kind)
        {
            return a.Kind switch
            {
                "int" => a.Bits >= b.Bits ? a : b,
                "enum" or "code" => a.Of == b.Of ? a : null,
                _ => a,
            };
        }

        return (a.Kind, b.Kind) switch
        {
            ("int", "decimal") or ("decimal", "int") => ExprType.Decimal,
            ("int", "double") or ("double", "int") => ExprType.Double,
            ("datetime", "datetimeoffset") or ("datetimeoffset", "datetime") => ExprType.DateTimeOffset,
            _ => null,
        };
    }

    private static string SiteName(ExprScope scope) => scope.ConstraintId is not null ? "constraint" : scope.Site switch
    {
        ExprSiteKind.Check => "check",
        ExprSiteKind.Guard => "guard",
        _ => "rule condition",
    };

    private sealed class Run(ExprScope scope, ICollection<Diagnostic> problems)
    {
        public HashSet<string> Citing { get; } = new(StringComparer.Ordinal);

        public void Report(string rule, string pointer, string message, DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
            problems.Add(new Diagnostic(rule, severity, message, scope.ElementId, scope.FilePath, pointer, null, null));

        public bool WithinSize(Expr e, string pointer)
        {
            var nodes = 0;
            var deepest = 0;
            Measure(e, 1, ref nodes, ref deepest);
            if (nodes <= ExprFunctions.MaxNodes && deepest <= ExprFunctions.MaxDepth)
                return true;
            Report("MQ3113", pointer, "The expression has " + nodes.ToString(CultureInfo.InvariantCulture) + " nodes and nests "
                + deepest.ToString(CultureInfo.InvariantCulture) + " deep; at most " + ExprFunctions.MaxNodes.ToString(CultureInfo.InvariantCulture)
                + " nodes and " + ExprFunctions.MaxDepth.ToString(CultureInfo.InvariantCulture) + " levels are allowed.");
            return false;
        }

        private static void Measure(Expr e, int depth, ref int nodes, ref int deepest)
        {
            nodes++;
            deepest = Math.Max(deepest, depth);
            if (depth > ExprFunctions.MaxDepth || nodes > ExprFunctions.MaxNodes)
                return; // already too large: stop before a hostile tree goes any deeper
            foreach (var child in Children(e))
                Measure(child, depth + 1, ref nodes, ref deepest);
        }

        private static IEnumerable<Expr> Children(Expr e)
        {
            foreach (var arg in e.Args ?? [])
                yield return arg;
            foreach (var branch in e.Case ?? [])
            {
                yield return branch.When;
                yield return branch.Then;
            }

            if (e.Else is { } otherwise)
                yield return otherwise;
            foreach (var argument in e.With ?? [])
                yield return argument.Value;
        }

        public TypedExpr? Node(Expr e, string pointer, bool flat)
        {
            var shapes = (e.Value is not null ? 1 : 0) + (e.Null is not null ? 1 : 0) + (e.Path is not null ? 1 : 0) + (e.Ref is not null ? 1 : 0)
                + (e.Member is not null ? 1 : 0) + (e.Now is not null ? 1 : 0) + (e.Op is not null ? 1 : 0) + (e.Call is not null ? 1 : 0)
                + (e.Case is not null ? 1 : 0) + (e.Satisfies is not null ? 1 : 0) + (e.Text is not null ? 1 : 0) + (e.Actor is not null ? 1 : 0)
                + (e.InState is not null ? 1 : 0);
            if (shapes != 1)
            {
                return Malformed(pointer, shapes == 0 ? "The node is empty."
                    : "The node holds more than one of value, null, path, ref, member, now, actor, inState, op, call, case, satisfies and text.");
            }

            if (e.Of is not null && e.InState is null)
                return Malformed(pointer, "'of' goes with 'inState' only.");
            if (e.Type is not null && e.Value is null)
                return Malformed(pointer, "'type' goes with 'value' only.");
            if (e.Args is not null && e.Op is null && e.Call is null)
                return Malformed(pointer, "'args' goes with 'op' or 'call' only.");
            if (e.Else is not null && e.Case is null)
                return Malformed(pointer, "'else' goes with 'case' only.");
            if (e.With is not null && e.Satisfies is null)
                return Malformed(pointer, "'with' goes with 'satisfies' only.");

            if (e.Value is { } value)
                return Literal(e, value, pointer);
            if (e.Null is not null)
                return e.Null == true ? new TypedExpr(e, pointer, ExprType.Null, true, []) : Malformed(pointer, "'null' must be true.");
            if (e.Path is { } path)
                return Path(e, path, pointer);
            if (e.Ref is { } id)
            {
                if (scope.RefType(id) is { } refType)
                    return new TypedExpr(e, pointer, refType, false, []);
                Report("MQ3102", pointer, "No enum member or reference-type code has the id '" + id + "'.");
                return null;
            }

            if (e.Member is { } member)
                return Member(e, member, pointer);
            if (e.Now is not null)
            {
                if (e.Now != true)
                    return Malformed(pointer, "'now' must be true.");
                if (!scope.AllowsNow)
                {
                    Report("MQ3104", pointer, "now() reads the clock; only a guard or a value an action sets may read it.");
                    return null;
                }

                return new TypedExpr(e, pointer, ExprType.DateTimeOffset, false, []);
            }

            if (e.Actor is not null)
            {
                if (e.Actor != true)
                    return Malformed(pointer, "'actor' must be true.");
                if (!scope.AllowsActor)
                {
                    Report("MQ3104", pointer, "actor() is whoever acts; only a guard or a value an action sets may read it.");
                    return null;
                }

                // A string until it meets a value of another type (Adopt); null when nobody acts.
                return new TypedExpr(e, pointer, ExprType.String, true, []);
            }

            if (e.InState is { } states)
                return InState(e, states, e.Of, pointer);
            if (e.Op is { } op)
                return Operator(e, op, e.Args ?? [], pointer, flat);
            if (e.Call is { } call)
                return Function(e, call, e.Args ?? [], pointer, flat);
            if (e.Case is { } branches)
                return Case(e, branches, pointer, flat);
            if (e.Satisfies is { } constraintId)
                return Satisfies(e, constraintId, pointer, flat);
            return Text(e.Text!, pointer);
        }

        private TypedExpr? Malformed(string pointer, string message)
        {
            Report("MQ3101", pointer, message);
            return null;
        }

        private TypedExpr? Literal(Expr e, JsonElement value, string pointer)
        {
            if (e.Type is { } type)
            {
                if (value.ValueKind != JsonValueKind.String)
                    return Malformed(pointer, "A value with a type is written as text, so that its digits are kept.");
                if (!ExprText.IsLiteral(type, value.GetString()!))
                    return Malformed(pointer, "'" + value.GetString() + "' is not a " + type + " literal.");
                return new TypedExpr(e, pointer, ExprType.FromBuiltin(type), false, []);
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return new TypedExpr(e, pointer, ExprType.String, false, []);
                case JsonValueKind.True or JsonValueKind.False:
                    return new TypedExpr(e, pointer, ExprType.Bool, false, []);
                case JsonValueKind.Number when value.TryGetInt64(out var n):
                    return new TypedExpr(e, pointer, n is >= int.MinValue and <= int.MaxValue ? ExprType.Int32 : ExprType.Int64, false, []);
                case JsonValueKind.Number:
                    return Malformed(pointer, "A number without a type is an integer of at most 64 bits; write a decimal as { \"value\": \"" + value.GetRawText()
                        + "\", \"type\": \"decimal\" }.");
                default:
                    return Malformed(pointer, "A value is a string, an integer or a boolean.");
            }
        }

        private TypedExpr? Path(Expr e, IReadOnlyList<string> path, string pointer)
        {
            if (path.Count == 0)
                return Malformed(pointer, "A path holds at least one id.");
            var container = scope.Root;
            var through = new List<ExprNavigation>();
            for (var i = 0; i < path.Count - 1; i++)
            {
                if (container.Navigation(path[i]) is not { } navigation)
                {
                    Report("MQ3102", pointer, container.Member(path[i]) is not null
                        ? "The path goes on after '" + path[i] + "', which is no navigation."
                        : "The site has no navigation with the id '" + path[i] + "'.");
                    return null;
                }

                if (!scope.AllowsNavigation)
                {
                    Report("MQ3102", pointer, "The path follows the navigation '" + navigation.Name + "'; only a guard reads through a relation.");
                    return null;
                }

                if (!navigation.ToOne)
                {
                    Report("MQ3102", pointer, "The navigation '" + navigation.Name + "' leads to many records; a path follows to-one navigations only.");
                    return null;
                }

                through.Add(navigation);
                container = navigation.Target;
            }

            if (container.Member(path[^1]) is not { } member)
            {
                Report("MQ3102", pointer, container.Navigation(path[^1]) is { } last
                    ? "The path ends at the navigation '" + last.Name + "'; it must go on to an attribute."
                    : "The site has no member with the id '" + path[^1] + "'.");
                return null;
            }

            if (member.Collection)
            {
                Report("MQ3102", pointer, "'" + member.Name + "' is a collection, which no expression reads.");
                return null;
            }

            if (!Available(member.Type, pointer, "'" + member.Name + "'"))
                return null;
            return new TypedExpr(e, pointer, member.Type, member.Nullable || through.Any(n => n.Optional), []) { Member = member, Through = through };
        }

        private TypedExpr? Member(Expr e, string path, string pointer)
        {
            if (scope.Site != ExprSiteKind.RuleCondition)
            {
                Report("MQ3102", pointer, "A 'member' node reads a declared rule's object; it belongs in a rule condition only.");
                return null;
            }

            if (scope.MemberPaths?.Invoke(path) is not { } member)
            {
                Report("MQ3102", pointer, "The selected object has no member '" + path + "'.");
                return null;
            }

            if (member.Collection)
            {
                Report("MQ3102", pointer, "'" + path + "' is a collection, which no expression reads.");
                return null;
            }

            return Available(member.Type, pointer, "'" + path + "'") ? new TypedExpr(e, pointer, member.Type, member.Nullable, []) { Member = member } : null;
        }

        private bool Available(ExprType type, string pointer, string what)
        {
            if (type.IsAvailable && type.Kind != "null")
                return true;
            Report("MQ3103", pointer, type.Kind switch
            {
                "float" => what + " is a float, which the expression language does not have, since a float rounds differently in each target; keep SQL text for it.",
                "value-object" => what + " is a value object; name one of its members.",
                "unknown" => what + " has no type the expression can read; give it one.",
                _ => what + " is " + type.Kind + ", which the expression language does not have.",
            });
            return false;
        }

        private List<TypedExpr>? Operands(IReadOnlyList<Expr> args, string pointer, bool flat)
        {
            var typed = new List<TypedExpr>(args.Count);
            var failed = false;
            for (var i = 0; i < args.Count; i++)
            {
                var t = Node(args[i], flat ? pointer : pointer + "/args/" + i.ToString(CultureInfo.InvariantCulture), flat);
                if (t is null)
                    failed = true;
                else
                    typed.Add(t);
            }

            return failed ? null : typed;
        }

        private TypedExpr? Operator(Expr e, string op, IReadOnlyList<Expr> args, string pointer, bool flat)
        {
            if (!ExprFunctions.Operators.TryGetValue(op, out var info))
                return Malformed(pointer, "'" + op + "' is not an operator of the expression language.");
            if (args.Count < info.MinArgs || args.Count > info.MaxArgs)
                return Malformed(pointer, "'" + op + "' takes " + Arity(info.MinArgs, info.MaxArgs) + " operands, not " + args.Count.ToString(CultureInfo.InvariantCulture) + ".");
            if (op is "in" or "notIn" && !InList(args, pointer, flat))
                return null;
            if (Operands(args, pointer, flat) is not { } typed)
                return null;
            if (op is "eq" or "ne" or "lt" or "le" or "gt" or "ge" or "distinct" or "notDistinct" or "between" or "in" or "notIn" or "coalesce")
                Adopt(typed, Enumerable.Range(0, typed.Count));

            var anyNull = typed.Any(t => t.Nullable);
            switch (op)
            {
                case "and" or "or" or "not" or "implies":
                    foreach (var t in typed)
                    {
                        if (!Fits(ExprType.Bool, t.Type))
                            return Misfit(t.Pointer, "'" + info.Text + "' takes bool operands, not " + t.Type + ".");
                    }

                    return new TypedExpr(e, pointer, ExprType.Bool, anyNull, typed);
                case "isNull" or "isNotNull":
                    return new TypedExpr(e, pointer, ExprType.Bool, false, typed);
                case "eq" or "ne" or "lt" or "le" or "gt" or "ge" or "distinct" or "notDistinct" or "between" or "in" or "notIn":
                {
                    var ordered = op is "lt" or "le" or "gt" or "ge" or "between";
                    if (Common(typed, pointer, "compare") is not { } common)
                        return null;
                    if (ordered ? !common.IsOrdered : !common.IsComparable)
                    {
                        return Misfit(pointer, common.Kind == "null"
                            ? "'" + info.Text + "' compares nothing but null; use 'is null'."
                            : "'" + info.Text + "' does not apply to " + common + (ordered ? ", which has no order." : "."));
                    }

                    return new TypedExpr(e, pointer, ExprType.Bool, op is not ("distinct" or "notDistinct") && anyNull, typed);
                }

                case "add" or "sub" or "mul" or "div" or "neg":
                    return Arithmetic(e, op, info, typed, pointer, anyNull);
                case "concat":
                    foreach (var t in typed)
                    {
                        if (!Fits(ExprType.String, t.Type))
                            return Misfit(t.Pointer, "'||' joins strings, not " + t.Type + ".");
                    }

                    return new TypedExpr(e, pointer, ExprType.String, anyNull, typed);
                default: // coalesce
                    if (Common(typed, pointer, "coalesce") is not { } type)
                        return null;
                    return new TypedExpr(e, pointer, type, typed.All(t => t.Nullable), typed);
            }
        }

        private bool InList(IReadOnlyList<Expr> args, string pointer, bool flat)
        {
            for (var i = 1; i < args.Count; i++)
            {
                var item = args[i];
                var at = flat ? pointer : pointer + "/args/" + i.ToString(CultureInfo.InvariantCulture);
                if (item.Null is not null)
                {
                    Report("MQ3106", at, "An 'in' list holds no null; test 'is null' beside it.");
                    return false;
                }

                var literal = item.Value is not null || item.Ref is not null
                    || (item.Op == "neg" && item.Args is [{ Value.ValueKind: JsonValueKind.Number or JsonValueKind.String }]);
                if (!literal)
                {
                    Report("MQ3106", at, "An 'in' list holds literals and enum members or codes only.");
                    return false;
                }
            }

            return true;
        }

        private ExprType? Common(IReadOnlyList<TypedExpr> typed, string pointer, string verb)
        {
            var common = typed[0].Type;
            for (var i = 1; i < typed.Count; i++)
            {
                if (Unify(common, typed[i].Type) is { } next)
                {
                    common = next;
                    continue;
                }

                Misfit(pointer, Mismatch(verb, common, typed[i]));
                return null;
            }

            return common;
        }

        private string Mismatch(string verb, ExprType a, TypedExpr other)
        {
            var b = other.Type;
            var refNode = other.Node.Ref is not null;
            if ((a.Kind == "date" && b.Kind is "datetime" or "datetimeoffset") || (b.Kind == "date" && a.Kind is "datetime" or "datetimeoffset"))
                return "Cannot " + verb + " a date with a timestamp; take the timestamp's date with date_of.";
            if (a.Kind is "enum" or "code" && b.Kind is "enum" or "code")
                return refNode ? "The member or code belongs to " + b + ", not to " + a + "." : "Cannot " + verb + " " + a + " with " + b + ".";
            if (refNode || a.Kind is "enum" or "code" || b.Kind is "enum" or "code")
                return "Cannot " + verb + " " + a + " with " + b + "; an enum member or code meets a value of its own enum or reference type only.";
            return "Cannot " + verb + " " + a + " with " + b + ".";
        }

        private TypedExpr? Arithmetic(Expr e, string op, ExprOperator info, List<TypedExpr> typed, string pointer, bool anyNull)
        {
            foreach (var t in typed)
            {
                if (t.Type.IsTemporal && op is "add" or "sub")
                    return Misfit(t.Pointer, "'" + info.Text + "' does not move a " + t.Type + "; use add_days or add_months.");
                if (!t.Type.IsNumber && t.Type.Kind != "null")
                    return Misfit(t.Pointer, "'" + info.Text + "' computes on numbers, not " + t.Type + ".");
            }

            if (Common(typed, pointer, "compute") is not { } common)
                return null;
            if (common.Kind == "null")
                return Misfit(pointer, "'" + info.Text + "' has no operand with a type.");
            var result = (op, common.Kind) switch
            {
                ("div", "double") => ExprType.Double,
                ("div", _) => ExprType.Decimal,
                (_, "int") => ExprType.Int64,
                _ => common,
            };
            return new TypedExpr(e, pointer, result, anyNull || op == "div", typed);
        }

        private TypedExpr? Function(Expr e, string name, IReadOnlyList<Expr> args, string pointer, bool flat)
        {
            if (!ExprFunctions.Functions.TryGetValue(name, out var info))
                return Malformed(pointer, "'" + name + "' is not a function of the expression language.");
            if (args.Count < info.MinArgs || args.Count > info.MaxArgs)
                return Malformed(pointer, name + "() takes " + Arity(info.MinArgs, info.MaxArgs) + " arguments, not " + args.Count.ToString(CultureInfo.InvariantCulture) + ".");
            string At(int i) => flat ? pointer : pointer + "/args/" + i.ToString(CultureInfo.InvariantCulture);

            // Literal arguments first, so that a computed pattern, part or digit count is MQ3106 rather than a type error.
            string? part = null;
            switch (name)
            {
                case "matches":
                    if (args[1].Value is not { ValueKind: JsonValueKind.String } pattern || args[1].Type is not null)
                    {
                        Report("MQ3106", At(1), "matches() takes its pattern as a string literal.");
                        return null;
                    }

                    if (!PortableRegex.TryValidate(pattern.GetString()!, out var problem))
                    {
                        Report("MQ3105", At(1), problem!);
                        return null;
                    }

                    break;
                case "date_part":
                    if (args[0].Value is not { ValueKind: JsonValueKind.String } p || args[0].Type is not null)
                    {
                        Report("MQ3106", At(0), "date_part() takes its part as a string literal: 'year', 'month', 'day', 'isodow', 'hour' or 'minute'.");
                        return null;
                    }

                    part = p.GetString()!;
                    if (!ExprFunctions.DateParts.Contains(part) && !ExprFunctions.TimeParts.Contains(part))
                        return Malformed(At(0), "'" + part + "' is not a part date_part() reads: 'year', 'month', 'day', 'isodow', 'hour' or 'minute'.");
                    break;
                case "round":
                    if (args[1].Value is not { ValueKind: JsonValueKind.Number } digits || args[1].Type is not null)
                    {
                        Report("MQ3106", At(1), "round() takes its digit count as an integer literal from 0 to 18.");
                        return null;
                    }

                    if (!digits.TryGetInt32(out var n) || n is < 0 or > 18)
                        return Misfit(At(1), "round() rounds to 0 to 18 digits.");
                    break;
            }

            if (Operands(args, pointer, flat) is not { } typed)
                return null;
            var anyNull = typed.Any(t => t.Nullable);

            TypedExpr? Want(int i, params string[] kinds)
            {
                var t = typed[i];
                return t.Type.Kind == "null" || kinds.Contains(t.Type.Kind)
                    ? t
                    : Misfit(t.Pointer, name + "() takes " + string.Join(" or ", kinds) + (typed.Count > 1 ? " as argument " + (i + 1).ToString(CultureInfo.InvariantCulture) : "")
                        + ", not " + t.Type + ".");
            }

            switch (name)
            {
                case "trim":
                    return Want(0, "string") is null ? null : new TypedExpr(e, pointer, ExprType.String, anyNull, typed);
                case "length":
                    return Want(0, "string") is null ? null : new TypedExpr(e, pointer, ExprType.Int32, anyNull, typed);
                case "matches":
                    return Want(0, "string") is null ? null : new TypedExpr(e, pointer, ExprType.Bool, typed[0].Nullable, typed);
                case "nonnull_count":
                    foreach (var t in typed)
                    {
                        if (!t.Type.IsAvailable)
                            return Misfit(t.Pointer, "nonnull_count() counts values of the expression language, not " + t.Type + ".");
                    }

                    return new TypedExpr(e, pointer, ExprType.Int32, false, typed);
                case "add_days" or "add_months":
                    if (Want(0, "date", "datetime", "datetimeoffset") is null || Want(1, "int") is null)
                        return null;
                    return new TypedExpr(e, pointer, typed[0].Type.Kind == "null" ? ExprType.Null : typed[0].Type, anyNull, typed);
                case "date_part":
                    if (Want(1, "date", "datetime", "datetimeoffset") is null)
                        return null;
                    if (typed[1].Type.Kind == "date" && ExprFunctions.TimeParts.Contains(part!))
                        return Misfit(typed[1].Pointer, "A date has no " + part + "; date_part() reads it from a timestamp only.");
                    return new TypedExpr(e, pointer, ExprType.Int32, anyNull, typed);
                case "date_of":
                    return Want(0, "datetime", "datetimeoffset") is null ? null : new TypedExpr(e, pointer, ExprType.Date, anyNull, typed);
                case "round":
                    return Want(0, "decimal", "int") is null ? null : new TypedExpr(e, pointer, ExprType.Decimal, anyNull, typed);
                case "json_type":
                    return Want(0, "json") is null ? null : new TypedExpr(e, pointer, ExprType.String, anyNull, typed);
                default: // json_length
                    return Want(0, "json") is null ? null : new TypedExpr(e, pointer, ExprType.Int32, true, typed);
            }
        }

        private TypedExpr? Case(Expr e, IReadOnlyList<ExprWhen> branches, string pointer, bool flat)
        {
            if (branches.Count == 0)
                return Malformed(pointer, "A case holds at least one branch.");
            var typed = new List<TypedExpr>();
            var failed = false;
            for (var i = 0; i < branches.Count; i++)
            {
                var at = flat ? pointer : pointer + "/case/" + i.ToString(CultureInfo.InvariantCulture);
                var when = Node(branches[i].When, flat ? at : at + "/when", flat);
                var then = Node(branches[i].Then, flat ? at : at + "/then", flat);
                if (when is null || then is null)
                {
                    failed = true;
                    continue;
                }

                if (!Fits(ExprType.Bool, when.Type))
                {
                    Misfit(when.Pointer, "A case condition is bool, not " + when.Type + ".");
                    failed = true;
                }

                typed.Add(when);
                typed.Add(then);
            }

            if (e.Else is { } otherwise)
            {
                if (Node(otherwise, flat ? pointer : pointer + "/else", flat) is { } typedElse)
                    typed.Add(typedElse);
                else
                    failed = true;
            }

            if (failed)
                return null;
            Adopt(typed, Enumerable.Range(0, typed.Count).Where(i => i % 2 == 1 || i == branches.Count * 2));
            var values = typed.Where((_, i) => i % 2 == 1 || i == branches.Count * 2).ToList();
            if (Common(values, pointer, "choose between") is not { } type)
                return null;
            return new TypedExpr(e, pointer, type, e.Else is null || values.Any(v => v.Nullable), typed);
        }

        private TypedExpr? Satisfies(Expr e, string constraintId, string pointer, bool flat)
        {
            if (scope.Constraint(constraintId) is not { } constraint)
            {
                Report("MQ3109", pointer, "'satisfies' names no constraint: '" + constraintId + "'.");
                return null;
            }

            var with = e.With ?? [];
            var bound = new Dictionary<string, (Expr Value, string Pointer)>(StringComparer.Ordinal);
            for (var i = 0; i < with.Count; i++)
            {
                var at = flat ? pointer : pointer + "/with/" + i.ToString(CultureInfo.InvariantCulture);
                if (!constraint.Parameters.Any(p => string.Equals(p.Id, with[i].Parameter, StringComparison.Ordinal)))
                {
                    Report("MQ3109", at, "Constraint '" + constraint.Name + "' has no parameter with the id '" + with[i].Parameter + "'.");
                    return null;
                }

                if (!bound.TryAdd(with[i].Parameter, (with[i].Value, flat ? at : at + "/value")))
                {
                    Report("MQ3109", at, "The parameter '" + with[i].Parameter + "' is bound twice.");
                    return null;
                }
            }

            var missing = constraint.Parameters.Where(p => !bound.ContainsKey(p.Id)).Select(p => p.Name).ToList();
            if (missing.Count > 0)
            {
                Report("MQ3109", pointer, "'satisfies " + constraint.Name + "' does not bind " + string.Join(", ", missing.Select(m => "'" + m + "'")) + ".");
                return null;
            }

            var args = new List<TypedExpr>();
            foreach (var parameter in constraint.Parameters)
            {
                var (value, at) = bound[parameter.Id];
                if (Node(value, at, flat) is not { } typed)
                    return null;
                if (!Fits(parameter.Type, typed.Type))
                {
                    Report("MQ3109", at, "The parameter '" + parameter.Name + "' of '" + constraint.Name + "' is " + parameter.Type + "; it is bound to " + typed.Type + ".");
                    return null;
                }

                args.Add(typed);
            }

            if (Citing.Contains(constraint.Id))
            {
                Report("MQ3109", pointer, "Constraint '" + constraint.Name + "' cites itself through 'satisfies'; constraints may not cite each other in a cycle.");
                return null;
            }

            if (!scope.AllowsNow && UsesNow(constraint, new HashSet<string>(StringComparer.Ordinal)))
            {
                Report("MQ3104", pointer, "Constraint '" + constraint.Name + "' reads now(); only a guard may cite it.");
                return null;
            }

            // The constraint's own condition, typed against its parameters; its own validation reports what is wrong inside it.
            var inner = new List<Diagnostic>();
            var bodyScope = scope.ForConstraint(constraint);
            var body = new Run(bodyScope, inner);
            body.Citing.UnionWith(Citing);
            body.Citing.Add(constraint.Id);
            var typedBody = body.WithinSize(constraint.Condition, bodyScope.Pointer) ? body.Node(constraint.Condition, bodyScope.Pointer, flat: false) : null;
            if (inner.FirstOrDefault(d => d.Rule == "MQ3109" && d.Message.Contains("cycle", StringComparison.Ordinal)) is not null)
            {
                Report("MQ3109", pointer, "Constraint '" + constraint.Name + "' takes part in a cycle of constraints citing each other.");
                return null;
            }

            // A condition that does not type, or is not bool, cannot be inlined, so no typed tree carries a satisfies without its body.
            if (typedBody is null || !Fits(ExprType.Bool, typedBody.Type))
            {
                Report("MQ3109", pointer, "Constraint '" + constraint.Name + "' does not type, so nothing can cite it; fix its condition first.");
                return null;
            }

            return new TypedExpr(e, pointer, ExprType.Bool, args.Any(a => a.Nullable) || typedBody.Nullable, args)
            {
                Constraint = constraint,
                Body = typedBody,
            };
        }

        private bool UsesNow(ExprConstraint constraint, HashSet<string> seen)
        {
            if (!seen.Add(constraint.Id))
                return false;
            var stack = new Stack<Expr>([constraint.Condition]);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node.Now == true)
                    return true;
                if (node.Satisfies is { } cited && scope.Constraint(cited) is { } next && UsesNow(next, seen))
                    return true;
                if (node.Text is { } text && ExprText.Parse(text, scope.ForConstraint(constraint)).Tree is { } parsed)
                    stack.Push(parsed);
                foreach (var child in Children(node))
                    stack.Push(child);
            }

            return false;
        }

        private TypedExpr? Text(string text, string pointer)
        {
            Report("MQ3108", pointer, "The expression is stored as text, so renames will not follow it; convert it with text-to-trees.", DiagnosticSeverity.Warning);
            var parse = ExprText.Parse(text, scope);
            if (parse.Tree is not { } tree)
            {
                var problem = parse.Problem!;
                problems.Add(problem with { JsonPointer = pointer });
                return null;
            }

            return WithinSize(tree, pointer) ? Node(tree, pointer, flat: true) : null;
        }

        /// <summary>
        /// Gives each <c>actor()</c> among the operands at <paramref name="indices"/> the type the others share (object-effects-design.md
        /// decision 5), as a null literal fits any type; it stays a string when none has a type it can take.
        /// </summary>
        private static void Adopt(List<TypedExpr> typed, IEnumerable<int> indices)
        {
            var at = indices.ToList();
            if (!at.Any(i => typed[i].Node.Actor == true))
                return;
            ExprType? common = null;
            foreach (var i in at)
            {
                if (typed[i].Node.Actor != true)
                    common = common is null ? typed[i].Type : Unify(common, typed[i].Type) ?? common;
            }

            if (common is null || common.Kind == "null" || !common.IsAvailable)
                return;
            foreach (var i in at)
            {
                if (typed[i].Node.Actor == true)
                    typed[i] = typed[i] with { Type = common };
            }
        }

        /// <summary>
        /// An <c>in_state</c> (object-effects-design.md decisions 7 to 9): states of one lifecycle that an object can be in, read on the
        /// subject or an object a to-one path of <c>of</c> reaches, of a lifecycle that can govern it (MQ3116); at guard and set sites only
        /// (MQ3104). It reads null when the object or its state is missing, except on the running chart.
        /// </summary>
        private TypedExpr? InState(Expr e, IReadOnlyList<string> ids, IReadOnlyList<string>? of, string pointer)
        {
            if (ids.Count == 0)
                return Malformed(pointer, "'inState' lists at least one state.");
            if (of is { Count: 0 })
                return Malformed(pointer, "'of' lists at least one relation end; leave it out to read the object itself.");
            if (!scope.AllowsActor || scope.Lifecycles is not { } lifecycles)
            {
                Report("MQ3104", pointer, "in_state reads a lifecycle's state; only a guard or a value an action sets may read it.");
                return null;
            }

            var states = new List<ExprState>(ids.Count);
            foreach (var id in ids)
            {
                if (lifecycles.State(id) is not { } state)
                {
                    Report("MQ3116", pointer, "in_state names '" + id + "', which is no state of a lifecycle.");
                    return null;
                }

                if (!state.IsState)
                {
                    Report("MQ3116", pointer, "in_state names " + StateText(state) + ", a history or choice state, which no object is in; name a state.");
                    return null;
                }

                if (states.Count > 0 && !string.Equals(states[0].Lifecycle, state.Lifecycle, StringComparison.Ordinal))
                {
                    Report("MQ3116", pointer, "in_state names states of two lifecycles, " + states[0].LifecycleName + " and " + state.LifecycleName
                        + "; join one call per lifecycle with 'or'.");
                    return null;
                }

                if (states.Any(s => string.Equals(s.Id, state.Id, StringComparison.Ordinal)))
                    return Malformed(pointer, "in_state names " + StateText(state) + " twice.");
                states.Add(state);
            }

            var container = scope.Root;
            var through = new List<ExprNavigation>();
            foreach (var end in of ?? [])
            {
                if (container.Navigation(end) is not { } navigation || (through.Count == 0 && navigation.Prefix is null))
                {
                    Report("MQ3116", pointer, "in_state's object is no path every subject holds: nothing it reaches has the relation end '" + end + "'.");
                    return null;
                }

                if (!navigation.ToOne)
                {
                    Report("MQ3116", pointer, "in_state's object follows '" + navigation.Name + "', which leads to many records; it follows to-one ends only.");
                    return null;
                }

                through.Add(navigation);
                container = navigation.Target;
            }

            var lifecycle = states[0].Lifecycle;
            var running = through.Count == 0 && string.Equals(lifecycle, lifecycles.Own, StringComparison.Ordinal);
            if (!running && lifecycles.CannotGovern(lifecycle, through) is { } why)
            {
                Report("MQ3116", pointer, why);
                return null;
            }

            var inside = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var state in states)
            {
                foreach (var id in state.Inside)
                {
                    if (seen.Add(id))
                        inside.Add(id);
                }
            }

            return new TypedExpr(e, pointer, ExprType.Bool, !running, [])
            {
                Lifecycle = lifecycle,
                States = states,
                Inside = inside,
                Through = through,
                Running = running,
            };
        }

        private static string StateText(ExprState state) => state.LifecycleName + "." + string.Join('.', state.Path);

        private TypedExpr? Misfit(string pointer, string message)
        {
            Report("MQ3103", pointer, message);
            return null;
        }

        private static string Arity(int min, int max) =>
            min == max ? min.ToString(CultureInfo.InvariantCulture)
            : max == int.MaxValue ? min.ToString(CultureInfo.InvariantCulture) + " or more"
            : min.ToString(CultureInfo.InvariantCulture) + " to " + max.ToString(CultureInfo.InvariantCulture);
    }
}
