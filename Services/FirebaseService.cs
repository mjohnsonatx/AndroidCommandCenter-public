using System.IO;
using System.Text;

namespace AndroidCommandCenter.Services;

public class FirebaseService
{
    private readonly ProcessRunner _runner;
    public string CliPath { get; set; } = "firebase";
    public event Action<string>? LogMessage;

    public FirebaseService(ProcessRunner runner) => _runner = runner;

    public async Task<int> Distribute(
        string apkPath, string appId, string groups, string testers,
        string releaseNotes, string? token, string? credentialsJson,
        CancellationToken ct)
    {
        var args = new StringBuilder();
        args.Append($"appdistribution:distribute \"{apkPath}\"");
        args.Append($" --app {appId}");
        if (!string.IsNullOrWhiteSpace(groups)) args.Append($" --groups \"{groups}\"");
        if (!string.IsNullOrWhiteSpace(testers)) args.Append($" --testers \"{testers}\"");
        if (!string.IsNullOrEmpty(token)) args.Append($" --token \"{token}\"");

        string? notesFile = null;
        if (!string.IsNullOrEmpty(releaseNotes))
        {
            notesFile = Path.Combine(Path.GetTempPath(), "acc_release_notes.txt");
            await File.WriteAllTextAsync(notesFile, releaseNotes, ct);
            args.Append($" --release-notes-file \"{notesFile}\"");
        }

        Dictionary<string, string>? env = null;
        if (!string.IsNullOrEmpty(credentialsJson) && string.IsNullOrEmpty(token))
            env = new() { ["GOOGLE_APPLICATION_CREDENTIALS"] = credentialsJson };

        LogMessage?.Invoke($"Uploading: {Path.GetFileName(apkPath)}");
        LogMessage?.Invoke($"App: {appId} | Groups: {groups} | Testers: {testers}");

        try
        {
            return await _runner.RunAsync("cmd.exe", $"/c {CliPath} {args}",
                Path.GetDirectoryName(apkPath) ?? ".", ct, env);
        }
        finally
        {
            if (notesFile != null) try { File.Delete(notesFile); } catch { }
        }
    }
}