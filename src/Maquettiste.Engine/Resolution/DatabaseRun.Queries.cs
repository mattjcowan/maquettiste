using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// Queries of one database (added 2026-10-02, engine-design.md section 7, "Queries"): each file resolved once the database's tables,
/// views, routines and database types exist, its sources to tables and views, its column references to their columns, its parameters
/// to native types, its trees to <see cref="RQueryExpression"/> and <see cref="RQueryPredicate"/>, its collections to correlated
/// statements with their keys. What a file gets wrong is reported here (MQ4021 to MQ4031, MQ4018 and MQ3001), where the columns are
/// known, with the JSON pointer of the offending node.
/// </summary>
internal sealed partial class DatabaseRun
{
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
            var query = new RQuery { Id = file.Id, Name = file.Name, Database = _rdb, Settings = _run.Settings, Definition = file };
            _run.FillPhysicalAnnotations(query, file, deps);
            new QueryBuilder(this, file, query, deps, sources, routines).Build();
            query.Dependencies = deps.ToList();
            _queries.Add((query, deps));
            _run.Register(query);
        }
    }

    /// <summary>The native type of a built-in keyword with the conventions' facet defaults.</summary>
    private string KeywordNativeType(string keyword)
    {
        var withDefaults = new RColumn { Type = keyword };
        ApplyFacetDefaults(withDefaults);
        return NativeType(keyword, withDefaults.Length, withDefaults.Precision, withDefaults.Scale);
    }

    /// <summary>The resolution of one query file.</summary>
    private sealed class QueryBuilder(
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

        /// <summary>The column references that reach past the current collection's query into its parent (a correlation), with their pointers.</summary>
        private List<(RQueryExpression Expression, string Pointer)>? _parentReferences;

        /// <summary>The scope a collection's nested query starts at: references to scopes outside it are correlations.</summary>
        private Scope? _barrier;

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
            query.Entity = file.Entity is { } entityId ? run._run.EntityById(entityId) : null;
            if (query.Entity is { } entity)
                AddEntityDependencies(entity);
            query.Parameters = ResolveParameters();
            var scope = new Scope(query, null);
            ResolveBody(query, scope, file.From, file.Joins, file.Select, file.Where, file.GroupBy, file.Having, file.OrderBy, "", query.Entity);
            query.Distinct = file.Distinct;
            query.Paging = ResolvePaging();
            query.Collections = [.. file.Collections.Select((c, i) => ResolveCollection(c, i, scope))];
            query.Uses = [.. _uses];
            // A query with errors has none: they are reported above, and generation stops on them before any template reads it.
            query.Sql = _failed ? "" : QuerySql.Render(query).Sql;
        }

        private void Report(string rule, string message, string pointer)
        {
            // Duplicate names and unselected attributes leave the query renderable; every other finding leaves a node unresolved.
            if (rule is not ("MQ3001" or "MQ4025" or "MQ4026" or "MQ4027" or "MQ4030"))
                _failed = true;
            run._run.AddDiagnostic(rule, message, file.Id, pointer);
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

        private void AddEntityDependencies(REntity entity)
        {
            for (var e = entity; e is not null; e = e.Base)
                deps.Element(e.Id);
        }

        private List<RQueryParameter> ResolveParameters()
        {
            var list = new List<RQueryParameter>();
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
                    NativeType = native,
                    CodeType = keyword ?? (dbType is { TypeKind: "domain", Base: { } baseType } ? baseType : "string"),
                    Collection = p.Collection,
                    Default = p.Default is { } value ? Plain(value) : null,
                    Description = p.Description,
                };
                if (!_parameters.TryAdd(p.Name, resolved))
                    Report("MQ3001", $"Parameter name '{p.Name}' is already used in query '{file.Name}'.", pointer + "/name");
                list.Add(resolved);
            }

            return list;
        }

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
            QueryPredicate? where, IReadOnlyList<QueryExpression> groupBy, QueryPredicate? having, IReadOnlyList<QueryOrder> orderBy, string at, REntity? entity)
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
                if (join.On is { } on && kind != "cross")
                    source.On = Predicate(on, scope, pointer + "/on");
                joined.Add(source);
            }

            target.Joins = joined;
            target.Select = ResolveFields(select, scope, at, entity);
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
            else
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
            return $"is no table or view of database '{run._db.Name}'";
        }

        private List<RQueryField> ResolveFields(IReadOnlyList<QueryField> select, Scope scope, string at, REntity? entity)
        {
            var fields = new List<RQueryField>();
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < select.Count; i++)
            {
                var f = select[i];
                var pointer = at + "/select/" + i.ToString(CultureInfo.InvariantCulture);
                RAttribute? attribute = null;
                if (f.Attribute is { } attributeId)
                {
                    attribute = entity?.Attributes.FirstOrDefault(a => a.Id == attributeId);
                    if (entity is null)
                        Report("MQ4025", $"Field {i} of query '{file.Name}' names an attribute, but the query{(at.Length > 0 ? " collection" : "")} names no entity.", pointer + "/attribute");
                    else if (attribute is null)
                        Report("MQ4025", $"Field {i} of query '{file.Name}' names attribute '{attributeId}', which entity '{entity.Name}' does not have.", pointer + "/attribute");
                }

                var expression = Expression(f.Expression, scope, pointer + "/expression");
                var field = new RQueryField
                {
                    Name = f.Name ?? attribute?.Name ?? f.Attribute ?? "",
                    Attribute = attribute,
                    Expression = expression,
                    Type = f.Type ?? expression.Type,
                    NativeType = f.Type is { } declared ? run.KeywordNativeType(declared) : expression.NativeType,
                    CodeType = f.Type ?? expression.CodeType,
                    Nullable = f.Nullable ?? expression.Nullable,
                };
                if (!names.TryAdd(field.Name, i))
                    Report("MQ3001", $"Field name '{field.Name}' is already used at index {names[field.Name]} of the select list of query '{file.Name}'.", pointer + (f.Name is null ? "/attribute" : "/name"));
                fields.Add(field);
            }

            // A value object attribute spans several columns, which one field cannot fill, so it is not asked for.
            if (entity is not null)
            {
                var selected = fields.Select(f => f.Attribute).OfType<RAttribute>().Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
                var missing = entity.Attributes.Where(a => a.Required && !a.Collection && a.Derived is null && a.Type.Kind != "value-object" && !selected.Contains(a.Id)).Select(a => a.Name).ToList();
                if (missing.Count > 0)
                    Report("MQ4026", $"Query '{file.Name}' selects no field for the required {(missing.Count == 1 ? "attribute" : "attributes")} {string.Join(", ", missing.Select(m => "'" + m + "'"))} of entity '{entity.Name}'.", at + "/select");
            }

            return fields;
        }

        private RQueryCollection ResolveCollection(QueryCollection c, int index, Scope parent)
        {
            var pointer = "/collections/" + index.ToString(CultureInfo.InvariantCulture);
            var result = new RQueryCollection { Index = index, Definition = c };
            var entity = query.Entity;
            if (entity is not null && entity.Attributes.FirstOrDefault(a => a.Id == c.Attribute) is { } attribute)
            {
                result.Attribute = attribute;
                result.Name = attribute.Name;
                if (!attribute.Collection)
                    Report("MQ4027", $"Collection {index} of query '{file.Name}' names attribute '{attribute.Name}' of entity '{entity.Name}', which is not a collection.", pointer + "/attribute");
            }
            else if (entity is not null && run._run.InheritedNavigations(entity.Id).FirstOrDefault(n => n.To.Id == c.Attribute || n.Id == c.Attribute) is { } navigation)
            {
                result.Navigation = navigation;
                result.Name = navigation.Name;
                deps.Element(navigation.Relation.Id);
                if (!navigation.IsCollection)
                    Report("MQ4027", $"Collection {index} of query '{file.Name}' names navigation '{navigation.Name}' of entity '{entity.Name}', which leads to one row, not a collection.", pointer + "/attribute");
            }
            else if (IdFormat.IsValid(c.Attribute))
            {
                result.Name = c.Attribute;
                Report("MQ4027", entity is null
                    ? $"Collection {index} of query '{file.Name}' names '{c.Attribute}', but the query names no entity: give an ad hoc collection a name."
                    : $"Collection {index} of query '{file.Name}' names '{c.Attribute}', which is neither a collection attribute nor a to-many navigation of entity '{entity.Name}'.",
                    pointer + "/attribute");
            }
            else
            {
                result.Name = c.Attribute;
            }

            result.Entity = c.Entity is { } entityId ? run._run.EntityById(entityId) : result.Navigation?.Target;
            if (result.Entity is { } element)
                AddEntityDependencies(element);

            var nested = new RQuery
            {
                Id = query.Id, Name = result.Name, Database = query.Database, Entity = result.Entity, Parent = query, Settings = query.Settings, Parameters = query.Parameters,
                Definition = c.Query,
            };
            var scope = new Scope(nested, parent);
            var (savedBarrier, savedReferences) = (_barrier, _parentReferences);
            _barrier = scope;
            _parentReferences = [];
            var q = c.Query;
            ResolveBody(nested, scope, q.From, q.Joins, q.Select, q.Where, q.GroupBy, q.Having, q.OrderBy, pointer + "/query", result.Entity);
            nested.Distinct = q.Distinct;
            var references = _parentReferences;
            (_barrier, _parentReferences) = (savedBarrier, savedReferences);
            result.Query = nested;
            if (nested.Select.Count == 0)
                Report("MQ4027", $"Collection '{result.Name}' of query '{file.Name}' selects nothing.", pointer + "/query");

            // The correlation: equalities between a column of the parent and a value of the nested query, at the top of its where.
            var keys = new List<RQueryKey>();
            var keyed = new HashSet<RQueryExpression>();
            if (nested.Where is { } where)
            {
                foreach (var conjunct in where.Node == "and" ? where.And : [where])
                {
                    if (conjunct is not { Node: "compare", Op: "eq", Left: { } left, Right: { } right })
                        continue;
                    var (outer, inner) = IsParentColumn(left, references) && !HasParentReference(right, references) ? (left, right)
                        : IsParentColumn(right, references) && !HasParentReference(left, references) ? (right, left)
                        : (null, null);
                    if (outer is null || inner is null)
                        continue;
                    var k = keys.Count.ToString(CultureInfo.InvariantCulture);
                    var parentField = query.Select.FirstOrDefault(f => f.Expression is { Node: "column" } e && ReferenceEquals(e.Source, outer.Source) && e.ColumnName == outer.ColumnName);
                    keys.Add(new RQueryKey
                    {
                        Outer = outer,
                        Inner = inner,
                        ParentField = parentField?.Name ?? "__key" + index.ToString(CultureInfo.InvariantCulture) + "_" + k,
                        Hidden = parentField is null,
                        ChildField = "__key" + k,
                        Parameter = "__keys" + k,
                        Type = outer.Type,
                        NativeType = outer.NativeType,
                        CodeType = outer.CodeType,
                    });
                    keyed.Add(outer);
                    result.KeyConjuncts.Add(conjunct);
                }
            }

            foreach (var (expression, at) in references.Where(r => !keyed.Contains(r.Expression)))
                Report("MQ4028", $"Collection '{result.Name}' of query '{file.Name}' names its parent's column '{expression.Column}' outside an equality at the top of its where, so it cannot run once for every parent row.", at);
            if (keys.Count == 0 && nested.Select.Count > 0)
                Report("MQ4028", $"Collection '{result.Name}' of query '{file.Name}' does not name its parent: add an equality between one of its columns and a parent column at the top of its where.", pointer + "/query");
            result.Keys = keys;
            return result;
        }

        private static bool IsParentColumn(RQueryExpression expression, List<(RQueryExpression Expression, string Pointer)> references) =>
            expression.Node == "column" && references.Any(r => ReferenceEquals(r.Expression, expression));

        private static bool HasParentReference(RQueryExpression expression, List<(RQueryExpression Expression, string Pointer)> references) =>
            references.Any(r => ReferenceEquals(r.Expression, expression)) || expression.Args.Any(a => HasParentReference(a, references))
            || (expression.Cast is { } cast && HasParentReference(cast, references));

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
                ResolveBody(nested, inner, exists.From, exists.Joins, [], exists.Where, exists.GroupBy, exists.Having, [], pointer + "/exists", null);
                return new RQueryPredicate { Node = "exists", Exists = nested };
            }

            var op = p.Op ?? "eq";
            var result = new RQueryPredicate { Node = "compare", Op = op, Left = p.Left is null ? Null() : Expression(p.Left, scope, pointer + "/left") };
            var right = p.Right ?? [];
            var values = right.Select((x, i) => Expression(x, scope, right.Count == 1 && p.Right is not null ? pointer + "/right" : pointer + "/right/" + i.ToString(CultureInfo.InvariantCulture))).ToList();
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
            if (e.Column is { } column)
                return Column(column, scope, pointer + "/column");
            if (e.Param is { } name)
            {
                if (!_parameters.TryGetValue(name, out var parameter))
                {
                    Report("MQ4024", $"Query '{file.Name}' uses parameter '{name}', which it does not declare.", pointer + "/param");
                    return new RQueryExpression { Node = "param", Param = new RQueryParameter { Name = name } };
                }

                return new RQueryExpression
                {
                    Node = "param", Param = parameter, Type = parameter.Type, NativeType = parameter.NativeType, CodeType = parameter.CodeType, Nullable = false,
                };
            }

            if (e.Value is { } value)
            {
                var plain = Plain(value);
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
                return new RQueryExpression
                {
                    Node = "cast", Cast = inner, Type = keyword, CodeType = keyword, NativeType = run.KeywordNativeType(keyword), Nullable = inner.Nullable,
                };
            }

            if (e.Sql is { } sql)
            {
                var text = run.ForDialect(sql);
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
                result.NativeType = routine.Returns is { Table: null } returns && returns.NativeType.Length > 0 ? returns.NativeType : null;
                result.CodeType = routine.Returns?.Type ?? (routine.Returns?.DbType is { TypeKind: "domain", Base: { } baseType } ? baseType : null);
                return result;
            }

            if (IdFormat.IsValid(call))
            {
                Report("MQ4021", $"Query '{file.Name}' calls '{call}', which {WhatIsRoutine(call)}.", pointer + "/call");
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
            if (source is not null)
            {
                (tableColumn, viewColumn) = FindColumn(source, name);
                if (tableColumn is null && viewColumn is null && source.Name.Length > 0 && (source.Table is not null || source.View is not null))
                    Report("MQ4023", $"Query '{file.Name}' names column '{name}' of '{source.Alias}' ({(source.View is null ? "table" : "view")} '{source.Name}'), which has no such column.", pointer);
            }
            else
            {
                // No alias: the one source of this query (or of the nearest enclosing one) that has the column.
                for (var s = scope; s is not null && source is null; s = s.Outer)
                {
                    var matches = s.Sources.Select(x => (Source: x, Found: FindColumn(x, text))).Where(m => m.Found.Table is not null || m.Found.View is not null).ToList();
                    if (matches.Count > 1)
                    {
                        Report("MQ4023", $"Query '{file.Name}' names column '{text}' without an alias, and {string.Join(" and ", matches.Select(m => "'" + m.Source.Alias + "'"))} both have one: write alias.column.", pointer);
                        return new RQueryExpression { Node = "column", Column = text, ColumnName = text };
                    }

                    if (matches.Count == 1)
                    {
                        (source, owner) = (matches[0].Source, s);
                        (tableColumn, viewColumn) = matches[0].Found;
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
                ColumnName = tableColumn?.Name ?? viewColumn?.Name ?? name,
                TableColumn = tableColumn,
                ViewColumn = viewColumn,
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

            if (_barrier is not null && owner is not null && !IsWithin(owner, _barrier))
                _parentReferences?.Add((result, pointer));
            return result;
        }

        /// <summary>Whether <paramref name="scope"/> is <paramref name="barrier"/> or nested inside it.</summary>
        private static bool IsWithin(Scope scope, Scope barrier)
        {
            for (var s = scope; s is not null; s = s.Outer)
            {
                if (ReferenceEquals(s, barrier))
                    return true;
            }

            return false;
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
        private static (RColumn? Table, RViewColumn? View) FindColumn(RQuerySource source, string name)
        {
            if (source.Table is { } table)
            {
                var column = table.Columns.FirstOrDefault(c => c.Name == name)
                    ?? table.Columns.FirstOrDefault(c => c.Key == name)
                    ?? table.Columns.FirstOrDefault(c => c.Attribute?.Id == name)
                    ?? Loose(table.Columns, c => c.Name, name);
                return (column, null);
            }

            if (source.View is { } view)
                return (null, view.Columns.FirstOrDefault(c => c.Name == name) ?? Loose(view.Columns, c => c.Name, name));
            return (null, null);
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

        /// <summary>A JSON literal as a plain value: a string, a long (or decimal, or double), a bool.</summary>
        private static object? Plain(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out var l) => l,
            JsonValueKind.Number when value.TryGetDecimal(out var d) => d,
            JsonValueKind.Number => value.GetDouble(),
            _ => null,
        };
    }
}
