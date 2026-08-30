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

    public async Task<RollbackPlan> PlanAsync(CiPipelineConfig cfg, CiRunManifest run, string repo, CancellationToken ct)
    {
        if (run.MergeSha is null)
            throw new InvalidOperationException($"Run {run.RunId} has no merge commit to revert (it failed before promotion or was a dry run).");

        var fetch = await _git.Fetch(repo, cfg.Remote, ct);
        if (!fetch.Ok) throw new InvalidOperationException($"fetch failed:\n{fetch.Output}");

        string head = await _git.RevParse(repo, $"{cfg.Remote}/{cfg.MainBranch}", ct);
        bool atMerge = head == run.MergeSha;
        bool contains = (await _git.Run(repo, ct, "merge-base", "--is-ancestor", run.MergeSha, head)).Ok;
        if (!contains)
            throw new InvalidOperationException(
                $"{cfg.MainBranch} ({head[..8]}) does not contain merge {run.MergeSha[..8]} — already rolled back or history diverged.");

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
        string? original = null;
        try { original = await _git.CurrentBranch(repo, ct); } catch { }

        try
        {
            if (await _git.IsDirty(repo, ct))
                return new(false, "Working tree is dirty. Commit or stash first.", null, null);

            await _git.EnsureIdentity(repo, ct);
            var sync = await _git.ResetToRemote(repo, cfg.Remote, cfg.MainBranch, ct);
            if (!sync.Ok) return new(false, $"Could not sync {cfg.MainBranch}:\n{sync.Output}", null, null);

            string headNow = await _git.RevParse(repo, "HEAD", ct);
            if (headNow != plan.MainHeadNow)
                return new(false, $"{cfg.MainBranch} changed since the plan was made. Re-plan.", null, null);

            var revert = await _git.RevertMerge(repo, run.MergeSha!, ct);
            if (!revert.Ok)
            {
                await _git.Run(repo, ct, "revert", "--abort");
                return new(false, $"Revert produced conflicts — resolve manually:\n{revert.Output}", null, null);
            }

            // Amend the message with why and what.
            string msg = $"Rollback {run.Tag ?? run.MergeSha![..8]}\n\nReverts merge {run.MergeSha} from CI run {run.RunId}.\n" +
                         $"Reason: {reason}\n\nNOTE: versionCode must be bumped above {run.VersionCode ?? 0} before the next release.";
            await _git.Run(repo, ct, "commit", "--amend", "-q", "-m", msg);

            string revertSha = await _git.RevParse(repo, "HEAD", ct);
            string tag = $"rollback-{(run.Tag ?? run.MergeSha![..8]).Replace('+', '_')}-{DateTime.UtcNow:yyyyMMddHHmm}";
            await _git.Tag(repo, tag, $"Rollback of {run.Tag ?? run.MergeSha}", ct);
            LogMessage?.Invoke($"Revert commit {revertSha[..8]}, tag {tag}");

            if (push)
            {
                var url = (await _git.Run(repo, ct, "remote", "get-url", cfg.Remote)).Output;
                var p = url.StartsWith("https://") && gitHubToken.Length > 0
                    ? await _git.PushWithToken(repo, profile.GitHubOwner, profile.GitHubRepo, gitHubToken, $"{cfg.MainBranch}:{cfg.MainBranch}", ct)
                    : await _git.Push(repo, cfg.Remote, $"{cfg.MainBranch}:{cfg.MainBranch}", ct);
                if (!p.Ok)
                {
                    await _git.Run(repo, ct, "reset", "--hard", plan.MainHeadNow);
                    await _git.Run(repo, ct, "tag", "-d", tag);
                    return new(false, $"Push failed (local revert undone):\n{p.Output}", null, null);
                }
                await _git.Push(repo, cfg.Remote, $"refs/tags/{tag}", ct);
            }

            return new(true,
                $"Rolled back {run.Tag ?? run.MergeSha![..8]} → revert {revertSha[..8]}{(push ? ", pushed" : " (not pushed)")}. " +
                $"Remember: next release needs versionCode > {run.VersionCode ?? 0}, and back-merge {cfg.MainBranch} → {cfg.DevBranch}.",
                revertSha, tag);
        }
        finally
        {
            if (original is { Length: > 0 } && original != "HEAD")
                try { await _git.Run(repo, CancellationToken.None, "checkout", "-q", original); } catch { }
        }
    }
}