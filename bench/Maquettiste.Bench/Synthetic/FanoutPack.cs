using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Bench.Synthetic;

/// <summary>
/// The <c>fanout</c> pack's manifest (engine-design.md section 17, D30): <c>n</c> units per entity cycling through seven templates
/// (C#, TypeScript, Markdown, SQL, JSON; three of them call JavaScript helpers), one unit in five written to the committed root
/// <c>gen/committed</c> and the rest to the built root <c>gen/built</c>, plus one index per package.
/// The checked-in <c>bench/packs/fanout/pack.json</c> is this manifest for the default of 20 units.
/// </summary>
internal static class FanoutPack
{
    /// <summary>The pack name.</summary>
    public const string Name = "fanout";

    /// <summary>The default number of files per entity.</summary>
    public const int DefaultFanout = 20;

    /// <summary>The number of files the default model should pass (S13).</summary>
    public const int TargetFiles = 100_000;

    private static readonly ImmutableArray<(string Template, string Extension)> Templates =
    [
        ("templates/record.scriban", "cs"),
        ("templates/dto.scriban", "ts"),
        ("templates/validator.scriban", "cs"),
        ("templates/doc.scriban", "md"),
        ("templates/query.scriban", "sql"),
        ("templates/links.scriban", "cs"),
        ("templates/api.scriban", "json"),
    ];

    /// <summary>The effective fanout: the option, else enough units per entity to pass <see cref="TargetFiles"/> (at least 1).</summary>
    /// <param name="options">The model options.</param>
    /// <returns>Units per entity.</returns>
    public static int EffectiveFanout(SyntheticModelOptions options) =>
        options.Fanout ?? Math.Max(1, (TargetFiles + options.Entities - 1) / options.Entities);

    /// <summary>Builds the manifest.</summary>
    /// <param name="fanout">Units per entity.</param>
    /// <returns>The manifest.</returns>
    public static PackManifest Manifest(int fanout)
    {
        var units = new List<PackUnit>(fanout + 1);
        for (var i = 1; i <= fanout; i++)
        {
            var (template, extension) = Templates[(i - 1) % Templates.Length];
            var id = "u" + i.ToString("D2", CultureInfo.InvariantCulture);
            var root = i % 5 == 0 ? "committed" : "built";
            units.Add(new PackUnit
            {
                Id = id,
                Template = template,
                For = "each entity",
                Output = root + "/{{ entity.package.name }}/{{ entity.name }}." + id + "." + extension,
            });
        }

        units.Add(new PackUnit { Id = "index", Template = "templates/index.scriban", For = "each package", Output = "built/{{ package.name }}/index.md" });
        using var ns = JsonDocument.Parse("\"Bench.Generated\"");
        return new PackManifest
        {
            Name = Name,
            Version = "1.0.0",
            Engine = ">=1.0 <2.0",
            Description = "Benchmark-only pack: many small files per entity over the synthetic model (engine-design.md section 17).",
            Parameters = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, [KeyValuePair.Create("namespace", ns.RootElement.Clone())]),
            Units = units,
        };
    }
}
