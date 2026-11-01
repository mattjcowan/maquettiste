using Maquettiste.Engine;

namespace Maquettiste.Functions.Tests;

/// <summary>The workspace name read from git's files: a host feature, which the command-line tool compiles from the same file.</summary>
public sealed class WorkspaceDetectionTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("mq-workspace-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void A_main_checkout_gives_its_branch_and_a_detached_head_its_short_commit()
    {
        var repo = Folder("main");
        Write(repo, ".git/HEAD", "ref: refs/heads/feature/billing\n");
        Directory.CreateDirectory(Path.Combine(repo, "sub"));

        Assert.Equal(new WorkspaceInfo("feature/billing", "feature/billing", null, null), WorkspaceDetection.Detect(repo, null));
        Assert.Equal(new WorkspaceInfo("feature/billing", "feature/billing", null, null), WorkspaceDetection.Detect(Path.Combine(repo, "sub"), "  "));
        Assert.Equal(new WorkspaceInfo("demo", "feature/billing", null, null), WorkspaceDetection.Detect(repo, " demo "));

        Write(repo, ".git/HEAD", "0e35bb0aa11f2c3d4e5f60718293a4b5c6d7e8f9\n");
        Assert.Equal(new WorkspaceInfo("0e35bb0", null, null, null), WorkspaceDetection.Detect(repo, null));
    }

    [Fact]
    public void A_linked_worktree_gives_its_name_and_its_branch_when_the_git_folder_is_in_reach()
    {
        var main = Folder("maquettiste");
        Write(main, ".git/worktrees/ver/HEAD", "ref: refs/heads/phase-1\n");
        var linked = Folder("ver");
        Write(linked, ".git", $"gitdir: {Path.Combine(main, ".git", "worktrees", "ver")}\n");

        Assert.Equal(new WorkspaceInfo("phase-1", "phase-1", "ver", "maquettiste"), WorkspaceDetection.Detect(linked, null));
    }

    [Fact]
    public void A_linked_worktree_whose_git_folder_is_not_mounted_gives_its_name()
    {
        // A container mounts the worktree on /repo; the path its .git file names is on the host only.
        var linked = Folder("repo");
        Write(linked, ".git", "gitdir: /home/someone/projects/maquettiste/.git/worktrees/modeling-and-codegen\n");

        Assert.Equal(new WorkspaceInfo("modeling-and-codegen", null, "modeling-and-codegen", "maquettiste"), WorkspaceDetection.Detect(linked, null));
        Assert.Equal(new WorkspaceInfo("billing", null, "modeling-and-codegen", "maquettiste"), WorkspaceDetection.Detect(linked, "billing"));
    }

    [Fact]
    public void Outside_git_only_the_variable_names_the_workspace()
    {
        var plain = Folder("plain");

        Assert.Equal(new WorkspaceInfo(null, null, null, null), WorkspaceDetection.Detect(plain, null));
        Assert.Equal(new WorkspaceInfo("demo", null, null, null), WorkspaceDetection.Detect(plain, "demo"));
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root.FullName, name)).FullName;

    private static void Write(string folder, string relative, string text)
    {
        var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
