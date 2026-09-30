using Maquettiste.Engine.Localization;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Rendering;

/// <summary>
/// The localization helpers (reference-types-seeds-localization.md section 3.7): <c>display_name</c>, <c>plural_name</c>,
/// <c>description_of</c>, <c>label_of</c> and <c>translate</c> walk the locale's fallback chain; <c>has_translation</c> reads one
/// locale only. Without a locale argument the locale is the unit's (an <c>each locale</c> unit), else the default. Every call records
/// <c>s:localization</c>, an <c>l:&lt;locale&gt;:&lt;owner&gt;</c> key per consulted locale up to the one that answered, and the
/// owner's <c>e:</c> key when the default text (or the built-in fallback) answered.
/// </summary>
internal static class LocalizationHelpers
{
    /// <summary>The helper names.</summary>
    public static readonly string[] Names = ["display_name", "plural_name", "description_of", "label_of", "translate", "has_translation"];

    /// <summary>Registers the helpers.</summary>
    /// <param name="add">Adds one helper: name, minimum and maximum argument counts, body.</param>
    public static void Register(Action<string, int, int, Func<TrackingTemplateContext, IReadOnlyList<object?>, object?>> add)
    {
        add("display_name", 1, 2, (c, a) => Translate(c, a[0], LocalizationIndex.DisplayNameField, Optional(a, 1)));
        add("plural_name", 1, 2, (c, a) => Translate(c, a[0], LocalizationIndex.PluralNameField, Optional(a, 1)));
        add("description_of", 1, 2, (c, a) => Translate(c, a[0], LocalizationIndex.DescriptionField, Optional(a, 1)));
        add("label_of", 1, 2, (c, a) => Translate(c, a[0], LocalizationIndex.LabelField, Optional(a, 1)));
        add("translate", 2, 3, (c, a) => Translate(c, a[0], Field(a[1]), Optional(a, 2)));
        add("has_translation", 3, 3, (c, a) => HasTranslation(c, a[0], Field(a[1]), BuiltinHelpers.AsText(a[2])));
    }

    private static string? Optional(IReadOnlyList<object?> args, int index) =>
        args.Count > index && TrackingTemplateContext.Unwrap(args[index]) is { } value ? BuiltinHelpers.AsText(value) : null;

    private static string Field(object? value)
    {
        var field = BuiltinHelpers.AsText(value);
        return LocalizationIndex.Fields.Contains(field, StringComparer.Ordinal)
            ? field
            : throw new RenderHelperException("MQ6006", $"'{field}' is not a translatable field: use displayName, pluralName, label or description.");
    }

    private static string? Translate(TrackingTemplateContext context, object? target, string field, string? locale)
    {
        var model = context.Unit.Run.Context.Model;
        var l10n = model.Source.Localization;
        var (id, fallback) = Node(context, TrackingTemplateContext.Unwrap(target), field, "translate");
        context.Recorder.Record(RLocale.SettingsKey);
        if (id is null)
            return fallback;
        var owner = model.Source.TryGetEntry(id, out var entry) ? entry.OwnerId : id;
        if (l10n.Settings is { } settings && (locale ?? (context.Unit.Planned.Element as RLocale)?.Tag) is { } wanted)
        {
            foreach (var consulted in l10n.ChainOf(wanted))
            {
                if (string.Equals(consulted, settings.DefaultLocale, StringComparison.Ordinal))
                    break;
                context.Recorder.Record("l:" + consulted + ":" + owner);
                if (l10n.Text(consulted, id, field) is { } text)
                    return text;
            }
        }

        context.Recorder.Record("e:" + owner);
        return fallback;
    }

    private static bool HasTranslation(TrackingTemplateContext context, object? target, string field, string locale)
    {
        var model = context.Unit.Run.Context.Model;
        var (id, _) = Node(context, TrackingTemplateContext.Unwrap(target), field, "has_translation");
        context.Recorder.Record(RLocale.SettingsKey);
        if (id is null)
            return false;
        var owner = model.Source.TryGetEntry(id, out var entry) ? entry.OwnerId : id;
        context.Recorder.Record("l:" + locale + ":" + owner);
        return model.Source.Localization.Text(locale, id, field) is not null;
    }

    /// <summary>The node id and its default-locale text of a field (the resolved value, with its built-in fallbacks).</summary>
    private static (string? Id, string? Default) Node(TrackingTemplateContext context, object? value, string field, string helper)
    {
        var nodes = context.Unit.Run.Context.Model.Source.Localization.Nodes;
        string? Source(string id) => nodes.TryGetValue(id, out var n) ? n.Source(field) : null;
        return value switch
        {
            null => (null, null),
            RElement e => (e.Id, field switch
            {
                LocalizationIndex.DisplayNameField => e.DisplayName,
                LocalizationIndex.PluralNameField => e.PluralName,
                LocalizationIndex.DescriptionField => e.Description,
                _ => null,
            }),
            REnumMember m => (m.Id, field switch
            {
                LocalizationIndex.DisplayNameField => m.DisplayName,
                LocalizationIndex.DescriptionField => m.Description,
                _ => null,
            }),
            RRow r => (r.Id, field switch
            {
                LocalizationIndex.LabelField => r.Label,
                LocalizationIndex.DescriptionField => r.Description,
                _ => null,
            }),
            RReferenceField f => (f.Id, field switch
            {
                LocalizationIndex.DisplayNameField => f.DisplayName,
                LocalizationIndex.DescriptionField => f.Description,
                _ => null,
            }),
            RProcessNode n => (n.Id, field switch
            {
                LocalizationIndex.DisplayNameField => n.DisplayName,
                LocalizationIndex.DescriptionField => n.Description,
                _ => null,
            }),
            RStep step => (step.Id, field == LocalizationIndex.DescriptionField ? step.Description : null),
            REnd end => (end.Id, Source(end.Id) ?? (field is LocalizationIndex.DisplayNameField or LocalizationIndex.PluralNameField ? end.Role : null)),
            string id when context.Unit.Run.Context.Model.Find(id) is { } found => Node(context, found, field, helper),
            string id => (id, Source(id)),
            RObject other => (other.Id, Source(other.Id)),
            _ => throw new RenderHelperException("MQ6006", $"`{helper}` takes a model element, sub-element, row or id, not a {TemplateValues.TypeName(value)}."),
        };
    }
}
