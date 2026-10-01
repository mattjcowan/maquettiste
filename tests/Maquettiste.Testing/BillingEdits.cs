namespace Maquettiste.Testing;

/// <summary>
/// Edits of the billing fixture shared by the validate tests of every host: the invoices table's overlay pins the foreign key column
/// <c>invoices.customer_id</c> to <c>string(40)</c> while <c>customers.id</c> is a <c>uuid</c>. Validation's file rules cannot see it
/// (the referenced table is synthesized); the resolver reports it as MQ4005 on the overlay entry, which every validate path carries.
/// </summary>
public static class BillingEdits
{
    /// <summary>The invoices table's overlay file.</summary>
    public const string InvoiceOverlayPath = ".maquettiste/model/databases/main/tables/01j92p0v1t0j6rh4my9h81nyb4.json";

    /// <summary>The invoices table's overlay id.</summary>
    public const string InvoiceOverlayId = "01J92P0V1T0J6RH4MY9H81NYB4";

    /// <summary>The pointer of the pinned entry (the first column entry).</summary>
    public const string PinnedPointer = "/columns/0";

    /// <summary>The MQ4005 message the pinned entry raises.</summary>
    public const string ForeignKeyMismatch =
        "Foreign key column 'invoices.customer_id' is string(40) (varchar(40)) but references 'customers.id', which is uuid (uuid) (database 'main').";

    private const string Columns = "\n  \"columns\": [\n";

    /// <summary>The overlay's text with the foreign key column's entry pinned first.</summary>
    /// <param name="overlay">The overlay file's text.</param>
    /// <returns>The edited text.</returns>
    public static string PinCustomerForeignKey(string overlay)
    {
        var at = overlay.IndexOf(Columns, StringComparison.Ordinal);
        if (at < 0)
            throw new InvalidOperationException("The invoices overlay has no columns array.");
        const string entry =
            "    {\n      \"id\": \"01J9ZZ0000000000000000FK40\",\n      \"attribute\": \"01J92P0V1EHF7PB28CZJG9C5SN.01J92P0V0KGPC29TQQG8R57EBM\",\n      \"type\": \"string\",\n      \"length\": 40\n    },\n";
        return overlay[..(at + Columns.Length)] + entry + overlay[(at + Columns.Length)..];
    }
}
