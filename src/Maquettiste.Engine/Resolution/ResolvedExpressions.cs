using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// A typed node of an expression (next-phase-3-design.md section 3.1, "Public API"): what templates and scripts read of a check's,
/// entity check's, derived value's or constraint's condition. <see cref="Node"/> says which shape the node has; a <c>satisfies</c> is
/// inlined, so its node is the constraint's condition with the arguments in place of the parameters. The root of a condition also
/// carries its JavaScript, lowered on first read.
/// </summary>
public sealed class RExpression
{
    private string? _text;
    private string? _javaScript;
    private bool? _engineOnly;

    /// <summary>
    /// The node's shape: <c>value</c> (a literal), <c>null</c>, <c>path</c>, <c>ref</c> (an enum member or a reference-type code),
    /// <c>member</c> (a declared rule's member), <c>now</c>, <c>actor</c> (who acts, <c>actor()</c>), <c>in-state</c> (whether an object is
    /// in a state of a lifecycle, <c>in_state</c>), <c>op</c>, <c>call</c> or <c>case</c> (object-effects-design.md section 6).
    /// </summary>
    public string Node { get; internal set; } = "";

    /// <summary>For an <c>op</c> node: the operator as stored (<c>eq</c>, <c>and</c>, <c>add</c>, <c>coalesce</c>…); else <see langword="null"/>.</summary>
    public string? Op { get; internal set; }

    /// <summary>For a <c>call</c> node: the function (<c>trim</c>, <c>matches</c>, <c>add_days</c>…); else <see langword="null"/>.</summary>
    public string? Call { get; internal set; }

    /// <summary>
    /// For a <c>value</c> node: the literal, a string, a 64-bit integer or a bool, or for a literal written as text (a decimal, a date,
    /// a uuid…) its text; for a <c>ref</c>, the member's name or the code; for a <c>member</c>, its path text; else <see langword="null"/>.
    /// </summary>
    public object? Value { get; internal set; }

    /// <summary>
    /// For a <c>value</c> node: the literal as text, exactly as the tree holds it (an integer's digits, a typed literal's text, a string,
    /// <c>true</c> or <c>false</c>), for a pack that writes literals in its own language: a script reads <see cref="Value"/>'s 64-bit
    /// integer as a double, exact only to 2^53. Else <see langword="null"/>.
    /// </summary>
    public string? ValueText { get; internal set; }

    /// <summary>
    /// The operands: an operator's or function's, in order; a case's when, then, when, then… and the else last when there is one.
    /// </summary>
    public IReadOnlyList<RExpression> Args { get; internal set; } = [];

    /// <summary>
    /// What a <c>path</c> ends at: an <see cref="RColumn"/> at a table check, an <see cref="RAttribute"/> at an entity check or a derived
    /// value (a value object member's attribute for <c>amount.value</c>), an <see cref="RConstraintParameter"/> in a constraint's own
    /// condition; for a <c>ref</c> to an enum member, the <see cref="REnumMember"/>; else <see langword="null"/>.
    /// </summary>
    public object? Target { get; internal set; }

    /// <summary>
    /// For an <c>in-state</c> node: the lifecycle whose states it names; else <see langword="null"/> (object-effects-design.md decision 7).
    /// </summary>
    public RProcess? Process { get; internal set; }

    /// <summary>For an <c>in-state</c> node: the states it names, in its order; it holds when the object is in any of them. Else empty.</summary>
    public RList<RState> States { get; internal set; } = RList<RState>.Empty;

    /// <summary>
    /// For an <c>in-state</c> node: the to-one relation ends from the subject to the object whose state it reads, in order; empty for the
    /// object itself, and for any other node (object-effects-design.md decision 8).
    /// </summary>
    public RList<REnd> Through { get; internal set; } = RList<REnd>.Empty;

    /// <summary>
    /// For an <c>in-state</c> node: the attribute that holds the reached object's state in the lifecycle, as the entity (or stereotype) that
    /// declares it resolves it, when the lifecycle binds the same one for every entity the node can reach; else <see langword="null"/>
    /// (object-effects-design.md decision 11).
    /// </summary>
    public RAttribute? BoundAttribute { get; internal set; }

