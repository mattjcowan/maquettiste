using Maquettiste.Engine.Localization;

namespace Maquettiste.Engine.Tests.Localization;

/// <summary>The CSV and XLIFF readers and writers of reference-types-seeds-localization.md sections 2.3 and 3.9.</summary>
public sealed class TranslationFilesTests
{
    [Fact]
    public void Csv_quotes_only_what_needs_it_and_reads_back_the_same_cells()
    {
        string?[][] rows = [["@id", "@label", "note"], ["A", "Kilo, gram", "say \"hi\""], ["B", "two\nlines", null]];

        var text = Csv.Write(rows);
        var read = Csv.Read(text);

        Assert.Equal("@id,@label,note\nA,\"Kilo, gram\",\"say \"\"hi\"\"\"\nB,\"two\nlines\",\n", text);
        Assert.Equal(rows.Select(r => r.Select(c => c ?? "")), read);
    }

    [Fact]
    public void Csv_bom_form_has_a_byte_order_mark_and_crlf_and_reads_the_same()
    {
        string?[][] rows = [["a", "b"], ["1", ""]];

        var text = Csv.Write(rows, bom: true);

        Assert.Equal("﻿a,b\r\n1,\r\n", text);
        Assert.Equal(Csv.Read(Csv.Write(rows)), Csv.Read(text));
    }

    [Fact]
    public void Csv_rejects_an_unclosed_quote_and_text_after_a_closing_quote()
    {
        Assert.Throws<FormatException>(() => Csv.Read("a,\"b\n"));
        Assert.Throws<FormatException>(() => Csv.Read("a,\"b\"c\n"));
        Assert.Empty(Csv.Read("\n\n"));
    }

    [Fact]
    public void Xliff_round_trips_units_keyed_id_and_field_with_their_state()
    {
        TranslationItem[] items =
        [
            new("01JBS3C4DWA7N36096Q14DR9GP", "01JB9EE0ZBGJ09TQM83XSSSS6Y", "label", "Kilogram", "Kilogramme", "Kilogramme", "translated", ".maquettiste/model/locales/fr/_reference-data.json"),
            new("01JBQY77ZXYYK596NGYA1DQ91K", "01JB9EE0ZBGJ09TQM83XSSSS6Y", "label", "Gram & <co>", null, "Gram & <co>", "missing", ".maquettiste/model/locales/fr/_reference-data.json"),
            new("01JBM9S346Q3D25VT4F5V37E3S", "01JBM9S346Q3D25VT4F5V37E3S", "displayName", "Unit", "Unité", "Unité", "stale", ".maquettiste/model/locales/fr/_root.json"),
        ];

        var text = TranslationFiles.WriteXliff("en", "fr", items);
        var units = TranslationFiles.ReadXliff(text);

        Assert.Contains("version=\"2.1\" srcLang=\"en\" trgLang=\"fr\"", text, StringComparison.Ordinal);
        Assert.Contains("xmlns=\"urn:oasis:names:tc:xliff:document:2.0\"", text, StringComparison.Ordinal);
        Assert.Contains("<unit id=\"01JBM9S346Q3D25VT4F5V37E3S/displayName\">", text, StringComparison.Ordinal);
        Assert.Contains("state=\"initial\" subState=\"maquettiste:stale\"", text, StringComparison.Ordinal);
        Assert.Contains("Gram &amp; &lt;co&gt;", text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split("<file ").Length - 1);
        Assert.Equal(
            [new TranslationUnit("01JBS3C4DWA7N36096Q14DR9GP", "label", "Kilogramme"), new TranslationUnit("01JBM9S346Q3D25VT4F5V37E3S", "displayName", "Unité")],
            units);
        Assert.Equal(text, TranslationFiles.WriteXliff("en", "fr", items));
    }

    [Fact]
    public void Xliff_reader_refuses_other_documents_and_a_doctype()
    {
        Assert.Throws<FormatException>(() => TranslationFiles.ReadXliff("<xliff version=\"1.2\"/>"));
        Assert.Throws<FormatException>(() => TranslationFiles.ReadXliff("<!DOCTYPE x [<!ENTITY e \"x\">]><xliff xmlns=\"urn:oasis:names:tc:xliff:document:2.0\"/>"));
        Assert.Throws<FormatException>(() => TranslationFiles.ReadXliff("not xml"));
    }

    [Fact]
    public void Translation_csv_round_trips_and_needs_its_three_columns()
    {
        TranslationItem[] items = [new("01JBS3C4DWA7N36096Q14DR9GP", "o", "label", "Kilogram", "Kilo, gramme", "Kilo, gramme", "translated", "s.json")];

        var text = TranslationFiles.WriteCsv(items);

        Assert.StartsWith("id,field,source,translation,state,shard\n", text, StringComparison.Ordinal);
        Assert.Equal([new TranslationUnit("01JBS3C4DWA7N36096Q14DR9GP", "label", "Kilo, gramme")], TranslationFiles.ReadCsv(text));
        Assert.Throws<FormatException>(() => TranslationFiles.ReadCsv("id,field\nA,label\n"));
    }
}
