using Dapper;

namespace ReferenceApp.Data.Sales;

// Hand-written query next to the generated CRUD: the lines of one order, in line-number order.
public partial class SalesOrderLineRepository
{
    private const string ListByOrderSql = "SELECT " + Columns + " FROM northwind.sales_order_lines WHERE sales_order_id = @SalesOrderId ORDER BY line_number";

    /// <summary>Lists the lines of a sales order by line number.</summary>
    public async Task<IReadOnlyList<SalesOrderLine>> ListBySalesOrderAsync(Guid salesOrderId, CancellationToken cancellationToken = default)
    {
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(ListByOrderSql, new { SalesOrderId = salesOrderId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(Row.ToEntity).ToList();
    }
}
