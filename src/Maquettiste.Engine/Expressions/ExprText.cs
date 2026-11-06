using System.Globalization;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Expressions;

/// <summary>What <see cref="ExprText.Parse"/> gives: the tree, or the problem and where in the text it is.</summary>
/// <param name="Tree">The tree, or <see langword="null"/> when the text does not parse.</param>
/// <param name="Problem">MQ3101 for text that does not parse, MQ3102 for a name the scope does not know, MQ3109 for a <c>satisfies</c> that
/// names no constraint or parameter, MQ3113 for text that nests too deep; <see langword="null"/> with a tree.</param>
/// <param name="Position">The zero-based offset in the text of the problem, or -1.</param>
public sealed record ExprParse(Expr? Tree, Diagnostic? Problem, int Position = -1)
{
    /// <summary>Whether the text parsed.</summary>
    public bool Succeeded => Tree is not null;
}

/// <summary>
/// The text form of expressions (next-phase-3-design.md section 3.1): it reads like SQL. <see cref="Parse"/> turns text into the tree
/// of ids, resolving names in the site's scope; <see cref="Print"/> writes a tree from current names with lower-case keywords, the
/// fewest parentheses and one space around operators, so printing a parsed text twice gives the same text.
/// </summary>
public static class ExprText
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "not", "implies", "is", "null", "true", "false", "between", "in", "distinct", "from", "case", "when", "then", "else",
        "end", "satisfies",
    };

    /// <summary>The word an <c>in_state</c> writes for the object itself, under which a guard reads it.</summary>
    private const string SubjectWord = "subject";

    private static readonly HashSet<string> TypedLiterals = new(StringComparer.OrdinalIgnoreCase)
    {
        "decimal", "double", "date", "time", "datetime", "datetimeoffset", "uuid",
    };

    private static readonly Dictionary<string, string> Comparisons = new(StringComparer.Ordinal)
    {
        ["="] = "eq", ["<>"] = "ne", ["!="] = "ne", ["<"] = "lt", ["<="] = "le", [">"] = "gt", [">="] = "ge",
    };

    /// <summary>Parses text in a scope.</summary>
    /// <param name="text">The text.</param>
    /// <param name="scope">The site's scope: its names, enums, reference types and constraints.</param>
    /// <returns>The tree, or the problem.</returns>
    public static ExprParse Parse(string text, ExprScope scope)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(scope);
        try
        {
            var parser = new Parser(text, scope);
            return new ExprParse(parser.Run(), null);
        }
        catch (ParseException e)
        {
            var message = e.Message + " (at position " + (e.Position + 1).ToString(CultureInfo.InvariantCulture) + " of the text)";
            return new ExprParse(null, new Diagnostic(e.Rule, DiagnosticSeverity.Error, message, scope.ElementId, scope.FilePath, scope.Pointer,
                null, null), e.Position);
        }
    }

    /// <summary>Prints a tree from the scope's current names.</summary>
    /// <param name="e">The tree.</param>
    /// <param name="scope">The site's scope.</param>
    /// <returns>The text.</returns>
    public static string Print(Expr e, ExprScope scope)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(scope);
        if (e.Text is { } text)
            return text;
        var sb = new StringBuilder();
        new Printer(sb, scope).Write(e, 1);
        return sb.ToString();
    }

    /// <summary>Whether a literal's text is well formed for its <c>type</c>; the typer and the parser share it.</summary>
    /// <param name="type">The literal's <c>type</c>.</param>
    /// <param name="text">The literal's text.</param>
    /// <returns><see langword="true"/> when the text is a value of the type.</returns>
    public static bool IsLiteral(string type, string text)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(text);
        return type switch
        {
            "decimal" => IsDecimal(text),
            "double" => double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out var d) && double.IsFinite(d),
            "date" => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "time" => TimeOnly.TryParseExact(text, ["HH:mm", "HH:mm:ss", "HH:mm:ss.FFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "datetime" => System.DateTime.TryParseExact(text, ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFF"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "datetimeoffset" => (text.EndsWith('Z') || (text.Length > 6 && text[^6] is '+' or '-'))
                && System.DateTimeOffset.TryParseExact(text,
                    ["yyyy-MM-ddTHH:mmK", "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss.FFFFFFK"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "uuid" => text.Length == 36 && Guid.TryParseExact(text, "D", out _),
            _ => false,
        };
    }

    private static bool IsDecimal(string text)
    {
        var i = text.Length > 0 && text[0] == '-' ? 1 : 0;
        var digits = 0;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
            digits++;
        }

        if (digits == 0)
            return false;
        if (i == text.Length)
            return true;
        if (text[i] != '.')
            return false;
        i++;
        var fraction = 0;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
            fraction++;
        }

        return fraction > 0 && i == text.Length;
    }

    /// <summary>Whether a name can be written without double quotes.</summary>
    internal static bool IsPlainName(string name)
    {
        if (name.Length == 0 || Reserved.Contains(name) || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
            return false;
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                return false;
        }

        return true;
    }

    private static string Quote(string name) => IsPlainName(name) ? name : "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string QuoteDotted(string name) => string.Join('.', name.Split('.').Select(Quote));

    private enum TokenKind { End, Name, QuotedName, Number, String, Symbol }

    private readonly record struct Token(TokenKind Kind, string Text, int Position)
    {
        public bool Is(string keyword) => Kind == TokenKind.Name && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);

        public bool IsSymbol(string symbol) => Kind == TokenKind.Symbol && string.Equals(Text, symbol, StringComparison.Ordinal);
    }

    private sealed class ParseException(string rule, string message, int position) : Exception(message)
    {
        public string Rule { get; } = rule;

        public int Position { get; } = position;
    }

    private sealed class Parser
    {
        private const int MaxNesting = 256;
        private readonly List<Token> _tokens;
        private readonly ExprScope _scope;
        private int _at;
        private int _depth;

        public Parser(string text, ExprScope scope)
        {
            _scope = scope;
            _tokens = Tokenize(text);
        }

        private Token Current => _tokens[_at];

        public Expr Run()
        {
            var e = Implies();
            if (Current.Kind != TokenKind.End)
                throw Unexpected();
            return e;
        }

        private Expr Implies()
        {
            Enter();
            var left = Or();
            if (Current.Is("implies"))
            {
                _at++;
                left = Op("implies", left, Implies());
            }

            _depth--;
            return left;
        }

        private Expr Or() => Chain("or", And);

        private Expr And() => Chain("and", Not);

        private Expr Chain(string keyword, Func<Expr> operand)
        {
            var first = operand();
            if (!Current.Is(keyword))
                return first;
            var args = new List<Expr> { first };
            while (Current.Is(keyword))
            {
                _at++;
                args.Add(operand());
            }

            return new Expr { Op = keyword, Args = args };
        }

        private Expr Not()
        {
            if (!Current.Is("not"))
                return Comparison();
            _at++;
            Enter();
            var operand = Not();
            _depth--;
            return Op("not", operand);
        }

        private Expr Comparison()
        {
            var left = Range();
            Expr result;
            if (Current.Kind == TokenKind.Symbol && Comparisons.TryGetValue(Current.Text, out var op))
            {
                _at++;
                result = Op(op, left, Range());
            }
            else if (Current.Is("is"))
            {
                _at++;
                var negated = Current.Is("not");
                if (negated)
                    _at++;
                if (Current.Is("null"))
                {
                    _at++;
                    result = Op(negated ? "isNotNull" : "isNull", left);
                }
                else if (Current.Is("distinct"))
                {
                    _at++;
                    Expect("from");
                    result = Op(negated ? "notDistinct" : "distinct", left, Range());
                }
                else
                {
                    throw new ParseException("MQ3101", "Expected 'null' or 'distinct from' after 'is'", Current.Position);
                }
            }
            else
            {
                return left;
            }

            if ((Current.Kind == TokenKind.Symbol && Comparisons.ContainsKey(Current.Text)) || Current.Is("is"))
                throw new ParseException("MQ3101", "Comparisons do not chain; add parentheses", Current.Position);
            return result;
        }

        private Expr Range()
        {
            var x = Concat();
            Expr result;
            if (Current.Is("between"))
            {
                _at++;
                var low = Concat();
                Expect("and");
                result = Op("between", x, low, Concat());
            }
            else if (Current.Is("in") || (Current.Is("not") && _tokens[_at + 1].Is("in")))
            {
                var op = Current.Is("not") ? "notIn" : "in";
                _at += op == "in" ? 1 : 2;
                ExpectSymbol("(");
                var args = new List<Expr> { x, Implies() };
                while (Current.IsSymbol(","))
                {
                    _at++;
                    args.Add(Implies());
                }

                ExpectSymbol(")");
                result = new Expr { Op = op, Args = args };
            }
            else
            {
                return x;
            }

            if (Current.Is("between") || Current.Is("in") || (Current.Is("not") && _tokens[_at + 1].Is("in")))
                throw new ParseException("MQ3101", "'between' and 'in' do not chain; add parentheses", Current.Position);
            return result;
        }

        private Expr Concat()
        {
            var left = Additive();
            while (Current.IsSymbol("||"))
            {
                _at++;
                left = Op("concat", left, Additive());
            }

            return left;
        }

        private Expr Additive()
        {
            var left = Multiplicative();
            while (Current.IsSymbol("+") || Current.IsSymbol("-"))
            {
                var op = Current.Text == "+" ? "add" : "sub";
                _at++;
                left = Op(op, left, Multiplicative());
            }

            return left;
        }

        private Expr Multiplicative()
        {
            var left = Unary();
            while (Current.IsSymbol("*") || Current.IsSymbol("/"))
            {
                var op = Current.Text == "*" ? "mul" : "div";
                _at++;
                left = Op(op, left, Unary());
            }

            return left;
        }

        private Expr Unary()
        {
            if (!Current.IsSymbol("-"))
                return Primary();
            _at++;
            if (Current.Kind == TokenKind.Number)
                return Number(Current, negative: true);
            Enter();
            var operand = Unary();
            _depth--;
            return Op("neg", operand);
        }

        private Expr Primary()
        {
            var token = Current;
            switch (token.Kind)
            {
                case TokenKind.Number:
                    return Number(token, negative: false);
                case TokenKind.String:
                    _at++;
                    return new Expr { Value = JsonSerializer.SerializeToElement(token.Text) };
                case TokenKind.Symbol when token.Text == "(":
                {
                    _at++;
                    Enter();
                    var inner = Implies();
                    _depth--;
                    ExpectSymbol(")");
                    return inner;
                }

                case TokenKind.Name:
                    return Word(token);
                case TokenKind.QuotedName:
                    return Name();
                default:
                    throw Unexpected();
            }
        }

        private Expr Word(Token token)
        {
            var next = _tokens[_at + 1];
            if (token.Is("null"))
            {
                _at++;
                return new Expr { Null = true };
            }

            if (token.Is("true") || token.Is("false"))
            {
                _at++;
                return new Expr { Value = JsonSerializer.SerializeToElement(token.Is("true")) };
            }

            if (TypedLiterals.Contains(token.Text) && next.Kind == TokenKind.String)
            {
                var type = token.Text.ToLowerInvariant();
                if (!IsLiteral(type, next.Text))
                    throw new ParseException("MQ3101", "'" + next.Text + "' is not a " + type + " literal", next.Position);
                _at += 2;
                return new Expr { Value = JsonSerializer.SerializeToElement(next.Text), Type = type };
            }

            if (token.Is("case"))
                return Case();
            if (token.Is("satisfies"))
                return Satisfies();
            if (next.IsSymbol("("))
                return Call(token);
            if (Reserved.Contains(token.Text))
                throw Unexpected();
            return Name();
        }

        private Expr Call(Token token)
        {
            var name = token.Text.ToLowerInvariant();
            _at += 2;
            if (name == "now")
            {
                ExpectSymbol(")");
                return new Expr { Now = true };
            }

            if (name == "actor")
            {
                ExpectSymbol(")");
                return new Expr { Actor = true };
            }

            if (name == "in_state")
                return InState(token);

            var isFunction = ExprFunctions.Functions.ContainsKey(name);
            if (!isFunction && name != "coalesce")
                throw new ParseException("MQ3101", "'" + token.Text + "' is not a function of the expression language", token.Position);
            var args = new List<Expr>();
            if (!Current.IsSymbol(")"))
            {
                args.Add(Implies());
                while (Current.IsSymbol(","))
                {
                    _at++;
                    args.Add(Implies());
                }
            }

            ExpectSymbol(")");
            return isFunction ? new Expr { Call = name, Args = args } : new Expr { Op = "coalesce", Args = args };
        }

        /// <summary>
        /// <c>in_state(object, Lifecycle.State, …)</c> (object-effects-design.md decision 7): the object is <c>subject</c>, or a path of
        /// to-one navigations from it; each state is its lifecycle's name, then its path of state names.
        /// </summary>
        private Expr InState(Token token)
        {
            if (_scope.Lifecycles is not { } lifecycles)
                throw new ParseException("MQ3104", "in_state reads a lifecycle's state; only a guard or a value an action sets may read it", token.Position);
            var start = Current.Position;
            var segments = Segments();
            List<string>? of = null;
            if (segments is not [SubjectWord])
            {
                of = Navigations(_scope.Root, segments, 0, [])
                    ?? throw new ParseException("MQ3116", "in_state reads the subject or an object a to-one path from it reaches; '" + string.Join('.', segments)
                        + "' is neither", start);
            }

            var states = new List<string>();
            do
            {
                ExpectSymbol(",");
                var at = Current.Position;
                var name = Segments();
                states.Add(lifecycles.Find(name)
                    ?? throw new ParseException("MQ3116", "No lifecycle has a state named '" + string.Join('.', name) + "'; write the lifecycle's name, then the state's path", at));
            }
            while (Current.IsSymbol(","));
            ExpectSymbol(")");
            return new Expr { InState = states, Of = of };
        }

        /// <summary>The relation end ids a run of names follows from a container, each a navigation; <see langword="null"/> when they do not.</summary>
        private static List<string>? Navigations(ExprContainer container, List<string> segments, int from, List<string> ids)
        {
            if (from == segments.Count)
                return ids.Count > 0 ? ids : null;
            for (var to = segments.Count; to > from; to--)
            {
                if (container.Find(segments.Skip(from).Take(to - from)) is ExprNavigation navigation
                    && Navigations(navigation.Target, segments, to, [.. ids, navigation.Id]) is { } path)
                    return path;
            }

            return null;
        }

        private Expr Case()
        {
            _at++;
            var branches = new List<ExprWhen>();
            while (Current.Is("when"))
            {
                _at++;
                var when = Implies();
                Expect("then");
                branches.Add(new ExprWhen { When = when, Then = Implies() });
            }

            if (branches.Count == 0)
                throw new ParseException("MQ3101", "Expected 'when' after 'case'", Current.Position);
            Expr? otherwise = null;
            if (Current.Is("else"))
            {
                _at++;
                otherwise = Implies();
            }

            Expect("end");
            return new Expr { Case = branches, Else = otherwise };
        }

        private Expr Satisfies()
        {
            _at++;
            var start = Current.Position;
            var segments = Segments();
            var constraint = _scope.FindConstraint(string.Join('.', segments))
                ?? throw new ParseException("MQ3109", "No constraint is named '" + string.Join('.', segments) + "'", start);
            ExpectSymbol("(");
            var with = new List<ExprArgument>();
            while (!Current.IsSymbol(")"))
            {
                if (with.Count > 0)
                    ExpectSymbol(",");
                var at = Current.Position;
                var name = Segments();
                var parameter = name.Count == 1 ? constraint.Parameters.FirstOrDefault(p => string.Equals(p.Name, name[0], StringComparison.Ordinal)) : null;
                if (parameter is null)
                    throw new ParseException("MQ3109", "Constraint '" + constraint.Name + "' has no parameter '" + string.Join('.', name) + "'", at);
                ExpectSymbol(":");
                with.Add(new ExprArgument { Parameter = parameter.Id, Value = Implies() });
            }

            _at++;
            return new Expr { Satisfies = constraint.Id, With = with };
        }

        private Expr Name()
        {
            var start = Current.Position;
            var segments = Segments();
            if (Resolve(_scope.Root, segments, 0, []) is { } path)
                return new Expr { Path = path };
            var text = string.Join('.', segments);
            if (_scope.Site == ExprSiteKind.RuleCondition && _scope.MemberPaths?.Invoke(text) is not null)
                return new Expr { Member = text };
            if (segments.Count >= 2 && _scope.FindRef(string.Join('.', segments.Take(segments.Count - 1)), segments[^1]) is { } id)
                return new Expr { Ref = id };
            throw new ParseException("MQ3102", "The site has no member, enum member or code named '" + text + "'", start);
        }

        private static List<string>? Resolve(ExprContainer container, List<string> segments, int from, List<string> ids)
        {
            // The longest run of segments that names a member (which must end the path) or a navigation (which goes on).
            for (var to = segments.Count; to > from; to--)
            {
                switch (container.Find(segments.Skip(from).Take(to - from)))
                {
                    case ExprMember member when to == segments.Count:
                        return [.. ids, member.Id];
                    case ExprNavigation navigation when to < segments.Count:
                        if (Resolve(navigation.Target, segments, to, [.. ids, navigation.Id]) is { } path)
                            return path;
                        break;
                }
            }

            return null;
        }

        private List<string> Segments()
        {
            var segments = new List<string>();
            while (true)
            {
                var token = Current;
                if (token.Kind == TokenKind.QuotedName || (token.Kind == TokenKind.Name && !Reserved.Contains(token.Text)))
                    segments.Add(token.Text);
                else
                    throw new ParseException("MQ3101", "Expected a name", token.Position);
                _at++;
                if (!Current.IsSymbol(".") || _tokens[_at + 1].Kind is not (TokenKind.Name or TokenKind.QuotedName))
                    return segments;
                _at++;
            }
        }

        private Expr Number(Token token, bool negative)
        {
            _at++;
            var text = negative ? "-" + token.Text : token.Text;
            if (token.Text.Contains('.', StringComparison.Ordinal))
                return new Expr { Value = JsonSerializer.SerializeToElement(text), Type = "decimal" };
            if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                throw new ParseException("MQ3101", "The integer " + text + " does not fit 64 bits; write it as a decimal (" + text + ".0)", token.Position);
            return new Expr { Value = JsonSerializer.SerializeToElement(value) };
        }

        private void Expect(string keyword)
        {
            if (!Current.Is(keyword))
                throw new ParseException("MQ3101", "Expected '" + keyword + "'", Current.Position);
            _at++;
        }

        private void ExpectSymbol(string symbol)
        {
            if (!Current.IsSymbol(symbol))
                throw new ParseException("MQ3101", "Expected '" + symbol + "'", Current.Position);
            _at++;
        }

        private void Enter()
        {
            if (++_depth > MaxNesting)
                throw new ParseException("MQ3113", "The text nests deeper than an expression may", Current.Position);
        }

        private ParseException Unexpected() => Current.Kind == TokenKind.End
            ? new ParseException("MQ3101", "The text ends too soon", Current.Position)
            : new ParseException("MQ3101", "Unexpected '" + Current.Text + "'", Current.Position);

        private static Expr Op(string op, params Expr[] args) => new() { Op = op, Args = args };

        private static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (true)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;
                if (i >= text.Length)
                {
                    tokens.Add(new Token(TokenKind.End, "", i));
                    tokens.Add(new Token(TokenKind.End, "", i));
                    return tokens;
                }

                var start = i;
                var c = text[i];
                if (char.IsAsciiLetter(c) || c == '_')
                {
                    while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_'))
                        i++;
                    tokens.Add(new Token(TokenKind.Name, text[start..i], start));
                }
                else if (char.IsAsciiDigit(c))
                {
                    while (i < text.Length && char.IsAsciiDigit(text[i]))
                        i++;
                    if (i + 1 < text.Length && text[i] == '.' && char.IsAsciiDigit(text[i + 1]))
                    {
                        i++;
                        while (i < text.Length && char.IsAsciiDigit(text[i]))
                            i++;
                    }

                    if (i < text.Length && (char.IsAsciiLetter(text[i]) || text[i] == '_'))
                        throw new ParseException("MQ3101", "A number runs into a name", i);
                    tokens.Add(new Token(TokenKind.Number, text[start..i], start));
                }
                else if (c is '\'' or '"')
                {
                    var sb = new StringBuilder();
                    i++;
                    while (true)
                    {
                        if (i >= text.Length)
                            throw new ParseException("MQ3101", c == '\'' ? "A string is not closed" : "A quoted name is not closed", start);
                        if (text[i] == c)
                        {
                            if (i + 1 < text.Length && text[i + 1] == c)
                            {
                                sb.Append(c);
                                i += 2;
                                continue;
                            }

                            i++;
                            break;
                        }

                        sb.Append(text[i++]);
                    }

                    if (c == '"' && sb.Length == 0)
                        throw new ParseException("MQ3101", "A quoted name is empty", start);
                    tokens.Add(new Token(c == '\'' ? TokenKind.String : TokenKind.QuotedName, sb.ToString(), start));
                }
                else
                {
                    var two = i + 1 < text.Length ? text.Substring(i, 2) : "";
                    if (two is "<=" or ">=" or "<>" or "!=" or "||")
                    {
                        tokens.Add(new Token(TokenKind.Symbol, two, start));
                        i += 2;
                    }
                    else if (c is '=' or '<' or '>' or '+' or '-' or '*' or '/' or '(' or ')' or ',' or '.' or ':')
                    {
                        tokens.Add(new Token(TokenKind.Symbol, c.ToString(), start));
                        i++;
                    }
                    else
                    {
                        throw new ParseException("MQ3101", "Unexpected '" + c + "'", start);
                    }
                }
            }
        }
    }

    private sealed class Printer(StringBuilder sb, ExprScope scope)
    {
        public void Write(Expr e, int least)
        {
            var precedence = Precedence(e);
            var parenthesise = precedence < least;
            if (parenthesise)
                sb.Append('(');
            WriteBare(e);
            if (parenthesise)
                sb.Append(')');
        }

        private static int Precedence(Expr e) =>
            e.Op is { } op && ExprFunctions.Operators.TryGetValue(op, out var info) ? info.Precedence : e.Text is not null ? 0 : ExprFunctions.Primary;

        private void WriteBare(Expr e)
        {
            if (e.Text is { } text)
            {
                sb.Append(text);
                return;
            }

            if (e.Value is { } value)
            {
                Literal(value, e.Type);
                return;
            }

            if (e.Null is not null)
            {
                sb.Append("null");
                return;
            }

            if (e.Path is { } path)
            {
                Path(path);
                return;
            }

            if (e.Ref is { } id)
            {
                sb.Append(scope.RefNames(id) is { } names ? QuoteDotted(names.Set) + "." + Quote(names.Name) : Quote(id));
                return;
            }

            if (e.Member is { } member)
            {
                sb.Append(member);
                return;
            }

            if (e.Now is not null)
            {
                sb.Append("now()");
                return;
            }

            if (e.Actor is not null)
            {
                sb.Append("actor()");
                return;
            }

            if (e.InState is { } states)
            {
                sb.Append("in_state(");
                Object(e.Of ?? []);
                foreach (var stateId in states)
                {
                    sb.Append(", ");
                    sb.Append(scope.Lifecycles?.State(stateId) is { } state ? string.Join('.', state.Path.Prepend(state.LifecycleName).Select(Quote)) : Quote(stateId));
                }

                sb.Append(')');
                return;
            }

            if (e.Op is { } op)
            {
                Operator(op, e.Args ?? []);
                return;
            }

            if (e.Call is { } call)
            {
                Arguments(call, e.Args ?? []);
                return;
            }

            if (e.Case is { } branches)
            {
                sb.Append("case");
                foreach (var branch in branches)
                {
                    sb.Append(" when ");
                    Write(branch.When, 1);
                    sb.Append(" then ");
                    Write(branch.Then, 1);
                }

                if (e.Else is { } otherwise)
                {
                    sb.Append(" else ");
                    Write(otherwise, 1);
                }

                sb.Append(" end");
                return;
            }

            if (e.Satisfies is { } constraintId)
            {
                var constraint = scope.Constraint(constraintId);
                sb.Append("satisfies ").Append(constraint is null ? Quote(constraintId) : QuoteDotted(constraint.Name)).Append('(');
                var first = true;
                foreach (var argument in e.With ?? [])
                {
                    if (!first)
                        sb.Append(", ");
                    first = false;
                    var parameter = constraint?.Parameters.FirstOrDefault(p => string.Equals(p.Id, argument.Parameter, StringComparison.Ordinal));
                    sb.Append(Quote(parameter?.Name ?? argument.Parameter)).Append(": ");
                    Write(argument.Value, 1);
                }

                sb.Append(')');
                return;
            }

            sb.Append("null");
        }

        private void Operator(string op, IReadOnlyList<Expr> args)
        {
            if (!ExprFunctions.Operators.TryGetValue(op, out var info) || args.Count < info.MinArgs || args.Count > info.MaxArgs)
            {
                Arguments(op, args); // a malformed node prints as a call, which the typer refuses
                return;
            }

            var p = info.Precedence;
            switch (op)
            {
                case "coalesce":
                    Arguments("coalesce", args);
                    break;
                case "implies":
                    Write(args[0], p + 1);
                    sb.Append(" implies ");
                    Write(args[1], p);
                    break;
                case "and" or "or" or "concat":
                    for (var i = 0; i < args.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(' ').Append(info.Text).Append(' ');
                        Write(args[i], i == 0 || op != "concat" ? p : p + 1);
                    }

                    break;
                case "not":
                    sb.Append("not ");
                    Write(args[0], p);
                    break;
                case "neg":
                    sb.Append('-');
                    var negative = args[0].Op == "neg" || (args[0].Value is { ValueKind: JsonValueKind.Number or JsonValueKind.String } v
                        && (v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString() ?? "").StartsWith('-'));
                    Write(args[0], negative ? ExprFunctions.Primary + 1 : p);
                    break;
                case "isNull" or "isNotNull":
                    Write(args[0], p + 1);
                    sb.Append(' ').Append(info.Text);
                    break;
                case "between":
                    Write(args[0], p + 1);
                    sb.Append(" between ");
                    Write(args[1], p + 1);
                    sb.Append(" and ");
                    Write(args[2], p + 1);
                    break;
                case "in" or "notIn":
                    Write(args[0], p + 1);
                    sb.Append(' ').Append(info.Text).Append(" (");
                    for (var i = 1; i < args.Count; i++)
                    {
                        if (i > 1)
                            sb.Append(", ");
                        Write(args[i], 1);
                    }

                    sb.Append(')');
                    break;
                default:
                    // Binary: comparisons do not chain, so both sides bind tighter; arithmetic is left-associative.
                    Write(args[0], p == 5 ? p + 1 : p);
                    sb.Append(' ').Append(info.Text).Append(' ');
                    Write(args[1], p + 1);
                    break;
            }
        }

        private void Arguments(string name, IReadOnlyList<Expr> args)
        {
            sb.Append(name).Append('(');
            for (var i = 0; i < args.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                Write(args[i], 1);
            }

            sb.Append(')');
        }

        private void Literal(JsonElement value, string? type)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.True:
                    sb.Append("true");
                    return;
                case JsonValueKind.False:
                    sb.Append("false");
                    return;
                case JsonValueKind.Number when type is null:
                    sb.Append(value.GetRawText());
                    return;
                case JsonValueKind.String when type is null:
                    String(value.GetString()!);
                    return;
                case JsonValueKind.String when type == "decimal" && value.GetString() is { } d && IsDecimal(d) && d.Contains('.', StringComparison.Ordinal):
                    sb.Append(d);
                    return;
                case JsonValueKind.String:
                    sb.Append(type).Append(' ');
                    String(value.GetString()!);
                    return;
                default:
                    sb.Append(value.GetRawText());
                    return;
            }
        }

        /// <summary>The object an <c>in_state</c> reads: <c>subject</c>, then the names of the navigations of its path.</summary>
        private void Object(IReadOnlyList<string> of)
        {
            var parts = new List<string> { SubjectWord };
            var container = scope.Root;
            foreach (var end in of)
            {
                if (container?.Navigation(end) is { } navigation)
                {
                    if (parts.Count > 1 && navigation.Prefix is { } prefix)
                        parts.Add(QuoteDotted(prefix));
                    parts.Add(Quote(navigation.Name));
                    container = navigation.Target;
                }
                else
                {
                    parts.Add(Quote(end));
                    container = null;
                }
            }

            sb.AppendJoin('.', parts);
        }

        private void String(string text) => sb.Append('\'').Append(text.Replace("'", "''", StringComparison.Ordinal)).Append('\'');

        private void Path(IReadOnlyList<string> path)
        {
            var container = scope.Root;
            var parts = new List<string>();
            for (var i = 0; i < path.Count; i++)
            {
                if (container is null)
                {
                    parts.Add(Quote(path[i]));
                    continue;
                }

                if (i < path.Count - 1 && container.Navigation(path[i]) is { } navigation)
                {
                    if (navigation.Prefix is { } prefix)
                        parts.Add(QuoteDotted(prefix));
                    parts.Add(Quote(navigation.Name));
                    container = navigation.Target;
                }
                else if (i == path.Count - 1 && container.Member(path[i]) is { } member)
                {
                    if (member.Prefix is { } prefix)
                        parts.Add(QuoteDotted(prefix));
                    parts.Add(Quote(member.Name));
                }
                else
                {
                    parts.Add(Quote(path[i]));
                    container = null;
                }
            }

            sb.AppendJoin('.', parts);
        }
    }
}
