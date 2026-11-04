using System.Text.Json;
using System.Text.Json.Serialization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine;

/// <summary>
/// What <see cref="ModelStore.PreviewExpressionAsync"/> previews (next-phase-3-design.md section 3.1, "CLI and MCP"): an expression an
/// element holds, at <see cref="At"/>, or <see cref="Text"/> written at a <see cref="Site"/> of the element and stored nowhere.
/// </summary>
public sealed record ExpressionPreviewRequest
{
    /// <summary>
    /// The element's id, or the id of the check, guard or derived attribute that holds the expression (a sub-element id names its own
    /// site, so <see cref="At"/> may be left out).
    /// </summary>
    public required string Element { get; init; }

    /// <summary>
    /// The JSON pointer of the stored expression in the element's file (<c>/checks/0/condition</c>, <c>/guards/1/condition</c>,
    /// <c>/attributes/3/derived/value</c>), or of the check, guard or attribute that holds it (<c>/checks/0</c>);
    /// <see langword="null"/> when the element or sub-element holds one expression, or with <see cref="Text"/>.
    /// </summary>
    public string? At { get; init; }

    /// <summary>The text to preview instead of a stored expression (<c>issuedOn &lt;= dueOn</c>); <see cref="Site"/> says where it goes.</summary>
    public string? Text { get; init; }

    /// <summary>
    /// With <see cref="Text"/>: <c>check</c> (a table, stereotype, entity or constraint), <c>value</c> (an entity's derived value, of any
    /// type) or <c>guard</c> (a process or an operation), or the pointer of one of the element's sites, whose scope and mode the text then
    /// takes (a JavaScript guard's <c>/guards/0/condition</c> reads the payloads of the events that use the guard).
    /// </summary>
    public string? Site { get; init; }

    /// <summary>
    /// The id or name of the database whose binding an entity's expression lowers to SQL through; <see langword="null"/> for the binding
    /// that lists the check, or the entity's only binding.
    /// </summary>
    public string? Database { get; init; }

    /// <summary>
    /// The row the expression reads, a JSON object of members by name: a table's column names (on a synthesized table, the names its
    /// text prints: an overlay's attributes as an entity's), an entity's attribute names (a value object's members nested), a process's
    /// context attributes with the event's payload under <c>event</c>, an operation's input members with the subject under
    /// <c>subject</c>. <see langword="null"/> previews no answer.
    /// </summary>
    public JsonElement? Values { get; init; }
}

/// <summary>One expression's forms (next-phase-3-design.md section 3.1, "CLI and MCP").</summary>
/// <param name="Text">The text, printed from current names.</param>
/// <param name="Type">The type as messages write it: <c>bool</c>, <c>int32</c>, <c>decimal</c>, <c>enum &lt;id&gt;</c>…</param>
/// <param name="Nullable">Whether the value may be null.</param>
/// <param name="JavaScript">The JavaScript function over a row, which calls the expression runtime as <c>mq</c>.</param>
/// <param name="Sql">The PostgreSQL text, as the site's table or its binding's table reads it; <see langword="null"/> when there is none.</param>
/// <param name="SqlNote">Why <paramref name="Sql"/> is <see langword="null"/>; <see langword="null"/> when there is SQL.</param>
/// <param name="Answer">
/// With values, the answer under the site's mode: a check's <see langword="true"/> when the row passes (true or unknown), a guard's
/// <see langword="true"/> only when it holds, a value's JSON form (a decimal or integer as exact text, a date or timestamp as ISO text)
/// or <see langword="null"/>; without values, <see langword="null"/>.
/// </param>
public sealed record ExpressionPreviewDetail(string Text, string Type, bool Nullable, [property: JsonPropertyName("javascript")] string JavaScript, string? Sql,
    string? SqlNote, object? Answer);

