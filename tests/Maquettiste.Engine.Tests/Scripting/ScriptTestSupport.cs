using System.Collections.Frozen;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;

namespace Maquettiste.Engine.Tests.Scripting;

/// <summary>A recorder that keeps every key in order.</summary>
internal sealed class ListRecorder : IReadRecorder
{
    public List<string> Keys { get; } = [];

    public void Record(string dependencyKey) => Keys.Add(dependencyKey);
}

/// <summary>A small hand-built resolved model (the resolver is another workstream).</summary>
internal sealed class ResolvedFixture
{
    public ResolvedFixture()
    {
        Billing = new RPackage { Id = "PKG", Name = "Billing", QualifiedName = "Billing", Dependencies = ["e:PKG"] };
        Customer = new REntity
        {
            Id = "CUSTOMER", Name = "Customer", DisplayName = "Customer", PluralName = "Customers", Package = Billing,
            Tags = ["core"], Stereotypes = [new RStereotype { Key = "audited", Name = "Audited" }],
            Properties = new Dictionary<string, object?> { ["zeta"] = 1L, ["alpha"] = "a" }.ToFrozenDictionary(StringComparer.Ordinal),
            Dependencies = ["e:CUSTOMER", "r:CUSTOMER"],
        };
        var stringType = new RType { Kind = "builtin", Name = "string", Builtin = "string" };
        CustomerName = new RAttribute { Id = "CUSTOMER_NAME", Name = "name", Owner = Customer, DeclaringEntity = Customer, Type = stringType, Required = true, Length = 120, Dependencies = ["e:CUSTOMER"] };
        var email = new RAttribute { Id = "CUSTOMER_EMAIL", Name = "email", Owner = Customer, DeclaringEntity = Customer, Type = stringType, Dependencies = ["e:CUSTOMER"] };
        Customer.Attributes = new RList<RAttribute>([CustomerName, email], ["m:customer-attributes"]);
        Customer.OwnAttributes = Customer.Attributes;

        Invoice = new REntity { Id = "INVOICE", Name = "Invoice", DisplayName = "Invoice", PluralName = "Invoices", Package = Billing, Dependencies = ["e:INVOICE"] };
        Invoice.Attributes = new RList<RAttribute>([], ["m:invoice-attributes"]);
        Billing.Entities = new RList<REntity>([Customer, Invoice], ["k:entity"]);

        Model = new ResolvedModel
        {
            Packages = new RList<RPackage>([Billing], ["k:package"]),
            Entities = new RList<REntity>([Customer, Invoice], ["k:entity"]),
            ById = new Dictionary<string, IResolvedObject>
            {
                ["PKG"] = Billing, ["CUSTOMER"] = Customer, ["INVOICE"] = Invoice, ["CUSTOMER_NAME"] = CustomerName, ["CUSTOMER_EMAIL"] = email,
            }.ToFrozenDictionary(StringComparer.Ordinal),
        };
    }

    public ResolvedModel Model { get; }

    public RPackage Billing { get; }

    public REntity Customer { get; }

    public RAttribute CustomerName { get; }

    public REntity Invoice { get; }
}

internal static class Scripts
{
    public const string PackScript = "templates/demo/helpers.js";
    public const string RuleScript = ".maquettiste/extensions/rules/naming.js";

    public static SandboxLimits Limits { get; } = new();

    public static ScriptSource Source(string code, string path = PackScript) => new(path, code, "hash-" + path);

    public static IScriptSandboxPool Pool(string code, SandboxLimits? limits = null, int size = 1, string path = PackScript) =>
        new ScriptSandboxFactory().CreatePool([Source(code, path)], limits ?? Limits, size, CancellationToken.None);

    public static IScriptSandboxPool PoolWithRunToken(string code, SandboxLimits limits, CancellationToken runToken) =>
        new ScriptSandboxFactory().CreatePool([Source(code)], limits, 1, runToken);

    public static IScriptSandboxPool Pool(IReadOnlyList<ScriptSource> sources, SandboxLimits? limits = null, int size = 1) =>
        new ScriptSandboxFactory().CreatePool(sources, limits ?? Limits, size, CancellationToken.None);

    public static ScriptCallContext Ctx(IReadRecorder? reads = null, string seed = "unit", IReadOnlyDictionary<string, object?>? parameters = null) =>
        new(reads, seed, parameters ?? new Dictionary<string, object?>(), CancellationToken.None);

    public static ScriptCallContext CtxWithToken(CancellationToken callToken) =>
        new(null, "unit", new Dictionary<string, object?>(), callToken);

    public static object? Helper(this IScriptSandboxPool pool, string name, params object?[] args)
    {
        using var lease = pool.Rent();
        return lease.Sandbox.CallHelper(name, args, Ctx());
    }
}
