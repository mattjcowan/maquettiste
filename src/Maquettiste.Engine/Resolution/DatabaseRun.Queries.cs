using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Text;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// Queries of one database (added 2026-10-02, engine-design.md section 7, "Queries"): each file resolved once the database's tables,
/// views, routines and database types exist, its sources to tables and views, its column references to their columns, its parameters
/// to native types, its trees to <see cref="RQueryExpression"/> and <see cref="RQueryPredicate"/>. What a file gets wrong is reported here (MQ4021 to MQ4043, MQ4018 and MQ3001), where the columns are
/// known, with the JSON pointer of the offending node.
/// </summary>
internal sealed partial class DatabaseRun
{
    /// <summary>A function name a call may write: an identifier, optionally schema-qualified (MQ4034).</summary>
    private static readonly Regex FunctionNamePattern = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$", RegexOptions.CultureInvariant);

    /// <summary>The text of a typed decimal literal (MQ4043).</summary>
    private static readonly Regex DecimalPattern = new(@"^-?[0-9]+(\.[0-9]+)?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The class name a pack makes of a query's name: Pascal-cased, with <c>Q</c> in front when it would start with a digit or be empty
    /// (MQ4040 compares queries of a database by it).
    /// </summary>
    internal static string QueryClassName(string name)
    {
        var pascal = Casing.Pascal(name);
        return pascal.Length == 0 || char.IsAsciiDigit(pascal[0]) ? "Q" + pascal : pascal;
    }

    private readonly List<(RQuery Query, DependencySet Deps)> _queries = [];

    /// <summary>Resolves the database's queries (after <see cref="FinishDatabaseObjects"/>, so routines and types are complete).</summary>
    private void ResolveQueries(List<RView> views)
    {
        var sources = new Dictionary<string, (TableBuild? Table, RView? View)>(StringComparer.Ordinal);
        foreach (var t in _tableOrder)
        {
            sources.TryAdd(t.Table.Key, (t, null));
            if (t.Overlay is { } overlay)
                sources.TryAdd(overlay.Id, (t, null));
            if (t.Source is { } designed)
                sources.TryAdd(designed.Id, (t, null));
        }

        foreach (var view in views)
            sources.TryAdd(view.Id, (null, view));
        var routines = _routines.ToDictionary(r => r.Routine.Id, r => r.Routine, StringComparer.Ordinal);

        foreach (var file in _run.Model.All<Query>().Where(q => q.Database == _db.Id).OrderBy(q => q.Id, StringComparer.Ordinal))
        {
            _run.Ct.ThrowIfCancellationRequested();
            var deps = new DependencySet(_run.Keys).Element(file.Id).Referrers(file.Id).Element(_db.Id).Add(TypeMaps).Add(Conventions);
            AddBinderKeys(deps, file.Id);
            var query = new RQuery { Id = file.Id, Name = file.Name, Database = _rdb, Settings = _run.Settings, Definition = file };
            _run.FillPhysicalAnnotations(query, file, deps);
            new QueryBuilder(this, file, query, deps, sources, routines).Build();
            query.Dependencies = deps.ToList();
            _queries.Add((query, deps));
            _run.Register(query);
        }

        // Each query becomes a class named after it: two names that differ only once Pascal-cased collide (MQ3001 reports names that
        // are equal ignoring case).
        var classes = new Dictionary<string, RQuery>(StringComparer.Ordinal);
        foreach (var (query, _) in _queries.OrderBy(q => q.Query.Name, StringComparer.Ordinal).ThenBy(q => q.Query.Id, StringComparer.Ordinal))
        {
            var name = QueryClassName(query.Name);
            if (!classes.TryAdd(name, query) && !string.Equals(classes[name].Name, query.Name, StringComparison.OrdinalIgnoreCase))
                _run.AddDiagnostic("MQ4040", $"Query '{query.Name}' becomes class '{name}', as query '{classes[name].Name}' of database '{_db.Name}' does: rename one of them.", query.Id, "/name");
        }
    }

    /// <summary>The native type of a built-in keyword with the conventions' facet defaults.</summary>
    private string KeywordNativeType(string keyword)
    {
        var withDefaults = new RColumn { Type = keyword };
        ApplyFacetDefaults(withDefaults);
        return NativeType(keyword, withDefaults.Length, withDefaults.Precision, withDefaults.Scale);
    }

    /// <summary>The resolution of one query file (its named queries in DatabaseRun.NamedQueries.cs).</summary>
    private sealed partial class QueryBuilder(
        DatabaseRun run,
        Query file,
        RQuery query,
        DependencySet deps,
        Dictionary<string, (TableBuild? Table, RView? View)> sources,
        Dictionary<string, RRoutine> routines)
    {
        private static readonly string[] IntegerTypes = ["int16", "int32", "int64"];

        private readonly Dictionary<string, RQueryParameter> _parameters = new(StringComparer.Ordinal);
        private readonly List<IResolvedObject> _uses = [];
        private bool _failed;

        /// <summary>The one expression a list parameter may be: the whole right side of the in or notIn being resolved.</summary>
        private QueryExpression? _listOperand;

        /// <summary>The sources a query (or nested query) declares, by alias, with the scope around it.</summary>
        private sealed class Scope(RQuery query, Scope? outer)
        {
            public RQuery Query { get; } = query;

            public Scope? Outer { get; } = outer;

            public Dictionary<string, RQuerySource> Aliases { get; } = new(StringComparer.Ordinal);

            public List<RQuerySource> Sources { get; } = [];
        }

        public void Build()
        {
            query.Parameters = ResolveParameters();
            if (file.IsSqlText)
            {
                BuildSqlText(); // DatabaseRun.SqlQueries.cs
                return;
            }

            if (file.From is null)
            {
                query.Sql = null; // neither form: the schema's (MQ1002)
                return;
            }

            ResolveWith();
            var scope = new Scope(query, null);
            ResolveBody(query, scope, file.From, file.Joins, file.Select, file.Where, file.GroupBy, file.Having, file.OrderBy, "");
            query.Distinct = file.Distinct;
            query.Paging = ResolvePaging();
            CheckWithNames();
            query.Uses = [.. _uses];
            if (!_failed)
                CheckDistinctOrder(query);

            // A query with errors has none: they are reported above, and generation stops on them before any template reads it.
            query.Sql = _failed ? null : QuerySql.Render(query).Sql;
        }

        private void Report(string rule, string message, string pointer)
        {
            // Names, paging types and dialect warnings leave the query renderable; every other finding leaves a node unresolved or the
            // SQL wrong.
            if (rule is not ("MQ3001" or "MQ4030" or "MQ4037" or "MQ4040" or "MQ4096" or "MQ4098"))
                _failed = true;
            run._run.AddDiagnostic(rule, message, file.Id, pointer);
        }

        /// <summary>Whether a name is reserved for the names generated code adds (a pack's own names start with <c>mq_</c>).</summary>
        private static bool IsReserved(string name) => name.StartsWith("mq_", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// MQ4038: a distinct query orders only by expressions of its select list, and not by nulls first or last where the dialect
        /// emulates them with an extra term.
        /// </summary>
        private void CheckDistinctOrder(RQuery target)
        {
            if (!target.Distinct || target.OrderBy.Count == 0)
                return;
            var selected = target.Select.Select(f => QuerySql.ExpressionText(target, f.Expression!)).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < target.OrderBy.Count; i++)
            {
                var order = target.OrderBy[i];
                var pointer = "/orderBy/" + i.ToString(CultureInfo.InvariantCulture);
                if (!selected.Contains(QuerySql.ExpressionText(target, order.Expression)))
                    Report("MQ4038", $"Query '{file.Name}' is distinct and orders by an expression its select list does not have: select it, or order by a selected one.", pointer + "/expression");
                else if (order.Nulls is not null && run._dialect is "sqlserver" or "mysql")
                    Report("MQ4038", $"Query '{file.Name}' is distinct and orders nulls {order.Nulls}, which {run._dialect} writes as an extra order term the select list does not have: leave nulls out.", pointer + "/nulls");
            }
        }

        private void Use(IResolvedObject value)
        {
            if (_uses.Contains(value))
                return;
            _uses.Add(value);
            switch (value)
            {
                case RTable table when sources.TryGetValue(table.Key, out var built) && built.Table is { } t:
                    deps.AddRange(t.Deps.ToList());
                    break;
                case RView view:
                    deps.AddRange(view.Dependencies);
                    break;
                default:
                    deps.Element(value.Id);
                    break;
            }
        }

        private List<RQueryParameter> ResolveParameters()
        {
            var list = new List<RQueryParameter>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < file.Parameters.Count; i++)
            {
                var p = file.Parameters[i];
                var pointer = "/parameters/" + i.ToString(CultureInfo.InvariantCulture);
                var (keyword, dbType, native) = run.Slot(p.Type, p.Length, p.Precision, p.Scale, null, deps);
                if (keyword is null && dbType is null)
                    Report("MQ4018", $"Parameter '{p.Name}' of query '{file.Name}' has type '{p.Type}', which is neither a built-in type nor a database type of database '{run._db.Name}'.", pointer + "/type");
                if (dbType is not null)
                    Use(dbType);
                var resolved = new RQueryParameter
                {
                    Name = p.Name,
                    Type = keyword,
                    DbType = dbType,
                    Length = p.Length,
                    Precision = p.Precision,
                    Scale = p.Scale,
                    NativeType = native ?? "",
                    CodeType = keyword ?? (dbType is { TypeKind: "domain", Base: { } baseType } ? baseType : "string"),
                    Collection = p.Collection,
                    Description = p.Description,
                };
                if (p.Default is { } value)
                {
                    if (!TryPlain(value, out var plain))
                        Report("MQ4043", $"The default of parameter '{p.Name}' of query '{file.Name}' is a number too large to hold.", pointer + "/default");
                    else if (!DefaultFits(plain, resolved.CodeType))
                        Report("MQ4043", $"The default of parameter '{p.Name}' of query '{file.Name}' is {Describe(plain)}, which is not a value of its type '{resolved.CodeType}'.", pointer + "/default");
                    else
                        resolved.Default = plain;
                }

                if (!names.Add(p.Name))
                    Report("MQ3001", $"Parameter name '{p.Name}' is already used in query '{file.Name}'{(_parameters.ContainsKey(p.Name) ? "" : " (ignoring case)")}.", pointer + "/name");
                else if (IsReserved(p.Name))
                    Report("MQ4040", $"Parameter '{p.Name}' of query '{file.Name}' starts with mq_, which generated code reserves for its own names.", pointer + "/name");
                _parameters.TryAdd(p.Name, resolved);
                list.Add(resolved);
            }

            return list;
        }

        /// <summary>Whether a parameter's default is a value of its code type (MQ4043).</summary>
        private static bool DefaultFits(object? value, string codeType) => codeType switch
        {
            "bool" => value is bool,
            "int16" => value is long l && l is >= short.MinValue and <= short.MaxValue,
            "int32" => value is long l && l is >= int.MinValue and <= int.MaxValue,
            "int64" => value is long,
            "decimal" or "double" or "float" => value is long or decimal or double,
            "uuid" => value is string s && Guid.TryParse(s, CultureInfo.InvariantCulture, out _),
            _ => value is string,
        };

        private static string Describe(object? value) => value switch
        {
            string s => "the text '" + s + "'",
            bool b => b ? "true" : "false",
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => "a value",
        };

        private RQueryPaging? ResolvePaging()
        {
            if (file.Paging is not { } paging)
                return null;
            var result = new RQueryPaging();
            (result.OffsetParameter, result.Offset) = Bound(paging.Offset, "/paging/offset");
            (result.LimitParameter, result.Limit) = Bound(paging.Limit, "/paging/limit");
            return result;

            (RQueryParameter?, long?) Bound(JsonElement? value, string pointer)
            {
                if (value is not { } v)
                    return (null, null);
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var number))
                    return (null, number);
                if (v.ValueKind != JsonValueKind.String)
                    return (null, null);
                var name = v.GetString()!;
                if (!_parameters.TryGetValue(name, out var parameter))
                {
                    Report("MQ4024", $"Query '{file.Name}' pages by parameter '{name}', which it does not declare.", pointer);
                    return (null, null);
                }

                if (!IntegerTypes.Contains(parameter.CodeType, StringComparer.Ordinal) || parameter.Collection)
                    Report("MQ4030", $"Query '{file.Name}' pages by parameter '{name}', whose type '{parameter.Type ?? parameter.DbType?.Name}' is not an integer type (int16, int32 or int64).", pointer);
                return (parameter, null);
            }
        }

        /// <summary>Resolves the sources, select list, conditions, grouping and ordering of a query or nested query into <paramref name="target"/>.</summary>
        private void ResolveBody(RQuery target, Scope scope, QuerySource from, IReadOnlyList<QueryJoin> joins, IReadOnlyList<QueryField> select,
            QueryPredicate? where, IReadOnlyList<QueryExpression> groupBy, QueryPredicate? having, IReadOnlyList<QueryOrder> orderBy, string at)
        {
            target.From = AddSource(scope, from.Source, from.Alias, "from", at + "/from");
            var joined = new List<RQuerySource>();
            for (var i = 0; i < joins.Count; i++)
            {
                var join = joins[i];
                var pointer = at + "/joins/" + i.ToString(CultureInfo.InvariantCulture);
                var kind = ResolutionValues.Kebab(join.Kind);
                var source = AddSource(scope, join.Source, join.Alias, kind, pointer);
                if (kind is "right" or "full")
                {
                    foreach (var earlier in scope.Sources.Where(s => !ReferenceEquals(s, source)))
                        earlier.Optional = true;
                }

                if (kind is "left" or "full")
                    source.Optional = true;
                if (kind == "cross" && join.On is not null)
                    Report("MQ4035", $"Query '{file.Name}' cross joins '{source.Alias}' with a condition: a cross join takes none (make it an inner join, or remove on).", pointer + "/on");
                else if (kind != "cross" && join.On is null)
                    Report("MQ4035", $"Query '{file.Name}' joins '{source.Alias}' ({kind}) without a condition: give the join an on, or make it a cross join.", pointer);
                if (kind == "full" && run._dialect == "mysql")
                    Report("MQ4036", $"Query '{file.Name}' full joins '{source.Alias}', which MySQL (database '{run._db.Name}') cannot run: it has no full join.", pointer + "/kind");
                else if (kind is "right" or "full" && run._dialect == "sqlite")
                    Report("MQ4037", $"Query '{file.Name}' {kind} joins '{source.Alias}', which SQLite (database '{run._db.Name}') runs from version 3.39 only.", pointer + "/kind");
                if (join.On is { } on && kind != "cross")
                    source.On = Predicate(on, scope, pointer + "/on");
                joined.Add(source);
            }

            target.Joins = joined;
            target.Select = ResolveFields(select, scope, at);
            target.Where = where is null ? null : Predicate(where, scope, at + "/where");
            target.GroupBy = [.. groupBy.Select((g, i) => Expression(g, scope, at + "/groupBy/" + i.ToString(CultureInfo.InvariantCulture)))];
            target.Having = having is null ? null : Predicate(having, scope, at + "/having");
            target.OrderBy = [.. orderBy.Select((o, i) => new RQueryOrder
            {
                Expression = Expression(o.Expression, scope, at + "/orderBy/" + i.ToString(CultureInfo.InvariantCulture) + "/expression"),
                Direction = ResolutionValues.Kebab(o.Direction),
                Nulls = o.Nulls is { } nulls ? ResolutionValues.Kebab(nulls) : null,
            })];
        }

        private RQuerySource AddSource(Scope scope, string reference, string? alias, string kind, string pointer)
        {
            var source = new RQuerySource { Source = reference, JoinKind = kind };
            var found = sources.TryGetValue(reference, out var hit) ? hit
                : sources.TryGetValue(reference + "@" + run._db.Id, out hit) && hit.Table is not null ? hit
                : default;
            if (found.Table is { } t)
            {
                source.Table = t.Table;
                source.Name = t.Table.Name;
                source.Schema = t.Table.Schema;
                Use(t.Table);
            }
            else if (found.View is { } view)
            {
                source.View = view;
                source.Name = view.Name;
                source.Schema = view.Schema;
                Use(view);
            }
            else if (!NamedSource(scope, source, reference, pointer))
            {
                Report("MQ4021", $"Query '{file.Name}' reads '{reference}', which {WhatIs(reference)}.", pointer + "/source");
                source.Name = reference;
            }

            source.Alias = alias ?? source.Name;
            if (!scope.Aliases.TryAdd(source.Alias, source))
                Report("MQ4022", $"Query '{file.Name}' declares alias '{source.Alias}' twice; give each source its own alias.", pointer + (alias is null ? "/source" : "/alias"));
            scope.Sources.Add(source);
            return source;
        }

        /// <summary>What a source reference that resolves to no table or view of the database is, for MQ4021.</summary>
        private string WhatIs(string reference)
        {
            var element = run._run.Model.Get<Element>(reference.Split('@')[0]);
            var other = element switch
            {
                Table t => t.Database,
                View v => v.Database,
                _ => null,
            };
            if (other is not null && other != run._db.Id)
                return $"is {element!.KindName} '{element.Name}' of database '{run._run.Model.Get<Database>(other)?.Name ?? other}', not of '{run._db.Name}'";
            if (element is Entity entity)
                return $"is entity '{entity.Name}', which has no table in database '{run._db.Name}'";
            if (element is not null)
                return $"is {element.KindName} '{element.Name}', not a table or view";
            return file.With.Count > 0
                ? $"is no table or view of database '{run._db.Name}' and no named query of the query"
                : $"is no table or view of database '{run._db.Name}'";
        }

        private List<RQueryField> ResolveFields(IReadOnlyList<QueryField> select, Scope scope, string at)
        {
            var fields = new List<RQueryField>();
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < select.Count; i++)
            {
                var f = select[i];
                var pointer = at + "/select/" + i.ToString(CultureInfo.InvariantCulture);
                var expression = Expression(f.Expression, scope, pointer + "/expression");
                var field = new RQueryField
                {
                    Name = f.Name,
                    Expression = expression,
                    Type = f.Type ?? expression.Type,
                    NativeType = f.Type is { } declared ? run.KeywordNativeType(declared) : expression.NativeType,
                    CodeType = f.Type ?? expression.CodeType,
                    Nullable = f.Nullable ?? expression.Nullable,
                };
                if (!names.TryAdd(field.Name, i))
                {
                    Report("MQ3001", $"Field name '{field.Name}' is already used at index {names[field.Name]} of the select list of query '{file.Name}'.", pointer + "/name");
                }
                else if (IsReserved(field.Name))
                {
                    Report("MQ4040", $"Field '{field.Name}' of query '{file.Name}' starts with mq_, which generated code reserves for its own names.", pointer + "/name");
                }
                else if (!_inWith && !properties.TryAdd(Casing.Pascal(field.Name), field.Name))
                {
                    // A row is a record whose properties are the fields' names Pascal-cased.
                    Report("MQ4040", $"Field '{field.Name}' of query '{file.Name}' becomes property '{Casing.Pascal(field.Name)}', as field '{properties[Casing.Pascal(field.Name)]}' does: give it another name.", pointer + "/name");
                }

                fields.Add(field);
            }

            return fields;
        }

        private RQueryPredicate Predicate(QueryPredicate p, Scope scope, string pointer)
        {
            if (p.And is { } and)
                return new RQueryPredicate { Node = "and", And = [.. and.Select((x, i) => Predicate(x, scope, pointer + "/and/" + i.ToString(CultureInfo.InvariantCulture)))] };
            if (p.Or is { } or)
                return new RQueryPredicate { Node = "or", Or = [.. or.Select((x, i) => Predicate(x, scope, pointer + "/or/" + i.ToString(CultureInfo.InvariantCulture)))] };
            if (p.Not is { } not)
                return new RQueryPredicate { Node = "not", Not = Predicate(not, scope, pointer + "/not") };
            if (p.Exists is { } exists)
            {
                var nested = new RQuery
                {
                    Id = query.Id, Name = "exists", Database = query.Database, Parent = scope.Query, Settings = query.Settings, Parameters = query.Parameters, Definition = exists,
                };
                var inner = new Scope(nested, scope);
                ResolveBody(nested, inner, exists.From, exists.Joins, [], exists.Where, exists.GroupBy, exists.Having, [], pointer + "/exists");
                return new RQueryPredicate { Node = "exists", Exists = nested };
            }

            var op = p.Op ?? "eq";
            var result = new RQueryPredicate { Node = "compare", Op = op, Left = p.Left is null ? Null() : Expression(p.Left, scope, pointer + "/left") };
            var right = p.Right ?? [];
            // A list parameter is the whole right side of in or notIn, or nothing (MQ4039).
            var saved = _listOperand;
            _listOperand = op is "in" or "notIn" && right.Count == 1 ? right[0] : null;
            var values = right.Select((x, i) => Expression(x, scope, right.Count == 1 && p.Right is not null ? pointer + "/right" : pointer + "/right/" + i.ToString(CultureInfo.InvariantCulture))).ToList();
            _listOperand = saved;
            switch (op)
            {
                case "isNull" or "isNotNull":
                    if (values.Count > 0)
                        Report("MQ4031", $"Comparison '{op}' in query '{file.Name}' takes no right side.", pointer + "/right");
                    break;
                case "in" or "notIn":
                    if (values.Count == 1 && values[0] is { Node: "param", Param.Collection: true })
                        result.Right = values[0];
                    else if (values.Count == 0)
                        Report("MQ4031", $"Comparison '{op}' in query '{file.Name}' needs a list of values or a list parameter on its right side.", pointer);
                    else
                        result.Values = values;
                    break;
                case "between":
                    if (values.Count != 2)
                        Report("MQ4031", $"Comparison 'between' in query '{file.Name}' needs two values on its right side (the low and the high bound).", values.Count == 0 ? pointer : pointer + "/right");
                    else
                        result.Values = values;
                    break;
                default:
                    if (values.Count != 1)
                        Report("MQ4031", $"Comparison '{op}' in query '{file.Name}' needs one value on its right side.", values.Count == 0 ? pointer : pointer + "/right");
                    else
                        result.Right = values[0];
                    break;
            }

            return result;
        }

        private static RQueryExpression Null() => new() { Node = "null", Null = true, Nullable = true };

        private RQueryExpression Expression(QueryExpression e, Scope scope, string pointer)
        {
            var listOperand = ReferenceEquals(e, _listOperand);
            _listOperand = null; // only the operand itself, not what it holds
            if (e.Column is { } column)
                return Column(column, scope, pointer + "/column");
            if (e.Param is { } name)
            {
                if (!_parameters.TryGetValue(name, out var parameter))
                {
                    Report("MQ4024", $"Query '{file.Name}' uses parameter '{name}', which it does not declare.", pointer + "/param");
                    return new RQueryExpression { Node = "param", Param = new RQueryParameter { Name = name } };
                }

                if (parameter.Collection && !listOperand)
                    Report("MQ4039", $"Query '{file.Name}' uses list parameter '{name}' as one value: a list parameter is only the whole right side of in or notIn.", pointer + "/param");
                return new RQueryExpression
                {
                    Node = "param", Param = parameter, Type = parameter.Type, NativeType = parameter.NativeType, CodeType = parameter.CodeType, Nullable = false,
                };
            }

            if (e.Value is { } value)
            {
                if (e.Type is { } typed)
                    return TypedLiteral(value, typed, pointer);
                if (!TryPlain(value, out var plain))
                {
                    Report("MQ4043", $"Query '{file.Name}' has a number literal too large to hold.", pointer + "/value");
                    return Null();
                }

                var keyword = plain switch
                {
                    string => "string",
                    bool => "bool",
                    long l => l is >= int.MinValue and <= int.MaxValue ? "int32" : "int64",
                    decimal => "decimal",
                    double => "double",
                    _ => null,
                };
                return new RQueryExpression
                {
                    Node = "value", Value = plain, Type = keyword, CodeType = keyword, NativeType = keyword is null ? null : run.KeywordNativeType(keyword), Nullable = false,
                };
            }

            if (e.Null is true)
                return Null();
            if (e.Op is { } op)
            {
                var args = (e.Args ?? []).Select((a, i) => Expression(a, scope, pointer + "/args/" + i.ToString(CultureInfo.InvariantCulture))).ToList();
                if (args.Count < 2 && !(op == "-" && args.Count == 1))
                    Report("MQ4042", $"Operation '{op}' in query '{file.Name}' has {(args.Count == 0 ? "no operands" : "one operand")}: it needs two or more{(op == "-" ? " (or one, to negate it)" : "")}.", pointer + "/args");
                var type = op == "concat" ? "string"
                    : args.Any(a => a.Type == "decimal") ? "decimal"
                    : args.Any(a => a.Type is "double" or "float") ? "double"
                    : args.Select(a => a.Type).FirstOrDefault(t => t is not null);
                return new RQueryExpression
                {
                    Node = "op", Op = op, Args = args, Type = type, CodeType = type, NativeType = type is null ? null : run.KeywordNativeType(type),
                    Nullable = args.Count == 0 || args.Any(a => a.Nullable),
                };
            }

            if (e.Call is { } call)
                return Call(call, e.Args ?? [], scope, pointer);
            if (e.Case is { } branches)
            {
                var whens = branches.Select((b, i) => new RQueryWhen
                {
                    When = Predicate(b.When, scope, pointer + "/case/" + i.ToString(CultureInfo.InvariantCulture) + "/when"),
                    Then = Expression(b.Then, scope, pointer + "/case/" + i.ToString(CultureInfo.InvariantCulture) + "/then"),
                }).ToList();
                var otherwise = e.Else is { } elseExpression ? Expression(elseExpression, scope, pointer + "/else") : null;
                var typed = whens.Select(w => w.Then).Append(otherwise).FirstOrDefault(x => x?.Type is not null);
                return new RQueryExpression
                {
                    Node = "case", Case = whens, Else = otherwise, Type = typed?.Type, NativeType = typed?.NativeType, CodeType = typed?.CodeType,
                    Nullable = otherwise is null || otherwise.Nullable || whens.Any(w => w.Then.Nullable),
                };
            }

            if (e.Cast is { } operand)
            {
                var inner = Expression(operand, scope, pointer + "/cast");
                var keyword = e.Type ?? "string";
                // MySQL casts to a short list of types only, which its CAST spells differently from its column types.
                var native = run._dialect == "mysql" ? QuerySql.MySqlCastType(keyword, run.KeywordNativeType("decimal")) : run.KeywordNativeType(keyword);
                return new RQueryExpression
                {
                    Node = "cast", Cast = inner, Type = keyword, CodeType = keyword, NativeType = native, Nullable = inner.Nullable,
                };
            }

            if (e.Sql is { } sql)
            {
                var text = QuerySql.SqlFor(sql, run._dialect);
                if (text is null)
                    Report("MQ4029", $"Query '{file.Name}' has an sql expression without a text for {run._dialect} (database '{run._db.Name}') and no \"*\" text.", pointer + "/sql");
                return new RQueryExpression { Node = "sql", Sql = sql.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal), SqlText = text, Nullable = true };
            }

            return Null();
        }

        private RQueryExpression Call(string call, IReadOnlyList<QueryExpression> arguments, Scope scope, string pointer)
        {
            var args = arguments.Select((a, i) => Expression(a, scope, pointer + "/args/" + i.ToString(CultureInfo.InvariantCulture))).ToList();
            var result = new RQueryExpression { Node = "call", Call = call, Args = args };
            if (routines.TryGetValue(call, out var routine))
            {
                result.Routine = routine;
                Use(routine);
                result.Type = routine.Returns?.Type;
                result.NativeType = routine.Returns is { Table: null } returns ? returns.NativeType : null;
                result.CodeType = routine.Returns?.Type ?? (routine.Returns?.DbType is { TypeKind: "domain", Base: { } baseType } ? baseType : null);
                return result;
            }

            if (IdFormat.IsValid(call))
            {
                Report("MQ4021", $"Query '{file.Name}' calls '{call}', which {WhatIsRoutine(call)}.", pointer + "/call");
                return result;
            }

            if (!FunctionNamePattern.IsMatch(call))
            {
                Report("MQ4034", $"Query '{file.Name}' calls '{call}', which is neither a function name (letters, digits and underscores, optionally schema.name) nor a routine id.", pointer + "/call");
                return result;
            }

            var first = args.FirstOrDefault();
            (result.Type, result.Nullable) = call.ToLowerInvariant() switch
            {
                "count" => ("int64", false),
                "sum" => (first?.Type is "int16" or "int32" ? "int64" : first?.Type, true),
                "avg" => (first?.Type is "double" or "float" ? "double" : "decimal", true),
                "min" or "max" => (first?.Type, true),
                "lower" or "upper" => (first?.Type ?? "string", first?.Nullable ?? true),
                "length" => ("int32", first?.Nullable ?? true),
                "coalesce" => (args.Select(a => a.Type).FirstOrDefault(t => t is not null), args.Count == 0 || args.All(a => a.Nullable)),
                "now" => ("datetime", false),
                _ => (null, true),
            };
            if (call.ToLowerInvariant() is "min" or "max" or "lower" or "upper" or "coalesce" && args.FirstOrDefault(a => a.Type == result.Type && a.NativeType is not null) is { } typed)
            {
                result.NativeType = typed.NativeType;
                result.CodeType = typed.CodeType;
            }
            else
            {
                result.NativeType = result.Type is { } keyword ? run.KeywordNativeType(keyword) : null;
                result.CodeType = result.Type;
            }

            return result;
        }

        private string WhatIsRoutine(string id)
        {
            var element = run._run.Model.Get<Element>(id);
            return element switch
            {
                Routine r => $"is routine '{r.Name}' of database '{run._run.Model.Get<Database>(r.Database)?.Name ?? r.Database}', not of '{run._db.Name}'",
                null => $"is no routine of database '{run._db.Name}'",
                _ => $"is {element.KindName} '{element.Name}', not a routine",
            };
        }

        private RQueryExpression Column(string text, Scope scope, string pointer)
        {
            var dot = text.IndexOf('.', StringComparison.Ordinal);
            RQuerySource? source = null;
            Scope? owner = null;
            string name = text;
            if (dot > 0 && Find(scope, text[..dot]) is { } aliased)
            {
                (source, owner) = aliased;
                name = text[(dot + 1)..];
            }
            else if (dot > 0 && !IdFormat.IsValid(text[..dot]) && !text[..dot].Contains('@', StringComparison.Ordinal))
            {
                var alias = text[..dot];
                Report(scope.Outer is null ? "MQ4022" : "MQ4028",
                    scope.Outer is null
                        ? $"Query '{file.Name}' names column '{text}', but no source of the query has the alias '{alias}'."
                        : $"A nested query of query '{file.Name}' names column '{text}', but neither it nor an enclosing query has the alias '{alias}'.",
                    pointer);
                return new RQueryExpression { Node = "column", Column = text, Alias = alias, ColumnName = text[(dot + 1)..] };
            }

            RColumn? tableColumn = null;
            RViewColumn? viewColumn = null;
            RQueryField? namedField = null;
            if (source is not null)
            {
                (tableColumn, viewColumn, namedField) = FindColumn(source, name);
                if (tableColumn is null && viewColumn is null && namedField is null && source.Name.Length > 0 && (source.Table is not null || source.View is not null || source.Named is not null))
                    Report("MQ4023", $"Query '{file.Name}' names column '{name}' of '{source.Alias}' ({(source.Named is not null ? "named query" : source.View is null ? "table" : "view")} '{source.Name}'), which has no such column.", pointer);
            }
            else
            {
                // No alias: the one source of this query (or of the nearest enclosing one) that has the column.
                for (var s = scope; s is not null && source is null; s = s.Outer)
                {
                    var matches = s.Sources.Select(x => (Source: x, Found: FindColumn(x, text))).Where(m => m.Found.Table is not null || m.Found.View is not null || m.Found.Named is not null).ToList();
                    if (matches.Count > 1)
                    {
                        Report("MQ4023", $"Query '{file.Name}' names column '{text}' without an alias, and {string.Join(" and ", matches.Select(m => "'" + m.Source.Alias + "'"))} both have one: write alias.column.", pointer);
                        return new RQueryExpression { Node = "column", Column = text, ColumnName = text };
                    }

                    if (matches.Count == 1)
                    {
                        (source, owner) = (matches[0].Source, s);
                        (tableColumn, viewColumn, namedField) = matches[0].Found;
                    }
                }

                if (source is null)
                {
                    Report("MQ4023", $"Query '{file.Name}' names column '{text}', which no source of the query has.", pointer);
                    return new RQueryExpression { Node = "column", Column = text, ColumnName = text };
                }
            }

            var result = new RQueryExpression
            {
                Node = "column",
                Column = text,
                Alias = source.Alias,
                ColumnName = tableColumn?.Name ?? viewColumn?.Name ?? namedField?.Name ?? name,
                TableColumn = tableColumn,
                ViewColumn = viewColumn,
                NamedField = namedField,
                Source = source,
                IsOuter = !ReferenceEquals(owner, scope),
            };
            if (tableColumn is not null)
            {
                result.Type = tableColumn.Type == "reference" && tableColumn.CodeType is { } code ? code : tableColumn.Type;
                result.NativeType = tableColumn.NativeType;
                result.CodeType = tableColumn.DbType is { TypeKind: "domain", Base: { } baseType } ? baseType
                    : tableColumn.DbType is { TypeKind: "enum" } ? "string" : result.Type;
                result.Nullable = tableColumn.Nullable || source.Optional;
            }
            else if (viewColumn is not null)
            {
                result.Type = viewColumn.Type;
                result.NativeType = viewColumn.NativeType;
                result.CodeType = viewColumn.Type;
                result.Nullable = viewColumn.Nullable || source.Optional;
            }
            else if (namedField is not null)
            {
                result.Type = namedField.Type;
                result.NativeType = namedField.NativeType;
                result.CodeType = namedField.CodeType;
                result.Nullable = namedField.Nullable || source.Optional;
            }

            return result;
        }

        private static (RQuerySource Source, Scope Owner)? Find(Scope scope, string alias)
        {
            for (var s = scope; s is not null; s = s.Outer)
            {
                if (s.Aliases.TryGetValue(alias, out var source))
                    return (source, s);
            }

            return null;
        }

        /// <summary>
        /// A column of a source by physical name, column key or attribute id; else by name ignoring case, then ignoring case and
        /// underscores, when only one column matches (so a change of the conventions' column case keeps a query valid).
        /// </summary>
        private static (RColumn? Table, RViewColumn? View, RQueryField? Named) FindColumn(RQuerySource source, string name)
        {
            if (source.Table is { } table)
            {
                var column = table.Columns.FirstOrDefault(c => c.Name == name)
                    ?? table.Columns.FirstOrDefault(c => c.Key == name)
                    ?? table.Columns.FirstOrDefault(c => c.Attribute?.Id == name)
                    ?? Loose(table.Columns, c => c.Name, name);
                return (column, null, null);
            }

            if (source.View is { } view)
                return (null, view.Columns.FirstOrDefault(c => c.Name == name) ?? Loose(view.Columns, c => c.Name, name), null);
            if (source.Named is { } named)
                return (null, null, named.Columns.FirstOrDefault(c => c.Name == name) ?? Loose(named.Columns, c => c.Name, name));
            return (null, null, null);
        }

        private static T? Loose<T>(IEnumerable<T> columns, Func<T, string> nameOf, string name) where T : class
        {
            var ignoringCase = columns.Where(c => string.Equals(nameOf(c), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (ignoringCase.Count > 0)
                return ignoringCase.Count == 1 ? ignoringCase[0] : null;
            var folded = Fold(name);
            var matches = columns.Where(c => Fold(nameOf(c)) == folded).Take(2).ToList();
            return matches.Count == 1 ? matches[0] : null;

            static string Fold(string text) => text.Replace("_", "", StringComparison.Ordinal).ToUpperInvariant();
        }

        /// <summary>
        /// A JSON literal as a plain value: a string, a long (or decimal, or double), a bool; false for a number no double holds
        /// (<c>1e400</c>).
        /// </summary>
        private static bool TryPlain(JsonElement value, out object? plain)
        {
            plain = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when value.TryGetInt64(out var l) => l,
                JsonValueKind.Number when value.TryGetDecimal(out var d) => d,
                JsonValueKind.Number when value.TryGetDouble(out var x) && double.IsFinite(x) => x,
                _ => null,
            };
            return value.ValueKind != JsonValueKind.Number || plain is not null;
        }

        /// <summary>
        /// A typed literal: <c>{ "value": "2.0", "type": "decimal" }</c>, a decimal number written as text so that its digits (a
        /// fractional zero) survive editors whose numbers do not keep them; SQL writes the text without leading zeros. MQ4043 for another
        /// type or a text that is not a decimal number.
        /// </summary>
        private RQueryExpression TypedLiteral(JsonElement value, string type, string pointer)
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : value.GetRawText();
            if (type != "decimal" || !DecimalPattern.IsMatch(text) || !decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
            {
                Report("MQ4043", $"Query '{file.Name}' has the literal '{text}' typed '{type}': a typed literal is a decimal number (type decimal) written as text.", pointer + (type != "decimal" ? "/type" : "/value"));
                return Null();
            }

            return new RQueryExpression
            {
                Node = "value", Value = number, LiteralText = DecimalText(text), Type = "decimal", CodeType = "decimal", NativeType = run.KeywordNativeType("decimal"), Nullable = false,
            };
        }

        /// <summary>A decimal number's text as SQL writes it: the sign (none for zero), the integer digits without leading zeros, the fraction as written.</summary>
        internal static string DecimalText(string text)
        {
            var negative = text.StartsWith('-');
            var digits = negative ? text[1..] : text;
            var dot = digits.IndexOf('.', StringComparison.Ordinal);
            var whole = (dot < 0 ? digits : digits[..dot]).TrimStart('0');
            var fraction = dot < 0 ? "" : digits[dot..];
            var result = (whole.Length == 0 ? "0" : whole) + fraction;
            return negative && result.Any(c => c is >= '1' and <= '9') ? "-" + result : result;
        }
    }
}
