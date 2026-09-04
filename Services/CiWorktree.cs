using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AndroidCommandCenter.Services;

/// <summary>
/// An isolated git worktree for a CI run. Your primary checkout is never
/// touched: no branch switching, no reset, no stashing of your work in progress.
/// The worktree is reused across runs (so Gradle caches stay warm) but always
/// hard-reset to the target commit before use.
/// </summary>
public sealed class CiWorktree : IAsyncDisposable
{
    private const string KeystorePropertiesFile = "keystore.properties";

    private readonly GitService _git;
    private readonly string _mainRepo;
    private readonly Action<string> _log;
    private readonly bool _keep;

    public string Path { get; }

    private CiWorktree(GitService git, string mainRepo, string path, Action<string> log, bool keep)
    {
        _git = git; _mainRepo = mainRepo; Path = path; _log = log; _keep = keep;
    }

    public static async Task<CiWorktree> CreateAsync(
        GitService git, string mainRepo, string projectName,
        Action<string> log, CancellationToken ct, bool keep = true)
    {
        string root = System.IO.Path.Combine(Paths.CacheDir, "ci-worktrees");
        Directory.CreateDirectory(root);
        string canonical = System.IO.Path.GetFullPath(mainRepo);
        string repoId = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..10].ToLowerInvariant();
        string path = System.IO.Path.Combine(root, $"{Sanitize(projectName)}-{repoId}");

        var wt = new CiWorktree(git, mainRepo, path, log, keep);

        if (Directory.Exists(System.IO.Path.Combine(path, ".git")) ||
            File.Exists(System.IO.Path.Combine(path, ".git")))
        {
            log($"[worktree] reusing {path}");
            // Prune stale registrations, then make sure it's actually usable.
            await git.Run(mainRepo, ct, "worktree", "prune");
            if (!await git.IsRepo(path, ct))
            {
                log("[worktree] existing directory is not a valid worktree — recreating.");
                await wt.RemoveAsync(ct);
                await wt.AddAsync(ct);
            }
        }
        else
        {
            await wt.AddAsync(ct);
        }

        // A cancelled process can leave a reusable worktree in the middle of a
        // merge/revert. Recover it before preflight so an interrupted run cannot
        // poison the next build.
        await wt.RecoverAsync(ct);

        return wt;
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        // reset --hard clears an interrupted merge and restores tracked files.
        // The CI worktree never performs reverts (rollback uses a disposable
        // worktree), so there is no revert sequencer to preserve or abort here.
        await _git.Require(Path, ct, "reset", "--hard");
        // Without -x, ignored Gradle caches and local.properties stay warm;
        // untracked, non-ignored files are removed for a deterministic build.
        await _git.Run(Path, ct, "clean", "-fd");
    }

    private async Task AddAsync(CancellationToken ct)
    {
        _log($"[worktree] creating {Path}");
        if (Directory.Exists(Path))
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
        // --detach: the worktree never holds a branch, so it can never conflict
        // with a branch checked out in your primary tree.
        var r = await _git.Run(_mainRepo, ct, "worktree", "add", "--detach", Path, "HEAD");
        if (!r.Ok)
            throw new InvalidOperationException($"Could not create CI worktree:\n{r.Output}");
    }

    /// <summary>Hard-reset the worktree to a specific commit, clean of all cruft.</summary>
    public async Task CheckoutAsync(string sha, CancellationToken ct)
    {
        _log($"[worktree] checking out {sha}");
        await _git.Require(Path, ct, "checkout", "--detach", sha);
        await _git.Require(Path, ct, "reset", "--hard", sha);
        // -x also removes ignored files (build/, .gradle/) — deliberately NOT
        // used, because wiping Gradle caches on every run makes CI unusably slow.
        // -d removes untracked dirs that could poison the build.
        await _git.Run(Path, ct, "clean", "-fd");
        CopyLocalSigningConfiguration(_mainRepo, Path, _log);
    }

    /// <summary>
    /// Copy the git-ignored Android signing configuration from the developer's
    /// primary checkout into the isolated CI worktree. The file contents are
    /// deliberately never logged, and the worktree copy is restricted to the
    /// current user on Unix-like systems.
    /// </summary>
    public static void CopyLocalSigningConfiguration(
        string mainRepo, string worktreePath, Action<string> log)
    {
        string source = System.IO.Path.Combine(mainRepo, KeystorePropertiesFile);
        if (!File.Exists(source))
        {
            log($"[worktree] no local {KeystorePropertiesFile} found in {mainRepo}");
            return;
        }

        string destination = System.IO.Path.Combine(worktreePath, KeystorePropertiesFile);
        File.Copy(source, destination, overwrite: true);

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (IOException)
            {
                // The copy is still usable on filesystems that do not support Unix modes.
            }
            catch (UnauthorizedAccessException)
            {
                // Gradle will report a clear error if it cannot read the copied file.
            }
        }

        log($"[worktree] copied local {KeystorePropertiesFile} from the primary checkout");
    }

    public async Task RemoveAsync(CancellationToken ct)
    {
        await _git.Run(_mainRepo, ct, "worktree", "remove", "--force", Path);
        if (Directory.Exists(Path))
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
        await _git.Run(_mainRepo, ct, "worktree", "prune");
    }

    public async ValueTask DisposeAsync()
    {
        if (_keep) { _log($"[worktree] kept at {Path} (warm caches)"); return; }
        await RemoveAsync(CancellationToken.None);
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
}
