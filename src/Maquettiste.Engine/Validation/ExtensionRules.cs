using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Json.Schema;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The custom property schemas of one validation run (MQ5001, MQ5004, MQ2009, and MQ2018 for stereotype parameters). Each
/// <c>extensions/*.json</c>, and each stereotype's <c>parameters</c> (next-phase-1-design.md section 4.2), becomes
/// <c>{ "type": "object", "properties": …, "required": … }</c>, compiled once per set of files by the snapshot's
/// <see cref="PropertySchemaSet"/> under the base URI <c>https://maquettiste.invalid/extensions/&lt;file&gt;</c> (offline, D24).
/// The holders of properties are <see cref="PropertyHost.Of"/>'s. A holder's stereotypes are its effective set
/// (<see cref="ModelSnapshot.EffectiveStereotypes(Element)"/> for the element, defaults included;
/// <see cref="ModelSnapshot.EffectiveStereotypes(IReadOnlyList{string})"/> for a sub-element; implied ones included): they decide which schemas apply, and their
/// <c>defaultProperties</c> are merged under the holder's own values (implied first, then own, later wins; the holder's own values
/// win over all). Failures are leaf keyword failures only, as in <see cref="Json.ISchemaRegistry.Evaluate"/> (D34). Thread-safe
/// after construction.
/// </summary>
internal sealed class ExtensionSet
{
    /// <summary>The applicator keywords whose failures only summarize their children's (the leaves are reported).</summary>
    internal static readonly FrozenSet<string> ApplicatorKeywords =
        new[] { "properties", "items", "prefixItems", "allOf", "$ref", "additionalProperties", "patternProperties", "dependentSchemas", "then", "else" }
            .ToFrozenSet(StringComparer.Ordinal);

    private readonly List<(ExtensionDocument Document, JsonSchema Schema)> _extensions = [];
    private readonly List<(PropertySchemaSource Source, string Key, JsonSchema Schema)> _parameters = [];
    private readonly EvaluationOptions _evaluation = new() { OutputFormat = OutputFormat.List, Culture = CultureInfo.InvariantCulture };

    /// <summary>Builds the extension schemas, reporting invalid extension files.</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="fileDiagnostics">Receives MQ5004 and MQ2009 diagnostics for extension files, or <see langword="null"/> to drop
    /// them.</param>
    public ExtensionSet(ModelSnapshot model, List<Diagnostic>? fileDiagnostics)
    {
        var schemas = model.PropertySchemas;
        if (fileDiagnostics is not null)
            ReportKeywords(model, schemas, fileDiagnostics);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var document in model.Extensions)
        {
            var extension = document.Schema;
            void Invalid(string message, string pointer) =>
                fileDiagnostics?.Add(RuleCatalog.Create("MQ5004", message, null, document.Path, pointer));

            if (!names.TryAdd(extension.Name, document.Path))
                Invalid($"Extension name '{extension.Name}' is already used by {names[extension.Name]}.", "/name");
            for (var i = 0; i < extension.AppliesTo.Stereotypes.Count; i++)
            {
                var key = extension.AppliesTo.Stereotypes[i];
                if (model.GetStereotype(key) is null)
                    Invalid($"appliesTo names stereotype '{key}', which is not defined.", Ptr.At("/appliesTo/stereotypes", i));
            }

            if (extension.Properties.ValueKind != JsonValueKind.Object)
            {
                Invalid("'properties' must be an object of property schemas.", "/properties");
                continue;
            }

            for (var i = 0; i < extension.Required.Count; i++)
            {
                if (!extension.Properties.TryGetProperty(extension.Required[i], out _))
                    Invalid($"'required' names '{extension.Required[i]}', which 'properties' does not define.", Ptr.At("/required", i));
            }

            var (schema, error) = schemas.SourceOf(document.Path)?.Compiled ?? (null, "the file is not in the snapshot's schema set");
            if (schema is null)
            {
                Invalid($"The extension is not a valid JSON Schema: {error}", "/properties");
                continue;
            }

            _extensions.Add((document, schema));
        }

