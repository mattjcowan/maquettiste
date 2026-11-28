using System.Collections.Immutable;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Edge cases of physical resolution: foreign keys that cannot be resolved, bindings the design leaves to the resolver,
/// what a relation's foreign key depends on, explicit plural names, stored derived attributes and list memberships.
/// </summary>
public sealed class ResolutionEdgeTests
{
    private static Table DesignedTable(ModelBuilder b, DatabaseBuilder db, string name, bool withPrimaryKey, params (string Name, string Type)[] columns)
    {
        var cols = columns.Select(c => new Column { Id = b.NewId(), Name = c.Name, Type = c.Type, Nullable = false }).ToImmutableArray();
        return new Table
        {
            Id = b.NewId(), Name = name, Database = db.Id, Columns = cols,
            PrimaryKey = withPrimaryKey ? new PrimaryKey { Columns = [cols[0].Id] } : null,
        };
    }

    [Fact]
    public void A_designed_foreign_key_to_a_missing_table_is_reported()
    {
        var b = new ModelBuilder(seed: 92);
        var db = b.Database("main", Dialect.PostgreSql);
        var table = DesignedTable(b, db, "audit", true, ("id", "int64"), ("ref", "int64"));
        table = table with { ForeignKeys = [new ForeignKey { Id = b.NewId(), Name = "fk_audit_ref", Columns = [table.Columns[1].Id], ReferencesTable = b.NewId() }] };
        b.Add(table);
        var model = ResolutionKit.Resolve(b);
        Assert.Empty(model.Db("main").Table("audit").ForeignKeys);
        var diagnostic = Assert.Single(model.Diagnostics);
        Assert.Equal(("MQ4008", table.Id), (diagnostic.Rule, diagnostic.ElementId));
        Assert.StartsWith("Foreign key 'fk_audit_ref' in table 'audit'", diagnostic.Message, StringComparison.Ordinal);
    }

}