    /// <summary>For an <c>in-state</c> node: the enum typing <see cref="BoundAttribute"/>, for an enum binding; else <see langword="null"/>.</summary>
    public REnum? Enum { get; internal set; }

    /// <summary>
    /// For an <c>in-state</c> node with a <see cref="BoundAttribute"/>: the stored values for which the node holds, when the stored value
    /// decides it. Under an enum binding, the <see cref="REnumMember"/>s (not their names, lifecycle-design.md decision 14) of the root-level
    /// states named; under a code binding whose codes form a map, the codes (a <see cref="string"/> or a <see cref="long"/>) of the states
    /// named and of the coded states inside them, in document order. Empty when the stored value cannot tell whether a named state is
    /// active (it sits under a bound or coded state, or a state inside it carries no code), and for any other node.
    /// </summary>
    public IReadOnlyList<object> Codes { get; internal set; } = [];

    /// <summary>
    /// The node's type: <see cref="ExprType.Kind"/>, with <see cref="ExprType.Bits"/> for an <c>int</c> and <see cref="ExprType.Of"/> for an enum
    /// or code. An <c>actor</c> node carries the type the typer gave it: the type of what it meets, else <c>string</c>
    /// (object-effects-design.md decision 5).
    /// </summary>
    public ExprType Type { get; internal set; } = ExprType.Null;

    /// <summary>Whether the node may be null.</summary>
    public bool Nullable { get; internal set; }

    /// <summary>The text form, printed from current names; an inlined constraint's nodes print with its parameters' names.</summary>
    public string Text => _text ??= ExprText.Print(PrintNode, PrintScope);

    /// <summary>
    /// On the root of a condition: the JavaScript function over a row that calls the expression runtime as <c>mq</c>, lowered on first
    /// read (<see cref="ExprLowering.ToJavaScript"/>); <see langword="null"/> on an operand, and for a tree that reads <c>actor()</c> or
    /// <c>in_state</c>, whose JavaScript calls helpers only the engine has (<see cref="ExprLowering.IsEngineOnly"/>).
    /// </summary>
    public string? JavaScript => Root is { } root && !(_engineOnly ??= ExprLowering.IsEngineOnly(root.Typed))
        ? _javaScript ??= ExprLowering.ToJavaScript(root.Typed, root.Site, root.Columns)
        : null;

    /// <summary>The node the text prints (for an inlined constraint's root, the <c>satisfies</c> that cites it).</summary>
    internal Expr PrintNode { get; set; } = null!;

    /// <summary>The scope the text prints in.</summary>
    internal ExprScope PrintScope { get; set; } = null!;

    /// <summary>On the root of a condition: what it was typed and lowered from; <see langword="null"/> on an operand.</summary>
    internal ExprRoot? Root { get; set; }

    /// <summary>A copy of the node that can print otherwise: an inlined constraint whose condition is one of its own arguments.</summary>
    internal RExpression Copy() => new()
    {
        Node = Node, Op = Op, Call = Call, Value = Value, ValueText = ValueText, Args = Args, Target = Target, Type = Type, Nullable = Nullable,
        Process = Process, States = States, Through = Through, BoundAttribute = BoundAttribute, Enum = Enum, Codes = Codes,
        PrintNode = PrintNode, PrintScope = PrintScope,
    };
}

/// <summary>What the root of a resolved condition comes from: the tree as stored, its typed form and scope, and its site's lowering.</summary>
/// <param name="Tree">The tree as the file holds it.</param>
/// <param name="Typed">The typed tree.</param>
/// <param name="Scope">The scope it was typed in.</param>
/// <param name="Site">The site's kind.</param>
/// <param name="Columns">The site's mapping, which the JavaScript lowering reads.</param>
internal sealed record ExprRoot(Expr Tree, TypedExpr Typed, ExprScope Scope, ExprSiteKind Site, ExprColumns Columns);

