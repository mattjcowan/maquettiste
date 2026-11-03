using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Maquettiste.Engine.Model;

/// <summary>
/// Stable ids (next-phase-2-design.md section 3.4, decisions 6 and 14): RFC 9562 UUID version 5 (name-based, SHA-1) of a row key.
/// The namespace is an element id's 128 bits: a ULID's 16 bytes, big-endian, as <c>row_uuid</c> writes them (a table id for a
/// column's stable id, a seed id for a scripted row's id); a UUID text is also taken, so the RFC's own namespaces work. The name is
/// the UTF-8 bytes of the key's values, each as text, joined by U+001F. Any tool computes the same value again: PostgreSQL's
/// <c>uuid_generate_v5</c> and the UUID libraries of most languages do. The resolver, the <c>stable_id</c> helper, tests and hosts
/// share this one implementation.
/// </summary>
public static class StableIds
{
    /// <summary>The separator between key parts: U+001F, the unit separator.</summary>
    private const char Separator = '\u001F';

    /// <summary>RFC 9562 version 5: namespace the ULID's 16 bytes, name the key's text forms joined by U+001F.</summary>
    /// <param name="namespaceId">The namespace: a ULID (a table id), or a UUID in its hyphenated text form.</param>
    /// <param name="key">The key's values in key order, in database terms; a list among them gives its items in order.</param>
    /// <returns>The UUID, lowercase and hyphenated.</returns>
    /// <exception cref="ArgumentException">The namespace is neither a ULID nor a UUID, or a key part is null or has no text form.</exception>
    public static string Of(string namespaceId, IReadOnlyList<object?> key)
    {
        Span<byte> uuid = stackalloc byte[16];
        Derive(namespaceId, key, uuid);
        var hex = Convert.ToHexStringLower(uuid);
        return string.Concat(hex[..8], "-", hex[8..12], "-", hex[12..16], "-", hex[16..20], "-", hex[20..]);
    }

    /// <summary>
    /// The name before UTF-8: each key value as text, joined by U+001F. A string as it is; an integer in invariant decimal digits
    /// with a leading <c>-</c> when negative; a decimal in invariant digits with no exponent, no trailing fractional zeros and no
    /// <c>.</c> when it is whole (<c>0</c>, never <c>-0</c>); a float or double as its shortest round-trip invariant text;
    /// <c>true</c> or <c>false</c>; a UUID (<see cref="Guid"/>) in lowercase with hyphens. A date, time or timestamp is the text the
    /// cell writes. A list (or a JSON array) gives its items in order.
    /// </summary>
    /// <param name="key">The key's values in key order.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentException">A key part is null or has no text form.</exception>
    public static string KeyText(IReadOnlyList<object?> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var text = new StringBuilder();
        var first = true;
        foreach (var part in key)
            Append(text, part, ref first);
        return text.ToString();
    }

    /// <summary>A scripted row's id (decision 14): the ULID text of <see cref="Of"/> with the seed id as namespace.</summary>
    /// <param name="seedId">The seed's id.</param>
    /// <param name="key">The row key in seed terms.</param>
    /// <returns>The ULID text (26 characters, uppercase Crockford).</returns>
    /// <exception cref="ArgumentException">The seed id is neither a ULID nor a UUID, or a key part is null or has no text form.</exception>
    public static string RowId(string seedId, IReadOnlyList<object?> key)
    {
        Span<byte> uuid = stackalloc byte[16];
        Derive(seedId, key, uuid);
        return new Ulid(uuid).ToString();
    }

    /// <summary>Whether an id can serve as a namespace: a ULID, or a UUID in its hyphenated text form.</summary>
    /// <param name="id">The id.</param>
    /// <returns><see langword="true"/> when <see cref="Of"/> takes it.</returns>
    internal static bool IsNamespace(string? id) => id is not null && (Ulid.TryParse(id, out _) || Guid.TryParseExact(id, "D", out _));

