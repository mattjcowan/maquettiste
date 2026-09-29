using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// Rules on reference types, their use as attribute types and seeds (reference-types-seeds-localization.md sections 1.7 and 2.4):
/// MQ7001 to MQ7012, MQ7101 to MQ7106, and the MQ3019 extension (a default code outside the rows). Pure functions of the model;
/// each diagnostic lands in the file where the fix belongs, and a finding across seeds of one target on the later one in
/// (seed name, seed id, file order).
/// </summary>
internal static class ReferenceDataRules
{
    /// <summary>The row count above which a seed is bulk data (MQ7104).</summary>
    internal const int MaxRows = 10_000;

    /// <summary>The file size above which a seed is bulk data (MQ7104).</summary>
    internal const int MaxBytes = 5 * 1024 * 1024;

    private static readonly string[] CodeTypes = ["string", "int16", "int32", "int64"];

    private static readonly string[] Keywords = ["code", "label", "description"];

    private enum ColumnKind { Code, Label, Description, Attribute, End }

    /// <summary>One seed column, resolved against the seed's target.</summary>
    private sealed record SeedColumn(ColumnKind Kind, ModelAttribute? Attribute = null, RelationEnd? End = null, ReferenceType? Type = null, EffectiveType? Effective = null);

    private static string Str(int i) => i.ToString(CultureInfo.InvariantCulture);

    // ---- reference types: MQ7010, MQ7007, MQ7008 ----

