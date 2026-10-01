using System.Globalization;

namespace Maquettiste.Cli;

/// <summary>A command-line usage error: exit code 4 with the message on stderr.</summary>
/// <param name="message">What is wrong.</param>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>
/// The hand-written parser (D27: no command-line library). Options may appear anywhere after the program name, as <c>--name value</c>,
/// <c>--name=value</c> or a bare flag; <c>--</c> ends option parsing. Which options take a value is fixed here; which ones a command
/// accepts is checked by <see cref="Expect"/>.
/// </summary>
internal sealed class CommandLine
{
    /// <summary>Options that take a value.</summary>
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--repo", "--cache-dir", "--jobs", "--progress", "--verbosity", "--format", "--output", "--pack", "--roots", "--hand-edits",
        "--from", "--out", "--docker", "--locale", "--mode", "--name", "--seed", "--entities", "--relations", "--enums", "--fanout", "--baseline", "--max-regression",
        "--inputs", "--scenario", "--domain", "--use", "--subject", "--into", "--processes",
        "--kind", "--package", "--tag", "--category", "--stereotype", "--query", "--ids", "--fields", "--scope", "--database", "--by",
        "--resolution",
    };

    /// <summary>Options that are flags.</summary>
    private static readonly HashSet<string> FlagOptions = new(StringComparer.Ordinal)
    {
        "--version", "--help", "--quiet", "--no-color", "--hooks", "--force", "--watch", "--dry-run", "--diff", "--check", "--keep", "--no-wait",
        "--no-example-packs", "--mcp", "--skill", "--agent-setup", "--apply", "--resolved",
    };

    /// <summary>Short aliases.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["-h"] = "--help",
        ["-q"] = "--quiet",
        ["-j"] = "--jobs",
        ["-p"] = "--pack",
    };

    /// <summary>The global options every command accepts (engine-design.md section 16).</summary>
    public static readonly IReadOnlyList<string> GlobalOptions = ["--repo", "--cache-dir", "--jobs", "--progress", "--verbosity", "--no-color", "--quiet", "--help"];

    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    private CommandLine(IReadOnlyList<string> positionals) => Positionals = positionals;

    /// <summary>The words that are not options (the command, sub-command and arguments), in order.</summary>
    public IReadOnlyList<string> Positionals { get; private set; }

    /// <summary>Parses arguments.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>The parsed command line.</returns>
    /// <exception cref="UsageException">An unknown option or a missing value.</exception>
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var positionals = new List<string>();
        var line = new CommandLine(positionals);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                positionals.AddRange(args.Skip(i + 1));
                break;
            }

            if (!arg.StartsWith('-') || arg == "-")
            {
                positionals.Add(arg);
                continue;
            }

            string name;
            string? value = null;
            var equals = arg.IndexOf('=', StringComparison.Ordinal);
            if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 2)
            {
                name = arg[..equals];
                value = arg[(equals + 1)..];
            }
            else
            {
                name = arg;
            }

            if (Aliases.TryGetValue(name, out var alias))
                name = alias;
            if (ValueOptions.Contains(name))
            {
                if (value is null)
                {
                    if (i + 1 >= args.Count)
                        throw new UsageException($"The option {name} needs a value.");
                    value = args[++i];
                }

                line.Add(name, value);
            }
            else if (FlagOptions.Contains(name))
            {
                if (value is not null)
                    throw new UsageException($"The option {name} takes no value.");
                line.Add(name, "");
            }
            else
            {
                throw new UsageException($"Unknown option '{arg}'.");
            }
        }

        return line;
    }

    /// <summary>Whether a flag or option was given.</summary>
    /// <param name="name">The option name.</param>
    /// <returns><see langword="true"/> when present.</returns>
    public bool Has(string name) => _values.ContainsKey(name);

    /// <summary>Returns the value of a single-valued option.</summary>
    /// <param name="name">The option name.</param>
    /// <returns>The value, or <see langword="null"/> when absent.</returns>
    /// <exception cref="UsageException">The option was given more than once.</exception>
    public string? Value(string name)
    {
        if (!_values.TryGetValue(name, out var values))
            return null;
        if (values.Count > 1)
            throw new UsageException($"The option {name} can be given only once.");
        return values[0];
    }

    /// <summary>Returns every value of a repeatable option, in order.</summary>
    /// <param name="name">The option name.</param>
    /// <returns>The values.</returns>
    public IReadOnlyList<string> Values(string name) => _values.TryGetValue(name, out var values) ? values : [];

    /// <summary>Returns a single-valued option that must be one of a set of words.</summary>
    /// <param name="name">The option name.</param>
    /// <param name="fallback">The value when absent.</param>
    /// <param name="allowed">The allowed values.</param>
    /// <returns>The value.</returns>
    public string Choice(string name, string fallback, params string[] allowed)
    {
        var value = Value(name) ?? fallback;
        if (!allowed.Contains(value, StringComparer.Ordinal))
            throw new UsageException($"The option {name} must be one of {string.Join(", ", allowed)}; got '{value}'.");
        return value;
    }

    /// <summary>Returns an integer option.</summary>
    /// <param name="name">The option name.</param>
    /// <param name="min">The smallest allowed value.</param>
    /// <returns>The value, or <see langword="null"/> when absent.</returns>
    public int? Int(string name, int min)
    {
        var text = Value(name);
        if (text is null)
            return null;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min)
            throw new UsageException($"The option {name} needs a whole number of at least {min.ToString(CultureInfo.InvariantCulture)}; got '{text}'.");
        return value;
    }

    /// <summary>Returns a decimal option.</summary>
    /// <param name="name">The option name.</param>
    /// <returns>The value, or <see langword="null"/> when absent.</returns>
    public double? Number(string name)
    {
        var text = Value(name);
        if (text is null)
            return null;
        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            throw new UsageException($"The option {name} needs a non-negative number; got '{text}'.");
        return value;
    }

    /// <summary>Checks that only the global options and the listed ones were given, and the number of positional words.</summary>
    /// <param name="command">The command name, for messages.</param>
    /// <param name="positionals">The expected number of positional words, the command words included.</param>
    /// <param name="options">The command's own options.</param>
    /// <exception cref="UsageException">Another option, or a different number of words.</exception>
    public void Expect(string command, int positionals, params string[] options)
    {
        foreach (var name in _order)
        {
            if (!GlobalOptions.Contains(name, StringComparer.Ordinal) && !options.Contains(name, StringComparer.Ordinal))
                throw new UsageException($"The option {name} does not apply to '{command}'.");
        }

        if (Positionals.Count > positionals)
            throw new UsageException($"Unexpected argument '{Positionals[positionals]}' for '{command}'.");
        if (Positionals.Count < positionals)
            throw new UsageException($"'{command}' is missing an argument.");
    }

    private void Add(string name, string value)
    {
        if (!_values.TryGetValue(name, out var list))
        {
            _values[name] = list = [];
            _order.Add(name);
        }

        list.Add(value);
    }
}
