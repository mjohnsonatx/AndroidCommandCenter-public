using AndroidCommandCenter.Models;

namespace AndroidCommandCenter.Services;

/// <summary>
/// Reverts a promotion by reverting its merge commit. No force-push, no history
/// rewrite — the revert is itself a normal commit, so the record of both the
/// release and its withdrawal stays intact.
/// </summary>
public sealed class RollbackService
{
    private readonly GitService _git;
    private readonly CiRunStore _store;
    public event Action<string>? LogMessage;

    public RollbackService(GitService git, CiRunStore store)
    {
        _git = git; _store = store;
    }

    public sealed record RollbackResult(
        bool Success, string Message, string? RevertSha = null, string? LogDetail = null);

    public async Task<RollbackResult> RollbackAsync(
        CiRunManifest m, string projectPath, string owner, string repo, string token,
        bool push, CancellationToken ct)
    {
        void Log(string s) { LogMessage?.Invoke(s); }

        if (m.MergeCommitSha is null)
            return new(false, "This run has no merge commit — nothing to revert.");
        if (m.RolledBack)
            return new(false, $"Run {m.RunId} was already rolled back at {m.RolledBackAt:g}.");

        Log($"Rolling back run {m.RunId} (merge {m.MergeCommitSha[..8]}) on {m.MainBranch}…");

        CiWorktree? wt = null;
        try
        {
            await _git.EnsureIdentity(projectPath, ct);
            var fetch = await _git.Fetch(projectPath, m.Remote, ct);
            if (!fetch.Ok) return new(false, $"Could not fetch {m.Remote}: {fetch.Output}");

            wt = await CiWorktree.CreateAsync(_git, projectPath, m.ProjectName + "-rollback", Log, ct);
            string path = wt.Path;

            await _git.Require(path, ct, "checkout", "-B", m.MainBranch, $"{m.Remote}/{m.MainBranch}");

            // Confirm the merge is actually an ancestor of main. If someone
            // already reverted or reset, reverting again would corrupt history.
            var isAncestor = await _git.Run(path, ct,
                "merge-base", "--is-ancestor", m.MergeCommitSha, "HEAD");
            if (!isAncestor.Ok)
                return new(false,
                    $"Merge commit {m.MergeCommitSha[..8]} is not an ancestor of " +
                    $"{m.Remote}/{m.MainBranch}. It may already have been reverted or history " +
                    "was rewritten. Resolve manually.");

            // Warn if main has moved on — the revert is still correct, but the
            // operator should know they are not undoing the latest release.
            string head = await _git.RevParse(path, "HEAD", ct);
            if (head != m.MergeCommitSha)
            {
                int since = await _git.CountBetween(path, m.MergeCommitSha, "HEAD", ct);
                Log($"[warn] {m.MainBranch} has {since} commit(s) after this merge. " +
                    "Reverting an older release may conflict with later work.");
            }

            var revert = await _git.RevertMerge(path, m.MergeCommitSha, ct);
            if (!revert.Ok)
            {
                await _git.Run(path, ct, "revert", "--abort");
                return new(false,
                    "Revert produced conflicts and was aborted. Later commits depend on this " +
                    "release; resolve manually.", LogDetail: revert.Output);
            }

            string revertSha = await _git.RevParse(path, "HEAD", ct);
            Log($"Created revert commit {revertSha[..8]}.");

            if (push)
            {
                var pr = await _git.PushWithToken(path, owner, repo, token,
                    $"{m.MainBranch}:{m.MainBranch}", ct);
                if (!pr.Ok)
                    return new(false,
                        $"Revert committed locally ({revertSha[..8]}) but the push was rejected.",
                        revertSha, pr.Output);
            }

            m.RolledBack = true;
            m.RolledBackAt = DateTimeOffset.Now;
            m.RolledBackBy = Environment.UserName;
            m.Summary += $" — ROLLED BACK ({revertSha[..8]})";
            _store.Save(m);

            string note = m.VersionCode is { } vc
                ? $"\n\nIMPORTANT: versionCode {vc} is burned — Google Play will never accept it " +
                  $"again. The next release must use {vc + 1} or higher."
                : "";

            return new(true,
                $"Rolled back {m.Tag ?? m.RunId}. {m.MainBranch} is back to the state before " +
                $"{m.MergeCommitSha[..8]}.{note}", revertSha);
        }
        catch (Exception ex)
        {
            return new(false, $"Rollback failed: {ex.Message}", LogDetail: ex.ToString());
        }
        finally
        {
            if (wt != null) await wt.RemoveAsync(CancellationToken.None);
        }
    }
}