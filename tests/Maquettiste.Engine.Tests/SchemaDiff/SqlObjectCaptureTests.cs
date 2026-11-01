using System.Collections.Immutable;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.SchemaDiff;

/// <summary>
/// SQL object environments and run-once (next-phase-1-design.md section 3.4): the resolved object carries its environments, apply
/// mode and body hash, and the snapshot's definition text gains <c>environments</c> and <c>apply</c> lines only when they are not
/// the defaults, so physical snapshots taken before they existed stay byte-identical.
/// </summary>
public sealed class SqlObjectCaptureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Body = "CREATE TRIGGER invoice_audit AFTER INSERT ON invoices FOR EACH ROW EXECUTE FUNCTION audit();";

    /// <summary>A PostgreSQL database <c>billing</c> with the SQL objects <c>invoice_audit</c> and <c>invoice_cleanup</c>.</summary>
    private static ModelBuilder Model(IReadOnlyList<string> environments, SqlObjectApply apply)
    {
        var b = new ModelBuilder(seed: 271);
        var db = b.Database("billing", Dialect.PostgreSql);
        b.Add(new SqlObject
        {
            Id = b.NewId(), Name = "invoice_audit", Database = db.Id, ObjectKind = "trigger", Environments = environments, Apply = apply,
            Body = ImmutableDictionary<string, string>.Empty.Add("postgresql", Body),
        });
        b.Add(new SqlObject
        {
            Id = b.NewId(), Name = "invoice_cleanup", Database = db.Id, ObjectKind = "job", Phase = SqlObjectPhase.Before,
            Body = ImmutableDictionary<string, string>.Empty.Add("*", "select 1;"),
        });
        return b;
    }

    [Fact]
    public void Default_environments_and_apply_keep_the_definition_text_byte_identical()
    {
        var plain = ResolutionKit.Resolve(Model([], SqlObjectApply.Converge)).Db("billing");
        var audit = plain.Objects.Single(o => o.Name == "invoice_audit");
        Assert.Empty(audit.Environments);
        Assert.Equal(("converge", ContentHash.Of(Body)), (audit.Apply, audit.ContentHash));

        var definitions = SnapshotCapture.Capture(plain, 1, ct: Ct).Objects.ToDictionary(o => o.Name, o => o.Definition, StringComparer.Ordinal);
        Assert.Equal("phase: after\nbody: " + Body, definitions["invoice_audit"]);
        Assert.Equal("phase: before\nbody: select 1;", definitions["invoice_cleanup"]);

        var scoped = ResolutionKit.Resolve(Model(["dev", "test"], SqlObjectApply.Once)).Db("billing");
        audit = scoped.Objects.Single(o => o.Name == "invoice_audit");
        Assert.Equal(["dev", "test"], audit.Environments);
        Assert.Equal(("once", ContentHash.Of(Body)), (audit.Apply, audit.ContentHash));
        definitions = SnapshotCapture.Capture(scoped, 1, ct: Ct).Objects.ToDictionary(o => o.Name, o => o.Definition, StringComparer.Ordinal);
        Assert.Equal("phase: after\nenvironments: dev,test\napply: once\nbody: " + Body, definitions["invoice_audit"]);
        Assert.Equal("phase: before\nbody: select 1;", definitions["invoice_cleanup"]);
    }

    [Fact]
    public void Environments_in_another_order_capture_the_same_definition_text()
    {
        var reordered = ResolutionKit.Resolve(Model(["test", "dev"], SqlObjectApply.Once)).Db("billing");
        var audit = reordered.Objects.Single(o => o.Name == "invoice_audit");
        Assert.Equal(["test", "dev"], audit.Environments);
        var definition = SnapshotCapture.Capture(reordered, 1, ct: Ct).Objects.Single(o => o.Name == "invoice_audit").Definition;
        Assert.Equal("phase: after\nenvironments: dev,test\napply: once\nbody: " + Body, definition);
    }
}
