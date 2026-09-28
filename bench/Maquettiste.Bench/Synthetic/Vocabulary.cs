using System.Collections.Immutable;

namespace Maquettiste.Bench.Synthetic;

/// <summary>
/// Word lists of the synthetic model. Words are single capitalized ASCII words of at most eight letters, so generated identifiers
/// (junction tables, foreign-key names) stay under PostgreSQL's 63-character limit and entity names split into exactly two words.
/// </summary>
internal static class Vocabulary
{
    /// <summary>Entity name qualifiers.</summary>
    public static readonly ImmutableArray<string> Qualifiers =
    [
        "Active", "Annual", "Archive", "Base", "Billing", "Branch", "Budget", "Campaign", "Cargo", "Client",
        "Contract", "Credit", "Custom", "Daily", "Debit", "Default", "Digital", "Direct", "Draft", "Export",
        "Field", "Final", "Fiscal", "Global", "Grant", "Group", "Guest", "Import", "Inbound", "Internal",
        "Joint", "Journal", "Launch", "Legacy", "Local", "Loyalty", "Main", "Manual", "Market", "Member",
        "Monthly", "Network", "Office", "Online", "Outbound", "Partner", "Payment", "Pending", "Planned", "Portal",
        "Primary", "Private", "Product", "Public", "Quality", "Quarter", "Regional", "Remote", "Retail", "Review",
        "Sales", "Seasonal", "Service", "Shared", "Site", "Standard", "Store", "Supplier", "System", "Target",
        "Team", "Trade", "Travel", "Trial", "Vendor", "Weekly", "Web", "Yearly",
    ];

    /// <summary>Entity name nouns.</summary>
    public static readonly ImmutableArray<string> Nouns =
    [
        "Account", "Address", "Agent", "Asset", "Batch", "Booking", "Bundle", "Card", "Case", "Charge",
        "Claim", "Contact", "Coupon", "Deal", "Device", "Discount", "Document", "Entry", "Event", "Fee",
        "Form", "Invoice", "Item", "Job", "Lead", "Ledger", "License", "Line", "Lot", "Message",
        "Meter", "Note", "Offer", "Order", "Owner", "Parcel", "Payout", "Period", "Permit", "Plan",
        "Policy", "Price", "Profile", "Project", "Quote", "Rate", "Receipt", "Record", "Refund", "Region",
        "Report", "Request", "Reserve", "Result", "Return", "Role", "Route", "Rule", "Sample", "Schedule",
        "Score", "Session", "Shift", "Slot", "Stock", "Survey", "Task", "Tax", "Ticket", "Token",
        "Transfer", "Unit", "Visit", "Voucher", "Warranty",
    ];

    /// <summary>Package names (a number is appended past the end of the list).</summary>
    public static readonly ImmutableArray<string> Packages =
    [
        "Accounting", "Billing", "Catalog", "Compliance", "Contracts", "Crm", "Delivery", "Documents", "Events", "Facilities",
        "Finance", "Fleet", "Grants", "Hr", "Identity", "Insurance", "Inventory", "Invoicing", "Legal", "Logistics",
        "Loyalty", "Maintenance", "Marketing", "Messaging", "Metering", "Orders", "Partners", "Payments", "Payroll", "Planning",
        "Pricing", "Procurement", "Projects", "Quality", "Recruiting", "Rentals", "Reporting", "Returns", "Risk", "Sales",
        "Scheduling", "Security", "Service", "Shipping", "Support", "Surveys", "Tax", "Ticketing", "Training", "Travel",
    ];

    /// <summary>Relation verbs.</summary>
    public static readonly ImmutableArray<string> Verbs = ["has", "owns", "references", "tracks", "covers", "books", "issues", "groups"];

