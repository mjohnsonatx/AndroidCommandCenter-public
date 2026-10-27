using AndroidCommandCenter.Ci;
using AndroidCommandCenter.Models;
using AndroidCommandCenter.Services;
using Manifest = AndroidCommandCenter.Ci.CiRunManifest;

// Dependency-free integration runner: real Git repositories, hooks and atomic
// receive transactions. No Android tasks or network.
string suiteRoot = Directory.CreateTempSubdirectory("acc-promotion-graphs-").FullName;
string emptyConfig = Path.Combine(suiteRoot, "gitconfig");
File.WriteAllText(emptyConfig, "");
Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", emptyConfig);
Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
var tests = new (string Name, Func<Task> Run)[]
{
    ("repeated promotions, identical trees, and dev-only isolation", RepeatedPromotions),
    ("history-only divergence checked before no-op", () => Divergence(false, false)),
    ("history-only divergence with staging work", () => Divergence(false, true)),
    ("real production hotfix and dev guidance", () => Divergence(true, true)),
    ("main movement during gates", () => GateRace("master")),
    ("staging movement during gates", () => GateRace("staging")),
    ("movement immediately before pushing", BeforePushRace),
    ("rewind between validation and push advertisement", AdvertisedHeadRace),
    ("server ref locks reject a change during push", ServerRace),
    ("interrupted push retains uncertain release evidence", InterruptedPush),
    ("atomic staging rejection and retry", AtomicRejection),
    ("remote without atomic support", UnsupportedAtomic),
    ("existing tag, including dry run", TagCollision),
    ("tag collision during push", TagRace),
    ("dev conflict reports already published release", DevConflict),
    ("dev push rejection reports already published release", DevRejection),
    ("local-only and tag-disabled modes", Options),
    ("narrow fetch refspec cannot leave stale branch refs", NarrowFetch),
    ("merge-only staging history is not a false no-op", MergeOnly),
    ("release tree modified by merge hook is rejected", ChangedTree),
    ("push validation rejects force, deletion and wildcard specs", UnsafeRefspecs),
};
int failed = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}\n{ex}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} promotion tests passed. Fixtures: {suiteRoot}");
return failed == 0 ? 0 : 1;

async Task<Fixture> New(bool stagingChange = true) => await Fixture.Create(suiteRoot, stagingChange);
static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
}
static async Task<string> Fails(Func<Task> action, string contains)
{
    try { await action(); }
    catch (StageFailedException ex)
    {
        Check(ex.Message.Contains(contains, StringComparison.OrdinalIgnoreCase), $"Expected '{contains}', got: {ex.Message}");
        return ex.Message;
    }
    throw new Exception($"Expected failure containing '{contains}'.");
}

async Task RepeatedPromotions()
{
    var f = await New();
    await f.Commit("dev", "unfinished.txt", "dev-only work");
    string primaryBranch = await f.Git.CurrentBranch(f.Primary, default);
    File.WriteAllText(Path.Combine(f.Primary, "local-edit.txt"), "untouched");
    var ctx = f.Context();
    await f.Run(ctx, new SyncStage());
    string testedTree = await f.Tree(ctx.Manifest.StagingSha);
    await f.Hook(f.Primary, "pre-push", $"printf 'policy ran' > '{f.Root}/policy-ran'\ncat >/dev/null\n");
    await f.Run(ctx, new PromoteStage());
    Check(File.Exists(Path.Combine(f.Root, "policy-ran")), "Existing pre-push policy hook was bypassed.");
    await f.AssertRelease(ctx, testedTree);
    await f.Run(ctx, new BackMergeStage());
    Check(await f.Remote("dev") == ctx.Manifest.BackMergeSha, "Dev was not pushed.");
    Check(ctx.Manifest.DevShaAfter == ctx.Manifest.BackMergeSha, "Confirmed dev head missing from manifest.");
    Check(await f.Git.CountBetween(f.Work, ctx.Manifest.BackMergeSha!, ctx.Manifest.MergeSha!, default) == 0, "Dev lacks release history.");
    Check((await f.Git.Run(f.Work, default, "cat-file", "-e", $"{ctx.Manifest.BackMergeSha}:unfinished.txt")).Ok, "Dev work was lost.");
    Check(!(await f.Git.Run(f.Work, default, "cat-file", "-e", $"{ctx.Manifest.StagingShaAfter}:unfinished.txt")).Ok, "Dev work leaked into staging.");
    Check(await f.Git.CurrentBranch(f.Primary, default) == primaryBranch, "Primary branch changed.");
    Check(File.ReadAllText(Path.Combine(f.Primary, "local-edit.txt")) == "untouched", "Primary edit was changed.");

    var noop = f.Context(dryRun: true);
    var synced = await f.Run(noop, new SyncStage());
    Check(synced.Data.GetValueOrDefault("nothingToPromote") is true, "Previous release caused a stale-history failure.");
    await f.Commit("staging", "next-release.txt", "second release");
    var dry = f.Context(dryRun: true, version: 2);
    await f.Run(dry, new SyncStage());
    await f.Run(dry, new PromoteStage());
    Check(dry.Manifest.MergeSha == null, "Dry run created a release.");
    var next = f.Context(version: 2);
    await f.Run(next, new SyncStage());
    await f.Run(next, new PromoteStage());
    await f.AssertRelease(next, await f.Tree(next.Manifest.StagingSha));
    await f.Run(next, new BackMergeStage());
}

