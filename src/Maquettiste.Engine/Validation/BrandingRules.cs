using System.Text.RegularExpressions;
using Maquettiste.Engine.Branding;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The branding rules on <c>maquettiste.json</c> <c>branding</c>, run on whole-model validation: MQ8001 (a color that is not
/// <c>#rrggbb</c> or <c>#rgb</c>), MQ8002 (an icon path that is not <c>branding/&lt;name&gt;.svg|png</c> or names no file) and
/// MQ8003 (an icon that is not a safe SVG or a PNG of at most 512 KB).
/// </summary>
internal static partial class BrandingRules
{
    /// <summary>Checks the branding settings.</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="modelRoot">The model root folder, where the icon is read.</param>
    /// <returns>The diagnostics.</returns>
    public static IReadOnlyList<Diagnostic> Check(ModelSnapshot model, string modelRoot)
    {
        var branding = model.Settings.Branding;
        var diagnostics = new List<Diagnostic>();
        var path = ReferenceDataRules.SettingsPath(model);
        foreach (var (theme, color) in new[] { ("light", branding.Colors.Light), ("dark", branding.Colors.Dark) })
        {
            if (color is not null && !IsColor(color))
                diagnostics.Add(RuleCatalog.Create("MQ8001", $"The {theme} primary color '{color}' is not a hex color such as #0b5cd5.", null, path,
                    "/branding/colors/" + theme));
        }

        if (branding.Icon is not { } icon)
            return diagnostics;
        if (!BrandingIcons.IsIconPath(icon))
        {
            diagnostics.Add(RuleCatalog.Create("MQ8002", $"The icon '{icon}' must be an .svg or .png file directly under branding/ in the model folder.",
                null, path, "/branding/icon"));
            return diagnostics;
        }

        var full = Path.Combine(modelRoot, icon);
        var file = new FileInfo(full);
        if (!file.Exists || file.LinkTarget is not null)
        {
            diagnostics.Add(RuleCatalog.Create("MQ8002", $"The icon file {icon} does not exist in the model folder.", null, path, "/branding/icon"));
            return diagnostics;
        }

        if (file.Length > BrandingIcons.MaxBytes)
        {
            diagnostics.Add(RuleCatalog.Create("MQ8003", $"The icon {icon} cannot be used: it is {file.Length / 1024} KB; the limit is {BrandingIcons.MaxBytes / 1024} KB.",
                null, path, "/branding/icon"));
            return diagnostics;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(full);
        }
        catch (IOException e)
        {
            diagnostics.Add(RuleCatalog.Create("MQ8002", $"The icon file {icon} cannot be read: {e.Message}", null, path, "/branding/icon"));
            return diagnostics;
        }

        var inspection = BrandingIcons.Inspect(BrandingIcons.ContentTypeOf(icon)!, bytes);
        if (!inspection.Clean)
            diagnostics.Add(RuleCatalog.Create("MQ8003", inspection.Usable
                ? $"The icon {icon} is not a safe SVG: it has {string.Join(", ", inspection.Problems)}. The editor shows it without them; upload it again to store the cleaned file."
                : $"The icon {icon} cannot be used: {string.Join(" ", inspection.Problems)}", null, path, "/branding/icon"));
        return diagnostics;
    }

    /// <summary>Whether a value is <c>#rrggbb</c> or <c>#rgb</c>.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> for a hex color.</returns>
    public static bool IsColor(string value) => HexColor().IsMatch(value);

    [GeneratedRegex("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();
}
