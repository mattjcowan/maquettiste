using System.Text.Encodings.Web;
using System.Text.Json;

namespace Maquettiste.Engine.Expressions;

/// <summary>
/// Lowers a typed tree to one JavaScript arrow function over a row (next-phase-3-design.md section 3.1, lowering): values are read by
/// current name and wrapped by type (<c>mq.dec</c>, <c>mq.date</c>, <c>mq.ts</c>…), every operator and function is a call into the
/// runtime, which implements SQL's semantics, and a <c>satisfies</c> inlines its constraint's condition with the arguments in place
/// of the parameters. The text is a pure function of the tree and the site's mapping, so it is byte-identical on every run.
/// </summary>
internal static class ExprJs
{
    private static readonly JsonSerializerOptions Strings = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Lower(TypedExpr e, ExprSiteKind site, ExprColumns columns)
    {
        var body = new Writer(columns).Value(e, null, null);
        return site switch
        {
            ExprSiteKind.Check => "(row) => mq.passes(" + body + ")",
            ExprSiteKind.Value => "(row) => mq.out(" + body + ", " + Str(e.Type.Kind) + ")",
            _ => "(row) => mq.holds(" + body + ")",
        };
    }

    internal static string Str(string text) => JsonSerializer.Serialize(text, Strings);

    /// <summary>A constraint parameter's binding: the argument, the bindings it is lowered with, and the parameter's type.</summary>
    private sealed record Bound(TypedExpr Arg, IReadOnlyDictionary<string, Bound>? Env, ExprType Type);

    private sealed class Writer(ExprColumns columns)
    {
        public string Value(TypedExpr t, IReadOnlyDictionary<string, Bound>? env, ExprStoredForm? form)
        {
            var e = t.Node;
            if (e.Value is { } value)
                return Literal(e.Type, value);
            if (e.Null is not null)
                return "null";
            if (e.Path is not null)
                return Path(t, env, form);
            if (e.Ref is { } id)
                return RefLiteral(id, form ?? ExprStoredForm.Text);
            if (e.Member is { } member)
                return Read(t, "mq.at(row" + string.Concat(member.Split('.').Select(k => ", " + Str(k))) + ")", ColumnOf(t));
            if (e.Now is not null)
                return "mq.now()";
            if (e.Op is { } op)
                return Operator(t, op, env, form);
            if (e.Call is { } call)
                return Function(t, call, env);
            if (e.Case is not null)
                return Case(t, env, form);
            if (e.Satisfies is not null)
                return Satisfies(t, env);
            throw new InvalidOperationException("A typed node has no shape the JavaScript lowering knows.");
        }

        private static string Literal(string? type, JsonElement value) => type switch
        {
            "decimal" => "mq.dec(" + Str(value.GetString()!) + ")",
            "double" => "mq.dbl(" + Str(value.GetString()!) + ")",
            "date" => "mq.date(" + Str(value.GetString()!) + ")",
            "time" => "mq.time(" + Str(value.GetString()!) + ")",
            "datetime" => "mq.ts(" + Str(value.GetString()!) + ")",
            "datetimeoffset" => "mq.tsz(" + Str(value.GetString()!) + ")",
            "uuid" => "mq.uuid(" + Str(value.GetString()!) + ")",
            _ => value.ValueKind switch
            {
                JsonValueKind.String => Str(value.GetString()!),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => "mq.dec(" + Str(value.GetRawText()) + ")",
            },
        };

        private ExprColumn ColumnOf(TypedExpr t) =>
            t.Node.Member is not null ? new ExprColumn(t.Member!.Name) { FixedLength = t.Member.FixedLength, Scale = t.Member.Scale } : columns.ColumnOf(t);

        private string Path(TypedExpr t, IReadOnlyDictionary<string, Bound>? env, ExprStoredForm? form)
        {
            var member = t.Member!;
            if (t.Through.Count == 0 && env is not null && env.TryGetValue(member.Id, out var bound))
                return Convert(Value(bound.Arg, bound.Env, form), bound.Arg.Type, bound.Type);
            var column = columns.ColumnOf(t);
            IEnumerable<string> keys = columns.Physical
                ? [column.Name]
                : t.Through.SelectMany(n => Segments(n.Prefix, n.Name)).Concat(Segments(member.Prefix, member.Name));
            return Read(t, "mq.at(row" + string.Concat(keys.Select(k => ", " + Str(k))) + ")", column);
        }

        private static IEnumerable<string> Segments(string? prefix, string name) => prefix is null ? [name] : [.. prefix.Split('.'), name];

