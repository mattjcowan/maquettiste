using System.Globalization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>Collects the diagnostics of one model file. Not thread-safe: one instance per document and worker.</summary>
/// <param name="document">The document being validated.</param>
internal sealed class Report(ElementDocument document)
{
    private readonly List<Diagnostic> _items = [];

    /// <summary>The document being validated.</summary>
    public ElementDocument Document => document;

    /// <summary>The diagnostics collected so far.</summary>
    public IReadOnlyList<Diagnostic> Items => _items;

    /// <summary>Adds a built-in rule diagnostic at a pointer in the document, with the rule's default severity.</summary>
    /// <param name="rule">The rule id.</param>
    /// <param name="message">The message.</param>
    /// <param name="pointer">The JSON pointer in the document.</param>
    /// <param name="elementId">The element or sub-element concerned; defaults to the document's element.</param>
    public void Add(string rule, string message, string pointer, string? elementId = null) =>
        _items.Add(RuleCatalog.Create(rule, message, elementId ?? document.Element.Id, document.Path, pointer));

    /// <summary>Adds a built-in rule diagnostic with an explicit severity.</summary>
    /// <param name="rule">The rule id.</param>
    /// <param name="severity">The severity.</param>
    /// <param name="message">The message.</param>
    /// <param name="pointer">The JSON pointer in the document.</param>
    /// <param name="elementId">The element or sub-element concerned; defaults to the document's element.</param>
    public void Add(string rule, DiagnosticSeverity severity, string message, string pointer, string? elementId = null) =>
        _items.Add(new Diagnostic(rule, severity, message, elementId ?? document.Element.Id, document.Path, pointer, null, null));

    /// <summary>Adds a ready-made diagnostic.</summary>
    /// <param name="diagnostic">The diagnostic.</param>
    public void Add(Diagnostic diagnostic) => _items.Add(diagnostic);
}

/// <summary>JSON pointer and formatting helpers (invariant culture).</summary>
internal static class Ptr
{
    /// <summary>Appends an array index.</summary>
    /// <param name="pointer">The base pointer.</param>
    /// <param name="index">The index.</param>
    /// <returns>The pointer.</returns>
    public static string At(string pointer, int index) => pointer + "/" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>Appends a property name, escaped.</summary>
    /// <param name="pointer">The base pointer.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The pointer.</returns>
    public static string Prop(string pointer, string name) => pointer + "/" + Escape(name);

    /// <summary>Escapes a pointer segment (<c>~</c> → <c>~0</c>, <c>/</c> → <c>~1</c>).</summary>
    /// <param name="segment">The segment.</param>
    /// <returns>The escaped segment.</returns>
    public static string Escape(string segment) =>
        segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>Splits a pointer into unescaped segments; the empty pointer has none.</summary>
    /// <param name="pointer">The pointer.</param>
    /// <returns>The segments.</returns>
    public static string[] Split(string pointer)
    {
        if (pointer.Length == 0)
            return [];
        var parts = pointer[1..].Split('/');
        for (var i = 0; i < parts.Length; i++)
            parts[i] = parts[i].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        return parts;
    }

    /// <summary>Returns the parent pointer, or <see langword="null"/> for the root.</summary>
    /// <param name="pointer">The pointer.</param>
    /// <returns>The parent.</returns>
    public static string? Parent(string pointer)
    {
        if (pointer.Length == 0)
            return null;
        var slash = pointer.LastIndexOf('/');
        return slash <= 0 ? "" : pointer[..slash];
    }

    /// <summary>Formats an integer with the invariant culture.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The text.</returns>
    public static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}
