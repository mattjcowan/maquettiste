using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Expressions;

/// <summary>
/// Lowers a typed tree to PostgreSQL (next-phase-3-design.md section 3.1, lowering) through the query tree, which
/// <see cref="QuerySql"/> writes in row mode: column names unqualified and <c>COLLATE "C"</c> after each string operand of a
/// comparison, <c>in</c>, <c>distinct</c> or <c>matches</c>. A bool value used as a condition becomes <c>x = true</c>, a condition used
/// as a value a <c>pred</c> node. Integers compute as <c>numeric</c>, <c>/</c> is
/// <c>round((a + 0.000000000000000000) / NULLIF(b, 0), 16)</c> (a double divides as a double), a fixed-length column reads as
/// <c>rtrim(col)</c>, a <c>datetime</c> meets a <c>datetimeoffset</c> as UTC, and an enum member or code is a literal in its
/// column's stored form.
/// </summary>
internal static class ExprSql
{
    /// <summary>The zero added to a dividend, so that it divides as a decimal of at least 18 digits.</summary>
    private const string DividendZero = "0.000000000000000000";

    /// <summary>A <c>datetime</c> read as UTC, where it meets a <c>datetimeoffset</c>.</summary>
    private const string AtUtc = "({0} AT TIME ZONE 'UTC')";

    public static string? Lower(TypedExpr e, ExprColumns columns, ExprSiteKind site, ICollection<Diagnostic> problems)
    {
        var writer = new Writer(columns, problems);
        string text;
        switch (site)
        {
            case ExprSiteKind.Check:
            {
                var condition = writer.Pred(e, null);
                text = QuerySql.RowText(condition, columns.Quoting);
                break;
            }

            case ExprSiteKind.Value:
                text = QuerySql.RowText(writer.Value(e, null, null), columns.Quoting);
                break;
            default:
            {
                var guard = Node("call", call: "coalesce", args: [PredValue(writer.Pred(e, null)), Literal(false, "bool")]);
                text = QuerySql.RowText(guard, columns.Quoting);
                break;
            }
        }

        return writer.Failed ? null : text;
    }

    private static RQueryExpression Node(string node, string? call = null, string? op = null, IReadOnlyList<RQueryExpression>? args = null,
        string? type = null) =>
        new() { Node = node, Call = call, Op = op, Args = args ?? [], Type = type };

    private static RQueryExpression Literal(object value, string? type) => new() { Node = "value", Value = value, Type = type, Nullable = false };

    private static RQueryExpression Template(string template, IReadOnlyList<RQueryExpression> args, string? type = null) =>
        new() { Node = "call", Call = "template", Template = template, Args = args, Type = type };

    private static RQueryExpression PredValue(RQueryPredicate predicate) => new() { Node = "pred", Pred = predicate, Type = "bool" };

    private static RQueryPredicate Compare(string op, RQueryExpression left, RQueryExpression? right = null, IReadOnlyList<RQueryExpression>? values = null) =>
        new() { Node = "compare", Op = op, Left = left, Right = right, Values = values ?? [] };

    /// <summary>A constraint parameter's binding: the argument, the bindings it is lowered with, and the parameter's type.</summary>
    private sealed record Bound(TypedExpr Arg, IReadOnlyDictionary<string, Bound>? Env, ExprType Type);

    private sealed class Writer(ExprColumns columns, ICollection<Diagnostic> problems)
    {
        public bool Failed { get; private set; }

        private void Report(string rule, string pointer, string message)
        {
            Failed = true;
            problems.Add(new Diagnostic(rule, DiagnosticSeverity.Error, message, columns.ElementId, columns.FilePath, pointer, null, null));
        }

        // Conditions -----------------------------------------------------------------------------------------------------

