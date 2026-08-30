using AndroidCommandCenter.Models;

namespace AndroidCommandCenter.Services;

public class GitService
{
    private readonly ProcessRunner _runner;

    public event Action<string>? LogMessage;

    public GitService(ProcessRunner runner) => _runner = runner;

    public async Task<int> UpdateToRemoteBranch(
        string projectPath,
        string remoteName,
        string branchName,
        CancellationToken ct)
    {
        remoteName = GitNames.Require(remoteName, "remote");
        branchName = GitNames.Require(branchName, "branch");

        LogMessage?.Invoke($"Updating selected repo from {remoteName}/{branchName}...");

        int code = await RunGit(projectPath, ["rev-parse", "--is-inside-work-tree"], ct);
        if (code != 0)
        {
            LogMessage?.Invoke("[ABORT] Selected project is not inside a git worktree.");
            return code;
        }

        code = await RunGit(projectPath, ["fetch", "--prune", remoteName, branchName], ct);
        if (code != 0) return code;

        code = await RunGit(projectPath, ["checkout", branchName], ct);
        if (code != 0) return code;

        code = await RunGit(projectPath, ["pull", "--ff-only", remoteName, branchName], ct);
        if (code == 0)
            LogMessage?.Invoke($"Repo is now at current {remoteName}/{branchName} head.");

        return code;
    }

    public async Task<bool> IsRepo(string projectPath, CancellationToken ct)
    {
        var (code, output) = await CaptureGit(
            projectPath, ["rev-parse", "--is-inside-work-tree"], ct);
        return code == 0 && output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> CurrentBranch(string projectPath, CancellationToken ct)
    {
        var (code, output) = await CaptureGit(
            projectPath, ["branch", "--show-current"], ct);
        if (code != 0) throw new InvalidOperationException("Could not read the current git branch.");
        return output.Trim() is { Length: > 0 } branch ? branch : "(detached HEAD)";
    }

    public async Task<string> ShortSha(string projectPath, string revision, CancellationToken ct)
    {
        revision = GitNames.Require(revision, "revision");
        var (code, output) = await CaptureGit(
            projectPath, ["rev-parse", "--short", revision], ct);
        if (code != 0) throw new InvalidOperationException($"Could not resolve git revision '{revision}'.");
        return output.Trim();
    }

    public async Task<bool> IsDirty(string projectPath, CancellationToken ct)
    {
        var (code, output) = await CaptureGit(
            projectPath, ["status", "--porcelain"], ct);
        if (code != 0) throw new InvalidOperationException("Could not read git working-tree status.");
        return !string.IsNullOrWhiteSpace(output);
    }

    public async Task<string> StatusSummary(string projectPath, CancellationToken ct)
    {
        var (code, output) = await CaptureGit(
            projectPath, ["status", "--short", "--branch"], ct);
        if (code != 0) throw new InvalidOperationException("Could not read git status.");
        return output.TrimEnd();
    }

    private Task<int> RunGit(
        string projectPath, IReadOnlyList<string> arguments, CancellationToken ct) =>
        _runner.RunAsync("git", arguments, projectPath, ct);

    private Task<(int ExitCode, string Output)> CaptureGit(
        string projectPath, IReadOnlyList<string> arguments, CancellationToken ct) =>
        _runner.RunAndCaptureAsync("git", arguments, projectPath, ct);
}
