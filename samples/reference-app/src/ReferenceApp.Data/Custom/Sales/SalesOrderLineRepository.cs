using Dapper;

namespace ReferenceApp.Data.Sales;

// Hand-written query next to the generated CRUD: the lines of one order, in line-number order. It reads the columns the generated
// select by key reads (the binding's columns), so it follows the table when the binding changes; if the generated select ever stops
// ending in its key filter, the first use fails loudly instead of running a different query.
public partial class SalesOrderLineRepository
{
    private const string KeyFilter = "WHERE t.id = @id";

    private static readonly Lazy<string> ListByOrderSql = new(() =>
    {
        var at = SelectSql.LastIndexOf(KeyFilter, StringComparison.Ordinal);
        if (at < 0)
            throw new InvalidOperationException($"The generated select of {nameof(SalesOrderLineRepository)} no longer ends in '{KeyFilter}'; update ListBySalesOrderAsync.");
        return string.Concat(SelectSql.AsSpan(0, at), "WHERE t.sales_order_id = @SalesOrderId ORDER BY t.line_number", SelectSql.AsSpan(at + KeyFilter.Length));
    });

    /// <summary>Lists the lines of a sales order by line number.</summary>
    public async Task<IReadOnlyList<SalesOrderLine>> ListBySalesOrderAsync(Guid salesOrderId, CancellationToken cancellationToken = default)
    {
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(ListByOrderSql.Value, new { SalesOrderId = salesOrderId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(Row.ToEntity).ToList();
    }
}
