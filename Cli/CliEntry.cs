using AndroidCommandCenter.Ci;
using AndroidCommandCenter.Models;
using AndroidCommandCenter.Services;

namespace AndroidCommandCenter.Cli;

/// <summary>
///   acc ci run      [--project PATH] [--dry-run] [--no-push] [--trigger schedule]
///   acc ci status   [--project PATH] [--last N]
///   acc ci probe    [--project PATH]          (Firestore connectivity)
///   acc rollback    [--project PATH] [--run ID] [--push] [--yes] [--reason TEXT]
/// Exit codes: 0 ok · 1 gates failed · 2 config error · 3 cancelled · 4 rollback refused
/// </summary>
public static class CliEntry
{
    public static async Task<int> RunAsync(string[] args)
    {
        var opts = Parse(args);
        string cmd = args[0];
        string sub = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "";

        var settings = AppSettings.Load(Console.Error.WriteLine);
        var secrets = new SecretStore();
        var profiles = new ProjectProfileService(secrets);

        string? projectPath = opts.GetValueOrDefault("project") ?? settings.LastSelectedProject;
        if (projectPath is null || !Directory.Exists(projectPath))
        { Console.Error.WriteLine("No project. Use --project PATH or select one in the GUI first."); return 2; }

        var profile = profiles.GetOrCreate(projectPath);
        var cfg = settings.Pipeline;
        if (opts.ContainsKey("no-push")) { cfg.PushMain = false; }

        var runner = new ProcessRunner();
        var git = new GitService(runner);
        var android = new AndroidService(runner);
        var tests = new TestReportService();
        runner.OutputReceived += l => Console.WriteLine("  " + l);
        git.LogMessage += l => Console.WriteLine("  " + l);

        FirestoreSink? sink = cfg.PublishToFirestore && !string.IsNullOrWhiteSpace(cfg.FirestoreCredentialsJson)
            ? new FirestoreSink(cfg.FirestoreProjectId, cfg.FirestoreCredentialsJson, cfg.FirestoreCollection)
            : null;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.Error.WriteLine("\nCancelling…"); };

        switch (cmd, sub)
        {
            case ("ci", "run"):
            {
                var ci = new LocalCiRunner(runner, git, android, tests, sink);
                ci.LogMessage += Console.WriteLine;
                try
                {
                    var m = await ci.RunAsync(cfg, profile, profiles.GitHubToken(profile),
                        opts.GetValueOrDefault("trigger") ?? "cli", opts.ContainsKey("dry-run"), cts.Token);
                    Console.WriteLine();
                    Console.WriteLine($"Manifest: {m.ManifestPath}");
                    Console.WriteLine($"Log:      {m.LogPath}");
                    Console.WriteLine(m.Success ? $"✔ {m.Result}" : $"✖ {m.Result}");
                    if (!m.Success && m.Stages.LastOrDefault(s => s.Failure != null)?.Failure is { } f)
                    { Console.WriteLine(); Console.WriteLine(f.ToString()); }
                    return m.Success ? 0 : 1;
                }
                catch (OperationCanceledException) { return 3; }
            }

            case ("ci", "status"):
            {
                int n = int.TryParse(opts.GetValueOrDefault("last"), out var k) ? k : 10;
                foreach (var m in LocalCiRunner.LoadManifests().Where(m => m.ProjectPath == projectPath).Take(n))
                    Console.WriteLine($"{m.StartedAt:yyyy-MM-dd HH:mm}  {(m.Success ? "PASS" : "FAIL"),-4}  {m.RunId}  {m.Tag ?? "",-16} {m.Result}");
                return 0;
            }

            case ("ci", "probe"):
            {
                if (sink is null) { Console.Error.WriteLine("Firestore not configured (Pipeline.PublishToFirestore / FirestoreCredentialsJson)."); return 2; }
                try { await sink.ProbeAsync(cts.Token); Console.WriteLine("✔ Firestore reachable."); return 0; }
                catch (Exception ex) { Console.Error.WriteLine($"✖ {ex.Message}"); return 2; }
            }

            case ("rollback", _):
            {
                var runs = LocalCiRunner.LoadManifests().Where(m => m.ProjectPath == projectPath && m.MergeSha != null).ToList();
                var run = opts.TryGetValue("run", out var id)
                    ? runs.FirstOrDefault(m => m.RunId.StartsWith(id, StringComparison.OrdinalIgnoreCase))
                    : runs.FirstOrDefault(m => m.Success);
                if (run is null) { Console.Error.WriteLine("No promoted run found to roll back."); return 4; }

                var rb = new RollbackService(git);
                rb.LogMessage += Console.WriteLine;
                RollbackPlan plan;
                try { plan = await rb.PlanAsync(cfg, run, projectPath, cts.Token); }
                catch (Exception ex) { Console.Error.WriteLine($"✖ {ex.Message}"); return 4; }

                Console.WriteLine(plan.Description);
                Console.WriteLine($"Run {run.RunId} · merge {run.MergeSha![..8]} · {run.Commits.Count} commits · versionCode {run.VersionCode}");
                if (!opts.ContainsKey("yes"))
                {
                    Console.Write($"Proceed{(opts.ContainsKey("push") ? " AND PUSH" : "")}? [y/N] ");
                    if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) return 4;
                }

                var r = await rb.ExecuteAsync(cfg, plan, projectPath, profile, profiles.GitHubToken(profile),
                    opts.ContainsKey("push"), opts.GetValueOrDefault("reason") ?? "manual rollback", cts.Token);
                Console.WriteLine((r.Success ? "✔ " : "✖ ") + r.Message);
                return r.Success ? 0 : 4;
            }

            default:
                Console.Error.WriteLine("Usage: acc ci run|status|probe [--project PATH] [--dry-run] [--no-push]\n" +
                                        "       acc rollback [--project PATH] [--run ID] [--push] [--yes] [--reason TEXT]");
                return 2;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            string k = args[i][2..];
            string v = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "";
            d[k] = v;
        }
        return d;
    }
}