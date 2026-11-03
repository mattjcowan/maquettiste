using System.Globalization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Planning;

namespace Maquettiste.Engine.Loading;

/// <summary>
/// The checks of <c>maquettiste.json</c>'s own <c>uses</c> entries (library packs, next-phase-2-design.md section 5.2), made by the
/// model loader so they appear in every validation: MQ6042 for a pack that does not exist or did not load, or one listed twice, and
/// MQ6043 for a version outside the range or a range that does not parse. The packs' own entries are the pack loader's.
/// </summary>
internal static class ProjectUses
{
    /// <summary>Checks the project's uses entries.</summary>
    /// <param name="uses">The entries, in file order.</param>
    /// <param name="exists">The packs that have a <c>pack.json</c>.</param>
    /// <param name="loaded">The libraries that loaded, by name, with their versions.</param>
    /// <param name="settingsPath">The repo-relative path of <c>maquettiste.json</c>.</param>
    /// <returns>The findings, at <c>/uses/&lt;i&gt;/pack</c> or <c>/uses/&lt;i&gt;/version</c>, without positions.</returns>
    public static IReadOnlyList<Diagnostic> Check(IReadOnlyList<PackUse> uses, IReadOnlySet<string> exists, IReadOnlyDictionary<string, string> loaded,
        string settingsPath)
    {
        var findings = new List<Diagnostic>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < uses.Count; i++)
        {
            var use = uses[i];
            var pointer = "/uses/" + i.ToString(CultureInfo.InvariantCulture);
            if (!names.Add(use.Pack))
            {
                findings.Add(RuleCatalog.Create("MQ6042", $"maquettiste.json lists '{use.Pack}' more than once in its uses.", null, settingsPath, pointer + "/pack"));
            }
            else if (!exists.Contains(use.Pack))
            {
                findings.Add(RuleCatalog.Create("MQ6042",
                    $"maquettiste.json uses '{use.Pack}', which does not exist: there is no templates/{use.Pack}/pack.json.", null, settingsPath, pointer + "/pack"));
            }
            else if (!loaded.TryGetValue(use.Pack, out var version))
            {
                findings.Add(RuleCatalog.Create("MQ6042",
                    $"maquettiste.json uses '{use.Pack}', which did not load (its pack.json, a script or a pack its uses reach); validate its pack to see why.",
                    null, settingsPath, pointer + "/pack"));
            }
            else if (!EngineRange.Satisfies(use.Version, version, out var valid))
            {
                findings.Add(RuleCatalog.Create("MQ6043", valid
                    ? $"maquettiste.json uses '{use.Pack}' {use.Version}, but '{use.Pack}' is version {version}."
                    : $"maquettiste.json has a uses range for '{use.Pack}' that cannot be parsed: '{use.Version}'.", null, settingsPath, pointer + "/version"));
            }
        }

        return findings;
    }
}