        public RQueryPredicate Pred(TypedExpr t, IReadOnlyDictionary<string, Bound>? env)
        {
            var e = t.Node;
            var args = t.Args;
            if (e.Satisfies is not null)
                return Inline(t, env, (body, bound) => Pred(body, bound));
            if (e.Call == "matches")
                return new RQueryPredicate { Node = "match", Left = Value(args[0], env, null), Right = Literal(e.Args![1].Value!.Value.GetString()!, null) };
            switch (e.Op)
            {
                case "and":
                    return new RQueryPredicate { Node = "and", And = [.. args.Select(a => Pred(a, env))] };
                case "or":
                    return new RQueryPredicate { Node = "or", Or = [.. args.Select(a => Pred(a, env))] };
                case "not":
                    return new RQueryPredicate { Node = "not", Not = Pred(args[0], env) };
                case "implies":
                    return new RQueryPredicate { Node = "or", Or = [new RQueryPredicate { Node = "not", Not = Pred(args[0], env) }, Pred(args[1], env)] };
                case "isNull" or "isNotNull":
                    return Compare(e.Op, Value(args[0], env, null));
                case "eq" or "ne" or "lt" or "le" or "gt" or "ge":
                {
                    var (left, rest) = Operands(args, env);
                    return Compare(e.Op, left, rest[0]);
                }

                case "distinct" or "notDistinct":
                {
                    var (left, rest) = Operands(args, env);
                    return new RQueryPredicate { Node = "distinct", Op = e.Op, Left = left, Right = rest[0] };
                }

                case "between" or "in" or "notIn":
                {
                    var (left, rest) = Operands(args, env);
                    return Compare(e.Op, left, values: rest);
                }

                default:
                    // A bool value used as a condition: x = true has the same three values.
                    return Compare("eq", Value(t, env, null), Literal(true, "bool"));
            }
        }

        private (RQueryExpression Left, List<RQueryExpression> Others) Operands(IReadOnlyList<TypedExpr> args, IReadOnlyDictionary<string, Bound>? env)
        {
            var common = Common(args);
            var form = Shared(args, env);
            var lowered = args.Select(a => Convert(Value(a, env, form), a.Type, common)).ToList();
            return (lowered[0], lowered.Skip(1).ToList());
        }

        // Values ---------------------------------------------------------------------------------------------------------

        public RQueryExpression Value(TypedExpr t, IReadOnlyDictionary<string, Bound>? env, ExprStoredForm? form)
        {
            var e = t.Node;
            RQueryExpression node;
            if (e.Value is { } value)
                node = LiteralOf(e.Type, value);
            else if (e.Null is not null)
                node = new RQueryExpression { Node = "null" };
            else if (e.Path is not null)
                return Path(t, env, form);
            else if (e.Ref is { } id)
                node = RefLiteral(t, id, form ?? ExprStoredForm.Text);
            else if (e.Member is not null)
            {
                Report("MQ3102", t.Pointer, "A 'member' node reads a declared rule's object, which has no SQL form.");
                node = new RQueryExpression { Node = "null" };
            }
            else if (e.Now is not null)
                node = Node("call", call: "now");
            else if (e.Satisfies is not null || e.Call == "matches" || IsCondition(e.Op))
                return PredValue(Pred(t, env));
            else if (e.Op is { } op)
                node = Operator(t, op, env, form);
            else if (e.Call is { } call)
                node = Function(t, call, env);
            else
                node = Case(t, env, form);
            node.Type ??= Keyword(t.Type, form ?? FormOf(t, env));
            return node;
        }

        private static bool IsCondition(string? op) => op is "and" or "or" or "not" or "implies" or "eq" or "ne" or "lt" or "le" or "gt" or "ge"
            or "isNull" or "isNotNull" or "distinct" or "notDistinct" or "between" or "in" or "notIn";

        /// <summary>The keyword a value node carries; <c>string</c> makes the row writer collate it.</summary>
        private static string? Keyword(ExprType type, ExprStoredForm? form) => type.Kind switch
        {
            "string" => "string",
            "enum" or "code" => form switch
            {
                ExprStoredForm.Integer => "int64",
                ExprStoredForm.Uuid => "uuid",
                _ => "string",
            },
            "int" => "int" + type.Bits.ToString(CultureInfo.InvariantCulture),
            "null" => null,
            _ => type.Kind,
        };

        /// <summary>A literal; a decimal without a point is <c>NUMERIC 'n'</c> (scale 0), since PostgreSQL reads bare digits as an integer.</summary>
        private static RQueryExpression LiteralOf(string? type, JsonElement value)
        {
            string Text() => value.GetString()!;
            return type switch
            {
                "decimal" when !Text().Contains('.', StringComparison.Ordinal) => Typed("NUMERIC", Text()),
                "decimal" => new RQueryExpression { Node = "value", LiteralText = Text(), Type = "decimal", Nullable = false },
                "double" => Typed("float8", Text()),
                "date" => Typed("DATE", Text()),
                "time" => Typed("TIME", Text()),
                "datetime" => Typed("TIMESTAMP", Text()),
                "datetimeoffset" => Typed("TIMESTAMPTZ", Text()),
                "uuid" => Typed("UUID", Text()),
                _ => value.ValueKind switch
                {
                    JsonValueKind.String => Literal(Text(), "string"),
                    JsonValueKind.True => Literal(true, "bool"),
                    JsonValueKind.False => Literal(false, "bool"),
                    _ => Literal(value.GetInt64(), null),
                },
            };
        }