async Task Divergence(bool hotfix, bool stagingWork)
{
    var f = await New(stagingWork);
    string main = await f.Commit("master", hotfix ? "hotfix.txt" : null, "production-only commit");
    if (!hotfix) await f.Command(f.Primary, "push", "origin", $"{main}:refs/heads/dev");
    var ctx = f.Context();
    string message = await Fails(async () => await f.Run(ctx, new SyncStage()), hotfix ? "File-content divergence" : "History-only divergence");
    Check(!message.Contains("discard"), "Misleading discard diagnostic returned.");
    Check(message.Contains("production-only commit"), "Missing commit subjects were omitted.");
    Check(message.Contains(hotfix ? "Also merge master into dev" : "dev is already synchronized"), "Dev guidance is inaccurate.");
    Check(!ctx.Manifest.Stages[0].Data.ContainsKey("nothingToPromote"), "History gap became a no-op.");
}

async Task GateRace(string branch)
{
    var f = await New();
    var ctx = f.Context();
    await f.Run(ctx, new SyncStage());
    string moved = await f.Commit(branch, "race.txt", "arrived during gates");
    await Fails(async () => await f.Run(ctx, new PromoteStage()), $"{branch} changed during the run");
    Check(await f.Remote(branch) == moved, "Concurrent commit overwritten.");
    Check(await f.Remote(branch == "master" ? "staging" : "master") ==
          (branch == "master" ? ctx.Manifest.StagingSha : ctx.Manifest.MainShaBefore), "Other release branch moved.");
    Check(ctx.Manifest.MergeSha == null, "Release created despite changed gate inputs.");
}

async Task BeforePushRace()
{
    var f = await New();
    var ctx = f.Context();
    await f.Run(ctx, new SyncStage());
    bool moved = false;
    void Move(string log)
    {
        if (moved || !log.StartsWith("$ git tag -a")) return;
        moved = true;
        f.Commit("staging", "late.txt", "arrived after merge").GetAwaiter().GetResult();
    }
    f.Git.LogMessage += Move;
    try { await Fails(async () => await f.Run(ctx, new PromoteStage()), "staging changed during the run"); }
    finally { f.Git.LogMessage -= Move; }
    Check(await f.Remote("master") == ctx.Manifest.MainShaBefore, "Master moved after late staging change.");
    Check(!(await f.Git.Run(f.Work, default, "show-ref", "--verify", "--quiet", "refs/tags/v1.0+1")).Ok, "Unpublished tag polluted next retry.");
}

