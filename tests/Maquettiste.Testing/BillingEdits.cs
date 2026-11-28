namespace Maquettiste.Testing;

/// <summary>
/// Edits of the billing fixture shared by the validate tests of every host: the invoices table pins its foreign key column
/// <c>customer_id</c> to the native type <c>char(36)</c> while <c>customers.id</c> is a plain <c>uuid</c>. Both columns state the type
/// uuid, so validation's file rule does not see it; the resolver reports it as MQ4005 on the column entry, which every validate path
/// carries.
/// </summary>
public static class BillingEdits
{
    /// <summary>The invoices table's file.</summary>
    public const string InvoiceOverlayPath = ".maquettiste/model/databases/main/tables/invoices.json";

    /// <summary>The invoices table's id.</summary>
    public const string InvoiceOverlayId = "01J92P0V1T0J6RH4MY9H81NYB4";

    /// <summary>The pointer of the pinned column entry.</summary>
    public const string PinnedPointer = "/columns/10";

    /// <summary>The MQ4005 message the pinned column raises.</summary>
    public const string ForeignKeyMismatch =
        "Foreign key column 'invoices.customer_id' is uuid (char(36)) but references 'customers.id', which is uuid (uuid) (database 'main').";

    private const string Column = "\"name\": \"customer_id\",\n      \"type\": \"uuid\",\n";

    /// <summary>The invoices table's text with the foreign key column pinned to <c>char(36)</c>.</summary>
    /// <param name="invoices">The table file's text.</param>
    /// <returns>The edited text.</returns>
    public static string PinCustomerForeignKey(string invoices)
    {
        var at = invoices.IndexOf(Column, StringComparison.Ordinal);
        if (at < 0)
            throw new InvalidOperationException("The invoices table has no customer_id column of type uuid.");
        return invoices[..(at + Column.Length)] + "      \"nativeType\": \"char(36)\",\n" + invoices[(at + Column.Length)..];
    }
}
