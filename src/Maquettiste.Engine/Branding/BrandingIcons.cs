using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Maquettiste.Engine.Branding;

/// <summary>
/// The result of <see cref="BrandingIcons.Inspect"/>.
/// </summary>
/// <param name="Usable">Whether the file is an SVG or PNG the editor can show (after removing the unsafe parts of an SVG).</param>
/// <param name="Safe">The bytes to store or serve: the file itself when nothing was removed, else the sanitized SVG; null when not usable.</param>
/// <param name="Problems">Why the file is not usable, or what was removed from the SVG; empty for a clean file.</param>
public sealed record IconInspection(bool Usable, byte[]? Safe, IReadOnlyList<string> Problems)
{
    /// <summary>Whether the file is usable as it is (MQ8003 otherwise).</summary>
    public bool Clean => Usable && Problems.Count == 0;
}

/// <summary>
/// The project icon's limits and safety check (settings <c>branding.icon</c>): an SVG without scripts, event handlers or external
/// references, or a PNG, at most 512 KB, stored under <c>&lt;model root&gt;/branding/</c>. Deterministic: the same bytes give the
/// same result.
/// </summary>
public static partial class BrandingIcons
{
    /// <summary>The largest icon file, in bytes.</summary>
    public const int MaxBytes = 512 * 1024;

    /// <summary>The icons' folder under the model root.</summary>
    public const string Folder = "branding";

    /// <summary>The SVG media type.</summary>
    public const string Svg = "image/svg+xml";

