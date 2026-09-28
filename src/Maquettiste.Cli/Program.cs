namespace Maquettiste.Cli;

/// <summary>The <c>maquettiste</c> entry point (engine-design.md section 16): wires the console and Ctrl+C to <see cref="CliApp"/>.</summary>
public static class Program
{
    /// <summary>Exit codes (SPEC section 17; D23). With several outcomes the precedence is 4, 1, 3, 2.</summary>
    public static class ExitCodes
    {
        /// <summary>Success.</summary>
        public const int Success = 0;

        /// <summary>Validation errors in the model or packs, template and script errors included.</summary>
        public const int Invalid = 1;

        /// <summary>Drift found by <c>--check</c> (and a failed budget in <c>bench</c>).</summary>
        public const int Drift = 2;

        /// <summary>Hand-edit or region conflicts.</summary>
        public const int Conflicts = 3;

        /// <summary>Internal error, usage error, busy lock or cancellation.</summary>
        public const int Internal = 4;
    }

    /// <summary>Runs the CLI.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>The exit code.</returns>
    public static int Main(string[] args) => MainAsync(args).GetAwaiter().GetResult();

    /// <summary>Runs the CLI asynchronously against the process console; Ctrl+C cancels the command.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> MainAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        using var cts = new CancellationTokenSource();
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            // The first Ctrl+C cancels gracefully (the writer finishes the current file); a second one kills the process.
            if (cts.IsCancellationRequested)
                return;
            e.Cancel = true;
            cts.Cancel();
        }

        Console.CancelKeyPress += OnCancel;
        try
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            var stderr = new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            await using (stdout.ConfigureAwait(false))
            await using (stderr.ConfigureAwait(false))
            {
                var environment = new CliEnvironment
                {
                    Out = stdout,
                    Error = TextWriter.Synchronized(stderr),
                    CurrentDirectory = Environment.CurrentDirectory,
                    GetEnvironmentVariable = Environment.GetEnvironmentVariable,
                    ErrorIsTerminal = !Console.IsErrorRedirected,
                };
                return await new CliApp(environment).RunAsync(args, cts.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }
    }
}