/// <summary>
/// An entity check (next-phase-3-design.md section 3.1, "Use sites"): a condition every record of the entity meets, unknown passing.
/// A derived entity lists its bases' checks first, by id, marked <see cref="IsInherited"/>. A database enforces a check only through a
/// binding that lists it, which adds an <see cref="RCheck"/> to its write table.
/// </summary>
public sealed class REntityCheck : RElement
{
    /// <inheritdoc/>
    public override string Kind => "entity-check";

    /// <summary>The entity whose checks list it (for an inherited check, the derived entity).</summary>
    public REntity Entity { get; internal set; } = null!;

    /// <summary>The attribute a failure points at, or <see langword="null"/>.</summary>
    public RAttribute? Attribute { get; internal set; }

    /// <summary>The condition over the entity's attributes, or <see langword="null"/> when it does not type (validation says why).</summary>
    public RExpression? Condition { get; internal set; }

    /// <summary>The text a failure reports, or <see langword="null"/>.</summary>
    public string? Message { get; internal set; }

    /// <summary>Whether the check comes from a base entity.</summary>
    public bool IsInherited { get; internal set; }

    /// <summary>The bindings that list the check, in database name order: each enforces it on its write table.</summary>
    public RList<REntityBinding> Bindings { get; internal set; } = RList<REntityBinding>.Empty;

    /// <summary>The bindings collected while the databases resolve.</summary>
    internal List<REntityBinding> ListedBy { get; } = [];

    /// <summary>
    /// The ids of the elements the condition depends on beyond the entity that lists it: the entity that declares it (a base for an
    /// inherited check), those that hold the members its paths read (a base, a value object), and the enums, seeds and reference types
    /// of its refs and the constraints it cites. A table a binding writes the check into depends on them too.
    /// </summary>
    internal IReadOnlyList<string> DependsOn { get; set; } = [];
}

/// <summary>
/// A named constraint (next-phase-3-design.md section 3.1; <c>model.constraints</c>): one condition over typed parameters, cited by any
/// site with <c>satisfies</c>, where it is inlined.
/// </summary>
public sealed class RConstraint : RElement
{
    /// <inheritdoc/>
    public override string Kind => "constraint";

    /// <summary>The typed parameters, in order.</summary>
    public RList<RConstraintParameter> Parameters { get; internal set; } = RList<RConstraintParameter>.Empty;

    /// <summary>The condition over the parameters, or <see langword="null"/> when it does not type (validation says why).</summary>
    public RExpression? Condition { get; internal set; }

    /// <summary>The text a failure reports, or <see langword="null"/>.</summary>
    public string? Message { get; internal set; }
}

/// <summary>A parameter of a constraint: its type is an attribute's type.</summary>
public sealed class RConstraintParameter : RElement
{
    /// <inheritdoc/>
    public override string Kind => "parameter";

    /// <summary>The constraint.</summary>
    public RConstraint Constraint { get; internal set; } = null!;

    /// <summary>The parameter's type.</summary>
    public RType Type { get; internal set; } = null!;
}

/// <summary>A check lowered to a table: its SQL, its condition as the table reads it and its shape.</summary>
/// <param name="Sql">The CHECK's PostgreSQL text.</param>
/// <param name="Condition">The condition over the table's columns.</param>
/// <param name="Shape">The hash of the check as lowered with column keys in place of column names.</param>
internal sealed record LoweredCheck(string Sql, RExpression Condition, string Shape);

