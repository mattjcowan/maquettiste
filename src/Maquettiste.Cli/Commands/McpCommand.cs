using Maquettiste.Cli.Mcp;
using Maquettiste.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste mcp</c>: serves the repository's model over the Model Context Protocol on stdin and stdout (docs/mcp.md). The
/// server runs in-process over one <see cref="ModelStore"/> and one <see cref="GenerationService"/> for the repository that
/// <c>generate</c> would use (<c>--repo</c>, else the nearest ancestor holding <c>.maquettiste/maquettiste.json</c>) with the same cache
/// folder (<c>--cache-dir</c>, <c>$MAQUETTISTE_CACHE_DIR</c>, else the user cache folder). Stdout carries JSON-RPC messages only;
/// messages for people go to stderr. The command ends with exit code 0 when the client closes stdin or on Ctrl+C.
/// </summary>
internal static class McpCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation (Ctrl+C); stops the server.</param>
    /// <returns>The exit code: 0 when the client disconnected or on Ctrl+C, 1 when there is no model.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        context.Line.Expect("mcp", 1);
        if (context.Environment.OpenStandardInput is not { } openInput || context.Environment.OpenStandardOutput is not { } openOutput)
            throw new UsageException("mcp needs the process's standard input and output.");
        if (await ProjectGuard.RepoAsync(context).ConfigureAwait(false) is not { } repo)
            return Program.ExitCodes.Invalid;

        var options = context.EngineOptions(repo);
        var store = new ModelStore(options);
        await using (store.ConfigureAwait(false))
        {
            await store.LoadAsync(ct).ConfigureAwait(false);
            var generation = new GenerationService(store, options);
            var tools = new ModelTools(store, generation, repo, TextWriter.Synchronized(context.Error));
            var serverOptions = McpServerSetup.CreateOptions(tools);
            context.Info($"maquettiste: serving {repo} over MCP (stdio).");

            var input = openInput();
            var output = openOutput();
            await using (input.ConfigureAwait(false))
            await using (output.ConfigureAwait(false))
            {
                var transport = new StreamServerTransport(input, output, "maquettiste", NullLoggerFactory.Instance);
                await using (transport.ConfigureAwait(false))
                {
                    var server = McpServer.Create(transport, serverOptions, NullLoggerFactory.Instance);
                    await using (server.ConfigureAwait(false))
                    {
                        try
                        {
                            await server.RunAsync(ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            // Ctrl+C: stop serving.
                        }
                    }
                }
            }
        }

        return Program.ExitCodes.Success;
    }
}