async Task AdvertisedHeadRace()
{
    var f = await New();
    var ctx = f.Context();
    await f.Run(ctx, new SyncStage());
    bool moved = false;
    void Move(string log)
    {
        if (moved || !log.StartsWith("$ git push --atomic")) return;
        moved = true;
        // Server-side fixture rewind: an ordinary non-force push could accept
        // this ancestor. The advertised-head guard must still reject it.
        f.Command(f.RemotePath, "update-ref", "refs/heads/staging", ctx.Manifest.MainShaBefore).GetAwaiter().GetResult();
    }
    f.Git.LogMessage += Move;
    try { await Fails(async () => await f.Run(ctx, new PromoteStage()), "changed since validation"); }
    finally { f.Git.LogMessage -= Move; }
    Check(await f.Remote("master") == ctx.Manifest.MainShaBefore, "Master moved on a staging rewind.");
    Check(ctx.Manifest.ReleasePushStatus == "Unknown" && ctx.Manifest.SynchronizationWarning != null,
        "Unexpected remote heads should require inspection, not a blind retry.");
}

async Task AtomicRejection()
{
    var f = await New();
    var ctx = f.Context();
    await f.Run(ctx, new SyncStage());
    await f.Hook(f.RemotePath, "update", "if [ \"$1\" = refs/heads/staging ]; then echo 'staging protected' >&2; exit 1; fi\n");
    await Fails(async () => await f.Run(ctx, new PromoteStage()), "staging protected");
    await f.AssertUnpublished(ctx);
    Check(ctx.Manifest.ReleasePushStatus == "Rejected", "Rejection status not recorded.");
    await f.Hook(f.RemotePath, "update", "exit 0\n");
    var retry = f.Context();
    await f.Run(retry, new SyncStage());
    await f.Run(retry, new PromoteStage());
    await f.AssertRelease(retry, await f.Tree(retry.Manifest.StagingSha));
}

async Task ServerRace()
{
    var f = await New();
    var ctx = f.Context();
    await f.Run(ctx, new SyncStage());
    await f.Hook(f.Primary, "pre-push", $"git --git-dir='{f.RemotePath}' update-ref refs/heads/staging {ctx.Manifest.MainShaBefore}\ncat >/dev/null\n");
    await Fails(async () => await f.Run(ctx, new PromoteStage()), "Atomic release push failed");
    Check(await f.Remote("master") == ctx.Manifest.MainShaBefore && await f.Remote("staging") == ctx.Manifest.MainShaBefore,
        "Server failed to guard a branch change after advertisement.");
    Check((await f.Command(f.RemotePath, "tag", "--list")).Length == 0, "Tag partially published on server race.");
}

async Task InterruptedPush()
{
    var f = await New();
    var ctx = f.Context();
    await f.Run(ctx, new SyncStage());
    await f.Hook(f.RemotePath, "post-receive", "echo 'fixture-release-received'\nsleep 5\n");
    using var cancel = new CancellationTokenSource();
    void Interrupt(string line) { if (line.Contains("fixture-release-received")) cancel.Cancel(); }
    f.Process.OutputReceived += Interrupt;
    try
    {
        await f.Run(ctx, new PromoteStage(), cancel.Token);
        throw new Exception("Expected cancellation during push acknowledgement.");
    }
    catch (OperationCanceledException) { }
    finally { f.Process.OutputReceived -= Interrupt; }
    Check(ctx.Manifest.ReleasePushStatus == "Unknown" && ctx.Manifest.SynchronizationWarning != null, "Interrupted push was falsely reported as a clean rejection.");
    Check(await f.Remote("master") == ctx.Manifest.MergeSha && await f.Remote("staging") == ctx.Manifest.MergeSha, "Fixture did not interrupt an already-published release.");
    Check(ctx.Manifest.Tag != null && (await f.Git.Run(f.Work, default, "show-ref", "--verify", "--quiet", "refs/tags/" + ctx.Manifest.Tag)).Ok, "Uncertain release tag evidence was removed.");
}

async Task UnsupportedAtomic()
{
    var f = await New();
    await f.Command(f.RemotePath, "config", "receive.advertiseAtomic", "false");
    var ctx = f.Context();
    await f.Run(ctx, new SyncStage());
    await Fails(async () => await f.Run(ctx, new PromoteStage()), "does not support --atomic");
    await f.AssertUnpublished(ctx);
}

