namespace ReferenceApp.Data.Crm;

// Hand-written half of Customer.
public partial class Customer
{
    /// <summary>Whether new sales orders may be released for this customer.</summary>
    public bool CanReleaseOrders => !OnCreditHold;
}