    /// <summary>The PNG media type.</summary>
    public const string Png = "image/png";

    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly HashSet<string> Removed = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "foreignObject", "iframe", "embed", "object", "handler", "listener", "audio", "video",
    };

    private static readonly HashSet<string> Animations = new(StringComparer.OrdinalIgnoreCase)
    {
        "set", "animate", "animateMotion", "animateTransform", "animateColor",
    };

    /// <summary>The media type of an icon path by its extension, or <see langword="null"/>.</summary>
    /// <param name="path">The path.</param>
    /// <returns><see cref="Svg"/>, <see cref="Png"/> or <see langword="null"/>.</returns>
    public static string? ContentTypeOf(string? path) =>
        path is null ? null
        : path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? Svg
        : path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? Png
        : null;

    /// <summary>Whether a <c>branding.icon</c> value names a file the editor may read: <c>branding/&lt;name&gt;.svg</c> or <c>.png</c>.</summary>
    /// <param name="path">The model-relative path.</param>
    /// <returns><see langword="true"/> for a well-formed icon path.</returns>
    public static bool IsIconPath(string? path) => path is not null && IconPath().IsMatch(path);

    /// <summary>
    /// The model-relative path the editor's upload writes: content-addressed (<c>branding/icon-&lt;12 hex&gt;.svg</c> or <c>.png</c>), so
    /// an upload never replaces the file the saved settings name; the settings save switches <c>branding.icon</c> to it.
    /// </summary>
    /// <param name="contentType"><see cref="Svg"/> or <see cref="Png"/>.</param>
    /// <param name="hash">The stored file's content hash (lowercase hex).</param>
    /// <returns>The path.</returns>
    public static string PathFor(string contentType, string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        return Folder + "/icon-" + hash[..Math.Min(12, hash.Length)] + (contentType == Png ? ".png" : ".svg");
    }

    /// <summary>Whether a model-relative path is one of the upload's content-addressed names (see <see cref="PathFor"/>).</summary>
    /// <param name="path">The model-relative path.</param>
    /// <returns><see langword="true"/> for an uploaded icon's name.</returns>
    public static bool IsUploadPath(string? path) => path is not null && UploadPath().IsMatch(path);

    /// <summary>Checks an icon file and, for an SVG, removes what is unsafe.</summary>
    /// <param name="contentType"><see cref="Svg"/> or <see cref="Png"/>.</param>
    /// <param name="bytes">The file.</param>
    /// <returns>The inspection.</returns>
    public static IconInspection Inspect(string contentType, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes)
            return new IconInspection(false, null, [$"The icon is {bytes.Length / 1024} KB; the limit is {MaxBytes / 1024} KB."]);
        if (contentType == Png)
            return bytes.StartsWith(PngSignature)
                ? new IconInspection(true, bytes.ToArray(), [])
                : new IconInspection(false, null, ["The file is not a PNG image."]);
        if (contentType != Svg)
            return new IconInspection(false, null, ["The icon must be an SVG or a PNG file."]);
        return InspectSvg(bytes);
    }

    private static IconInspection InspectSvg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("\xEF\xBB\xBF"u8))
            bytes = bytes[3..];
        XDocument document;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersFromEntities = 0 };
            using var text = new StringReader(Encoding.UTF8.GetString(bytes));
            using var reader = XmlReader.Create(text, settings);
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException e)
        {
            return new IconInspection(false, null, ["The file is not well-formed SVG: " + e.Message]);
        }

        if (document.Root is not { } root || root.Name.LocalName != "svg" || root.Name.NamespaceName != SvgNamespace)
            return new IconInspection(false, null, ["The root element is not <svg> in the SVG namespace."]);

        var problems = new List<string>();
        // The reader ignores a DTD, so the DOCTYPE node may not be in the tree; an internal subset can still declare default
        // attributes (an ATTLIST onload) that a browser applies. Any DOCTYPE makes the file unclean: the stored copy is re-serialized
        // from the root without it.
        if (document.DocumentType is not null || HasDoctype(bytes))
            problems.Add("a DOCTYPE declaration");
        document.DocumentType?.Remove();
        foreach (var instruction in document.Nodes().OfType<XProcessingInstruction>().ToList())
        {
            problems.Add($"a <?{instruction.Target}?> processing instruction");
            instruction.Remove();
        }

        foreach (var element in root.DescendantsAndSelf().ToList())
        {
            if (element.Document is null)
                continue; // removed with an ancestor
            var name = element.Name.LocalName;
            if (Removed.Contains(name))
            {
                problems.Add($"a <{name}> element");
                element.Remove();
                continue;
            }

            if (Animations.Contains(name)
                && ((string?)element.Attribute("attributeName") is { } raw && Squeezed(raw) is var target
                    && (target.EndsWith("href", StringComparison.OrdinalIgnoreCase) || target.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                        || target.Contains(':', StringComparison.Ordinal) && target[(target.LastIndexOf(':') + 1)..].StartsWith("on", StringComparison.OrdinalIgnoreCase))))
            {
                problems.Add($"a <{name}> element that changes '{target}'");
                element.Remove();
                continue;
            }

            if (name == "style" && UnsafeCss(element.Value))
            {
                problems.Add("a <style> element with an external reference");
                element.Remove();
                continue;
            }

            foreach (var attribute in element.Attributes().ToList())
            {
                if (attribute.IsNamespaceDeclaration)
                    continue;
                var local = attribute.Name.LocalName;
                var value = attribute.Value;
                string? reason = null;
                if (local.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                    reason = $"an event handler attribute '{local}'";
                else if (Squeezed(value).Contains("javascript:", StringComparison.OrdinalIgnoreCase))
                    reason = $"a script URL in '{local}'";
                else if ((local is "href" or "src") && !LocalReference(value))
                    reason = $"an external reference in '{local}' on <{name}>";
                else if (UnsafeCss(value))
                    reason = $"an external reference in '{local}' on <{name}>";
                if (reason is null)
                    continue;
                problems.Add(reason);
                attribute.Remove();
            }
        }

        if (problems.Count == 0)
            return new IconInspection(true, bytes.ToArray(), []);
        var safe = Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting | SaveOptions.OmitDuplicateNamespaces));
        return new IconInspection(true, safe, [.. problems.Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>A reference inside the file, or an embedded raster image.</summary>
    private static bool LocalReference(string value)
    {
        var v = value.Trim();
        return v.StartsWith('#')
            || v.StartsWith("data:image/png;", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("data:image/jpeg;", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("data:image/gif;", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("data:image/webp;", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// CSS that imports or references something outside the file (<c>url(#id)</c> and raster data URLs are fine). Any backslash is
    /// refused (a CSS escape such as <c>u\72l(</c> or <c>@\69mport</c> hides a function from the scan), and so are <c>image-set(</c>,
    /// <c>src(</c> and a quoted string that holds a URL with a scheme or a <c>//</c> host.
    /// </summary>
    private static bool UnsafeCss(string css)
    {
        if (css.Contains('\\', StringComparison.Ordinal))
            return true;
        var squeezed = Squeezed(css);
        if (squeezed.Contains("@import", StringComparison.OrdinalIgnoreCase) || squeezed.Contains("expression(", StringComparison.OrdinalIgnoreCase)
            || squeezed.Contains("image-set(", StringComparison.OrdinalIgnoreCase) || CssSrc().IsMatch(squeezed))
            return true;
        foreach (Match match in CssQuoted().Matches(css))
        {
            var quoted = match.Groups[2].Value.Trim();
            if ((quoted.Contains("//", StringComparison.Ordinal) || UrlScheme().IsMatch(quoted)) && !LocalReference(quoted))
                return true;
        }

        foreach (Match match in CssUrl().Matches(css))
        {
            if (!LocalReference(match.Groups[1].Value.Trim().Trim('"', '\'')))
                return true;
        }

        return false;
    }

    /// <summary>Whether the text declares a DOCTYPE (outside a comment or not: either way the file is re-serialized without it).</summary>
    private static bool HasDoctype(ReadOnlySpan<byte> bytes) => bytes.IndexOf("<!DOCTYPE"u8) >= 0;

    private static string Squeezed(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)));

    [GeneratedRegex(@"^branding/[A-Za-z0-9_-][A-Za-z0-9._-]*\.(svg|png)$", RegexOptions.CultureInvariant)]
    private static partial Regex IconPath();

    [GeneratedRegex(@"url\s*\(([^)]*)\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CssUrl();

    [GeneratedRegex(@"(?<![\w-])src\(", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CssSrc();

    [GeneratedRegex(@"([""'])(.*?)\1", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex CssQuoted();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:", RegexOptions.CultureInvariant)]
    private static partial Regex UrlScheme();

    [GeneratedRegex(@"^branding/icon-[0-9a-f]{12}\.(svg|png)$", RegexOptions.CultureInvariant)]
    private static partial Regex UploadPath();
}