/// <summary>
/// How the resolver types, lowers and exposes expressions (next-phase-3-design.md section 3.1, "Where lowering runs"): each site is typed
/// in the scope validation reads (<see cref="ExprSites"/>), silently (validation reports what does not type), a table's check is
/// lowered to PostgreSQL through its columns, and every condition becomes an <see cref="RExpression"/> tree whose JavaScript is lowered
/// on first read. Nothing here runs for a model without expressions.
/// </summary>
internal static class ExprResolution
{
    /// <summary>
    /// Types one site of an element, its sub-element's id given; <see langword="null"/> when it holds no tree, does not type, or is a
    /// process guard that reads a context attribute named <c>event</c> (<see cref="ExprSites.EventContextRead"/>).
    /// </summary>
    public static (Expr Tree, TypedExpr Typed, ExprScope Scope)? Type(ModelSnapshot model, Element element, string subId)
    {
        foreach (var site in ExprSites.Of(model, element))
        {
            if (!string.Equals(site.SubId, subId, StringComparison.Ordinal) || site.Tree is not { } tree)
                continue;
            var scope = ExprSites.Scope(model, element, model.GetDocument(element.Id), site, text: false);
            var problems = new List<Diagnostic>();
            var typed = ExprTyper.Type(tree, scope, problems);
            if (typed is null || problems.Any(p => p.Severity == DiagnosticSeverity.Error))
                return null;
            return element is Process process && ExprSites.EventContextRead(process, typed) is not null ? null : (tree, typed, scope);
        }

        return null;
    }

    /// <summary>
    /// The ids of the elements a typed tree names beyond its site, which its holder depends on: the enum, or the seed and its reference
    /// type, of each ref; each constraint it cites with what that constraint names; for a path through a relation, the relation, the
    /// entity each navigation leads to with every base of it (whose attributes and navigations the path reads) and the attribute's owner;
    /// and the lifecycle of each <c>in_state</c>, with the relations and entities its object's path goes through.
    /// </summary>
    public static IReadOnlyList<string> Names(ModelSnapshot model, TypedExpr typed)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<TypedExpr>();
        pending.Push(typed);
        while (pending.Count > 0)
        {
            var t = pending.Pop();
            if (t.Node.Ref is { } id && model.TryGetEntry(id, out var entry))
            {
                names.Add(entry.OwnerId);
                if (model.Get<Seed>(entry.OwnerId) is { } seed)
                    names.Add(seed.Target);
            }

            // A path through a relation depends on the relation and on the record it reads: the entity at the far end and its bases.
            foreach (var navigation in t.Through)
            {
                if (!model.TryGetEntry(navigation.Id, out var end))
                    continue;
                names.Add(end.OwnerId);
                var far = model.Get<Relation>(end.OwnerId)?.Ends.FirstOrDefault(e => string.Equals(e.Id, navigation.Id, StringComparison.Ordinal))?.Entity;
                var entity = far is null ? null : model.Get<Entity>(far);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (entity is not null && seen.Add(entity.Id))
                {
                    names.Add(entity.Id);
                    entity = entity.Base is { } b ? model.Get<Entity>(b) : null;
                }
            }

            if (t.Through.Count > 0 && t.Member is { } member && model.TryGetEntry(member.Id.Split('.')[0], out var attribute))
                names.Add(attribute.OwnerId);

            if (t.Constraint is { } constraint)
                names.Add(constraint.Id);
            // An in_state reads its lifecycle's states (object-effects-design.md section 6); its path's relations and entities are above.
            if (t.Lifecycle is { } lifecycle)
                names.Add(lifecycle);
            if (t.Body is { } body)
                pending.Push(body);
            foreach (var arg in t.Args)
                pending.Push(arg);
        }

