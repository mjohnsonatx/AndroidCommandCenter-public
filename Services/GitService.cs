using AndroidCommandCenter.Models;

namespace AndroidCommandCenter.Services;

public sealed record GitResult(int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0;
}

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
        return output.Trim() is { Length: > 0 } branch ? branch : "HEAD";
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

    public async Task<GitResult> Run(
        string projectPath, CancellationToken ct, params string[] arguments)
    {
        LogMessage?.Invoke($"$ git {string.Join(' ', arguments.Select(DisplayArgument))}");
        var (code, output) = await _runner.RunAndCaptureAsync(
            "git", arguments, projectPath, ct, echoCommand: false);
        return new GitResult(code, output);
    }

    public async Task<string> Require(
        string projectPath, CancellationToken ct, params string[] arguments)
    {
        var result = await Run(projectPath, ct, arguments);
        if (!result.Ok)
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed with exit code {result.ExitCode}:\n{result.Output}");
        return result.Output.TrimEnd();
    }

    public Task<GitResult> Fetch(string projectPath, string remote, CancellationToken ct)
    {
        remote = GitNames.Require(remote, "remote");
        return Run(projectPath, ct, "fetch", "--prune", "--tags", remote);
    }

    public async Task<bool> BranchExistsRemote(
        string projectPath, string remote, string branch, CancellationToken ct)
    {
        remote = GitNames.Require(remote, "remote");
        branch = GitNames.Require(branch, "branch");
        var result = await Run(projectPath, ct, "show-ref", "--verify", "--quiet",
            $"refs/remotes/{remote}/{branch}");
        return result.Ok;
    }

    public async Task<GitResult> ResetToRemote(
        string projectPath, string remote, string branch, CancellationToken ct)
    {
        remote = GitNames.Require(remote, "remote");
        branch = GitNames.Require(branch, "branch");
        string remoteRef = $"{remote}/{branch}";

        var checkout = await Run(projectPath, ct, "checkout", "-B", branch, remoteRef);
        if (!checkout.Ok) return checkout;

        var reset = await Run(projectPath, ct, "reset", "--hard", remoteRef);
        return reset.Ok
            ? new GitResult(0, JoinOutput(checkout.Output, reset.Output))
            : new GitResult(reset.ExitCode, JoinOutput(checkout.Output, reset.Output));
    }

    public async Task<string> RevParse(
        string projectPath, string revision, CancellationToken ct)
    {
        revision = RequireRevision(revision);
        var result = await Run(projectPath, ct, "rev-parse", "--verify", revision);
        if (!result.Ok)
            throw new InvalidOperationException($"Could not resolve git revision '{revision}':\n{result.Output}");
        return result.Output.Trim();
    }

    public async Task<int> CountBetween(
        string projectPath, string fromRevision, string toRevision, CancellationToken ct)
    {
        fromRevision = RequireRevision(fromRevision);
        toRevision = RequireRevision(toRevision);
        var result = await Run(projectPath, ct, "rev-list", "--count",
            $"{fromRevision}..{toRevision}");
        if (!result.Ok || !int.TryParse(result.Output.Trim(), out int count))
            throw new InvalidOperationException(
                $"Could not count commits between '{fromRevision}' and '{toRevision}':\n{result.Output}");
        return count;
    }

    public async Task<List<string>> LogBetween(
        string projectPath, string fromRevision, string toRevision, CancellationToken ct)
    {
        fromRevision = RequireRevision(fromRevision);
        toRevision = RequireRevision(toRevision);
        var result = await Run(projectPath, ct, "log", "--no-merges",
            "--pretty=format:%h %s", $"{fromRevision}..{toRevision}");
        if (!result.Ok)
            throw new InvalidOperationException($"Could not read the promotion commit list:\n{result.Output}");
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
    }

    public async Task<string?> LatestTag(
        string projectPath, string revision, CancellationToken ct)
    {
        revision = RequireRevision(revision);
        var result = await Run(projectPath, ct, "describe", "--tags", "--abbrev=0", revision);
        return result.Ok ? result.Output.Trim() : null;
    }

    public async Task EnsureIdentity(string projectPath, CancellationToken ct)
    {
        var name = await Run(projectPath, ct, "config", "--get", "user.name");
        if (!name.Ok || string.IsNullOrWhiteSpace(name.Output))
            await Require(projectPath, ct, "config", "--local", "user.name", "Android Command Center CI");

        var email = await Run(projectPath, ct, "config", "--get", "user.email");
        if (!email.Ok || string.IsNullOrWhiteSpace(email.Output))
            await Require(projectPath, ct, "config", "--local", "user.email", "android-command-center@localhost");
    }

    public async Task<GitResult> MergeNoFf(
        string projectPath, string revision, string message, CancellationToken ct)
    {
        revision = RequireRevision(revision);
        var result = await Run(projectPath, ct, "merge", "--no-ff", revision, "-m", message);
        if (result.Ok) return result;

        var abort = await Run(projectPath, CancellationToken.None, "merge", "--abort");
        return new GitResult(result.ExitCode, JoinOutput(result.Output,
            abort.Ok ? "Merge aborted; working tree restored." : $"Merge abort also failed:\n{abort.Output}"));
    }

    public Task<GitResult> Tag(
        string projectPath, string tag, string message, CancellationToken ct)
    {
        tag = GitNames.Require(tag, "tag");
        return Run(projectPath, ct, "tag", "-a", tag, "-m", message);
    }

    public Task<GitResult> Push(
        string projectPath, string remote, string refspec, CancellationToken ct)
    {
        remote = GitNames.Require(remote, "remote");
        refspec = RequireRefspec(refspec);
        return Run(projectPath, ct, "push", "--porcelain", remote, refspec);
    }

    public async Task<GitResult> PushWithToken(
        string projectPath, string owner, string repository, string token,
        string refspec, CancellationToken ct)
    {
        owner = GitNames.Require(owner, "GitHub owner");
        repository = GitNames.Require(repository, "GitHub repository");
        refspec = RequireRefspec(refspec);
        if (string.IsNullOrWhiteSpace(token))
            return new GitResult(2, "GitHub token is empty.");

        string askPass = Path.Combine(Path.GetTempPath(),
            $"acc-git-askpass-{Guid.NewGuid():N}" + (Platform.IsWindows ? ".cmd" : ".sh"));
        try
        {
            string script = Platform.IsWindows
                ? "@echo off\r\necho %ACC_GIT_PASSWORD%\r\n"
                : "#!/bin/sh\nprintf '%s\\n' \"$ACC_GIT_PASSWORD\"\n";
            await File.WriteAllTextAsync(askPass, script, ct);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(askPass,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var env = new Dictionary<string, string>
            {
                ["GIT_ASKPASS"] = askPass,
                ["GIT_ASKPASS_REQUIRE"] = "force",
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["ACC_GIT_PASSWORD"] = token
            };
            string url = $"https://github.com/{owner}/{repository}.git";
            var (code, output) = await _runner.RunAndCaptureAsync(
                "git",
                ["-c", "credential.username=x-access-token", "push", "--porcelain", url, refspec],
                projectPath, ct, env, echoCommand: false);
            return new GitResult(code, output);
        }
        finally
        {
            try { File.Delete(askPass); } catch { }
        }
    }

    public async Task<GitResult> RevertMerge(
        string projectPath, string mergeRevision, CancellationToken ct)
    {
        mergeRevision = RequireRevision(mergeRevision);
        return await Run(projectPath, ct, "revert", "-m", "1", "--no-edit", mergeRevision);
    }

    private Task<int> RunGit(
        string projectPath, IReadOnlyList<string> arguments, CancellationToken ct) =>
        _runner.RunAsync("git", arguments, projectPath, ct);

    private Task<(int ExitCode, string Output)> CaptureGit(
        string projectPath, IReadOnlyList<string> arguments, CancellationToken ct) =>
        _runner.RunAndCaptureAsync("git", arguments, projectPath, ct);

    private static string RequireRevision(string value)
    {
        value = value.Trim();
        if (value.Length == 0 || value.StartsWith('-') ||
            value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException($"Invalid git revision '{value}'.", nameof(value));
        return value;
    }

    private static string RequireRefspec(string value)
    {
        value = value.Trim();
        if (value.Length == 0 || value.StartsWith('-') ||
            value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException($"Invalid git refspec '{value}'.", nameof(value));
        return value;
    }

    private static string JoinOutput(string first, string second) =>
        string.Join(Environment.NewLine,
            new[] { first.TrimEnd(), second.TrimEnd() }.Where(s => s.Length > 0));

    private static string DisplayArgument(string value) =>
        value.Length == 0 || value.Any(char.IsWhiteSpace)
            ? $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""
            : value;
}
