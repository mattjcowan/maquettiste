namespace Maquettiste.Engine;

/// <summary>
/// What the served checkout is called, so two editors or servers running side by side can be told apart. Plain data: the engine
/// reads no version control (next-phase-1-design.md section 3.1, decision 7). The hosts detect it from the files a checkout keeps
/// (<c>Maquettiste.Functions.WorkspaceDetection</c>, which the command-line tool compiles too) and pass it in
/// (<see cref="AgentToolsOptions.Workspace"/>).
/// </summary>
/// <param name="Workspace">The name to show: <see cref="Variable"/> when set, else <see cref="Branch"/>, else the short commit of a
/// detached head, else <see cref="Worktree"/>; <see langword="null"/> when none is known.</param>
/// <param name="Branch">The checked-out branch, or <see langword="null"/> on a detached head, outside a checkout, or when the linked
/// worktree's repository folder is out of reach (a container that mounts only the worktree).</param>
/// <param name="Worktree">The name of the linked worktree, or <see langword="null"/> for a main checkout.</param>
/// <param name="Repository">The folder name of the main checkout a linked worktree belongs to, or <see langword="null"/>.</param>
public sealed record WorkspaceInfo(string? Workspace, string? Branch, string? Worktree, string? Repository)
{
    /// <summary>The environment variable that names the workspace, overriding what the checkout says.</summary>
    public const string Variable = "MAQUETTISTE_WORKSPACE";

    /// <summary>Nothing known: four <see langword="null"/>s.</summary>
    public static WorkspaceInfo None { get; } = new(null, null, null, null);
}
