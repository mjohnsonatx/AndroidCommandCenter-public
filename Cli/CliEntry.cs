using AndroidCommandCenter.Ci;
using AndroidCommandCenter.Models;
using AndroidCommandCenter.Services;

namespace AndroidCommandCenter.Cli;

/// <summary>
///   acc ci run      [--project PATH] [--dry-run] [--no-push] [--trigger schedule]
///   acc ci status   [--project PATH] [--last N]
///   acc ci env      [--project PATH]          (SDK/JDK/toolchain diagnostics)
///   acc ci probe    [--project PATH]          (Firestore connectivity)
///   acc rollback    [--project PATH] [--run ID] [--plan] [--push] [--yes] [--reason TEXT]
/// Exit codes: 0 ok · 1 gates failed · 2 config error · 3 cancelled · 4 rollback refused
/// </summary>
public static class CliEntry
{
    public static async Task<int> RunAsync(string[] args)
    {
        var opts = Parse(args);
        string cmd = args[0];
        string sub = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "";

        bool known = (cmd, sub) is ("ci", "run") or ("ci", "status") or ("ci", "env") or
            ("ci", "probe") || cmd == "rollback";
        if (!known) return Usage();

        var settings = AppSettings.Load(Console.Error.WriteLine);
        string? projectPath = opts.GetValueOrDefault("project") ?? settings.LastSelectedProject;
        if (projectPath is null || !Directory.Exists(projectPath))
        { Console.Error.WriteLine("No project. Use --project PATH or select one in the GUI first."); return 2; }

        if ((cmd, sub) == ("ci", "status"))
        {
            int n = int.TryParse(opts.GetValueOrDefault("last"), out var k)
                ? Math.Clamp(k, 1, 1000) : 10;
            foreach (var manifest in AndroidCommandCenter.Ci.LocalCiRunner.LoadManifests()
                         .Where(m => m.ProjectPath == projectPath).Take(n))
                Console.WriteLine($"{manifest.StartedAt:yyyy-MM-dd HH:mm}  " +
                    $"{(manifest.Success ? "PASS" : "FAIL"),-4}  {manifest.RunId}  " +
                    $"{manifest.Tag ?? "",-16} {manifest.Result}");
            return 0;
        }

        if ((cmd, sub) == ("ci", "env"))
        {
            var diagnosticAndroid = new AndroidService(new ProcessRunner()) { ProjectPath = projectPath };
            Console.WriteLine($"Project:  {projectPath}");
            Console.WriteLine($"SDK:      {diagnosticAndroid.SdkPath}");
            Console.WriteLine($"Gradle:   {Platform.GradleWrapper(projectPath) ?? "NOT FOUND"}");
            Console.WriteLine(diagnosticAndroid.JavaEnvironment.Report());
            var problems = diagnosticAndroid.Diagnose();
            if (problems.Count == 0) { Console.WriteLine("Environment looks good."); return 0; }
            foreach (var problem in problems) Console.Error.WriteLine("- " + problem);
            return 2;
        }

        var cfg = settings.Pipeline;
        if (opts.ContainsKey("no-push")) { cfg.PushMain = false; }
        if (cfg.PublishToFirestore &&
            (string.IsNullOrWhiteSpace(cfg.FirestoreCollection) ||
             !File.Exists(cfg.FirestoreCredentialsJson)))
        {
            Console.Error.WriteLine(
                "Firestore publishing is enabled, but its collection or service-account JSON is invalid.");
            return 2;
        }

        var secrets = new SecretStore();
        ProjectProfileService? profiles = null;
        ProjectProfile? profile = null;
        if ((cmd, sub) == ("ci", "run") || cmd == "rollback")
        {
            try
            {
                profiles = new ProjectProfileService(secrets);
                profile = profiles.GetOrCreate(projectPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not load or create the project profile: {ex.Message}");
                return 2;
            }
        }

        var runner = new ProcessRunner();
        var git = new GitService(runner);
        var android = new AndroidService(runner);
        var tests = new TestReportService();
        runner.OutputReceived += l => Console.WriteLine("  " + l);
        git.LogMessage += l => Console.WriteLine("  " + l);

        FirestoreSink? sink = cfg.PublishToFirestore
            ? new FirestoreSink(cfg.FirestoreProjectId, cfg.FirestoreCredentialsJson, cfg.FirestoreCollection)
            : null;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.Error.WriteLine("\nCancelling…"); };

        switch (cmd, sub)
        {
            case ("ci", "run"):
            {
                var ci = new AndroidCommandCenter.Ci.LocalCiRunner(runner, git, android, tests, sink);
                ci.LogMessage += Console.WriteLine;
                try
                {
                    var m = await ci.RunAsync(cfg, profile!, profiles!.GitHubToken(profile!),
                        opts.GetValueOrDefault("trigger") ?? "cli", opts.ContainsKey("dry-run"), cts.Token);
                    Console.WriteLine();
                    Console.WriteLine($"Manifest: {m.ManifestPath}");
                    Console.WriteLine($"Log:      {m.LogPath}");
                    Console.WriteLine(m.Success ? $"✔ {m.Result}" : $"✖ {m.Result}");
                    if (!m.Success && m.PrimaryFailure is { } f)
                    { Console.WriteLine(); Console.WriteLine(f.ToString()); }
                    return m.Success ? 0 : 1;
                }
                catch (OperationCanceledException) { return 3; }
            }

            case ("ci", "probe"):
            {
                if (sink is null) { Console.Error.WriteLine("Firestore not configured (Pipeline.PublishToFirestore / FirestoreCredentialsJson)."); return 2; }
                try { await sink.ProbeAsync(cts.Token); Console.WriteLine("✔ Firestore reachable."); return 0; }
                catch (Exception ex) { Console.Error.WriteLine($"✖ {ex.Message}"); return 2; }
            }

            case ("rollback", _):
            {
                var runs = AndroidCommandCenter.Ci.LocalCiRunner.LoadManifests()
                    .Where(m => m.ProjectPath == projectPath && m.MergeSha != null).ToList();
                var run = opts.TryGetValue("run", out var id)
                    ? runs.FirstOrDefault(m => m.RunId.StartsWith(id, StringComparison.OrdinalIgnoreCase))
                    : runs.FirstOrDefault(m => m.Success);
                if (run is null) { Console.Error.WriteLine("No promoted run found to roll back."); return 4; }

                var rb = new AndroidCommandCenter.Ci.RollbackService(git);
                rb.LogMessage += Console.WriteLine;
                RollbackPlan plan;
                try
                {
                    plan = await rb.PlanAsync(cfg, run, projectPath, cts.Token,
                        profiles!.GitHubToken(profile!));
                }
                catch (Exception ex) { Console.Error.WriteLine($"✖ {ex.Message}"); return 4; }

                Console.WriteLine(plan.Description);
                Console.WriteLine($"Run {run.RunId} · merge {run.MergeSha![..8]} · {run.Commits.Count} commits · versionCode {run.VersionCode}");
                if (opts.ContainsKey("plan")) return 0;
                if (!opts.ContainsKey("yes"))
                {
                    Console.Write($"Proceed{(opts.ContainsKey("push") ? " AND PUSH" : "")}? [y/N] ");
                    if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) return 4;
                }

                var r = await rb.ExecuteAsync(cfg, plan, projectPath, profile!, profiles!.GitHubToken(profile!),
                    opts.ContainsKey("push"), opts.GetValueOrDefault("reason") ?? "manual rollback", cts.Token);
                Console.WriteLine((r.Success ? "✔ " : "✖ ") + r.Message);
                return r.Success ? 0 : 4;
            }

            default:
                return Usage();
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: acc ci run|status|env|probe [--project PATH] [--dry-run] [--no-push] [--trigger NAME]\n" +
            "       acc rollback [--project PATH] [--run ID] [--plan] [--push] [--yes] [--reason TEXT]");
        return 2;
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