/// <summary>The answer of <see cref="ModelStore.PreviewExpressionAsync"/>.</summary>
/// <param name="Preview">The forms, or <see langword="null"/> when the expression does not parse or type (the diagnostics say why).</param>
/// <param name="Diagnostics">What parsing, typing, lowering and evaluating found, sorted.</param>
public sealed record ExpressionPreview(ExpressionPreviewDetail? Preview, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Previews one expression (next-phase-3-design.md section 3.1, decision 24): types it in its site's scope, prints it, lowers it to
/// JavaScript and, through the one table or binding its site reads, to PostgreSQL, and with values runs the JavaScript in the sandbox.
/// It reads the element, the elements its paths and names lead to and that one table; it neither validates nor resolves the model.
/// </summary>
public sealed partial class ModelStore
{
    /// <summary>The kinds <see cref="ExpressionPreviewRequest.Site"/> takes, by name.</summary>
    private static readonly IReadOnlyDictionary<string, ExprSiteKind> SiteKinds = new Dictionary<string, ExprSiteKind>(StringComparer.Ordinal)
    {
        ["check"] = ExprSiteKind.Check,
        ["guard"] = ExprSiteKind.Guard,
        ["value"] = ExprSiteKind.Value,
    };

    /// <summary>
    /// Previews one expression: a stored one (<see cref="ExpressionPreviewRequest.At"/>) or text at a site
    /// (<see cref="ExpressionPreviewRequest.Text"/>, <see cref="ExpressionPreviewRequest.Site"/>). Reads the store's snapshot (a stat
    /// rescan), never validates or resolves the model, and writes nothing.
    /// </summary>
    /// <param name="request">What to preview.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The forms and the diagnostics; <see cref="ExpressionPreview.Preview"/> is <see langword="null"/> when the expression does
    /// not parse or type.</returns>
    /// <exception cref="ArgumentException">The request names no element, no site or no database, holds both or neither of a stored
    /// expression and text, or its values are not an object.</exception>
    public async Task<ExpressionPreview> PreviewExpressionAsync(ExpressionPreviewRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Element))
            throw new ArgumentException("element is required: the id of the element, or of the check, guard or attribute, that holds the expression.", nameof(request));
        if (request.Text is null && request.Site is not null)
            throw new ArgumentException("site goes with text: pass at to preview a stored expression, or text and site to preview text.", nameof(request));
        if (request.Text is not null && (request.Site is null || request.At is not null))
            throw new ArgumentException("text needs site (check, value, guard, or the pointer of a site) and no at.", nameof(request));
        if (request.Values is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) })
            throw new ArgumentException("values must be a JSON object: the members the expression reads, by name.", nameof(request));

        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        if (snapshot.GetDocument(request.Element) is not { } document)
            throw new ArgumentException($"No element or sub-element has the id '{request.Element}'.", nameof(request));
        var element = document.Element;
        var sub = string.Equals(element.Id, request.Element, StringComparison.Ordinal) ? null : request.Element;
        var database = DatabaseOf(snapshot, request.Database);
        var site = SiteOf(snapshot, element, sub, request);

        var scope = ExprSites.Scope(snapshot, element, document, site, text: request.Text is not null);
        var problems = new List<Diagnostic>();
        Expr tree;
        if (request.Text is { } text)
        {
            var parsed = ExprText.Parse(text, scope);
            if (parsed.Tree is null)
                return new ExpressionPreview(null, [parsed.Problem ?? RuleCatalog.Create("MQ3101", "The expression '" + text + "' does not parse.", site.SubId, document.Path, site.Pointer)]);
            tree = parsed.Tree;
        }
        else
        {
            tree = site.Tree!;
        }

        var typed = ExprTyper.Type(tree, scope, problems);
        if (typed is null || problems.Any(p => p.Severity == DiagnosticSeverity.Error))
            return new ExpressionPreview(null, Outcomes.Sort(problems));

        var (mapping, note) = SqlMapping(snapshot, element, site, typed, database, document.Path);
        string? sql = null;
        if (mapping is not null)
        {
            var lowering = new List<Diagnostic>();
            sql = ExprLowering.ToPostgreSql(typed, mapping, site.Kind, lowering);
            problems.AddRange(lowering);
            if (sql is null)
                note = lowering.FirstOrDefault()?.Message ?? "The expression does not lower to SQL.";
        }

        // A table's checks read the row by column names; every other site reads the API's values by member name, and so does a synthesized
        // table's, whose column names only resolution knows: by the names its text prints (an attribute, a value object's members nested).
        var columns = element is Table { Origin: not TableOrigin.Synthesized } or Stereotype
            ? TableMapping(snapshot, element, null, site.SubId, document.Path)
            : ExprResolution.ApiColumns(snapshot, site.SubId, document.Path);
        var javaScript = ExprLowering.ToJavaScript(typed, site.Kind, columns);
        var answer = request.Values is { ValueKind: JsonValueKind.Object } values ? Answer(javaScript, values, problems, ct) : null;
        var detail = new ExpressionPreviewDetail(ExprText.Print(tree, scope), typed.Type.ToString(), typed.Nullable, javaScript, sql, sql is null ? note : null, answer);
        return new ExpressionPreview(detail, Outcomes.Sort(problems));
    }

    /// <summary>An element's kind after its article, to begin a sentence: "An entity", "A table".</summary>
    private static string AKind(Element element) => (BuiltinRules.Article(element.KindName) == "an" ? "An " : "A ") + element.KindName;

    /// <summary>The database a request names by id, else by name; <see langword="null"/> for none.</summary>
    private static Database? DatabaseOf(ModelSnapshot snapshot, string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        if (snapshot.Get<Database>(key) is { } byId)
            return byId;
        var named = snapshot.All<Database>().Where(d => string.Equals(d.Name, key, StringComparison.Ordinal)).ToList();
        return named.Count == 1 ? named[0] : throw new ArgumentException(named.Count == 0
            ? $"No database has the id or name '{key}'."
            : $"'{key}' names {named.Count} databases; pass one id.", nameof(key));
    }

    /// <summary>
    /// The site a request previews: a stored expression's (by the sub-element and the pointer, or the element's only one), or for text the
    /// site its pointer names or a free site of its kind.
    /// </summary>
    private static ExprSite SiteOf(ModelSnapshot snapshot, Element element, string? sub, ExpressionPreviewRequest request)
    {
        var sites = ExprSites.Of(snapshot, element);
        var pointer = request.Text is null ? request.At : request.Site!.StartsWith('/') ? request.Site : null;
        if (request.Text is not null && pointer is null)
        {
            if (!SiteKinds.TryGetValue(request.Site!, out var kind))
                throw new ArgumentException($"site must be check, value, guard or the pointer of a site, not '{request.Site}'.", nameof(request));
            return ExprSites.Free(snapshot, element, kind)
                ?? throw new ArgumentException($"{AKind(element)} has no {request.Site} site: a table, stereotype, entity or constraint has checks, an entity derived values, "
                    + "a process or an operation guards.", nameof(request));
        }

        var matches = sites.Where(s => (sub is null || string.Equals(s.SubId, sub, StringComparison.Ordinal))
            && (pointer is null || string.Equals(s.Pointer, pointer, StringComparison.Ordinal) || s.Pointer.StartsWith(pointer + "/", StringComparison.Ordinal))).ToList();
        if (matches.Count != 1)
        {
            var where = string.Join(", ", sites.Select(s => s.Pointer));
            throw new ArgumentException(matches.Count == 0
                ? $"'{pointer ?? sub ?? element.Id}' names no expression site of {element.KindName} '{element.Name}'" + (where.Length > 0 ? $"; its sites are {where}." : ", which has none.")
                : $"{element.KindName} '{element.Name}' holds {matches.Count} expression sites; pass at, one of {where}.", nameof(request));
        }

        var site = matches[0];
        if (request.Text is null && site.Tree is null)
        {
            throw new ArgumentException($"The site at {site.Pointer} holds no expression in the expression language" + (site.Raw ? ", only its raw form" : "")
                + $"; pass text with site {site.Pointer} to try one there.", nameof(request));
        }

        return site;
    }

    /// <summary>
    /// The mapping a site's expression lowers to PostgreSQL through, or why it has none: a table's own columns at its checks; for an
    /// entity's check or value, the columns its binding's field map names on the binding's write table (a check) or source table (a value).
    /// Guards, stereotypes and constraints read no one table.
    /// </summary>
    private static (ExprColumns? Mapping, string? Note) SqlMapping(ModelSnapshot snapshot, Element element, ExprSite site, TypedExpr typed, Database? database, string? path)
    {
        switch (element)
        {
            case Table table:
                if (table.Origin == TableOrigin.Synthesized)
                    return (null, $"Table '{table.Name}' is synthesized from a mapping, so its column names come from resolution; generate to see its CHECK.");
                if (database is not null && !string.Equals(database.Id, table.Database, StringComparison.Ordinal))
                    return (null, $"Table '{table.Name}' is not in database '{database.Name}'.");
                return (TableMapping(snapshot, table, snapshot.Get<Database>(table.Database), site.SubId, path), null);
            case Entity entity:
                return BindingMapping(snapshot, entity, site, typed, database, path);
            case Stereotype:
                return (null, "A stereotype's check lowers through each table that carries it; preview it at one of those tables.");
            case Constraint:
                return (null, "A constraint is inlined where a site cites it; preview a check that cites it for its SQL.");
            case Process:
                return (null, "A process guard reads the process's context and the event, not a table: it runs as JavaScript only.");
            default:
                return (null, $"{AKind(element)} guard reads its input, not a table: it runs as JavaScript only.");
        }
    }

    /// <summary>The mapping onto a table's columns (its own, and those its stereotypes contribute by keyed id), or a stereotype's own.</summary>
    private static ExprColumns TableMapping(ModelSnapshot snapshot, Element holder, Database? database, string elementId, string? path)
    {
        var byId = new Dictionary<string, Column>(StringComparer.Ordinal);
        if (holder is Table table)
        {
            foreach (var column in table.Columns)
                byId.TryAdd(column.Id, column);
            foreach (var inherited in TableMembers.Of(snapshot, table).Columns)
                byId.TryAdd(inherited.Member.Id, inherited.Member);
        }

        return new ExprColumns
        {
            Column = t => t.Member is { } m && byId.TryGetValue(m.Id, out var c) ? ColumnOf(c) : null,
            Ref = (id, form) => ExprResolution.RefLiteral(snapshot, id, form),
            Physical = true,
            Dialect = database is null ? "postgresql" : DialectTypeMaps.Name(database.Dialect),
            Quoting = database?.Quoting ?? Quoting.Reserved,
            ElementId = elementId,
            FilePath = path,
        };
    }

    /// <summary>
    /// The mapping of an entity's check or value through one binding: the binding on <paramref name="database"/>, else for a check the
    /// first that lists it, else the entity's only binding. A path whose attribute the binding maps to no column of the table, or a
    /// binding that writes or reads no table file, has no SQL.
    /// </summary>
    private static (ExprColumns? Mapping, string? Note) BindingMapping(ModelSnapshot snapshot, Entity entity, ExprSite site, TypedExpr typed, Database? database, string? path)
    {
        EntityBinding? binding;
        if (database is not null)
        {
            binding = entity.Bindings.FirstOrDefault(b => string.Equals(b.Database, database.Id, StringComparison.Ordinal));
            if (binding is null)
                return (null, $"Entity '{entity.Name}' has no binding to database '{database.Name}'.");
        }
        else
        {
            binding = (site.Kind == ExprSiteKind.Check ? entity.Bindings.FirstOrDefault(b => b.Checks.Contains(site.SubId, StringComparer.Ordinal)) : null)
                ?? (entity.Bindings.Count == 1 ? entity.Bindings[0] : null);
            if (binding is null)
            {
                return (null, entity.Bindings.Count == 0 ? $"Entity '{entity.Name}' has no binding, so no table holds its values."
                    : site.Kind == ExprSiteKind.Check ? "No binding lists the check, so no database enforces it; pass a database to see the SQL through its binding."
                    : $"Entity '{entity.Name}' is bound to several databases; pass a database to choose the binding.");
            }
        }

        var bound = snapshot.Get<Database>(binding.Database);
        var tableId = site.Kind == ExprSiteKind.Check ? (binding.Write is { None: true } ? null : binding.Write?.Table ?? binding.Source) : binding.Source;
        if ((tableId is null ? null : snapshot.Get<Table>(tableId)) is not { Origin: not TableOrigin.Synthesized } table)
        {
            return (null, $"The binding of entity '{entity.Name}' to database '{bound?.Name ?? binding.Database}' "
                + (site.Kind == ExprSiteKind.Check ? "writes no table file" : "reads no table file") + ", so no table holds the columns the expression reads.");
        }

        var columns = new Dictionary<string, ExprColumn>(StringComparer.Ordinal);
        foreach (var id in ExprResolution.Paths(typed))
        {
            var field = binding.Fields.FirstOrDefault(f => string.Equals(f.Attribute, id, StringComparison.Ordinal));
            if ((field is null ? null : FindColumn(snapshot, table, field.Column)) is not { } column)
            {
                var name = Member(typed, id)?.Name ?? id;
                return (null, $"The binding of entity '{entity.Name}' to database '{bound?.Name ?? binding.Database}' maps '{name}' to no column of table '{table.Name}'.");
            }

            columns[id] = ColumnOf(column);
        }

        return (new ExprColumns
        {
            Column = t => t.Member is { } m && columns.TryGetValue(m.Id, out var c) ? c : null,
            Ref = (id, form) => ExprResolution.RefLiteral(snapshot, id, form),
            Dialect = bound is null ? "postgresql" : DialectTypeMaps.Name(bound.Dialect),
            Quoting = bound?.Quoting ?? Quoting.Reserved,
            ElementId = site.SubId,
            FilePath = path,
        }, null);
    }

    /// <summary>A table's column a binding field names: by id, by a contributed column's keyed id, by name, then by name ignoring case.</summary>
    private static Column? FindColumn(ModelSnapshot snapshot, Table table, string reference)
    {
        var all = table.Columns.Concat(TableMembers.Of(snapshot, table).Columns.Select(m => m.Member)).ToList();
        return all.FirstOrDefault(c => string.Equals(c.Id, reference, StringComparison.Ordinal))
            ?? all.FirstOrDefault(c => string.Equals(c.Name, reference, StringComparison.Ordinal))
            ?? (all.Where(c => string.Equals(c.Name, reference, StringComparison.OrdinalIgnoreCase)).ToList() is [var one] ? one : null);
    }

    /// <summary>The member a typed tree reads under an id, for messages.</summary>
    private static ExprMember? Member(TypedExpr typed, string id) =>
        typed.Member is { } m && string.Equals(m.Id, id, StringComparison.Ordinal) ? m : typed.Args.Select(a => Member(a, id)).FirstOrDefault(m => m is not null);

    /// <summary>What a path reads in a column of a table file: its name, padding, scale and the form it stores enum members and codes in.</summary>
    private static ExprColumn ColumnOf(Column column) => new(column.Name)
    {
        FixedLength = column.FixedLength || (column.NativeType?.StartsWith("char(", StringComparison.OrdinalIgnoreCase) ?? false),
        Scale = column.Scale,
        Form = column.Type switch
        {
            "int16" or "int32" or "int64" => ExprStoredForm.Integer,
            "uuid" or "ulid" => ExprStoredForm.Uuid,
            _ => ExprStoredForm.Text,
        },
    };

    /// <summary>Runs the lowered function over the values in the sandbox; a failure becomes its diagnostic and no answer.</summary>
    private static object? Answer(string javaScript, JsonElement values, List<Diagnostic> problems, CancellationToken ct)
    {
        using var evaluator = ExprEvaluator.Create([KeyValuePair.Create("preview", javaScript)], null, ct);
        try
        {
            return evaluator.Evaluate("preview", values, ct);
        }
        catch (ScriptErrorException ex)
        {
            problems.Add(ex.Diagnostic);
        }
        catch (ScriptLimitException ex)
        {
            problems.Add(ex.Diagnostic);
        }

        return null;
    }
}
