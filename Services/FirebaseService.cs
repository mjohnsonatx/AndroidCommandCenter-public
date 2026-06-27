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
        string apkPath, string appId, string groups,
        string releaseNotes, string? token, string? serviceAccountPath,
        CancellationToken ct)
    {
        // Build arguments
        var args = new StringBuilder();
        args.Append($"appdistribution:distribute \"{apkPath}\"");
        args.Append($" --app {appId}");
        if (!string.IsNullOrEmpty(groups))
            args.Append($" --groups \"{groups}\"");
        if (!string.IsNullOrEmpty(token))
            args.Append($" --token \"{token}\"");

        // Write release notes to temp file to avoid escaping issues
        string? notesFile = null;
        if (!string.IsNullOrEmpty(releaseNotes))
        {
            notesFile = Path.Combine(Path.GetTempPath(), "acc_release_notes.txt");
            await File.WriteAllTextAsync(notesFile, releaseNotes, ct);
            args.Append($" --release-notes-file \"{notesFile}\"");
        }

        // Service account auth via env var
        Dictionary<string, string>? env = null;
        if (!string.IsNullOrEmpty(serviceAccountPath) && string.IsNullOrEmpty(token))
            env = new() { ["GOOGLE_APPLICATION_CREDENTIALS"] = serviceAccountPath };

        LogMessage?.Invoke($"Uploading: {Path.GetFileName(apkPath)}");
        LogMessage?.Invoke($"App: {appId} | Groups: {groups}");

        try
        {
            // cmd /c handles .cmd files and PATH resolution on Windows
            return await _runner.RunAsync(
                "cmd.exe", $"/c {CliPath} {args}",
                Path.GetDirectoryName(apkPath) ?? ".", ct, env);
        }
        finally
        {
            if (notesFile != null)
                try { File.Delete(notesFile); } catch { }
        }
    }

    public async Task<bool> VerifyInstallation(CancellationToken ct)
    {
        try
        {
            var (code, output) = await _runner.RunAndCaptureAsync(
                "cmd.exe", $"/c {CliPath} --version", ".", ct);
            if (code == 0)
            {
                LogMessage?.Invoke($"Firebase CLI: {output.Trim()}");
                return true;
            }
        }
        catch { }

        LogMessage?.Invoke("[ERROR] Firebase CLI not found.");
        LogMessage?.Invoke("  Install with: npm install -g firebase-tools");
        return false;
    }
}