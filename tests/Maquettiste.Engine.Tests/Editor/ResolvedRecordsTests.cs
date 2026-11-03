using System.Collections.Immutable;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Operations.OperationFixture;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>The flat records of <c>get_resolved_model</c> (<see cref="ResolvedRecords"/>): the <c>migrations</c> and <c>operations</c>
/// scopes.</summary>
public sealed class ResolvedRecordsTests
{
    private const string F1 = "01JFCT00000000000000000001";
    private const string F2 = "01JFCT00000000000000000002";
    private const string F3 = "01JFCT00000000000000000003";

    [Fact]
    public void The_migrations_scope_lists_facts_by_database_name_then_applied_order()
    {
        var model = new ModelBuilder();
        var main = model.Database("main", Dialect.PostgreSql);
        var audit = model.Database("audit", Dialect.PostgreSql);
        var notes = model.Table("notes", main).Column("id", "int64").Column("body", "text").PrimaryKey("id");
        var body = notes.ColumnId("body");
        var column = new MigrationObject { Kind = "column", Table = notes.Id, Key = body, Schema = "public", TableName = "notes", Name = "body" };
        var guard = ImmutableSortedDictionary<string, string>.Empty.Add("*", "select 1");
        // In main, F1 follows F2, so F2 comes first; audit sorts before main.
        model.Add(new Migration
            {
                Id = F1, Database = main.Id, Fact = MigrationFact.ChangeType, After = [F2], Object = column,
                From = new MigrationState { Type = "text", NativeType = "text" }, To = new MigrationState { Type = "string", NativeType = "varchar(20)" },
            })
            .Add(new Migration
            {
                Id = F2, Database = main.Id, Fact = MigrationFact.Rename, Object = column with { Name = "text" },
                From = new MigrationState { Name = "text" }, To = new MigrationState { Name = "body" },
            })
            .Add(new Migration { Id = F3, Database = audit.Id, Fact = MigrationFact.Raw, Sql = guard, SkipWhen = guard });
        var resolved = ResolutionKit.Resolve(model);

        Assert.Contains("migrations", ResolvedRecords.ScopeNames);
        Assert.Equal("migration", ResolvedRecords.Scopes["migrations"]);

        var (page, next) = ModelPages.Page(ResolvedRecords.Entries(resolved, "migrations", null), e => (e.Kind, e.Name, e.Id), null, 100);
        Assert.Null(next);
        var records = page.Select(e => Assert.IsType<MigrationRecord>(e.Project())).ToList();
        Assert.Equal([F3, F2, F1], records.Select(r => r.Id));
        Assert.Equal([audit.Id, main.Id, main.Id], records.Select(r => r.Database));
        Assert.Equal([1, 1, 2], records.Select(r => r.Order));

        var change = records[2];
        Assert.Equal("migration", change.Kind);
        Assert.Equal("", change.Name);
        Assert.Equal("change-type", change.Fact);
        Assert.Equal("pending", change.State);
        Assert.Equal(["conversion", "dataCheck"], change.Missing);
        Assert.Equal([F2], change.After);
        Assert.Equal(column, change.Object);
        Assert.Equal(body, change.CurrentKey);
        Assert.Equal("varchar(20)", change.To!.NativeType);
        Assert.Equal(resolved.Find(F1) is Engine.Resolution.RMigration m ? m.Hash : null, change.Hash);
        Assert.Matches("^[0-9a-f]{64}$", change.Hash);
        Assert.Null(records[0].Object);
        Assert.Null(records[0].CurrentKey);

        // Paged one at a time, the cursor walks the same order.
        var walked = new List<string>();
        string? cursor = null;
        do
        {
            (page, cursor) = ModelPages.Page(ResolvedRecords.Entries(resolved, "migrations", null), e => (e.Kind, e.Name, e.Id), cursor, 1);
            walked.AddRange(page.Select(e => e.Id));
        }
        while (cursor is not null);
        Assert.Equal([F3, F2, F1], walked);

        // A database narrows the scope; all holds the facts too.
        Assert.Equal([F1, F2], ResolvedRecords.Entries(resolved, "migrations", main.Id).Select(e => e.Id).Order(StringComparer.Ordinal));
        Assert.Equal(3, ResolvedRecords.Entries(resolved, "all", null).Count(e => e.Kind == "migration"));
    }

    [Fact]
    public async Task Operations_scope_lists_send_with_members_outcomes_errors_and_fires()
    {
        await using var s = await OpenAsync();
        var resolved = ResolutionKit.Resolve(s.Model);

        Assert.Equal("operation", ResolvedRecords.Scopes["operations"]);
        Assert.Equal(ResolvedRecords.ScopeNames.ToList().IndexOf("scenarios") + 1, ResolvedRecords.ScopeNames.ToList().IndexOf("operations"));
        var send = Assert.IsType<OperationRecord>(Assert.Single(ResolvedRecords.Entries(resolved, "operations", null)).Project());
        Assert.Equal((Send, "operation", "send", Billing, Invoice, "record", "change"), (send.Id, send.Kind, send.Name, send.Package, send.Subject, send.Level, send.Effect));

        var recipient = Assert.Single(send.Input);
        Assert.Equal((Recipient, "recipient", "string", 254, true, 0), (recipient.Id, recipient.Name, recipient.Type!.Builtin, recipient.Length, recipient.Required, recipient.Order));
        Assert.Null(recipient.KeyOf);
        Assert.Equal("recipient", recipient.DisplayName);

        Assert.Equal([Applied, Waiting], send.Outcomes.Select(o => o.Id));
        Assert.Equal(Applied, send.DefaultOutcome);
        var (applied, waiting) = (send.Outcomes[0], send.Outcomes[1]);
        Assert.Equal((true, "now", Invoice, false), (applied.IsDefault, applied.Completes, applied.Returns!.Entity, applied.Returns.List));
        Assert.Empty(applied.Returns.Members);
        Assert.Equal((false, "later", (string?)null), (waiting.IsDefault, waiting.Completes, waiting.Returns!.Entity));
        Assert.Equal([(TrackingId, "ulid")], waiting.Returns.Members.Select(m => (m.Id, m.Type!.Builtin!)));

        Assert.Equal([(AlreadySent, "already_sent", (string?)null), (MissingRecipient, "missing_recipient", Recipient)],
            send.Errors.Select(e => (e.Id, e.Code, e.Input)));
        Assert.Equal([Sent], send.Fires);
        Assert.Empty(send.Requires);

        // all holds it too; the database narrowing leaves operations alone.
        Assert.Equal([Send], ResolvedRecords.Entries(resolved, "all", null).Where(e => e.Kind == "operation").Select(e => e.Id));
    }
}