        private static RQueryExpression Typed(string sqlType, string text) =>
            new() { Node = "typed", NativeType = sqlType, LiteralText = text, Nullable = false };

        private RQueryExpression Path(TypedExpr t, IReadOnlyDictionary<string, Bound>? env, ExprStoredForm? form)
        {
            var member = t.Member!;
            if (t.Through.Count == 0 && env is not null && env.TryGetValue(member.Id, out var bound))
                return Bind(Value(bound.Arg, bound.Env, form), bound.Arg, bound.Type);
            var column = columns.ColumnOf(t);
            RQueryExpression node;
            if (column.Sql is { } sql)
            {
                node = new RQueryExpression { Node = "sql", Sql = new Dictionary<string, string>(StringComparer.Ordinal) { ["postgresql"] = sql }, SqlText = sql };
            }
            else
            {
                if (t.Through.Count > 0)
                    Report("MQ3102", t.Pointer, "The path reads through '" + t.Through[0].Name + "'; the site gives no SQL that follows the relation.");
                node = new RQueryExpression { Node = "column", ColumnName = column.Name };
            }

            var stored = t.Type.Kind is "enum" or "code" ? column.Form : (ExprStoredForm?)null;
            var text = t.Type.Kind == "string" || stored == ExprStoredForm.Text;
            if (column.FixedLength && text)
                node = Node("call", call: "rtrim", args: [node]);
            node.Type = Keyword(t.Type, stored);
            return node;
        }

        /// <summary>
        /// An enum member or code as a literal in its stored form. A code that is not an integer where the column stores integers (a
        /// reference type's text code mapped to an integer column) is MQ3103 and fails the lowering, so no conversion can throw.
        /// </summary>
        private RQueryExpression RefLiteral(TypedExpr t, string id, ExprStoredForm form)
        {
            var literal = columns.Ref?.Invoke(id, form);
            if (literal is null)
            {
                Report("MQ3102", t.Pointer, "No enum member or reference-type code has the id '" + id + "'.");
                return new RQueryExpression { Node = "null" };
            }

            var text = System.Convert.ToString(literal, CultureInfo.InvariantCulture)!;
            switch (form)
            {
                case ExprStoredForm.Integer when literal is long or int:
                    return Literal(System.Convert.ToInt64(literal, CultureInfo.InvariantCulture), "int64");
                case ExprStoredForm.Integer:
                    Report("MQ3103", t.Pointer, "The code '" + text + "' is not an integer, but the column it meets stores its codes as integers: "
                        + "the code's type does not match the column.");
                    return new RQueryExpression { Node = "null" };
                case ExprStoredForm.Uuid:
                    return Typed("UUID", text);
                default:
                    return Literal(text, "string");
            }
        }

        private ExprStoredForm? FormOf(TypedExpr t, IReadOnlyDictionary<string, Bound>? env)
        {
            if (t.Type.Kind is not ("enum" or "code"))
                return null;
            var e = t.Node;
            if (e.Path is not null)
            {
                if (t.Through.Count == 0 && env is not null && env.TryGetValue(t.Member!.Id, out var bound))
                    return FormOf(bound.Arg, bound.Env);
                return columns.ColumnOf(t).Form;
            }

            if (e.Op == "coalesce" || e.Case is not null)
                return t.Args.Select(a => FormOf(a, env)).FirstOrDefault(f => f is not null);
            return null;
        }

        private ExprStoredForm? Shared(IEnumerable<TypedExpr> operands, IReadOnlyDictionary<string, Bound>? env) =>
            operands.Select(a => FormOf(a, env)).FirstOrDefault(f => f is not null);

        private static ExprType Common(IEnumerable<TypedExpr> operands) =>
            operands.Select(a => a.Type).Aggregate((a, b) => ExprTyper.Unify(a, b) ?? a);

        /// <summary>A <c>datetime</c> where a <c>datetimeoffset</c> is wanted reads as UTC.</summary>
        private static RQueryExpression Convert(RQueryExpression node, ExprType from, ExprType to) =>
            from.Kind == "datetime" && to.Kind == "datetimeoffset" ? Template(AtUtc, [node], "datetimeoffset") : node;

