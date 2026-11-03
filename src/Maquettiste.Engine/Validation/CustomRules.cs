using System.Globalization;
using System.Text.Json;
using Json.Schema;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The rules on a custom element (next-phase-2-design.md section 4.2): MQ1019 when its type names no custom kind file, MQ3033 when its
/// content fails the custom kind's compiled schema (a required key missing, an undeclared key, a value of the wrong shape, a member
/// without an id), and MQ3001 for a member name repeated in one array. A custom element's own name is unique per type and package
/// through <see cref="ValidationContext.NameKeys"/>. Every finding is about the custom element's own file, so its save reports it. Pure
/// functions of the context.
/// </summary>
internal static class CustomRules
{
    private static readonly EvaluationOptions Evaluation = new() { OutputFormat = OutputFormat.List, Culture = CultureInfo.InvariantCulture };

    /// <summary>Runs the custom element rules on one element file.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="report">The report of the document to validate.</param>
    public static void Validate(ValidationContext context, Report report)
    {
        if (report.Document.Element is Custom custom)
            Check(context, custom, report);
    }

    private static void Check(ValidationContext context, Custom custom, Report report)
    {
        if (context.Model.CustomKindOf(custom.Type) is null || context.Model.PropertySchemas.ContentOf(custom.Type) is not { } source)
        {
            report.Add("MQ1019", $"Custom element '{custom.Name}' has type '{custom.Type}', but no custom kind file extensions/kinds/{custom.Type}.json declares it; "
                + "its content is not checked. Declare the custom kind, or change type.", "/type");
            return;
        }

        CheckContent(custom, source, report);
        CheckMemberNames(custom, source, report);
    }

    /// <summary>MQ3033: the content against the custom kind's compiled schema (a schema that does not compile is MQ1017 on the file).</summary>
    private static void CheckContent(Custom custom, PropertySchemaSource source, Report report)
    {
        if (source.Compiled.Schema is not { } schema)
            return;
        var content = report.Document.Json.ValueKind == JsonValueKind.Object && report.Document.Json.TryGetProperty("content", out var written)
            ? written
            : JsonSerializer.SerializeToElement(custom.Content);
        EvaluationResults results;
        try
        {
            results = schema.Evaluate(content, Evaluation);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            report.Add("MQ3033", $"Custom kind '{custom.Type}' ({source.Path}) could not be evaluated: {e.Message}", "/content");
            return;
        }

        if (results.IsValid)
            return;
        var any = false;
        foreach (var detail in results.Details ?? [])
        {
            if (detail.Errors is null || ExtensionSet.InsideDiscardedBranch(detail))
                continue;
            foreach (var (keyword, message) in detail.Errors.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (ExtensionSet.ApplicatorKeywords.Contains(keyword))
                    continue;
                var location = detail.InstanceLocation.ToString();
                var text = keyword switch
                {
                    "" => "is not declared by the custom kind" + (message.Length > 0 ? " (" + message + ")" : ""),
                    _ => keyword + ": " + message,
                };
                report.Add("MQ3033", $"Custom kind '{custom.Type}': content{location} {text}. Fix the value.", "/content" + location);
                any = true;
            }
        }

        if (!any)
            report.Add("MQ3033", $"Custom kind '{custom.Type}': the content does not match the custom kind ({source.Path}).", "/content");
    }

    /// <summary>MQ3001: a member name repeated among the items of one array.</summary>
    private static void CheckMemberNames(Custom custom, PropertySchemaSource source, Report report)
    {
        if (source.Members.Count == 0)
            return;
        var members = XMemberNode.Walk(source.Members, custom.Content);
        var seen = new Dictionary<(string Array, string Name), int>();
        foreach (var member in members)
        {
            if (member.Name is not { Length: > 0 } name)
                continue;
            var slash = member.Pointer.LastIndexOf('/');
            var array = member.Pointer[..slash];
            var index = int.Parse(member.Pointer[(slash + 1)..], CultureInfo.InvariantCulture);
            if (!seen.TryAdd((array, name), index))
            {
                report.Add("MQ3001", string.Create(CultureInfo.InvariantCulture, $"Member name '{name}' is already used at index {seen[(array, name)]} of {member.Array}."),
                    member.Pointer + "/name", member.Id);
            }
        }
    }
}