    /// <summary>Checks a reference type's built-in fields, user fields and storage choices.</summary>
    public static void CheckReferenceType(ValidationContext context, ReferenceType type, Report report)
    {
        if (!CodeTypes.Contains(type.Code.Type, StringComparer.Ordinal))
            report.Add("MQ7010", $"The code of reference type '{type.Name}' has the type '{type.Code.Type}'; a code is a string, int16, int32 or int64.", "/code/type", type.Code.Id);
        if (type.Code.Pattern is { } pattern && AttributeRules.MatchesPattern("", pattern) is null)
            report.Add("MQ7010", $"The code pattern of reference type '{type.Name}' is not a valid regular expression.", "/code/pattern", type.Code.Id);

        for (var i = 0; i < type.Attributes.Count; i++)
        {
            var attribute = type.Attributes[i];
            var pointer = "/attributes/" + Str(i);
            if (Keywords.Contains(attribute.Name, StringComparer.OrdinalIgnoreCase))
                report.Add("MQ7010", $"Field '{attribute.Name}' of reference type '{type.Name}' uses a reserved name: code, label and description are built in.", pointer + "/name", attribute.Id);
            if (attribute.Type.Ref is { } typeId && context.Model.TryGetEntry(typeId, out var entry) && entry.Kind is "value-object" or "entity")
                report.Add("MQ7010", $"Field '{attribute.Name}' of reference type '{type.Name}' is typed by {Article(entry.Kind)} {entry.Kind}; a row stays one flat line, so a field is a built-in, a scalar, an enum or a reference type.", pointer + "/type", attribute.Id);
            AttributeRules.Check(context, attribute, pointer, report);
        }

        foreach (var (key, choice) in type.Storage.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            CheckChoice(context.Model.Settings, choice, "/storage/" + Ptr.Escape(key), $"Reference type '{type.Name}'", report.Add);
    }

    /// <summary>Checks one storage choice against the project's strategy declarations (MQ7007, MQ7008).</summary>
    /// <param name="settings">The project settings.</param>
    /// <param name="choice">The choice.</param>
    /// <param name="pointer">The choice's pointer.</param>
    /// <param name="subject">Who makes the choice, for messages.</param>
    /// <param name="add">Adds a diagnostic (rule, message, pointer).</param>
    public static void CheckChoice(ProjectSettings settings, StorageChoice choice, string pointer, string subject, Action<string, string, string, string?> add)
    {
        if (choice.Strategy is not { } name)
            return;
        if (!settings.ReferenceData.Strategies.TryGetValue(name, out var declaration))
        {
            var declared = settings.ReferenceData.Strategies.Keys.Order(StringComparer.Ordinal).ToList();
            add("MQ7007", $"{subject} chooses the storage strategy '{name}', which the project does not declare in referenceData.strategies ({(declared.Count == 0 ? "none declared" : "declared: " + string.Join(", ", declared))}).", pointer + "/strategy", null);
            return;
        }

        foreach (var reason in OptionErrors(declaration, choice.Options))
            add("MQ7008", $"{subject}: the options of strategy '{name}' are invalid: {reason.Message}.", pointer + "/options" + (reason.Key is null ? "" : "/" + Ptr.Escape(reason.Key)), null);
    }

    private static IEnumerable<(string? Key, string Message)> OptionErrors(StrategyDeclaration declaration, IReadOnlyDictionary<string, JsonElement> options)
    {
        var schema = declaration.Options is { ValueKind: JsonValueKind.Object } o ? o : (JsonElement?)null;
        foreach (var (key, value) in options.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (schema is not { } properties || !properties.TryGetProperty(key, out var property))
            {
                yield return (key, $"'{key}' is not an option of the strategy");
                continue;
            }

            if (property.ValueKind == JsonValueKind.Object && property.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                && !FitsJsonType(t.GetString()!, value))
            {
                yield return (key, $"'{key}' must be of type {t.GetString()}");
            }
        }

        foreach (var required in declaration.Required)
        {
            if (!options.ContainsKey(required))
                yield return (null, $"the required option '{required}' is missing");
        }
    }

    private static bool FitsJsonType(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    // ---- settings: MQ7007, MQ7008 for project and database choices (MQ7012, the retired enum lookup storage, is refused at load) ----

    /// <summary>Checks the storage choices of <c>maquettiste.json</c> (whole-model runs only).</summary>
    /// <param name="model">The snapshot.</param>
    /// <returns>The diagnostics, on the settings file.</returns>
    public static IEnumerable<Diagnostic> CheckSettings(ModelSnapshot model)
    {
        var diagnostics = new List<Diagnostic>();
        var settings = model.Settings;
        void Add(string rule, string message, string pointer, string? _) =>
            diagnostics.Add(RuleCatalog.Create(rule, message, null, SettingsPath(model), pointer));
        if (settings.Conventions.ReferenceStorage is { } project)
            CheckChoice(settings, project, "/conventions/referenceStorage", "The project", Add);
        foreach (var (name, conventions) in settings.Databases.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (conventions.ReferenceStorage is { } choice)
                CheckChoice(settings, choice, "/databases/" + Ptr.Escape(name) + "/referenceStorage", $"Database '{name}'", Add);
        }

        return diagnostics;
    }

    /// <summary>The repo path of <c>maquettiste.json</c>, next to the <c>model</c> folder the documents live in.</summary>
    internal static string SettingsPath(ModelSnapshot model)
    {
        var sample = model.Documents.Count > 0 ? model.Documents[0].Path : null;
        var at = sample?.IndexOf("model/", StringComparison.Ordinal) ?? -1;
        return (at >= 0 ? sample![..at] : ".maquettiste/") + ModelPaths.SettingsFile;
    }

    // ---- attributes typed by a reference type: MQ3019, MQ7011, MQ7006 ----

    /// <summary>Checks an attribute typed by a reference type: its default codes, allowed codes and collection storage.</summary>
    public static void CheckUsage(ValidationContext context, ModelAttribute attribute, ReferenceType type, string pointer, Report report)
    {
        if (attribute.Default is { } literal && literal.ValueKind != JsonValueKind.Null)
        {
            var codes = attribute.Collection && literal.ValueKind == JsonValueKind.Array ? [.. literal.EnumerateArray()] : new[] { literal };
            foreach (var code in codes)
            {
                if (CodeProblem(context, type, code) is { } problem)
                {
                    report.Add("MQ3019", $"The default of attribute '{attribute.Name}' does not fit reference type {type.Name}: {problem}.", pointer + "/default", attribute.Id);
                    break;
                }
            }
        }

        if (attribute.Validation is { } validation)
        {
            for (var j = 0; j < validation.AllowedValues.Count; j++)
            {
                if (CodeProblem(context, type, validation.AllowedValues[j]) is { } problem)
                    report.Add("MQ7011", $"validation.allowedValues of attribute '{attribute.Name}' names a code that reference type {type.Name} does not have: {problem}.", pointer + "/validation/allowedValues/" + Str(j), attribute.Id);
            }
        }

        if (!attribute.Collection)
            return;
        foreach (var database in context.Model.All<Database>())
        {
            var (choice, source) = EffectiveStorage(context.Model.Settings, type, database);
            if (choice?.Strategy is not { } strategy || !context.Model.Settings.ReferenceData.Strategies.TryGetValue(strategy, out var declaration))
                continue;
            var dialect = DialectInfo.Name(database.Dialect);
            if (!declaration.Collections.For(dialect))
            {
                report.Add("MQ7006", $"Attribute '{attribute.Name}' is a collection of {type.Name}, but in database '{database.Name}' the type is stored with strategy '{strategy}' (chosen by {source}), which declares no collection support for {dialect}.", pointer + "/collection", attribute.Id);
            }
        }
    }

    /// <summary>
    /// The effective storage choice of a reference type in a database, and where it came from: the type's entry for the database,
    /// the type's <c>*</c> entry, the database's conventions, the project's conventions, else none (template-defined).
    /// </summary>
    public static (StorageChoice? Choice, string? Source) EffectiveStorage(ProjectSettings settings, ReferenceType type, Database database)
    {
        if (type.Storage.TryGetValue(database.Id, out var own))
            return (own, $"the type's storage for '{database.Name}'");
        if (type.Storage.TryGetValue("*", out var any))
            return (any, "the type's storage '*'");
        if (settings.Databases.TryGetValue(database.Name, out var conventions) && conventions.ReferenceStorage is { } byDatabase)
            return (byDatabase, $"databases.{database.Name}.referenceStorage");
        if (settings.Conventions.ReferenceStorage is { } project)
            return (project, "conventions.referenceStorage");
        return (null, null);
    }

    /// <summary>Why a value is not a code of a type's rows, or <see langword="null"/> when it is.</summary>
    private static string? CodeProblem(ValidationContext context, ReferenceType type, JsonElement value)
    {
        var integer = type.Code.Type != "string";
        if (integer ? value.ValueKind != JsonValueKind.Number : value.ValueKind != JsonValueKind.String)
            return $"a code of {type.Name} is {(integer ? "an integer" : "a string")}, not {Describe(value)}";
        if (context.ReferenceData.RowOf(type.Id, value) is null)
            return $"'{ReferenceDataIndex.CodeKey(value)}' is not a code of its rows";
        return null;
    }

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "null",
    };

    // ---- enum lookup storage on a mapping: MQ7012 ----

    // ---- seeds: MQ7001 to MQ7005, MQ7009, MQ7101 to MQ7106 ----

    /// <summary>Checks a seed against its target.</summary>
    public static void CheckSeed(ValidationContext context, ElementDocument document, Seed seed, Report report)
    {
        var model = context.Model;
        if (seed.Rows.Count > MaxRows || (seed.Rows.Count > 1000 && document.Json.GetRawText().Length > MaxBytes))
            report.Add("MQ7104", $"Seed '{seed.Name}' holds {seed.Rows.Count} rows; seeds are reference data, not bulk data (at most {MaxRows:N0} rows or 5 MB).", "/rows");

        var target = model.GetDocument(seed.Target)?.Element;
        if (target is not (Entity or Relation or ReferenceType))
            return; // MQ2001 or MQ2002
        if (target is Entity { Abstract: true } abstractEntity)
            report.Add("MQ7101", $"Seed '{seed.Name}' targets the abstract entity '{abstractEntity.Name}'; seed one of its concrete subtypes.", "/target");

        var columns = ResolveColumns(context, seed, target, report);
        CheckMissingColumns(context, seed, target, columns, report);
        CheckRows(context, seed, target, columns, report);
        if (target is ReferenceType type)
            CheckCodes(context, seed, type, columns, report);
        if (target is Entity entity)
            CheckKeys(context, seed, entity, columns, report);
        for (var r = 0; r < seed.Rows.Count; r++)
        {
            if (context.IsCyclicRow(seed.Rows[r].Id))
                report.Add("MQ7103", $"Row {seed.Rows[r].Id} is part of a cycle of required row references, so no insert order exists; the rows keep file order.", "/rows/" + Str(r), seed.Rows[r].Id);
        }
    }

    private static SeedColumn?[] ResolveColumns(ValidationContext context, Seed seed, Element target, Report report)
    {
        var model = context.Model;
        var columns = new SeedColumn?[seed.Columns.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var attributes = target switch
        {
            Entity e => context.Flatten(e).Select(f => f.Attribute).ToList(),
            Relation r => [.. r.Attributes, .. context.VirtualAttributes(r).Select(f => f.Attribute)],
            ReferenceType t => [.. t.Attributes, .. context.VirtualAttributes(t).Select(f => f.Attribute)],
            _ => [],
        };
        for (var k = 0; k < seed.Columns.Count; k++)
        {
            var name = seed.Columns[k];
            var pointer = "/columns/" + Str(k);
            if (!seen.Add(name))
            {
                report.Add("MQ7005", $"Seed '{seed.Name}' lists the column '{name}' twice.", pointer);
                continue;
            }

            if (Keywords.Contains(name, StringComparer.Ordinal))
            {
                if (target is ReferenceType)
                    columns[k] = new SeedColumn(name switch { "code" => ColumnKind.Code, "label" => ColumnKind.Label, _ => ColumnKind.Description });
                else
                    report.Add("MQ7005", $"The column '{name}' is built into reference types; {target.KindName} '{target.Name}' has no such column.", pointer);
                continue;
            }

            if (attributes.FirstOrDefault(a => a.Id == name) is { } attribute)
            {
                var effective = AttributeRules.Resolve(model, attribute.Type);
                var referenceType = attribute.Type.Ref is { } typeId ? model.Get<ReferenceType>(typeId) : null;
                columns[k] = new SeedColumn(ColumnKind.Attribute, attribute, Type: referenceType, Effective: effective);
                continue;
            }

            if (EndOf(context, name, target) is { } found)
            {
                var (relation, end) = found;
                if (target is Entity && end.Max == MaxCardinality.Many)
                {
                    report.Add("MQ7105", $"The column for end '{end.Role}' of relation '{relation.Name}' names a to-many end; the links belong in a seed of the relation.", pointer);
                    continue;
                }

                if (target is Entity && context.SeedsOf(relation.Id).Length > 0)
                {
                    report.Add("MQ7106", $"The links of relation '{relation.Name}' are stated twice: this seed has a column for its end '{end.Role}', and the relation has seeds of its own. Keep one.", pointer);
                    continue;
                }

                columns[k] = new SeedColumn(ColumnKind.End, End: end);
                continue;
            }

            report.Add("MQ7005", $"The column '{name}' names neither a field nor an end of {target.KindName} '{target.Name}'; remove the stale column.", pointer);
        }

        return columns;
    }

    /// <summary>The relation end a column names, when it belongs to the target: an end of the relation itself, or for an entity the
    /// far end of a binary relation whose near end is the entity or one of its ancestors.</summary>
    private static (Relation Relation, RelationEnd End)? EndOf(ValidationContext context, string id, Element target)
    {
        if (!context.Model.TryGetEntry(id, out var entry) || entry.Kind != "end" || context.Model.Get<Relation>(entry.OwnerId) is not { } relation)
            return null;
        var index = -1;
        for (var i = 0; i < relation.Ends.Count; i++)
        {
            if (relation.Ends[i].Id == id)
                index = i;
        }

        if (index < 0)
            return null;
        if (target is Relation r)
            return r.Id == relation.Id ? (relation, relation.Ends[index]) : null;
        if (target is not Entity entity || relation.Ends.Count != 2)
            return null;
        var near = relation.Ends[1 - index].Entity;
        var lineage = context.Ancestors(entity).Select(a => a.Id).Prepend(entity.Id);
        return lineage.Contains(near, StringComparer.Ordinal) ? (relation, relation.Ends[index]) : null;
    }

    private static bool IsGeneratedKey(ValidationContext context, Element target, string attributeId) =>
        target is Entity entity && KeyOf(context, entity) is { } key && key.Attributes.Contains(attributeId, StringComparer.Ordinal);

    private static EntityKey? KeyOf(ValidationContext context, Entity entity) =>
        entity.Key ?? context.Ancestors(entity).Select(a => a.Key).FirstOrDefault(k => k is not null);

    private static bool Needed(ModelAttribute attribute) => attribute.Required && attribute.Default is null && attribute.DefaultExpression is null;

    private static void CheckMissingColumns(ValidationContext context, Seed seed, Element target, SeedColumn?[] columns, Report report)
    {
        bool Has(Func<SeedColumn, bool> test) => columns.Any(c => c is not null && test(c));
        if (target is ReferenceType)
        {
            if (!Has(c => c.Kind == ColumnKind.Code))
                report.Add("MQ7003", $"Seed '{seed.Name}' has no code column; every row of a reference type needs a code.", "/columns");
            if (!Has(c => c.Kind == ColumnKind.Label))
                report.Add("MQ7003", $"Seed '{seed.Name}' has no label column; every row of a reference type needs a label.", "/columns");
        }

        var required = target switch
        {
            Entity e => context.Flatten(e).Select(f => f.Attribute),
            Relation r => r.Attributes,
            ReferenceType t => t.Attributes,
            _ => [],
        };
        foreach (var attribute in required)
        {
            if (Needed(attribute) && !IsGeneratedKey(context, target, attribute.Id) && !Has(c => c.Attribute?.Id == attribute.Id))
                report.Add("MQ7003", $"Seed '{seed.Name}' has no column for the required attribute '{attribute.Name}', which has no default.", "/columns");
        }

        if (target is Relation relation)
        {
            foreach (var end in relation.Ends)
            {
                if (!Has(c => c.End?.Id == end.Id))
                    report.Add("MQ7003", $"Seed '{seed.Name}' has no column for end '{end.Role}'; a relation seed names both ends of each link.", "/columns");
            }
        }
    }

    private static void CheckRows(ValidationContext context, Seed seed, Element target, SeedColumn?[] columns, Report report)
    {
        for (var r = 0; r < seed.Rows.Count; r++)
        {
            var row = seed.Rows[r];
            var rowPointer = "/rows/" + Str(r);
            if (row.Values.Count > columns.Length)
                report.Add("MQ7005", $"Row {row.Id} has {row.Values.Count} values for {columns.Length} columns.", rowPointer + "/values", row.Id);
            for (var k = 0; k < columns.Length; k++)
            {
                if (columns[k] is not { } column)
                    continue;
                var cell = k < row.Values.Count ? row.Values[k] : default;
                var pointer = rowPointer + "/values/" + Str(k);
                var isNull = cell.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;
                if (CellProblem(context, seed, target, column, cell, isNull) is { } problem)
                    report.Add(problem.Rule, $"Row {row.Id}, column '{ColumnName(column)}': {problem.Message}.", pointer, row.Id);
            }
        }
    }

    private static string ColumnName(SeedColumn column) => column.Kind switch
    {
        ColumnKind.Code => "code",
        ColumnKind.Label => "label",
        ColumnKind.Description => "description",
        ColumnKind.Attribute => column.Attribute!.Name,
        _ => column.End!.Role,
    };

    private static (string Rule, string Message)? CellProblem(ValidationContext context, Seed seed, Element target, SeedColumn column, JsonElement cell, bool isNull)
    {
        switch (column.Kind)
        {
            case ColumnKind.Code:
            {
                if (isNull)
                    return ("MQ7003", "a reference row needs a code");
                var type = (ReferenceType)target;
                var keyword = CodeTypes.Contains(type.Code.Type, StringComparer.Ordinal) ? type.Code.Type : "string";
                if (AttributeRules.Fits(keyword, cell, type.Code.Length, null, null) is { } reason)
                    return ("MQ7004", reason);
                if (type.Code.Pattern is { } pattern && cell.ValueKind == JsonValueKind.String && AttributeRules.MatchesPattern(cell.GetString()!, pattern) == false)
                    return ("MQ7004", $"the code does not match the pattern {pattern}");
                return null;
            }

            case ColumnKind.Label:
            {
                if (isNull)
                    return ("MQ7003", "a reference row needs a label");
                return AttributeRules.Fits("string", cell, ((ReferenceType)target).Label.Length, null, null) is { } reason ? ("MQ7004", reason) : null;
            }

            case ColumnKind.Description:
                return isNull ? null : AttributeRules.Fits("text", cell, null, null, null) is { } why ? ("MQ7004", why) : null;

            case ColumnKind.End:
            {
                var end = column.End!;
                if (isNull)
                    return end.Min >= 1 || target is Relation ? ("MQ7003", $"end '{end.Role}' is required") : null;
                if (cell.ValueKind != JsonValueKind.String)
                    return ("MQ7004", $"an end cell holds a row id, not {Describe(cell)}");
                var rowId = cell.GetString()!;
                if (context.SeedOfRow(rowId) is not { } holder || !IsEntityOrDescendant(context, holder.Target, end.Entity))
                {
                    var entityName = context.Model.Get<Entity>(end.Entity)?.Name ?? end.Entity;
                    return ("MQ7009", $"'{rowId}' is not a row of a seed of entity '{entityName}'");
                }

                return null;
            }
        }

        var attribute = column.Attribute!;
        if (isNull)
        {
            return Needed(attribute) && !IsGeneratedKey(context, target, attribute.Id) && !IsKeyOf(context, target, attribute.Id)
                ? ("MQ7003", $"the attribute '{attribute.Name}' is required and has no default")
                : null;
        }

        if (column.Type is { } referenceType)
        {
            if (attribute.Collection && cell.ValueKind != JsonValueKind.Array)
                return ("MQ7004", "a collection cell is an array of codes");
            foreach (var code in attribute.Collection ? [.. cell.EnumerateArray()] : new[] { cell })
            {
                var integer = referenceType.Code.Type != "string";
                if (integer ? code.ValueKind != JsonValueKind.Number : code.ValueKind != JsonValueKind.String)
                    return ("MQ7004", $"a code of {referenceType.Name} is {(integer ? "an integer" : "a string")}, not {Describe(code)}");
                if (context.ReferenceData.RowOf(referenceType.Id, code) is null)
                    return ("MQ7009", $"'{ReferenceDataIndex.CodeKey(code)}' is not a code of reference type {referenceType.Name}");
                if (attribute.Validation is { AllowedValues.Count: > 0 } validation && !validation.AllowedValues.Any(a => JsonElement.DeepEquals(a, code)))
                    return ("MQ7004", $"'{ReferenceDataIndex.CodeKey(code)}' is not one of validation.allowedValues");
            }

            return null;
        }

        return AttributeRules.DefaultMismatch(column.Effective!, attribute, cell) is { } mismatch ? ("MQ7004", $"the value does not fit {column.Effective!.Describe()}: {mismatch}") : null;
    }

    private static bool IsKeyOf(ValidationContext context, Element target, string attributeId) =>
        target is Entity entity && KeyOf(context, entity) is { Strategy: IdentityStrategy.Application } key && key.Attributes.Contains(attributeId, StringComparer.Ordinal);

    private static bool IsEntityOrDescendant(ValidationContext context, string entityId, string expected) =>
        entityId == expected || (context.Model.Get<Entity>(entityId) is { } entity && context.Ancestors(entity).Any(a => a.Id == expected));

    private static void CheckCodes(ValidationContext context, Seed seed, ReferenceType type, SeedColumn?[] columns, Report report)
    {
        var k = Array.FindIndex(columns, c => c?.Kind == ColumnKind.Code);
        if (k < 0)
            return;
        // Earlier occurrences: rows of the seeds before this one (in (name, id) order), then earlier rows of this seed.
        var ordinal = new Dictionary<string, (string Seed, string Row)>(StringComparer.Ordinal);
        var folded = new Dictionary<string, (string Code, string Seed, string Row)>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in context.SeedsOf(type.Id))
        {
            var otherK = IndexOf(other.Columns, "code");
            for (var r = 0; r < other.Rows.Count; r++)
            {
                var row = other.Rows[r];
                if (otherK < 0 || otherK >= row.Values.Count || ReferenceDataIndex.CodeKey(row.Values[otherK]) is not { } code)
                    continue;
                if (other.Id == seed.Id)
                {
                    var pointer = "/rows/" + Str(r) + "/values/" + Str(otherK);
                    if (ordinal.TryGetValue(code, out var first))
                    {
                        report.Add("MQ7001", $"Code '{code}' of reference type {type.Name} is already used by row {first.Row}{(first.Seed == seed.Name ? "" : " in seed '" + first.Seed + "'")}.", pointer, row.Id);
                        continue;
                    }

                    if (row.Values[otherK].ValueKind == JsonValueKind.String && folded.TryGetValue(code, out var near) && near.Code != code)
                        report.Add("MQ7002", $"Code '{code}' of reference type {type.Name} differs only by case from '{near.Code}' (row {near.Row}).", pointer, row.Id);
                }

                ordinal.TryAdd(code, (other.Name, row.Id));
                folded.TryAdd(code, (code, other.Name, row.Id));
            }

            if (other.Id == seed.Id)
                break;
        }
    }

    private static void CheckKeys(ValidationContext context, Seed seed, Entity entity, SeedColumn?[] columns, Report report)
    {
        var keys = new List<(string Name, IReadOnlyList<string> Attributes, bool Primary)>();
        if (KeyOf(context, entity) is { } key)
            keys.Add(("primary key", key.Attributes, true));
        foreach (var alternate in context.Ancestors(entity).Reverse().SelectMany(a => a.AlternateKeys).Concat(entity.AlternateKeys))
            keys.Add(("alternate key '" + alternate.Name + "'", alternate.Attributes, false));

        foreach (var (name, attributes, primary) in keys)
        {
            var indexes = attributes.Select(a => Array.FindIndex(columns, c => c?.Attribute?.Id == a)).ToArray();
            var application = primary && KeyOf(context, entity)!.Strategy == IdentityStrategy.Application;
            if (application && indexes.Any(i => i < 0))
            {
                report.Add("MQ7102", $"Seed '{seed.Name}' has no column for part of the {name} of '{entity.Name}', whose values the application assigns.", "/columns");
                continue;
            }

            if (indexes.Any(i => i < 0))
                continue;
            var seen = new Dictionary<string, (string Seed, string Row)>(StringComparer.Ordinal);
            foreach (var other in context.SeedsOf(seed.Target))
            {
                var otherIndexes = attributes.Select(a => IndexOf(other.Columns, a)).ToArray();
                for (var r = 0; r < other.Rows.Count; r++)
                {
                    var row = other.Rows[r];
                    string? tuple = otherIndexes.Any(i => i < 0) ? null : string.Join("\u0000", otherIndexes.Select(i => i < row.Values.Count ? Normalize(row.Values[i]) : "null"));
                    var missing = tuple is null || otherIndexes.Any(i => i >= row.Values.Count || row.Values[i].ValueKind == JsonValueKind.Null);
                    if (other.Id == seed.Id)
                    {
                        if (missing && application)
                        {
                            report.Add("MQ7102", $"Row {row.Id} has no value for the {name} of '{entity.Name}', whose values the application assigns.", "/rows/" + Str(r), row.Id);
                            continue;
                        }

                        if (!missing && seen.TryGetValue(tuple!, out var first))
                        {
                            report.Add("MQ7102", $"Row {row.Id} repeats the {name} value of row {first.Row}{(first.Seed == seed.Name ? "" : " in seed '" + first.Seed + "'")}.", "/rows/" + Str(r), row.Id);
                            continue;
                        }
                    }

                    if (!missing)
                        seen.TryAdd(tuple!, (other.Name, row.Id));
                }

                if (other.Id == seed.Id)
                    break;
            }
        }
    }

    private static string Normalize(JsonElement value) => value.ValueKind == JsonValueKind.Number ? CanonicalNumberText(value.GetRawText()) : value.GetRawText();

    private static string CanonicalNumberText(string raw) =>
        decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d.ToString("G29", CultureInfo.InvariantCulture) : raw;

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    // ---- MQ7103: cycles of required row references ----

    /// <summary>
    /// The rows that lie on a cycle of required row references: required end cells, and required reference-typed cells of a
    /// reference type's seed (a code names another type's row, which orders lookup rows). Tarjan's algorithm over every seed row.
    /// </summary>
    internal static FrozenSet<string> CyclicRows(ValidationContext context)
    {
        var model = context.Model;
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (targetId, seeds) in context.AllSeeds)
        {
            var target = model.GetDocument(targetId)?.Element;
            foreach (var seed in seeds)
            {
                var required = new List<(int Index, Func<JsonElement, IEnumerable<string>> Targets)>();
                for (var k = 0; k < seed.Columns.Count; k++)
                {
                    var column = seed.Columns[k];
                    if (model.TryGetEntry(column, out var entry) && entry.Kind == "end" && model.Get<Relation>(entry.OwnerId) is { } relation
                        && relation.Ends.FirstOrDefault(e => e.Id == column) is { Min: >= 1 })
                    {
                        required.Add((k, cell => cell.ValueKind == JsonValueKind.String ? [cell.GetString()!] : []));
                    }
                    else if (target is ReferenceType owner && owner.Attributes.FirstOrDefault(a => a.Id == column) is { Required: true, Type.Ref: { } typeId }
                        && context.ReferenceData.ReferenceTypeIds.Contains(typeId))
                    {
                        required.Add((k, cell => (cell.ValueKind == JsonValueKind.Array ? [.. cell.EnumerateArray()] : new[] { cell })
                            .Select(code => context.ReferenceData.RowOf(typeId, code)).OfType<string>()));
                    }
                }

                if (required.Count == 0)
                    continue;
                foreach (var row in seed.Rows)
                {
                    foreach (var (index, targets) in required)
                    {
                        if (index >= row.Values.Count)
                            continue;
                        foreach (var to in targets(row.Values[index]))
                        {
                            if (!edges.TryGetValue(row.Id, out var list))
                                edges[row.Id] = list = [];
                            list.Add(to);
                        }
                    }
                }
            }
        }

        return Tarjan(edges).ToFrozenSet(StringComparer.Ordinal);
    }

