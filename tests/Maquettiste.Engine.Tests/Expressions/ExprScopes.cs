using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Tests.Expressions;

/// <summary>An invented billing scope the expression tests share: an invoice's members, a customer, enums, codes and constraints.</summary>
internal static class ExprScopes
{
    public static readonly string IssuedOn = Id(1), DueOn = Id(2), Total = Id(3), Quantity = Id(4), Code = Id(5), Status = Id(6), Terms = Id(7),
        Weight = Id(8), Notes = Id(9), Payload = Id(10), CreatedAt = Id(11), SentAt = Id(12), Price = Id(13), PriceAmount = Id(13) + "." + Id(14),
        OrderDate = Id(15), Small = Id(16), Big = Id(17), Rate = Id(18), Opens = Id(19), Reference = Id(20), Customer = Id(21), CreditLimit = Id(22),
        CustomerName = Id(23), Lines = Id(24), LineAmount = Id(25), StatusEnum = Id(26), Draft = Id(27), Paid = Id(28), PaymentTerm = Id(29),
        Net30 = Id(30), Positive = Id(31), Amount = Id(32), Recent = Id(33), At = Id(34), LoopA = Id(35), LoopB = Id(36), ValueA = Id(37),
        ValueB = Id(38), Flag = Id(39), Region = Id(40), Country = Id(41), CountryCode = Id(42), PaddedCode = Id(43);

    public static string Id(int n) => "01JEXT" + n.ToString("D20", CultureInfo.InvariantCulture);

    /// <summary>The invoice scope at a site.</summary>
    public static ExprScope Invoice(ExprSiteKind site = ExprSiteKind.Check, ExprType? valueType = null, string dueOnName = "dueOn")
    {
        var lines = new ExprContainer().Add(new ExprMember(LineAmount, "amount", ExprType.Decimal, false));
        var country = new ExprContainer().Add(new ExprMember(CountryCode, "code", ExprType.String, false));
        var region = new ExprContainer().Add(new ExprNavigation(Country, "country", country, ToOne: true, Optional: false));
        var customer = new ExprContainer()
            .Add(new ExprMember(CreditLimit, "creditLimit", ExprType.Decimal, false))
            .Add(new ExprMember(CustomerName, "name", ExprType.String, false))
            .Add(new ExprNavigation(Region, "region", region, ToOne: true, Optional: false));
        var scope = new ExprScope(site) { ValueType = valueType, ElementId = Id(99), FilePath = ".maquettiste/model/entities/invoice.json", Pointer = "/checks/0/condition" };
        scope.Root
            .Add(new ExprMember(IssuedOn, "issuedOn", ExprType.Date, false))
            .Add(new ExprMember(DueOn, dueOnName, ExprType.Date, true))
            .Add(new ExprMember(Total, "total", ExprType.Decimal, false) { Scale = 2 })
            .Add(new ExprMember(Quantity, "quantity", ExprType.Int32, false))
            .Add(new ExprMember(Code, "code", ExprType.String, false))
            .Add(new ExprMember(Status, "status", ExprType.Enum(StatusEnum), false))
            .Add(new ExprMember(Terms, "terms", ExprType.Code(PaymentTerm), true))
            .Add(new ExprMember(Weight, "weight", ExprType.FromBuiltin("float"), false))
            .Add(new ExprMember(Notes, "notes", ExprType.String, false) { Collection = true })
            .Add(new ExprMember(Payload, "payload", ExprType.Json, true))
            .Add(new ExprMember(CreatedAt, "createdAt", ExprType.DateTime, false))
            .Add(new ExprMember(SentAt, "sentAt", ExprType.DateTimeOffset, true))
            .Add(new ExprMember(Price, "price", new ExprType("value-object", Id(90)), false))
            .Add(new ExprMember(PriceAmount, "amount", ExprType.Decimal, false) { Prefix = "price" })
            .Add(new ExprMember(OrderDate, "order date", ExprType.Date, false))
            .Add(new ExprMember(Small, "small", ExprType.Int16, false))
            .Add(new ExprMember(Big, "big", ExprType.Int64, false))
            .Add(new ExprMember(Rate, "rate", ExprType.Double, false))
            .Add(new ExprMember(Opens, "opens", ExprType.Time, false))
            .Add(new ExprMember(Reference, "reference", ExprType.Uuid, false))
            .Add(new ExprMember(Flag, "flag", ExprType.Bool, true))
            .Add(new ExprMember(PaddedCode, "padded", ExprType.String, false) { FixedLength = true })
            .Add(new ExprNavigation(Customer, "customer", customer, ToOne: true, Optional: true))
            .Add(new ExprNavigation(Lines, "lines", lines, ToOne: false, Optional: false));
        scope.AddEnum(StatusEnum, "InvoiceStatus", [(Draft, "Draft"), (Paid, "Paid")]);
        scope.AddReferenceType(PaymentTerm, "PaymentTerm", [(Net30, "NET30")]);
        scope.AddConstraint(new ExprConstraint(Positive, "PositiveAmount", [new ExprMember(Amount, "amount", ExprType.Decimal, false)],
            Parse("""{ "op": "gt", "args": [{ "path": ["{{Amount}}"] }, { "value": 0 }] }""")));
        scope.AddConstraint(new ExprConstraint(Recent, "Recent", [new ExprMember(At, "at", ExprType.DateTimeOffset, false)],
            Parse("""{ "op": "ge", "args": [{ "path": ["{{At}}"] }, { "now": true }] }""")));
        scope.AddConstraint(new ExprConstraint(LoopA, "LoopA", [new ExprMember(ValueA, "value", ExprType.Int32, false)],
            Parse("""{ "satisfies": "{{LoopB}}", "with": [{ "parameter": "{{ValueB}}", "value": { "path": ["{{ValueA}}"] } }] }""")));
        scope.AddConstraint(new ExprConstraint(LoopB, "LoopB", [new ExprMember(ValueB, "value", ExprType.Int32, false)],
            Parse("""{ "satisfies": "{{LoopA}}", "with": [{ "parameter": "{{ValueA}}", "value": { "path": ["{{ValueB}}"] } }] }""")));
        return scope;
    }

    /// <summary>A tree from JSON, with <c>{{Name}}</c> replaced by the id of that name.</summary>
    public static Expr Parse(string json) => JsonSerializer.Deserialize<Expr>(Ids(json), EngineJson.Options)!;

    /// <summary>A tree as compact JSON, ids replaced by their names, so expectations read.</summary>
    public static string Show(Expr e) => Names(JsonSerializer.Serialize(e, EngineJson.Options));

    /// <summary>Replaces <c>{{Name}}</c> with the id of that name.</summary>
    public static string Ids(string text)
    {
        foreach (var (name, id) in Known)
            text = text.Replace("{{" + name + "}}", id, StringComparison.Ordinal);
        return text;
    }

    private static string Names(string text)
    {
        foreach (var (name, id) in Known.OrderByDescending(k => k.Id.Length))
            text = text.Replace(id, name, StringComparison.Ordinal);
        return text;
    }

    private static readonly (string Name, string Id)[] Known = typeof(ExprScopes).GetFields()
        .Where(f => f.IsStatic && f.FieldType == typeof(string))
        .Select(f => (f.Name, (string)f.GetValue(null)!))
        .ToArray();
}