async Task TagCollision()
{
    var f = await New();
    await f.Command(f.Primary, "tag", "v1.0+1", "master");
    await f.Command(f.Primary, "push", "origin", "refs/tags/v1.0+1");
    foreach (bool dry in new[] { false, true })
    {
        var ctx = f.Context(dryRun: dry);
        await f.Run(ctx, new SyncStage());
        await Fails(async () => await f.Run(ctx, new PromoteStage()), "Tag v1.0+1 already exists");
        Check(await f.Remote("master") == ctx.Manifest.MainShaBefore, "Tag collision changed master.");
        Check(ctx.Manifest.MergeSha == null, "Tag collision created a merge.");
    }
}

async Task TagRace()
{
    var f = await New();
    var ctx = f.Context();
    await f.Run(ctx, new SyncStage());
    bool moved = false;
    void Move(string log)
    {
        if (moved || !log.StartsWith("$ git push --atomic")) return;
        moved = true;
        f.Command(f.RemotePath, "update-ref", "refs/tags/v1.0+1", ctx.Manifest.MainShaBefore).GetAwaiter().GetResult();
    }
    f.Git.LogMessage += Move;
    try { await Fails(async () => await f.Run(ctx, new PromoteStage()), "Atomic release push failed"); }
    finally { f.Git.LogMessage -= Move; }
    Check(await f.Remote("master") == ctx.Manifest.MainShaBefore && await f.Remote("staging") == ctx.Manifest.StagingSha, "Tag race partially published branches.");
    Check(await f.Command(f.RemotePath, "rev-parse", "refs/tags/v1.0+1") == ctx.Manifest.MainShaBefore, "Existing remote tag overwritten.");
}

async Task DevConflict()
{
    var f = await New();
    await f.Commit("staging", "shared.txt", "release edit");
    await f.Commit("dev", "shared.txt", "conflicting unfinished edit");
    var ctx = f.Context();
    await f.Run(ctx, new SyncStage());
    await f.Run(ctx, new PromoteStage());
    var rec = await f.Run(ctx, new BackMergeStage());
    Check(rec.Status == StageStatus.Warning && rec.Summary.Contains("published successfully") && rec.Summary.Contains("CONFLICT"), "Conflict obscures already-published release.");
    Check(ctx.Manifest.SynchronizationWarning != null && ctx.Manifest.DevShaAfter == null, "Partial dev failure missing from manifest.");
    await f.AssertRelease(ctx, await f.Tree(ctx.Manifest.StagingSha));
    Check(!(await f.Git.Run(f.Work, default, "rev-parse", "--verify", "MERGE_HEAD")).Ok, "Conflicting merge not aborted.");
}

async Task DevRejection()
{
    var f = await New();
    await f.Hook(f.RemotePath, "update", "if [ \"$1\" = refs/heads/dev ]; then echo 'dev protected' >&2; exit 1; fi\n");
    var ctx = f.Context();
    await f.Run(ctx, new SyncStage());
    await f.Run(ctx, new PromoteStage());
    var rec = await f.Run(ctx, new BackMergeStage());
    Check(rec.Status == StageStatus.Warning && rec.Summary.Contains("published successfully") && rec.Summary.Contains("dev protected"), "Dev rejection not reported as post-release failure.");
    Check(ctx.Manifest.BackMergeSha != null && ctx.Manifest.DevShaAfter == null, "Unpushed dev commit misreported.");
    await f.AssertRelease(ctx, await f.Tree(ctx.Manifest.StagingSha));
}

async Task Options()
{
    var local = await New();
    var ctx = local.Context(push: false);
    await local.Run(ctx, new SyncStage());
    var rec = await local.Run(ctx, new PromoteStage());
    await local.Run(ctx, new BackMergeStage());
    Check(ctx.Manifest.ReleasePushStatus == "LocalOnly" && rec.Summary.Contains("NOT pushed"), "Local-only run claims publication.");
    Check(ctx.Manifest.MainShaAfter == null && ctx.Manifest.StagingShaAfter == null && ctx.Manifest.DevShaAfter == null, "Local-only run recorded remote updates.");
    Check(await local.Remote("master") == ctx.Manifest.MainShaBefore && await local.Remote("staging") == ctx.Manifest.StagingSha, "Local-only changed remote branches.");
    var noTag = await New();
    ctx = noTag.Context(tag: false);
    await noTag.Run(ctx, new SyncStage());
    await noTag.Run(ctx, new PromoteStage());
    await noTag.AssertRelease(ctx, await noTag.Tree(ctx.Manifest.StagingSha));
    Check((await noTag.Command(noTag.RemotePath, "tag", "--list")).Length == 0, "Disabled tag created a remote tag.");
}