    private static HashSet<string> Tarjan(Dictionary<string, List<string>> edges)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var counter = 0;
        foreach (var start in edges.Keys.Order(StringComparer.Ordinal))
        {
            if (index.ContainsKey(start))
                continue;
            // Iterative depth-first search: (node, next edge position).
            var work = new Stack<(string Node, int Next)>();
            work.Push((start, 0));
            index[start] = low[start] = counter++;
            stack.Push(start);
            onStack.Add(start);
            while (work.Count > 0)
            {
                var (node, next) = work.Pop();
                var targets = edges.TryGetValue(node, out var list) ? list : [];
                if (next < targets.Count)
                {
                    work.Push((node, next + 1));
                    var to = targets[next];
                    if (!index.ContainsKey(to))
                    {
                        index[to] = low[to] = counter++;
                        stack.Push(to);
                        onStack.Add(to);
                        work.Push((to, 0));
                    }
                    else if (onStack.Contains(to))
                    {
                        low[node] = Math.Min(low[node], index[to]);
                    }

                    continue;
                }

                if (work.Count > 0)
                {
                    var parent = work.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[node]);
                }

                if (low[node] != index[node])
                    continue;
                var component = new List<string>();
                string member;
                do
                {
                    member = stack.Pop();
                    onStack.Remove(member);
                    component.Add(member);
                }
                while (member != node);
                if (component.Count > 1 || (edges.TryGetValue(node, out var self) && self.Contains(node, StringComparer.Ordinal)))
                    result.UnionWith(component);
            }
        }

        return result;
    }

    private static string Article(string word) => word.Length > 0 && "aeiou".Contains(word[0], StringComparison.Ordinal) ? "an" : "a";
}