        /// <summary>
        /// An argument in place of its constraint parameter: an <c>int</c> bound to a <c>decimal</c> or <c>double</c> parameter becomes
        /// one, since the condition's arithmetic sees the parameter's type and leaves out the cast that keeps integers from overflowing.
        /// </summary>
        private static RQueryExpression Bind(RQueryExpression node, TypedExpr arg, ExprType to) => (arg.Type.Kind, to.Kind) switch
        {
            ("int", "decimal") when !IsNumeric(arg) => Template("CAST({0} AS numeric)", [node], "decimal"),
            ("int", "double") => Template("CAST({0} AS float8)", [node], "double"),
            _ => Convert(node, arg.Type, to),
        };

        /// <summary>Whether a lowered int is <c>numeric</c> already: the result of integer arithmetic.</summary>
        private static bool IsNumeric(TypedExpr t) => t.Node.Op is "add" or "sub" or "mul" or "neg";

        private RQueryExpression Operator(TypedExpr t, string op, IReadOnlyDictionary<string, Bound>? env, ExprStoredForm? form)
        {
            var args = t.Args;
            switch (op)
            {
                case "concat":
                    return Node("op", op: "concat", args: [.. args.Select(a => Value(a, env, null))], type: "string");
                case "coalesce":
                {
                    var shared = form ?? Shared(args, env);
                    return Node("call", call: "coalesce", args: [.. args.Select(a => Convert(Value(a, env, shared), a.Type, t.Type))]);
                }

                case "neg":
                {
                    var operand = Value(args[0], env, null);
                    if (args[0].Type.Kind == "int" && !IsNumeric(args[0]))
                        operand = Template("CAST({0} AS numeric)", [operand]);
                    return Node("op", op: "-", args: [operand]);
                }
            }

            var common = Common(args);
            var left = Value(args[0], env, null);
            var right = Value(args[1], env, null);
            if (op == "div")
            {
                var divisor = Node("call", call: "NULLIF", args: [right, Literal(0L, null)]);
                if (common.Kind == "double")
                    return Node("op", op: "/", args: [left, divisor]);
                var dividend = Node("op", op: "+", args: [left, new RQueryExpression { Node = "value", LiteralText = DividendZero, Nullable = false }]);
                return Node("call", call: "round", args: [Node("op", op: "/", args: [dividend, divisor]), Literal(16L, null)]);
            }

            // Integers compute as numeric, so nothing overflows: one numeric operand makes the operation numeric.
            if (common.Kind == "int" && !IsNumeric(args[0]) && !IsNumeric(args[1]))
                left = Template("CAST({0} AS numeric)", [left]);
            return Node("op", op: op switch { "add" => "+", "sub" => "-", _ => "*" }, args: [left, right]);
        }

        private RQueryExpression Function(TypedExpr t, string call, IReadOnlyDictionary<string, Bound>? env)
        {
            var info = ExprFunctions.Functions[call];
            var args = t.Args.Select(a => Value(a, env, null)).ToList();
            var kind = call == "date_part" ? t.Args[1].Type.Kind : t.Args[0].Type.Kind;
            var template = info.PostgreSqlByKind.TryGetValue(kind, out var specific) ? specific : info.PostgreSql;
            return Template(template, args);
        }

        private RQueryExpression Case(TypedExpr t, IReadOnlyDictionary<string, Bound>? env, ExprStoredForm? form)
        {
            var branches = t.Node.Case!.Count;
            var values = t.Args.Where((_, i) => i % 2 == 1 || i == branches * 2).ToList();
            var shared = form ?? Shared(values, env);
            var whens = new List<RQueryWhen>();
            for (var i = 0; i < branches; i++)
            {
                var then = t.Args[2 * i + 1];
                whens.Add(new RQueryWhen { When = Pred(t.Args[2 * i], env), Then = Convert(Value(then, env, shared), then.Type, t.Type) });
            }

            var otherwise = t.Args.Count > branches * 2 ? t.Args[^1] : null;
            return new RQueryExpression
            {
                Node = "case",
                Case = whens,
                Else = otherwise is null ? null : Convert(Value(otherwise, env, shared), otherwise.Type, t.Type),
            };
        }

        private T Inline<T>(TypedExpr t, IReadOnlyDictionary<string, Bound>? env, Func<TypedExpr, IReadOnlyDictionary<string, Bound>, T> lower)
        {
            var constraint = t.Constraint!;
            var body = t.Body ?? throw new InvalidOperationException("Constraint '" + constraint.Name + "' does not type, so it cannot be inlined.");
            var bound = new Dictionary<string, Bound>(StringComparer.Ordinal);
            for (var i = 0; i < constraint.Parameters.Count; i++)
                bound[constraint.Parameters[i].Id] = new Bound(t.Args[i], env, constraint.Parameters[i].Type);
            return lower(body, bound);
        }
    }
}