async Task NarrowFetch()
{
    var f = await New();
    await f.Command(f.Primary, "config", "remote.origin.fetch", "+refs/heads/master:refs/remotes/origin/master");
    string next = await f.Commit("staging", "fresh.txt", "new staging commit");
    await f.Command(f.Work, "update-ref", "refs/remotes/origin/staging", await f.Remote("master"));
    var ctx = f.Context();
    await f.Run(ctx, new SyncStage());
    Check(ctx.Manifest.StagingSha == next, "Narrow fetch left a stale staging snapshot.");
}

async Task MergeOnly()
{
    var f = await New();
    // Main already contains the staging changes but staging has an additional
    // merge commit whose parents are both in main. --no-merges hides that commit.
    await f.Command(f.Primary, "checkout", "master");
    await f.Command(f.Primary, "merge", "--ff-only", "staging");
    await f.Command(f.Primary, "push", "origin", "master");
    string main = await f.Remote("master");
    string parent = await f.Command(f.Primary, "rev-parse", "master^");
    string merge = await f.Command(f.Primary, "commit-tree", await f.Tree(main), "-p", main, "-p", parent, "-m", "merge-only staging history");
    await f.Command(f.Primary, "push", "origin", $"{merge}:refs/heads/staging");
    var ctx = f.Context();
    var rec = await f.Run(ctx, new SyncStage());
    Check(!rec.Data.ContainsKey("nothingToPromote") && ctx.Manifest.Commits.Count == 1, "Merge-only history misclassified as no-op.");
}

async Task ChangedTree()
{
    var f = await New();
    var ctx = f.Context();
    await f.Run(ctx, new SyncStage());
    await f.Hook(f.Primary, "post-merge", "printf 'untested hook change' > unexpected.txt\ngit add unexpected.txt\n" +
        "altered=$(git commit-tree \"$(git write-tree)\" -p HEAD^1 -p HEAD^2 -m altered)\ngit update-ref HEAD \"$altered\"\n");
    await Fails(async () => await f.Run(ctx, new PromoteStage()), "Release tree differs");
    await f.AssertUnpublished(ctx);
}

async Task UnsafeRefspecs()
{
    var f = await New();
    string sha = await f.Remote("master");
    foreach (string spec in new[] { $"+{sha}:refs/heads/master", ":refs/heads/master", "refs/heads/*:refs/heads/*", $"{sha}:refs/heads/master:extra", "--force" })
    {
        try
        {
            await f.Git.PushAtomic(f.Work, "origin", [spec], new Dictionary<string, string> { ["refs/heads/master"] = sha }, "", default);
            throw new Exception($"Accepted unsafe refspec {spec}");
        }
        catch (ArgumentException) { }
    }
}

sealed class Fixture
{
    public required string Root { get; init; }
    public string Primary => Path.Combine(Root, "primary");
    public string Work => Path.Combine(Root, "worktree");
    public string RemotePath => Path.Combine(Root, "remote.git");
    public ProcessRunner Process { get; } = new();
    public GitService Git { get; }
    public Fixture() => Git = new GitService(Process);

    public static async Task<Fixture> Create(string suiteRoot, bool stagingChange)
    {
        var f = new Fixture { Root = Path.Combine(suiteRoot, "graph-" + Guid.NewGuid().ToString("N")) };
        Directory.CreateDirectory(f.Primary);
        await f.Command(f.Primary, "init", "-b", "master");
        await f.Command(f.Primary, "config", "user.name", "Promotion graph tests");
        await f.Command(f.Primary, "config", "user.email", "promotion-tests@example.invalid");
        await f.Command(f.Primary, "config", "commit.gpgSign", "false");
        await f.Command(f.Primary, "config", "tag.gpgSign", "false");
        await f.Command(f.Primary, "init", "--bare", "--initial-branch=master", f.RemotePath);
        await f.Command(f.Primary, "remote", "add", "origin", f.RemotePath);
        Directory.CreateDirectory(Path.Combine(f.Primary, "app"));
        File.WriteAllText(Path.Combine(f.Primary, "app", "build.gradle.kts"), "android {\n productFlavors {\n  create(\"production\") { }\n }\n}\n");
        File.WriteAllText(Path.Combine(f.Primary, "shared.txt"), "base\n");
        await f.Command(f.Primary, "add", ".");
        await f.Command(f.Primary, "commit", "-m", "base");
        await f.Command(f.Primary, "branch", "staging");
        await f.Command(f.Primary, "branch", "dev");
        await f.Command(f.Primary, "push", "origin", "master", "staging", "dev");
        if (stagingChange) await f.Commit("staging", "release.txt", "staged release");
        await f.Command(f.Primary, "worktree", "add", "--detach", f.Work, "staging");
        return f;
    }

