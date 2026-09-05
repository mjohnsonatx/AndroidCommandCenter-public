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
        CancellationToken ct,
        string? gitHubToken = null)
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

        var fetch = await Fetch(projectPath, remoteName, gitHubToken, ct, branchName);
        code = fetch.ExitCode;
        if (code != 0) return code;

        code = await RunGit(projectPath, ["checkout", branchName], ct);
        if (code != 0) return code;

        // Fetch already updated the remote-tracking ref. Fast-forward locally
        // without doing a second network operation (and a second auth prompt).
        code = await RunGit(projectPath,
            ["merge", "--ff-only", $"{remoteName}/{branchName}"], ct);
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

    public Task<GitResult> Fetch(string projectPath, string remote, CancellationToken ct) =>
        Fetch(projectPath, remote, null, ct);

    public async Task<GitResult> Fetch(
        string projectPath, string remote, string? gitHubToken,
        CancellationToken ct, string? branch = null)
    {
        remote = GitNames.Require(remote, "remote");
        if (branch is not null) branch = GitNames.Require(branch, "branch");

        var args = new List<string> { "fetch", "--prune", "--tags", remote };
        // Do not inherit a single-branch clone's narrow fetch refspec: CI must
        // refresh every branch it compares. '+' only refreshes local tracking
        // refs; it is never used for a push.
        args.Add(branch is null
            ? $"+refs/heads/*:refs/remotes/{remote}/*"
            : $"+refs/heads/{branch}:refs/remotes/{remote}/{branch}");

        string remoteUrl = (await Run(projectPath, ct,
            "remote", "get-url", remote)).Output.Trim();
        bool https = remoteUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (https && !string.IsNullOrWhiteSpace(gitHubToken))
        {
            LogMessage?.Invoke($"$ git fetch --prune --tags {remote}" +
                               (branch is null ? "" : $" {branch}") + " (authenticated)");
            return await RunWithToken(projectPath, args, gitHubToken, ct);
        }

        // Never invoke a host-specific credential helper from a Flatpak
        // sandbox. Public and SSH remotes still work; private HTTPS remotes get
        // a deterministic authentication error instead of /usr/bin/gh noise.
        LogMessage?.Invoke($"$ git fetch --prune --tags {remote}" +
                           (branch is null ? "" : $" {branch}"));
        return await RunCredentialIsolated(projectPath, args, ct);
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

    public async Task<Dictionary<string, string>> ReadRemoteRefs(
        string projectPath, string remote, string? token, CancellationToken ct)
    {
        remote = GitNames.Require(remote, "remote");
        string url = (await Require(projectPath, ct, "remote", "get-url", remote)).Trim();
        string[] args = ["ls-remote", "--refs", remote];
        LogMessage?.Invoke($"$ git ls-remote --refs {remote}");
        var result = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(token)
            ? await RunWithToken(projectPath, args, token, ct)
            : await RunCredentialIsolated(projectPath, args, ct);
        if (!result.Ok) throw new InvalidOperationException($"Could not inspect remote refs:\n{result.Output}");
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split('\t'))
            .Where(parts => parts.Length == 2 && IsObjectId(parts[0]))
            .ToDictionary(parts => parts[1], parts => parts[0], StringComparer.Ordinal);
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

    public async Task<GitResult> PushToRemote(
        string projectPath, string remote, string refspec, string? token, CancellationToken ct)
    {
        remote = GitNames.Require(remote, "remote");
        string[] parts = refspec.Split(':');
        if (parts.Length != 2 || !IsSafePushRef(parts[0], source: true) || !IsSafePushRef(parts[1], source: false))
            throw new ArgumentException($"Invalid push refspec '{refspec}'.", nameof(refspec));
        string url = (await Require(projectPath, ct, "remote", "get-url", "--push", remote)).Trim();
        string[] args = ["-c", $"remote.{remote}.mirror=false", "-c", "push.followTags=false", "push", "--porcelain", remote, refspec];
        bool authenticated = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(token);
        LogMessage?.Invoke($"$ git push --porcelain {remote} {refspec}" + (authenticated ? " (authenticated)" : ""));
        return authenticated
            ? await RunWithToken(projectPath, args, token!, ct)
            : await RunCredentialIsolated(projectPath, args, ct);
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

        string url = $"https://github.com/{owner}/{repository}.git";
        LogMessage?.Invoke($"$ git push --porcelain {owner}/{repository} {refspec} (authenticated)");
        return await RunWithToken(projectPath,
            ["push", "--porcelain", url, refspec], token, ct);
    }

    /// <summary>
    /// Publish an all-or-nothing release to the configured remote. The temporary
    /// pre-push guard compares the server's advertised old OIDs, including when
    /// a concurrent rewind would otherwise look like a valid fast-forward.
    /// Git's receive transaction checks those OIDs again under the ref locks.
    /// No force/lease option or non-atomic fallback is permitted.
    /// </summary>
    public async Task<GitResult> PushAtomic(
        string projectPath, string remote, IReadOnlyList<string> refspecs,
        IReadOnlyDictionary<string, string> expectedHeads, string? token, CancellationToken ct)
    {
        remote = GitNames.Require(remote, "remote");
        var destinations = new HashSet<string>(StringComparer.Ordinal);
        foreach (string spec in refspecs)
        {
            string[] parts = spec.Split(':');
            if (parts.Length != 2 || !IsSafePushRef(parts[0], source: true) ||
                !IsSafePushRef(parts[1], source: false) || !destinations.Add(parts[1]) ||
                !expectedHeads.TryGetValue(parts[1], out string? old) ||
                (old.Length != 0 && !IsObjectId(old)))
                throw new ArgumentException($"Invalid guarded push refspec '{spec}'.", nameof(refspecs));
        }
        if (destinations.Count == 0 || destinations.Count != expectedHeads.Count)
            throw new ArgumentException("Every atomic push destination must have exactly one expected head.");

        string fetchUrl = (await Require(projectPath, ct, "remote", "get-url", remote)).Trim();
        string pushUrls = (await Require(projectPath, ct, "remote", "get-url", "--push", "--all", remote)).Trim();
        if (fetchUrl != pushUrls)
            return new GitResult(2, "Atomic promotion requires one push URL matching the fetch URL, " +
                "so the validated branches and published branches belong to the same repository.");

        string originalHook = (await Require(projectPath, ct, "rev-parse",
            "--path-format=absolute", "--git-path", "hooks/pre-push")).Trim();
        string hookDir = Directory.CreateTempSubdirectory("acc-release-push-").FullName;
        try
        {
            string input = ShellQuote(Path.Combine(hookDir, "updates"));
            var script = new System.Text.StringBuilder("#!/bin/sh\nset -eu\n");
            script.AppendLine($"cat > {input}");
            // Preserve the repository's policy hooks rather than bypassing them.
            script.AppendLine($"if [ -x {ShellQuote(originalHook)} ]; then {ShellQuote(originalHook)} \"$@\" < {input}; fi");
            script.AppendLine("count=0\nwhile read -r local_ref local_oid remote_ref remote_oid; do\n  case \"$remote_ref\" in");
            foreach (var (reference, old) in expectedHeads)
            {
                string matches = old.Length == 0
                    ? $"{{ [ \"$remote_oid\" = {new string('0', 40)} ] || [ \"$remote_oid\" = {new string('0', 64)} ]; }}"
                    : $"[ \"$remote_oid\" = {ShellQuote(old)} ]";
                script.AppendLine($"    {ShellQuote(reference)}) {matches} || {{ echo \"Release aborted: $remote_ref changed since validation ($remote_oid).\" >&2; exit 1; }} ;;");
            }
            script.AppendLine("    *) echo \"Unexpected release ref: $remote_ref\" >&2; exit 1 ;;\n  esac\n  count=$((count + 1))");
            script.AppendLine($"done < {input}");
            script.AppendLine($"[ \"$count\" -eq {destinations.Count} ] || {{ echo 'Release refs changed or are already published; inspect the remote before retrying.' >&2; exit 1; }}");
            string hook = Path.Combine(hookDir, "pre-push");
            // Git for Windows also runs this hook through sh, which requires LF.
            await File.WriteAllTextAsync(hook, script.ToString().Replace("\r\n", "\n"), ct);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var args = new List<string>
            {
                "-c", $"core.hooksPath={hookDir}", "-c", $"remote.{remote}.mirror=false",
                "-c", "push.followTags=false", "push", "--atomic", "--porcelain", remote
            };
            args.AddRange(refspecs);
            bool authenticated = fetchUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                                 !string.IsNullOrWhiteSpace(token);
            LogMessage?.Invoke($"$ git push --atomic --porcelain {remote} {string.Join(' ', refspecs)}" +
                               (authenticated ? " (authenticated)" : ""));
            return authenticated
                ? await RunWithToken(projectPath, args, token!, ct)
                : await RunCredentialIsolated(projectPath, args, ct);
        }
        finally
        {
            try { Directory.Delete(hookDir, recursive: true); } catch { }
        }
    }

    private static bool IsObjectId(string value) =>
        value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    private static bool IsSafePushRef(string value, bool source) =>
        (source && IsObjectId(value)) ||
        ((value.StartsWith("refs/heads/", StringComparison.Ordinal) ||
          value.StartsWith("refs/tags/", StringComparison.Ordinal)) &&
         value == value.Trim() && GitNames.IsValid(value));

    private static string ShellQuote(string path) =>
        "'" + (OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path).Replace("'", "'\"'\"'") + "'";

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

    private async Task<GitResult> RunCredentialIsolated(
        string projectPath, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        string rejectAskPass = Path.Combine(Path.GetTempPath(),
            $"acc-git-no-prompt-{Guid.NewGuid():N}" + (Platform.IsWindows ? ".cmd" : ".sh"));
        try
        {
            string script = Platform.IsWindows
                ? "@exit /b 1\r\n"
                : "#!/bin/sh\nexit 1\n";
            await File.WriteAllTextAsync(rejectAskPass, script, ct);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(rejectAskPass,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var safeArgs = new List<string> { "-c", "credential.helper=" };
            safeArgs.AddRange(arguments);
            var env = new Dictionary<string, string>
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GIT_ASKPASS"] = rejectAskPass
            };
            var (code, output) = await _runner.RunAndCaptureAsync(
                "git", safeArgs, projectPath, ct, env, echoCommand: false);
            return new GitResult(code, output);
        }
        finally
        {
            try { File.Delete(rejectAskPass); } catch { }
        }
    }

    private async Task<GitResult> RunWithToken(
        string projectPath, IReadOnlyList<string> arguments,
        string token, CancellationToken ct)
    {
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
            var safeArgs = new List<string>
            {
                "-c", "credential.helper=",
                "-c", "credential.username=x-access-token"
            };
            safeArgs.AddRange(arguments);
            var (code, output) = await _runner.RunAndCaptureAsync(
                "git", safeArgs, projectPath, ct, env, echoCommand: false);
            return new GitResult(code, output);
        }
        finally
        {
            try { File.Delete(askPass); } catch { }
        }
    }

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