    private static void Derive(string namespaceId, IReadOnlyList<object?> key, Span<byte> uuid)
    {
        ArgumentNullException.ThrowIfNull(namespaceId);
        var name = Encoding.UTF8.GetBytes(KeyText(key));
        var input = new byte[16 + name.Length];
        if (Ulid.TryParse(namespaceId, out var ulid))
            ulid.TryWriteBytes(input.AsSpan(0, 16));
        else if (Guid.TryParseExact(namespaceId, "D", out var guid))
            guid.TryWriteBytes(input.AsSpan(0, 16), bigEndian: true, out _);
        else
            throw new ArgumentException($"'{namespaceId}' is neither a ULID nor a UUID.", nameof(namespaceId));
        name.CopyTo(input, 16);

        // SHA-1 is what RFC 9562 fixes for version 5; the id is not a secret, so the hash's weakness does not matter (decision 7).
#pragma warning disable CA5350
        Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
        SHA1.HashData(input, hash);
#pragma warning restore CA5350
        hash[..16].CopyTo(uuid);
        uuid[6] = (byte)((uuid[6] & 0x0F) | 0x50); // version 5
        uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80); // variant 10
    }

    private static void Append(StringBuilder text, object? value, ref bool first)
    {
        switch (value)
        {
            case null:
                throw new ArgumentException("A key part is null: a stable id needs every part of its key.", nameof(value));
            case string s:
                Part(text, s, ref first);
                break;
            case JsonElement json:
                AppendJson(text, json, ref first);
                break;
            case bool b:
                Part(text, b ? "true" : "false", ref first);
                break;
            case Guid g:
                Part(text, g.ToString("D", CultureInfo.InvariantCulture), ref first);
                break;
            case decimal m:
                Part(text, DecimalText(m), ref first);
                break;
            case double d:
                Part(text, d.ToString("R", CultureInfo.InvariantCulture), ref first);
                break;
            case float f:
                Part(text, f.ToString("R", CultureInfo.InvariantCulture), ref first);
                break;
            case sbyte or byte or short or ushort or int or uint or long or ulong or Int128 or UInt128 or BigInteger:
                Part(text, ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture), ref first);
                break;
            case IDictionary or IReadOnlyDictionary<string, object?>:
                throw new ArgumentException("A key part is an object; a key holds values, or lists of values.", nameof(value));
            case IEnumerable items:
                foreach (var item in items)
                    Append(text, item, ref first);
                break;
            default:
                throw new ArgumentException($"A key part is a {value.GetType().Name}, which has no text form for a stable id.", nameof(value));
        }
    }

    private static void AppendJson(StringBuilder text, JsonElement json, ref bool first)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.String:
                Part(text, json.GetString()!, ref first);
                break;
            case JsonValueKind.True or JsonValueKind.False:
                Part(text, json.ValueKind == JsonValueKind.True ? "true" : "false", ref first);
                break;
            case JsonValueKind.Number:
                Part(text, json.TryGetInt64(out var l) ? l.ToString(CultureInfo.InvariantCulture)
                    : json.TryGetDecimal(out var m) ? DecimalText(m)
                    : json.GetDouble().ToString("R", CultureInfo.InvariantCulture), ref first);
                break;
            case JsonValueKind.Array:
                foreach (var item in json.EnumerateArray())
                    AppendJson(text, item, ref first);
                break;
            case JsonValueKind.Object:
                throw new ArgumentException("A key part is an object; a key holds values, or lists of values.", nameof(json));
            default:
                throw new ArgumentException("A key part is null: a stable id needs every part of its key.", nameof(json));
        }
    }

    private static void Part(StringBuilder text, string part, ref bool first)
    {
        if (!first)
            text.Append(Separator);
        text.Append(part);
        first = false;
    }

    /// <summary>A decimal in invariant digits: no exponent, no trailing fractional zeros, no point when whole, never <c>-0</c>.</summary>
    private static string DecimalText(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (text.Contains('.', StringComparison.Ordinal))
            text = text.TrimEnd('0').TrimEnd('.');
        return text is "-0" ? "0" : text;
    }
}