    /// <summary>Attribute words and the type family each one gets.</summary>
    public static readonly ImmutableArray<(string Name, AttributeFamily Family)> Attributes =
    [
        ("name", AttributeFamily.ShortString), ("code", AttributeFamily.Code), ("title", AttributeFamily.ShortString),
        ("label", AttributeFamily.ShortString), ("city", AttributeFamily.ShortString), ("country", AttributeFamily.ShortString),
        ("locale", AttributeFamily.ShortString), ("timezone", AttributeFamily.ShortString), ("channel", AttributeFamily.ShortString),
        ("color", AttributeFamily.ShortString), ("owner", AttributeFamily.ShortString), ("source", AttributeFamily.ShortString),
        ("notes", AttributeFamily.Text), ("summary", AttributeFamily.Text), ("content", AttributeFamily.Text), ("remarks", AttributeFamily.Text),
        ("amount", AttributeFamily.Money), ("price", AttributeFamily.Money), ("total", AttributeFamily.Money), ("balance", AttributeFamily.Money),
        ("discount", AttributeFamily.Money), ("tax", AttributeFamily.Money),
        ("quantity", AttributeFamily.Integer), ("priority", AttributeFamily.Integer), ("capacity", AttributeFamily.Integer),
        ("revision", AttributeFamily.Integer), ("sequence", AttributeFamily.Long), ("counter", AttributeFamily.Long),
        ("weight", AttributeFamily.Real), ("height", AttributeFamily.Real), ("width", AttributeFamily.Real), ("score", AttributeFamily.Real),
        ("active", AttributeFamily.Flag), ("enabled", AttributeFamily.Flag), ("visible", AttributeFamily.Flag), ("archived", AttributeFamily.Flag),
        ("verified", AttributeFamily.Flag),
        ("opened", AttributeFamily.Date), ("closed", AttributeFamily.Date), ("due", AttributeFamily.Date), ("issued", AttributeFamily.Date),
        ("shipped", AttributeFamily.Instant), ("received", AttributeFamily.Instant), ("expires", AttributeFamily.Instant),
        ("external", AttributeFamily.Uuid),
        ("status", AttributeFamily.Enum), ("stage", AttributeFamily.Enum), ("tier", AttributeFamily.Enum), ("mode", AttributeFamily.Enum),
        ("address", AttributeFamily.ValueObject), ("location", AttributeFamily.ValueObject), ("dimensions", AttributeFamily.ValueObject),
        ("email", AttributeFamily.Scalar), ("phone", AttributeFamily.Scalar), ("website", AttributeFamily.Scalar), ("reference", AttributeFamily.Scalar),
    ];

    /// <summary>Value-object member words and their type family.</summary>
    public static readonly ImmutableArray<(string Name, AttributeFamily Family)> Members =
    [
        ("street", AttributeFamily.ShortString), ("city", AttributeFamily.ShortString), ("region", AttributeFamily.ShortString),
        ("postcode", AttributeFamily.Code), ("country", AttributeFamily.Code), ("amount", AttributeFamily.Money),
        ("currency", AttributeFamily.Code), ("latitude", AttributeFamily.Real), ("longitude", AttributeFamily.Real),
        ("unit", AttributeFamily.Code), ("value", AttributeFamily.Real), ("starts", AttributeFamily.Date), ("ends", AttributeFamily.Date),
    ];

    /// <summary>Enum member words.</summary>
    public static readonly ImmutableArray<string> EnumMembers =
        ["Draft", "Open", "Pending", "Active", "Paused", "Closed", "Archived", "Cancelled", "Low", "Medium", "High", "Urgent"];
}

/// <summary>The type an attribute word gets.</summary>
internal enum AttributeFamily
{
    /// <summary>string, length 40 to 200.</summary>
    ShortString,

    /// <summary>string, length 3 to 16.</summary>
    Code,

    /// <summary>text.</summary>
    Text,

    /// <summary>decimal(18,2).</summary>
    Money,

    /// <summary>int32.</summary>
    Integer,

    /// <summary>int64.</summary>
    Long,

    /// <summary>double.</summary>
    Real,

    /// <summary>bool.</summary>
    Flag,

    /// <summary>date.</summary>
    Date,

    /// <summary>datetimeoffset.</summary>
    Instant,

    /// <summary>uuid.</summary>
    Uuid,

    /// <summary>A reference to an enum.</summary>
    Enum,

    /// <summary>A reference to a value object.</summary>
    ValueObject,

    /// <summary>A reference to a custom scalar type.</summary>
    Scalar,
}