        // Stereotype parameters that compile (one that does not is MQ2019 on the stereotype).
        foreach (var source in schemas.Sources)
        {
            if (source.StereotypeKey is { } key && source.Compiled.Schema is { } compiled)
                _parameters.Add((source, key, compiled));
        }
    }

    /// <summary>The number of usable extensions.</summary>
    public int Count => _extensions.Count;

    /// <summary>
    /// MQ2009 for the extension files: each malformed or misplaced <c>x-ref</c> the schema set found, and each <c>of</c> that names
    /// no element of the model (the keyword is then ignored by no walk: its values are judged by validation).
    /// </summary>
    private static void ReportKeywords(ModelSnapshot model, PropertySchemaSet schemas, List<Diagnostic> fileDiagnostics)
    {
        fileDiagnostics.AddRange(schemas.Diagnostics);
        foreach (var source in schemas.Sources)
        {
            if (source.StereotypeKey is not null)
                continue; // a stereotype's keywords are judged with the stereotype (BuiltinRules.CheckStereotype)
            foreach (var (pointer, xref) in source.Keywords)
            {
                if (xref.Of is { } of && of != XRef.ElementScope && !(model.TryGetEntry(of, out var entry) && entry.OwnerId == of))
                    fileDiagnostics.Add(RuleCatalog.Create("MQ2009", $"x-ref of names {of}, which is no element of the model.", null, source.Path, pointer + "/of"));
            }
        }
    }

    /// <summary>
    /// Evaluates each holder of the element's properties (<see cref="PropertyHost.Of"/>) against the applicable extensions and the
    /// parameters of the stereotypes it carries.
    /// </summary>
    /// <param name="context">The validation context.</param>
    /// <param name="report">The report of the element's document.</param>
    public void Check(ValidationContext context, Report report)
    {
        if (_extensions.Count == 0 && _parameters.Count == 0)
            return;
        var element = report.Document.Element;
        foreach (var host in PropertyHost.Of(element, null))
        {
            // The element's set holds its defaults (next-phase-2-design.md section 3.1); a sub-element's is its own list's.
            var effective = host.Pointer.Length == 0 ? context.Model.EffectiveStereotypes(element) : context.Model.EffectiveStereotypes(host.Stereotypes);
            CheckItem(host.Id, effective, host.Stereotypes.Count > 0, host.Properties, host.Pointer, host.KindName, report);
        }
    }

    private void CheckItem(string? id, IReadOnlyList<AppliedStereotype> effective, bool hasList, IReadOnlyDictionary<string, JsonElement> properties,
        string pointer, string kind, Report report)
    {
        if (properties.Count == 0 && effective.Count == 0 && _extensions.Count == 0)
            return;
        var keys = effective.Count == 0 ? [] : effective.Select(s => s.Stereotype.Key).ToList();
        JsonElement? merged = null;
        foreach (var (document, schema) in _extensions)
        {
            var target = document.Schema.AppliesTo;
            if (target.Kinds.Count > 0 && !target.Kinds.Contains(kind, StringComparer.Ordinal))
                continue;
            if (target.Stereotypes.Count > 0 && !target.Stereotypes.Any(s => keys.Contains(s, StringComparer.Ordinal)))
                continue;
            merged ??= Merge(effective, properties);
            Evaluate(schema, merged.Value, "MQ5001", $"Extension '{document.Schema.Name}'", document.Path, "the properties do not match the extension schema", null);
        }

        if (keys.Count == 0)
            return;
        foreach (var (source, key, schema) in _parameters)
        {
            if (!source.AppliesTo(kind, keys))
                continue;
            merged ??= Merge(effective, properties);
            Evaluate(schema, merged.Value, "MQ2018", $"Stereotype '{key}' parameters", source.Path, "the properties do not match its parameters", key);
        }

        void Evaluate(JsonSchema schema, JsonElement values, string rule, string what, string path, string mismatch, string? stereotypeKey)
        {
            EvaluationResults results;
            try
            {
                results = schema.Evaluate(values, _evaluation);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                report.Add(rule == "MQ5001" ? "MQ5004" : "MQ2019", $"{what} ({path}) could not be evaluated: {e.Message}", pointer + "/properties", id);
                return;
            }

            if (results.IsValid)
                return;
            var any = false;
            foreach (var detail in results.Details ?? [])
            {
                if (detail.Errors is null || InsideDiscardedBranch(detail))
                    continue;
                foreach (var (keyword, message) in detail.Errors.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    if (ApplicatorKeywords.Contains(keyword))
                        continue;
                    var location = detail.InstanceLocation.ToString();
                    var text = keyword.Length == 0 ? message : keyword + ": " + message;
                    var (at, origin) = Locate(effective, hasList, properties, pointer, location, stereotypeKey);
                    report.Add(rule, $"{what}: properties{location} {text}{origin}", at, id);
                    any = true;
                }
            }

            if (!any)
                report.Add(rule, $"{what}: {mismatch}.", pointer + "/properties", id);
        }
    }

    /// <summary>
    /// The pointer of a failing property value: in the item's own <c>properties</c>, or, for a value that comes from a stereotype's
    /// <c>defaultProperties</c>, that stereotype's entry in the item's <c>stereotypes</c> list (for an implied stereotype, the entry
    /// of the stereotype that implies it): the file has no value to point at. A missing value a stereotype's parameters require
    /// (<paramref name="stereotypeKey"/>) points at that stereotype's entry too. A default stereotype has no entry: it points at the
    /// <c>stereotypes</c> list, or at the item when its file has none (next-phase-2-design.md section 3.1).
    /// </summary>
    private static (string Pointer, string Origin) Locate(IReadOnlyList<AppliedStereotype> effective, bool hasList,
        IReadOnlyDictionary<string, JsonElement> properties, string pointer, string location, string? stereotypeKey)
    {
        var segments = Ptr.Split(location);
        if (segments.Length == 0)
        {
            if (stereotypeKey is not null && effective.FirstOrDefault(s => s.Stereotype.Key == stereotypeKey) is { } carrier)
                return (EntryPointer(carrier), Implied(carrier));
            return (pointer + "/properties" + location, "");
        }

        if (properties.ContainsKey(segments[0]))
            return (pointer + "/properties" + location, "");
        for (var i = effective.Count - 1; i >= 0; i--)
        {
            var applied = effective[i];
            if (applied.Stereotype.DefaultProperties.ContainsKey(segments[0]))
                return (EntryPointer(applied), $" (the value comes from the defaultProperties of stereotype '{applied.Stereotype.Key}'{Implied(applied)})");
        }

        return (pointer + "/properties" + location, "");

        string EntryPointer(AppliedStereotype applied) =>
            applied.Entry >= 0 ? Ptr.At(pointer + "/stereotypes", applied.Entry) : hasList ? pointer + "/stereotypes" : pointer;

        static string Implied(AppliedStereotype applied) =>
            (applied.ImpliedBy is { } by ? $", which '{by}' implies" : "") + applied.Source switch
            {
                StereotypeSource.SchemaDefault => $", a default of database schema {applied.DefaultHolder}",
                StereotypeSource.PackageDefault => $", a default of package {applied.DefaultHolder}",
                _ => "",
            };
    }

    /// <summary>
    /// Merges the default properties of the item's effective stereotypes (implied first, then own; later wins) under the item's own
    /// properties.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="item">The element or sub-element.</param>
    /// <returns>The merged properties as a JSON object with ordinal key order.</returns>
    public static JsonElement Merge(ModelSnapshot model, ElementBase item)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(item);
        // A top-level element's set holds its defaults (next-phase-2-design.md section 3.1).
        return Merge(item is Element element ? model.EffectiveStereotypes(element) : model.EffectiveStereotypes(item.Stereotypes), item.Properties);
    }

    private static JsonElement Merge(IReadOnlyList<AppliedStereotype> effective, IReadOnlyDictionary<string, JsonElement> properties)
    {
        var merged = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var applied in effective)
        {
            foreach (var (name, value) in applied.Stereotype.DefaultProperties)
                merged[name] = value;
        }

        foreach (var (name, value) in properties)
            merged[name] = value;
        return JsonSerializer.SerializeToElement(merged);
    }

    /// <summary>Whether a result sits in a branch the evaluation discards (an <c>if</c> or <c>not</c>, an alternative that another
    /// one satisfied).</summary>
    /// <param name="node">The result.</param>
    /// <returns><see langword="true"/> when its failures do not count.</returns>
    internal static bool InsideDiscardedBranch(EvaluationResults node)
    {
        for (var child = node; child.Parent is { } parent; child = parent)
        {
            var parentPath = parent.EvaluationPath.ToString();
            var childPath = child.EvaluationPath.ToString();
            if (childPath.Length <= parentPath.Length + 1 || !childPath.StartsWith(parentPath + "/", StringComparison.Ordinal))
                continue;
            var rest = childPath.AsSpan(parentPath.Length + 1);
            var slash = rest.IndexOf('/');
            var keyword = (slash < 0 ? rest : rest[..slash]).ToString();
            switch (keyword)
            {
                case "if" or "not":
                    return true;
                case "oneOf" or "anyOf" or "contains" when parent.Errors is null || !parent.Errors.ContainsKey(keyword):
                    return true;
            }
        }

        return false;
    }
}