    public CiContext Context(bool dryRun = false, bool push = true, bool tag = true, int version = 1) => new()
    {
        Config = new CiPipelineConfig { PushMain = push, TagRelease = tag },
        Profile = new ProjectProfile { ProjectPath = Primary }, ProjectPath = Work,
        GitHubToken = "", DryRun = dryRun,
        Manifest = new Manifest { VersionName = "1.0", VersionCode = version, DryRun = dryRun },
        Runner = Process, Git = Git, Android = new AndroidService(Process), TestReports = new TestReportService(),
        Logger = new PipelineLogger("test", "graph", Root), Log = _ => { }
    };

    public async Task<StageRecord> Run(CiContext ctx, ICiStage stage, CancellationToken ct = default)
    {
        var rec = new StageRecord { Name = stage.Name, Status = StageStatus.Running };
        ctx.Manifest.Stages.Add(rec);
        await stage.RunAsync(ctx, rec, new StageOutput(), ct);
        if (rec.Status == StageStatus.Running) rec.Status = StageStatus.Passed;
        return rec;
    }

    public Task<string> Command(string repo, params string[] args) => Git.Require(repo, default, args);
    public Task<string> Remote(string branch) => Command(RemotePath, "rev-parse", "refs/heads/" + branch);
    public Task<string> Tree(string sha) => Command(Primary, "rev-parse", sha + "^{tree}");

    public async Task<string> Commit(string branch, string? file, string message)
    {
        await Command(Primary, "checkout", branch);
        await Command(Primary, "fetch", "origin", branch);
        await Command(Primary, "merge", "--ff-only", "FETCH_HEAD");
        if (file != null)
        {
            File.WriteAllText(Path.Combine(Primary, file), message + "\n");
            await Command(Primary, "add", file);
        }
        await Command(Primary, "commit", "--allow-empty", "-m", message);
        await Command(Primary, "push", "origin", $"HEAD:refs/heads/{branch}");
        return await Command(Primary, "rev-parse", "HEAD");
    }

    public async Task Hook(string repo, string name, string script)
    {
        string path = await Command(repo, "rev-parse", "--path-format=absolute", "--git-path", "hooks/" + name);
        await File.WriteAllTextAsync(path, "#!/bin/sh\nset -eu\n" + script);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public async Task AssertRelease(CiContext ctx, string testedTree)
    {
        var m = ctx.Manifest;
        if (await Remote("master") != m.MergeSha || await Remote("staging") != m.MergeSha ||
            m.MainShaAfter != m.MergeSha || m.StagingShaAfter != m.MergeSha || m.ReleasePushStatus != "Published")
            throw new Exception("Production/staging/manifest are not synchronized to the exact release.");
        if (await Tree(m.MergeSha!) != testedTree) throw new Exception("Release tree differs from tested staging.");
        if (m.Tag != null && await Command(RemotePath, "rev-parse", $"refs/tags/{m.Tag}^{{}}") != m.MergeSha)
            throw new Exception("Release tag missing or pointing elsewhere.");
    }

    public async Task AssertUnpublished(CiContext ctx)
    {
        if (await Remote("master") != ctx.Manifest.MainShaBefore || await Remote("staging") != ctx.Manifest.StagingSha ||
            (await Command(RemotePath, "tag", "--list")).Length != 0)
            throw new Exception("Failed release partially updated remote refs.");
    }
}
