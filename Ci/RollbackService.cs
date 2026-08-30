using AndroidCommandCenter.Models;
using AndroidCommandCenter.Services;

namespace AndroidCommandCenter.Ci;

public sealed record RollbackPlan(
    CiRunManifest Run, string MainHeadNow, bool MainIsAtMerge, string Description);

public sealed record RollbackResult(bool Success, string Message, string? RevertSha, string? Tag);

/// <summary>
/// Rollback = `git revert -m 1 &lt;mergeSha&gt;` on main. It is a new commit, so
/// nothing is rewritten, every collaborator fast-forwards cleanly, and the
/// rollback itself is auditable. Never force-pushes.
/// </summary>
public sealed class RollbackService
{
    private readonly GitService _git;
    public event Action<string>? LogMessage;

    public RollbackService(GitService git) => _git = git;

    public async Task<RollbackPlan> PlanAsync(
        CiPipelineConfig cfg, CiRunManifest run, string repo,
        CancellationToken ct, string? gitHubToken = null)
    {
        if (run.MergeSha is null)
            throw new InvalidOperationException($"Run {run.RunId} has no merge commit to revert (it failed before promotion or was a dry run).");

        var fetch = await _git.Fetch(repo, cfg.Remote, gitHubToken, ct);
        if (!fetch.Ok) throw new InvalidOperationException($"fetch failed:\n{fetch.Output}");

        string head = await _git.RevParse(repo, $"{cfg.Remote}/{cfg.MainBranch}", ct);
        bool atMerge = head == run.MergeSha;
        bool contains = (await _git.Run(repo, ct, "merge-base", "--is-ancestor", run.MergeSha, head)).Ok;
        if (!contains)
            throw new InvalidOperationException(
                $"{cfg.MainBranch} ({head[..8]}) does not contain merge {run.MergeSha[..8]} — already rolled back or history diverged.");

        var priorRollback = await _git.Run(repo, ct, "log", "-n", "1", "--format=%H",
            "--fixed-strings", "--grep", $"Reverts merge {run.MergeSha} from CI run {run.RunId}.",
            $"{cfg.Remote}/{cfg.MainBranch}");
        if (priorRollback.Ok && !string.IsNullOrWhiteSpace(priorRollback.Output))
            throw new InvalidOperationException(
                $"Run {run.RunId} was already rolled back by {priorRollback.Output.Trim()[..8]}.");

        string desc = atMerge
            ? $"Revert {run.Tag ?? run.MergeSha[..8]} ({run.Commits.Count} commits). {cfg.MainBranch} is exactly at that merge."
            : $"Revert {run.Tag ?? run.MergeSha[..8]}. NOTE: {cfg.MainBranch} has moved on since ({head[..8]}); " +
              "later releases stay in place and only this release's changes are undone.";
        return new RollbackPlan(run, head, atMerge, desc);
    }