        private static string Read(TypedExpr t, string access, ExprColumn column)
        {
            var text = column.FixedLength ? "mq.fixed(" : "mq.str(";
            return t.Type.Kind switch
            {
                "int" => "mq.dec(" + access + ")",
                "decimal" => column.Scale is { } scale ? "mq.dec(" + access + ", " + scale.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" : "mq.dec(" + access + ")",
                "double" => "mq.dbl(" + access + ")",
                "bool" => "mq.bool(" + access + ")",
                "date" => "mq.date(" + access + ")",
                "time" => "mq.time(" + access + ")",
                "datetime" => "mq.ts(" + access + ")",
                "datetimeoffset" => "mq.tsz(" + access + ")",
                "uuid" => "mq.uuid(" + access + ")",
                "json" => "mq.json(" + access + ")",
                "enum" or "code" => column.Form switch
                {
                    ExprStoredForm.Integer => "mq.dec(" + access + ")",
                    ExprStoredForm.Uuid => "mq.uuid(" + access + ")",
                    _ => text + access + ")",
                },
                _ => text + access + ")",
            };
        }

        private string RefLiteral(string id, ExprStoredForm form)
        {
            var literal = columns.RefLiteral(id, form);
            return form switch
            {
                ExprStoredForm.Integer => "mq.dec(" + Str(System.Convert.ToString(literal, System.Globalization.CultureInfo.InvariantCulture)!) + ")",
                ExprStoredForm.Uuid => "mq.uuid(" + Str(literal.ToString()!) + ")",
                _ => Str(literal.ToString()!),
            };
        }

        /// <summary>The stored form an enum or code value has: its column's, through a parameter, a coalesce or a case.</summary>
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

        private static string Convert(string js, ExprType from, ExprType to) =>
            to.Kind == "double" && from.Kind == "int" ? "mq.toDouble(" + js + ")" : js;

        private string Operands(IReadOnlyList<TypedExpr> args, IReadOnlyDictionary<string, Bound>? env, ExprType to, ExprStoredForm? form) =>
            string.Join(", ", args.Select(a => Convert(Value(a, env, form), a.Type, to)));

        private string Operator(TypedExpr t, string op, IReadOnlyDictionary<string, Bound>? env, ExprStoredForm? form)
        {
            var fn = ExprFunctions.Operators[op].JavaScript;
            var args = t.Args;
            switch (op)
            {
                case "in" or "notIn":
                {
                    var common = Common(args);
                    var shared = Shared(args, env);
                    return fn + "(" + Convert(Value(args[0], env, shared), args[0].Type, common) + ", [" + Operands([.. args.Skip(1)], env, common, shared) + "])";
                }

                case "eq" or "ne" or "lt" or "le" or "gt" or "ge" or "distinct" or "notDistinct" or "between" or "add" or "sub" or "mul" or "div":
                {
                    var common = Common(args);
                    return fn + "(" + Operands(args, env, common, Shared(args, env)) + ")";
                }

                case "coalesce":
                    return fn + "(" + Operands(args, env, t.Type, form ?? Shared(args, env)) + ")";
                default:
                    return fn + "(" + string.Join(", ", args.Select(a => Value(a, env, null))) + ")";
            }
        }

        private string Function(TypedExpr t, string call, IReadOnlyDictionary<string, Bound>? env)
        {
            var fn = ExprFunctions.Functions[call].JavaScript;
            return call switch
            {
                "matches" => fn + "(" + Value(t.Args[0], env, null) + ", " + Str(t.Node.Args![1].Value!.Value.GetString()!) + ")",
                "round" => fn + "(" + Value(t.Args[0], env, null) + ", " + t.Node.Args![1].Value!.Value.GetRawText() + ")",
                _ => fn + "(" + string.Join(", ", t.Args.Select(a => Value(a, env, null))) + ")",
            };
        }

        private string Case(TypedExpr t, IReadOnlyDictionary<string, Bound>? env, ExprStoredForm? form)
        {
            var branches = t.Node.Case!.Count;
            var values = t.Args.Where((_, i) => i % 2 == 1 || i == branches * 2).ToList();
            var shared = form ?? Shared(values, env);
            var parts = new List<string>();
            for (var i = 0; i < branches; i++)
            {
                parts.Add(Value(t.Args[2 * i], env, null));
                parts.Add(Convert(Value(t.Args[2 * i + 1], env, shared), t.Args[2 * i + 1].Type, t.Type));
            }

            parts.Add(t.Args.Count > branches * 2 ? Convert(Value(t.Args[^1], env, shared), t.Args[^1].Type, t.Type) : "null");
            return "mq.case(" + string.Join(", ", parts) + ")";
        }

        private string Satisfies(TypedExpr t, IReadOnlyDictionary<string, Bound>? env)
        {
            var constraint = t.Constraint!;
            var body = t.Body ?? throw new InvalidOperationException("Constraint '" + constraint.Name + "' does not type, so it cannot be inlined.");
            var bound = new Dictionary<string, Bound>(StringComparer.Ordinal);
            for (var i = 0; i < constraint.Parameters.Count; i++)
                bound[constraint.Parameters[i].Id] = new Bound(t.Args[i], env, constraint.Parameters[i].Type);
            return Value(body, bound, null);
        }
    }
}
