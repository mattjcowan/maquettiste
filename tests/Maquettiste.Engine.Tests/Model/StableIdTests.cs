using System.Text.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Tests.Model;

/// <summary>
/// Stable ids (next-phase-2-design.md section 3.4, decisions 6 and 14): RFC 9562 UUID version 5 with an element id's 128 bits as
/// namespace and a row key's text forms, joined by U+001F, as name.
/// </summary>
public sealed class StableIdTests
{
    private const string Table = "01JCA4M2A1B2C3D4E5F6G7H8J9";

    [Fact]
    public void Version_5_matches_the_RFC_9562_example()
    {
        // RFC 9562, appendix A.4: the DNS namespace and the name www.example.com.
        Assert.Equal("2ed6657d-e927-568b-95e1-2665a8aea6a2", StableIds.Of("6ba7b810-9dad-11d1-80b4-00c04fd430c8", ["www.example.com"]));
    }

    [Fact]
    public void The_namespace_is_the_table_id_as_row_uuid_writes_it()
    {
        // row_uuid writes a ULID's 16 bytes big-endian, lowercase and dashed.
        var hex = Convert.ToHexStringLower(Ulid.Parse(Table).ToByteArray());
        var asRowUuid = hex[..8] + "-" + hex[8..12] + "-" + hex[12..16] + "-" + hex[16..20] + "-" + hex[20..];
        Assert.Equal("0193144a-0941-5898-3691-c579a078a249", asRowUuid);
        Assert.Equal(StableIds.Of(asRowUuid, ["ada@example.com"]), StableIds.Of(Table, ["ada@example.com"]));

        // The value is a version 5 UUID of the RFC variant.
        var id = StableIds.Of(Table, ["ada@example.com"]);
        Assert.Equal('5', id[14]);
        Assert.Contains(id[19], "89ab".ToCharArray());
        Assert.Equal(id.ToLowerInvariant(), id);
    }

    [Fact]
    public void Key_parts_join_with_the_unit_separator_in_key_order()
    {
        Assert.Equal("ACME\u001FEU", StableIds.KeyText(["ACME", "EU"]));
        Assert.Equal("ACME\u001FEU", StableIds.KeyText([new object?[] { "ACME", "EU" }])); // an array cell gives its items in order
        Assert.Equal("ada@example.com", StableIds.KeyText(["ada@example.com"]));
        Assert.Equal(StableIds.Of(Table, ["ACME", "EU"]), StableIds.Of(Table, [JsonDocument.Parse("[\"ACME\",\"EU\"]").RootElement]));
        Assert.NotEqual(StableIds.Of(Table, ["ACME", "EU"]), StableIds.Of(Table, ["EU", "ACME"]));
        Assert.Throws<ArgumentException>(() => StableIds.KeyText(["ACME", null]));
        Assert.Throws<ArgumentException>(() => StableIds.Of("not an id", ["ACME"]));
    }

    [Fact]
    public void Numbers_use_the_documented_text_forms()
    {
        static string Text(object? value) => StableIds.KeyText([value]);

        // Integers: invariant digits, a leading minus when negative.
        Assert.Equal(["42", "-7", "0", "9223372036854775807"], new object?[] { 42L, -7, (short)0, long.MaxValue }.Select(Text));

        // Decimals: no exponent, no trailing fractional zeros, no point when whole, never -0.
        Assert.Equal(["12.5", "3", "0", "0", "-0.25", "1000000000000000000000"],
            new object?[] { 12.50m, 3.000m, 0.0m, -0.0m, -0.250m, 1e21m }.Select(Text));

        // Floats and doubles: the shortest round-trip invariant text.
        Assert.Equal(["0.1", "1E+21", "2.5"], new object?[] { 0.1, 1e21, 2.5f }.Select(Text));

        // Booleans and UUIDs.
        Assert.Equal(["true", "false", "0193a3c2-0000-7000-8000-000000000001"],
            new object?[] { true, false, Guid.Parse("0193A3C2-0000-7000-8000-000000000001") }.Select(Text));

        // JSON numbers read as written: an integer as an integer, a fraction as a decimal.
        Assert.Equal(["7", "12.5", "1000"], new[] { "7", "12.50", "1e3" }.Select(n => Text(JsonDocument.Parse(n).RootElement)));
    }

    [Fact]
    public void The_worked_example_of_the_guide_holds()
    {
        Assert.Equal("713ef598-46bf-59bd-aecd-d0be95b552e3", StableIds.Of(Table, ["ada@example.com"]));
        Assert.Equal("e2aa38bc-b70c-52f3-ac0e-c50c98d04b1e", StableIds.Of(Table, ["ACME", "EU"]));

        // A scripted row's id is the ULID text of the same UUID, with the seed id as namespace.
        var rowId = StableIds.RowId(Table, ["ada@example.com"]);
        Assert.Equal(26, rowId.Length);
        Assert.Equal("713ef59846bf59bdaecdd0be95b552e3", Convert.ToHexStringLower(Ulid.Parse(rowId).ToByteArray()));
    }

    [Fact]
    public void Ten_thousand_keys_give_ten_thousand_ids()
    {
        var ids = Enumerable.Range(0, 10_000).Select(i => StableIds.Of(Table, ["customer-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)])).ToList();
        Assert.Equal(10_000, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ids, Enumerable.Range(0, 10_000).Select(i => StableIds.Of(Table, ["customer-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)])));
    }
}