        return [.. names];
    }

    /// <summary>
    /// The ids of the elements that hold the members the paths of a tree read: an attribute's entity (a base's for an inherited one) and,
    /// for a value object member (<c>attributeId.memberId</c>), the value object too.
    /// </summary>
    public static IReadOnlyList<string> Holders(ModelSnapshot model, TypedExpr typed)
    {
        var holders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in Paths(typed))
        {
            foreach (var segment in path.Split('.'))
            {
                if (model.TryGetEntry(segment, out var entry))
                    holders.Add(entry.OwnerId);
            }
        }

        return [.. holders];
    }

    /// <summary>The ids of the members the paths of a tree read in its own scope (a cited constraint's parameters are bound, not read).</summary>
    public static IReadOnlyList<string> Paths(TypedExpr typed)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Walk(typed);
        return paths;

        void Walk(TypedExpr t)
        {
            if (t.Node.Path is not null && t.Member is { } member && seen.Add(member.Id))
                paths.Add(member.Id);
            foreach (var arg in t.Args)
                Walk(arg);
        }
    }

    /// <summary>
    /// The literal of an enum member or a reference-type code in a stored form: an integer column holds a member's value, else its
    /// position from 0, or an integer code; any other form a member's code, else its name, or the code's text.
    /// </summary>
    public static object? RefLiteral(ModelSnapshot model, string id, ExprStoredForm form)
    {
        if (!model.TryGetEntry(id, out var entry))
            return null;
        if (model.Get<EnumType>(entry.OwnerId) is { } e)
        {
            for (var i = 0; i < e.Members.Count; i++)
            {
                var member = e.Members[i];
                if (string.Equals(member.Id, id, StringComparison.Ordinal))
                    return form == ExprStoredForm.Integer ? member.Value ?? i : member.Code ?? member.Name;
            }

            return null;
        }

        if (model.Get<Seed>(entry.OwnerId) is not { } seed || model.Get<ReferenceType>(seed.Target) is null)
            return null;
        var at = seed.Columns.ToList().IndexOf("code");
        var row = seed.Rows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
        if (at < 0 || row is null || at >= row.Values.Count)
            return null;
        var code = row.Values[at];
        var text = code.ValueKind == JsonValueKind.String ? code.GetString()! : code.GetRawText();
        return form == ExprStoredForm.Integer && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : text;
    }

    /// <summary>How a column stores an enum member or a code: by its physical type (a reference column's code type).</summary>
    public static ExprStoredForm FormOf(RColumn column) =>
        (column.Type == "reference" && column.CodeType is { } code ? code : column.Type) switch
        {
            "int16" or "int32" or "int64" => ExprStoredForm.Integer,
            "uuid" or "ulid" => ExprStoredForm.Uuid,
            _ => ExprStoredForm.Text,
        };

    /// <summary>What a path reads in a column: its name (or another name, such as its key), padding, scale and stored form.</summary>
    public static ExprColumn ColumnOf(RColumn column, string? name = null) => new(name ?? column.Name)
    {
        FixedLength = column.FixedLength || column.NativeType.StartsWith("char(", StringComparison.OrdinalIgnoreCase),
        Scale = column.Scale,
        Form = FormOf(column),
    };

    /// <summary>
    /// Lowers a check to a table whose columns <paramref name="columns"/> gives by the ids its paths hold: the SQL, the condition as the
    /// table reads it, and its shape. <see langword="null"/>, with the lowering's findings, when a path reads no column of the table or
    /// the database's dialect is not PostgreSQL (MQ4056).
    /// </summary>
    public static LoweredCheck? LowerCheck(ModelSnapshot model, RDatabase database, Quoting quoting, ExprRoot source, IReadOnlyDictionary<string, RColumn> columns,
        string elementId, string? filePath, Func<string, object?> refTarget, ICollection<Diagnostic> problems)
    {
        var typed = source.Typed;
        foreach (var id in Paths(typed))
        {
            if (!columns.ContainsKey(id))
                return null;
        }

        ExprColumns Mapping(Func<RColumn, string> name) => new()
        {
            Column = t => t.Member is { } m && columns.TryGetValue(m.Id, out var c) ? ColumnOf(c, name(c)) : null,
            Ref = (id, form) => RefLiteral(model, id, form),
            Physical = true,
            Dialect = database.Dialect,
            Quoting = quoting,
            ElementId = elementId,
            FilePath = filePath,
        };

        var mapping = Mapping(c => c.Name);
        if (ExprLowering.ToPostgreSql(typed, mapping, ExprSiteKind.Check, problems) is not { } sql)
            return null;
        // A CHECK keeps its identity across a column rename (decision 22): the shape is the check written with column keys for names, so
        // only what changes its meaning (its tree, a cited constraint, the columns it reads, a literal's stored form) changes it.
        var keyed = ExprLowering.ToPostgreSql(typed, Mapping(c => c.Key), ExprSiteKind.Check, new List<Diagnostic>()) ?? sql;
        var condition = Build(source with { Columns = mapping }, m => columns.GetValueOrDefault(m.Id), refTarget);
        return new LoweredCheck(sql, condition, ContentHash.Of(keyed));
    }

    /// <summary>
    /// The resolved tree of a typed condition: each node with its shape, type and target, a <c>satisfies</c> inlined with its arguments
    /// in place of the constraint's parameters, and the root able to lower its JavaScript. <paramref name="inState"/> fills an
    /// <c>in-state</c> node's lifecycle, states, ends and binding; the resolver may defer that until every process is built.
    /// </summary>
    public static RExpression Build(ExprRoot root, Func<ExprMember, object?> target, Func<string, object?> refTarget, Action<TypedExpr, RExpression>? inState = null)
    {
        var built = Node(root.Typed, root.Scope, null);
        built.Root = root;
        return built;

        RExpression Node(TypedExpr t, ExprScope scope, IReadOnlyDictionary<string, RExpression>? bound)
        {
            var e = t.Node;
            if (e.Satisfies is not null && t.Constraint is { } constraint && t.Body is { } body)
            {
                var arguments = new Dictionary<string, RExpression>(StringComparer.Ordinal);
                for (var i = 0; i < constraint.Parameters.Count && i < t.Args.Count; i++)
                    arguments[constraint.Parameters[i].Id] = Node(t.Args[i], scope, bound);
                var inlined = Node(body, scope.ForConstraint(constraint), arguments);
                if (arguments.Values.Any(a => ReferenceEquals(a, inlined)))
                    inlined = inlined.Copy();
                inlined.PrintNode = e;
                inlined.PrintScope = scope;
                return inlined;
            }

            if (e.Path is not null && t.Through.Count == 0 && t.Member is { } parameter && bound is not null && bound.TryGetValue(parameter.Id, out var argument))
                return argument;

            var r = new RExpression { Type = t.Type, Nullable = t.Nullable, PrintNode = e, PrintScope = scope };
            if (e.Value is { } value)
            {
                r.Node = "value";
                r.Value = e.Type is not null ? value.GetString() : Plain(value);
                r.ValueText = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            }
            else if (e.Null is not null)
            {
                r.Node = "null";
            }
            else if (e.Path is not null)
            {
                r.Node = "path";
                r.Target = t.Member is { } member ? target(member) : null;
            }
            else if (e.Ref is { } id)
            {
                r.Node = "ref";
                r.Value = scope.RefNames(id)?.Name;
                r.Target = refTarget(id);
            }
            else if (e.Member is { } path)
            {
                r.Node = "member";
                r.Value = path;
            }
            else if (e.Now is not null)
            {
                r.Node = "now";
            }
            else if (e.Actor is not null)
            {
                r.Node = "actor";
            }
            else if (e.InState is not null)
            {
                r.Node = "in-state";
                inState?.Invoke(t, r);
            }
            else if (e.Op is { } op)
            {
                r.Node = "op";
                r.Op = op;
            }
            else if (e.Call is { } call)
            {
                r.Node = "call";
                r.Call = call;
            }
            else
            {
                r.Node = "case";
            }

            if (t.Args.Count > 0)
                r.Args = [.. t.Args.Select(a => Node(a, scope, bound))];
            return r;
        }
    }

    /// <summary>A plain literal: a string, a 64-bit integer (the raw text when it does not fit) or a bool.</summary>
    private static object? Plain(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => value.TryGetInt64(out var n) ? n : value.GetRawText(),
        _ => null,
    };

    /// <summary>
    /// The mapping a condition read from the API has (an entity check, a derived value, a constraint's own condition): paths read the
    /// members by name, and an enum member or code is the form the API carries, a member's code, else its name, or the code's text.
    /// </summary>
    public static ExprColumns ApiColumns(ModelSnapshot model, string elementId, string? filePath) => new()
    {
        Ref = (id, form) => RefLiteral(model, id, form),
        ElementId = elementId,
        FilePath = filePath,
    };
}
