namespace Maquettiste.Engine;

/// <summary>
/// The migration read tools (next-phase-1-design.md sections 3.2 and 3.3), shared by MCP and the assistant: <c>list_migrations</c>
/// and <c>get_ledger_ddl</c>.
/// </summary>
public sealed partial class AgentTools
{
    private async Task<AgentToolResult> ListMigrationsAsync(ToolArguments args, CancellationToken ct)
    {
        var limit = args.Int("limit") ?? ModelPages.DefaultLimit;
        if (limit is < 1 or > ModelPages.MaxLimit)
            return BadRequest($"limit must be from 1 to {ModelPages.MaxLimit}.");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        try
        {
            return Ok(await _store.ListMigrationsAsync(args.String("database") is { Length: > 0 } database ? database : null, args.String("cursor"), limit, ct)
                .ConfigureAwait(false));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message); // an unknown database or cursor
        }
    }

    private async Task<AgentToolResult> GetLedgerDdlAsync(ToolArguments args, CancellationToken ct)
    {
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        try
        {
            return Ok(await _store.GetLedgerDdlAsync(args.String("database") is { Length: > 0 } database ? database : null, args.String("grantRead"), ct)
                .ConfigureAwait(false));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message); // no PostgreSQL database, an unknown or other one, several and none named, an empty role
        }
    }
}
