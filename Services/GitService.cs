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
        remoteName = CleanGitName(remoteName, "remote");
        branchName = CleanGitName(branchName, "branch");

        LogMessage?.Invoke($"Updating selected repo from {remoteName}/{branchName}...");

        int code = await RunGit(projectPath, "rev-parse --is-inside-work-tree", ct);
        if (code != 0)
        {
            LogMessage?.Invoke("[ABORT] Selected project is not inside a git worktree.");
            return code;
        }

        code = await RunGit(projectPath, $"fetch --prune {remoteName} {branchName}", ct);
        if (code != 0) return code;

        code = await RunGit(projectPath, $"checkout {branchName}", ct);
        if (code != 0) return code;

        code = await RunGit(projectPath, $"pull --ff-only {remoteName} {branchName}", ct);
        if (code == 0)
            LogMessage?.Invoke($"Repo is now at current {remoteName}/{branchName} head.");

        return code;
    }

    private Task<int> RunGit(string projectPath, string arguments, CancellationToken ct)
    {
        LogMessage?.Invoke($"git {arguments}");
        return _runner.RunAsync("git", arguments, projectPath, ct);
    }

    private static string CleanGitName(string value, string label)
    {
        value = value.Trim();
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsWhiteSpace) ||
            value.Contains('"') ||
            value.Contains('\''))
        {
            throw new InvalidOperationException($"Invalid git {label}: {value}");
        }

        return value;
    }
}
