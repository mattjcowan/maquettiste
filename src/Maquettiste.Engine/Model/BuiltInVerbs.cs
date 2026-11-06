using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace Maquettiste.Engine.Model;

/// <summary>A built-in verb (verbs-design.md decision 1): a fixed key, label, group and meaning that never change.</summary>
/// <param name="Key">The kebab-case key every use names, such as <c>promote</c> or <c>change-owner</c>.</param>
/// <param name="DisplayName">The fixed label, such as <c>Change owner</c>.</param>
/// <param name="Group">What the verb acts on: <c>move</c>, <c>signature</c>, <c>task</c>, <c>object</c> or <c>link</c> (decision 4).</param>
/// <param name="Meaning">The fixed meaning.</param>
public sealed record BuiltInVerb(string Key, string DisplayName, string Group, string Meaning);

/// <summary>
/// The built-in verbs (verbs-design.md decisions 1 to 5): the one table the schema's <c>builtInVerb</c>, the editor's list and the user
/// guide's table are copies of. A built-in verb's meaning never changes and a project cannot redefine it, only hide it.
/// </summary>
public static partial class BuiltInVerbs
{
    /// <summary>The group names, in the order pickers and the docs list them.</summary>
    public static IReadOnlyList<string> Groups { get; } = ["move", "signature", "task", "object", "link"];

    /// <summary>The group of a project's own verb in the resolved model.</summary>
    public const string ProjectGroup = "project";

    private static readonly ImmutableArray<BuiltInVerb> Table =
    [
        new("promote", "Promote", "move", "Move the object forward to a next state of its lifecycle."),
        new("demote", "Demote", "move", "Move the object back to an earlier state of its lifecycle."),
        new("override", "Override", "move", "Make a move although its conditions or signatures are not met."),
        new("set-state", "Set state", "move", "Put the object in a chosen state directly, without a declared move."),
        new("sign", "Sign", "signature", "Give a signature that a move needs."),
        new("reject", "Reject", "signature", "Refuse a signature, so the move does not happen."),
        new("ignore", "Ignore", "signature", "Waive a signature, so the move no longer waits for it."),
        new("withdraw", "Withdraw", "signature", "Take back a signature already given."),
        new("delegate", "Delegate", "signature", "Hand a signature or a task to another actor."),
        new("route", "Route", "signature", "Send the object to actors for review, signature or a task."),
        new("claim", "Claim", "task", "Take a task offered to several actors, so only you hold it."),
        new("complete", "Complete", "task", "Finish a task."),
        new("create", "Create", "object", "Make a new object."),
        new("read", "Read", "object", "See an object or a link, and its data."),
        new("modify", "Modify", "object", "Change the values of an object or a link."),
        new("delete", "Delete", "object", "Remove an object."),
        new("copy", "Copy", "object", "Make a new object from an existing one."),
        new("revise", "Revise", "object", "Make the next revision of an object."),
        new("lock", "Lock", "object", "Reserve an object, so only the actor holding the lock may change it."),
        new("unlock", "Unlock", "object", "Release a lock."),
        new("change-owner", "Change owner", "object", "Give an object another owner."),
        new("change-type", "Change type", "object", "Make an object an instance of another type."),
        new("change-lifecycle", "Change lifecycle", "object", "Put an object under another lifecycle."),
        new("freeze", "Freeze", "object", "Make an object or a link read-only until it is thawed. While frozen it can't be modified, connected, disconnected or deleted."),
        new("thaw", "Thaw", "object", "Undo a freeze."),
        new("connect", "Connect", "link", "Link two objects through a relation."),
        new("disconnect", "Disconnect", "link", "Remove a link between two objects."),
    ];

    private static readonly FrozenDictionary<string, BuiltInVerb> ByKey = Table.ToFrozenDictionary(v => v.Key, StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, BuiltInVerb> ByCompactKey = Table.ToFrozenDictionary(v => Compact(v.Key), StringComparer.Ordinal);

    /// <summary>
    /// Verbs left to projects (decision 1): the editor's "Add a verb…" offers them as suggestions, which only pre-fill an own verb's key,
    /// display name and description. They fix no name and are no built-in.
    /// </summary>
    public static IReadOnlyList<string> Suggestions { get; } =
    [
        "check-out", "check-in", "schedule", "enable", "disable", "grant", "revoke", "suspend", "resume", "reassign", "start", "stop", "cancel",
        "archive", "publish", "notify",
    ];

    /// <summary>Every built-in verb, in table order (groups in <see cref="Groups"/> order).</summary>
    public static IReadOnlyList<BuiltInVerb> All => Table;

    /// <summary>Looks a built-in verb up by key.</summary>
    /// <param name="key">A verb key.</param>
    /// <returns>The built-in verb, or <see langword="null"/>.</returns>
    public static BuiltInVerb? Get(string? key) => key is null ? null : ByKey.GetValueOrDefault(key);

    /// <summary>Whether a key is a built-in verb's.</summary>
    /// <param name="key">A verb key.</param>
    /// <returns><see langword="true"/> for a built-in key.</returns>
    public static bool IsBuiltIn(string? key) => key is not null && ByKey.ContainsKey(key);

    /// <summary>
    /// The built-in verb whose key equals <paramref name="key"/> once hyphens are dropped, when the keys differ (MQ9706): <c>setstate</c> beside
    /// <c>set-state</c>.
    /// </summary>
    /// <param name="key">An own verb's key.</param>
    /// <returns>The built-in verb, or <see langword="null"/>.</returns>
    public static BuiltInVerb? LookAlike(string key) =>
        ByCompactKey.TryGetValue(Compact(key), out var found) && !string.Equals(found.Key, key, StringComparison.Ordinal) ? found : null;

    /// <summary>Whether a key has the shape of a verb key: kebab-case, as a stereotype key.</summary>
    /// <param name="key">The key.</param>
    /// <returns><see langword="true"/> when well formed.</returns>
    public static bool IsKey(string? key) => key is { Length: > 0 } && KeyPattern().IsMatch(key);

    /// <summary>The label of a key without a display name: the first letter upper case, hyphens as spaces (<c>check-out</c> to <c>Check out</c>).</summary>
    /// <param name="key">A verb key.</param>
    /// <returns>The label.</returns>
    public static string DisplayNameOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0)
            return key;
        var text = key.Replace('-', ' ');
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>The Pascal-case name of a key, as an enum member: <c>change-owner</c> to <c>ChangeOwner</c>.</summary>
    /// <param name="key">A verb key.</param>
    /// <returns>The name.</returns>
    public static string PascalName(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var builder = new StringBuilder(key.Length);
        foreach (var part in key.Split('-', StringSplitOptions.RemoveEmptyEntries))
            builder.Append(char.ToUpperInvariant(part[0])).Append(part.AsSpan(1));
        return builder.ToString();
    }

    /// <summary>A key without its hyphens: two keys alike this way may get one name in generated code (<c>x-1</c> and <c>x1</c>).</summary>
    internal static string Compact(string key) => key.Replace("-", "", StringComparison.Ordinal);

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
