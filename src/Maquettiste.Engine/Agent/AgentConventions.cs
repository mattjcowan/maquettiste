using System.Text;

namespace Maquettiste.Engine;

/// <summary>
/// The modeling conventions an agent reads before it changes the model: the <c>maquettiste-modeling</c> skill embedded in the engine,
/// followed by the repository's own <c>CONVENTIONS.md</c>. <c>maquettiste mcp</c> serves the text as a resource and a prompt; the
/// editor's assistant puts it in its system prompt.
/// </summary>
public static class AgentConventions
{
    /// <summary>The repository's own conventions, beside the shipped skill: <c>.claude/skills/maquettiste-modeling/CONVENTIONS.md</c>.</summary>
    public const string ProjectConventionsPath = ".claude/skills/maquettiste-modeling/CONVENTIONS.md";

    /// <summary>The most of the repository's <c>CONVENTIONS.md</c> the text carries, in bytes.</summary>
    public const int ProjectConventionsLimit = 64 * 1024;

    private const string Resource = "Maquettiste.Engine.Skills.maquettiste-modeling.SKILL.md";

    /// <summary>Returns the modeling conventions (the embedded <c>skills/maquettiste-modeling/SKILL.md</c>) without its front matter.</summary>
    /// <returns>The Markdown text, LF line endings.</returns>
    public static string Read()
    {
        using var stream = typeof(AgentConventions).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException("The modeling conventions are not embedded in the engine.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        var text = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (end >= 0)
                text = text[(end + 5)..].TrimStart('\n');
        }

        return text;
    }

    /// <summary>
    /// Appends the repository's own conventions (<see cref="ProjectConventionsPath"/>), read fresh on each call, after a separator and a
    /// heading; the text is unchanged when the file does not exist or is empty. At most <see cref="ProjectConventionsLimit"/> bytes are
    /// read, and a note says so when the file is longer.
    /// </summary>
    /// <param name="conventions">The embedded conventions (<see cref="Read"/>).</param>
    /// <param name="repoRoot">The repository root.</param>
    /// <returns>The Markdown text, LF line endings.</returns>
    public static string WithProjectConventions(string conventions, string repoRoot)
    {
        ArgumentNullException.ThrowIfNull(conventions);
        ArgumentNullException.ThrowIfNull(repoRoot);
        var path = Path.Combine(repoRoot, ProjectConventionsPath.Replace('/', Path.DirectorySeparatorChar));
        const string Heading = "\n\n---\n\n# This repository's conventions (CONVENTIONS.md)\n\n";
        string text;
        bool truncated;
        try
        {
            if (!File.Exists(path))
                return conventions;
            var buffer = new byte[ProjectConventionsLimit + 1];
            int length;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            truncated = length > ProjectConventionsLimit;
            if (truncated)
            {
                // Cut before the character that straddles the limit.
                length = ProjectConventionsLimit;
                while (length > 0 && (buffer[length] & 0xC0) == 0x80)
                    length--;
            }

            text = new UTF8Encoding(false).GetString(buffer, 0, length).TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return conventions.TrimEnd('\n') + Heading + "CONVENTIONS.md exists but could not be read: " + e.Message + "\n";
        }

        if (text.Length == 0)
            return conventions;
        return conventions.TrimEnd('\n') + Heading + text + "\n"
            + (truncated ? "\n(CONVENTIONS.md is longer than 64 KB; only its first 64 KB are shown here. Read the file for the rest.)\n" : "");
    }
}
