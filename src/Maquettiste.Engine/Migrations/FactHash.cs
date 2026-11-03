using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Migrations;

/// <summary>
/// The fact content hash (next-phase-1-design.md section 3.2, decision 15): what <see cref="Resolution.RMigration.Hash"/>, the
/// <c>migration list</c> entries and a ledger row's <c>content_hash</c> record. It is the 64 lowercase hex digits of SHA-256 over
/// the UTF-8 bytes of the fact document as the canonical writer writes it once the annotations (<c>$schema</c>, <c>name</c>,
/// <c>displayName</c>, <c>pluralName</c>, <c>description</c>, <c>stereotypes</c>, <c>tags</c>, <c>category</c>,
/// <c>rationale</c>, <c>decision</c>, <c>status</c>, <c>properties</c>, <c>generation</c>, <c>source</c>) and the order and hold
/// fields (<c>after</c>, <c>held</c>, <c>released</c>) are removed. It covers <c>kind</c>, <c>id</c>, <c>database</c>,
/// <c>fact</c> and the fact's own fields, so two copies of one fact hash alike whatever their annotations, and holding, releasing
/// or rewiring a fact never changes it.
/// </summary>
internal static class FactHash
{
    /// <summary>The top-level keys the hash leaves out.</summary>
    internal static readonly IReadOnlyList<string> Excluded =
    [
        "$schema", "name", "displayName", "pluralName", "description", "stereotypes", "tags", "category", "rationale", "decision",
        "status", "properties", "generation", "source", "after", "held", "released",
    ];

    private const string SchemaFile = "migration.json";

    /// <summary>
    /// The path the writer lays the document out for. The canonical writer always writes a <c>$schema</c> line computed from the
    /// path; <see cref="Of(JsonNode, ICanonicalJson?)"/> removes that line, so the path never reaches the hash.
    /// </summary>
    private const string LayoutPath = "model/databases/db/migrations/open/fact.json";

    private static readonly Lazy<ICanonicalJson> SharedWriter = new(() => new CanonicalJson(new SchemaRegistry()), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The hash of a fact document as a file holds it.</summary>
    /// <param name="document">The document.</param>
    /// <param name="json">The canonical writer, or <see langword="null"/> for one over the embedded schemas.</param>
    /// <returns>64 lowercase hex digits.</returns>
    public static string Of(JsonElement document, ICanonicalJson? json = null) =>
        Of(JsonNode.Parse(document.GetRawText()) ?? throw new ArgumentException("A fact document is a JSON object.", nameof(document)), json);

    /// <summary>The hash of a fact record.</summary>
    /// <param name="fact">The fact.</param>
    /// <param name="json">The canonical writer, or <see langword="null"/> for one over the embedded schemas.</param>
    /// <returns>64 lowercase hex digits.</returns>
    public static string Of(Migration fact, ICanonicalJson? json = null)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return Of(JsonSerializer.SerializeToNode(fact, fact.GetType(), EngineJson.Options)!, json);
    }

    /// <summary>The hash of a fact document as a node; the node is not changed.</summary>
    /// <param name="document">The document.</param>
    /// <param name="json">The canonical writer, or <see langword="null"/> for one over the embedded schemas.</param>
    /// <returns>64 lowercase hex digits.</returns>
    public static string Of(JsonNode document, ICanonicalJson? json = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document is not JsonObject source)
            throw new ArgumentException("A fact document is a JSON object.", nameof(document));
        var copy = new JsonObject();
        foreach (var (key, value) in source)
        {
            if (!Excluded.Contains(key, StringComparer.Ordinal))
                copy[key] = value?.DeepClone();
        }

        var bytes = (json ?? SharedWriter.Value).Write(copy, SchemaFile, LayoutPath);
        return Convert.ToHexStringLower(SHA256.HashData(WithoutSchemaLine(bytes)));
    }

    /// <summary>
    /// The canonical bytes without the <c>$schema</c> line the writer puts first: <c>{\n  "$schema": "...",\n  "kind": ...</c>
    /// becomes <c>{\n  "kind": ...</c>.
    /// </summary>
    private static ReadOnlySpan<byte> WithoutSchemaLine(byte[] bytes)
    {
        ReadOnlySpan<byte> prefix = "{\n  \"$schema\""u8;
        if (!bytes.AsSpan().StartsWith(prefix))
            return bytes;
        var end = bytes.AsSpan(prefix.Length).IndexOf((byte)'\n');
        if (end < 0)
            return bytes;
        var lineEnd = prefix.Length + end + 1;
        var result = new byte[bytes.Length - (lineEnd - 2)];
        bytes.AsSpan(0, 2).CopyTo(result);
        bytes.AsSpan(lineEnd).CopyTo(result.AsSpan(2));
        return result;
    }
}
