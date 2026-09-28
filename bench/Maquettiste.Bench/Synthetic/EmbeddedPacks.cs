using System.Collections.Immutable;
using System.Reflection;

namespace Maquettiste.Bench.Synthetic;

/// <summary>
/// The packs embedded in the bench assembly as <c>Maquettiste.Bench.Packs/&lt;pack&gt;/&lt;path&gt;</c> (the example packs and
/// <c>fanout</c>), so <c>maquettiste bench</c> from an installed tool can write them into its synthetic repo.
/// </summary>
internal static class EmbeddedPacks
{
    private const string Prefix = "Maquettiste.Bench.Packs/";

    /// <summary>The example packs the design puts next to <c>fanout</c> (engine-design.md section 17).</summary>
    public static readonly ImmutableArray<string> ExamplePacks = ["csharp-dapper", "sql-ddl"];

    /// <summary>Lists a pack's files: pack-relative path with <c>/</c> separators, ordinal.</summary>
    /// <param name="pack">The pack name.</param>
    /// <returns>The paths; empty when the pack is not embedded.</returns>
    public static IReadOnlyList<string> Files(string pack)
    {
        var prefix = Prefix + pack + "/";
        return [.. Assembly.GetManifestResourceNames()
            .Select(n => n.Replace('\\', '/'))
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => n[prefix.Length..])
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>Reads one embedded file.</summary>
    /// <param name="pack">The pack name.</param>
    /// <param name="path">The pack-relative path.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The bytes.</returns>
    public static async Task<byte[]> ReadAsync(string pack, string path, CancellationToken ct)
    {
        var name = Prefix + pack + "/" + path;
        var stream = Assembly.GetManifestResourceStream(name)
            ?? Assembly.GetManifestResourceStream(name.Replace('/', '\\'))
            ?? throw new FileNotFoundException("The bench assembly does not embed " + name + ".");
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
            return buffer.ToArray();
        }
    }

    private static Assembly Assembly => typeof(EmbeddedPacks).Assembly;
}