    public async Task<RollbackResult> ExecuteAsync(
        CiPipelineConfig cfg, RollbackPlan plan, string repo, ProjectProfile profile,
        string gitHubToken, bool push, string reason, CancellationToken ct)
    {
        var run = plan.Run;
        var refresh = await _git.Fetch(repo, cfg.Remote, gitHubToken, ct);
        if (!refresh.Ok)
            return new(false, $"Could not refresh {cfg.Remote}:\n{refresh.Output}", null, null);

        string mainNow = await _git.RevParse(repo, $"{cfg.Remote}/{cfg.MainBranch}", ct);
        if (mainNow != plan.MainHeadNow)
            return new(false, $"{cfg.MainBranch} changed since the plan was made. Re-plan.", null, null);

        string project = Path.GetFileName(repo.TrimEnd('/', '\\')) + "-rollback";
        await using var worktree = await CiWorktree.CreateAsync(
            _git, repo, project, m => LogMessage?.Invoke(m), ct, keep: false);
        await worktree.CheckoutAsync(mainNow, ct);
        string isolatedRepo = worktree.Path;

        await _git.EnsureIdentity(isolatedRepo, ct);
        var revert = await _git.RevertMerge(isolatedRepo, run.MergeSha!, ct);
        if (!revert.Ok)
        {
            await _git.Run(isolatedRepo, CancellationToken.None, "revert", "--abort");
            return new(false, $"Revert produced conflicts — the remote was not changed:\n{revert.Output}", null, null);
        }

        string versionReminder = run.VersionCode is int versionCode
            ? $"versionCode must be bumped above {versionCode} before the next release."
            : "versionCode must be bumped before the next release.";
        string msg = $"Rollback {run.Tag ?? run.MergeSha![..8]}\n\nReverts merge {run.MergeSha} from CI run {run.RunId}.\n" +
                     $"Reason: {reason}\n\nNOTE: {versionReminder}";
        var amend = await _git.Run(isolatedRepo, ct, "commit", "--amend", "-q", "-m", msg);
        if (!amend.Ok)
            return new(false, $"Could not annotate the rollback commit:\n{amend.Output}", null, null);

        string revertSha = await _git.RevParse(isolatedRepo, "HEAD", ct);
        string tag = $"rollback-{(run.Tag ?? run.MergeSha![..8]).Replace('+', '_')}-{DateTime.UtcNow:yyyyMMddHHmm}";
        var tagged = await _git.Tag(isolatedRepo, tag, $"Rollback of {run.Tag ?? run.MergeSha}", ct);
        if (!tagged.Ok)
            return new(false, $"Could not create rollback tag:\n{tagged.Output}", null, null);
        LogMessage?.Invoke($"Revert commit {revertSha[..8]}, tag {tag}");

        if (push)
        {
            var pushed = await PushAsync(isolatedRepo, cfg, profile, gitHubToken,
                $"HEAD:{cfg.MainBranch}", ct);
            if (!pushed.Ok)
                return new(false, $"Push failed; the remote was not changed:\n{pushed.Output}", null, null);

            var tagPush = await PushAsync(isolatedRepo, cfg, profile, gitHubToken,
                $"refs/tags/{tag}", CancellationToken.None);
            if (!tagPush.Ok)
                LogMessage?.Invoke($"[warn] rollback commit was pushed, but tag push failed: {tagPush.Output}");

            if (cfg.BackMergeToDev)
            {
                var backMerge = await BackMergeAsync(isolatedRepo, cfg, profile,
                    gitHubToken, run, revertSha, CancellationToken.None);
                if (backMerge is { Length: > 0 })
                    return new(true,
                        $"Rolled back {run.Tag ?? run.MergeSha![..8]} → revert {revertSha[..8]}, pushed. " +
                        $"Production is safe, but the development back-merge needs attention: {backMerge}",
                        revertSha, tag);
            }
        }

        string branchNote = push && cfg.BackMergeToDev
            ? $" {cfg.DevBranch} was back-merged."
            : $" Back-merge {cfg.MainBranch} → {cfg.DevBranch} before the next release.";
        return new(true,
            $"Rolled back {run.Tag ?? run.MergeSha![..8]} → revert {revertSha[..8]}{(push ? ", pushed" : " (not pushed)")}. " +
            $"{versionReminder}{branchNote}",
            revertSha, tag);
    }

    private async Task<string?> BackMergeAsync(
        string repo, CiPipelineConfig cfg, ProjectProfile profile, string gitHubToken,
        CiRunManifest run, string revertSha, CancellationToken ct)
    {
        var fetch = await _git.Fetch(repo, cfg.Remote, gitHubToken, ct);
        if (!fetch.Ok) return $"fetch failed: {fetch.Output}";
        var checkout = await _git.Run(repo, ct, "checkout", "--detach",
            $"{cfg.Remote}/{cfg.DevBranch}");
        if (!checkout.Ok) return $"could not check out {cfg.DevBranch}: {checkout.Output}";
        var merge = await _git.MergeNoFf(repo, revertSha,
            $"Back-merge rollback of {run.Tag ?? run.MergeSha?[..8]} into {cfg.DevBranch}\n\nCI run {run.RunId}", ct);
        if (!merge.Ok) return $"merge conflict on {cfg.DevBranch}; resolve it manually.";
        var pushed = await PushAsync(repo, cfg, profile, gitHubToken,
            $"HEAD:{cfg.DevBranch}", ct);
        return pushed.Ok ? null : $"push of {cfg.DevBranch} failed: {pushed.Output}";
    }

    private async Task<GitResult> PushAsync(
        string repo, CiPipelineConfig cfg, ProjectProfile profile,
        string gitHubToken, string refspec, CancellationToken ct)
    {
        var url = (await _git.Run(repo, ct, "remote", "get-url", cfg.Remote)).Output;
        return url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && gitHubToken.Length > 0
            ? await _git.PushWithToken(repo, profile.GitHubOwner, profile.GitHubRepo,
                gitHubToken, refspec, ct)
            : await _git.Push(repo, cfg.Remote, refspec, ct);
    }
}
