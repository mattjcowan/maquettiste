using ReferenceApp.Data.ReferenceData;

namespace ReferenceApp.Data.Sales;

// Hand-written half of SalesOrderLine: business rules the model does not generate.
public partial class SalesOrderLine
{
    /// <summary>Whether the line still waits for stock or shipment.</summary>
    public bool IsOutstanding => Status is OrderLineStatus.Open or OrderLineStatus.Allocated or OrderLineStatus.Backordered;

    /// <summary>Quantity times unit price, less the line discount (a percentage from 0 to 100), rounded to cents.</summary>
    public Money ComputeLineTotal()
    {
        var gross = Quantity * UnitPrice.Amount;
        var discount = Discount is { } percent ? gross * percent / 100m : 0m;
        return new Money { Amount = Math.Round(gross - discount, 2, MidpointRounding.ToEven), Currency = UnitPrice.Currency };
    }
}
