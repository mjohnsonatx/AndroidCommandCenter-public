using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AndroidCommandCenter.Models;

namespace AndroidCommandCenter.Services;

public sealed class FirebaseService
{
    private readonly ProcessRunner _runner;
    public string CliPath { get; set; } = "firebase";
    public event Action<string>? LogMessage;

    public FirebaseService(ProcessRunner runner) => _runner = runner;

    public sealed record DistributeResult(int ExitCode, string? ReleaseUrl, string Tail);

    public async Task<DistributeResult> Distribute(string apkPath, FirebaseSpec fb, CancellationToken ct)
    {
        if (!File.Exists(apkPath)) throw new FileNotFoundException("APK not found", apkPath);

        var args = new List<string> { "appdistribution:distribute", apkPath, "--app", fb.AppId };
        if (!string.IsNullOrWhiteSpace(fb.TesterGroups)) { args.Add("--groups"); args.Add(fb.TesterGroups); }
        if (!string.IsNullOrWhiteSpace(fb.TesterEmails)) { args.Add("--testers"); args.Add(fb.TesterEmails); }
        if (!string.IsNullOrEmpty(fb.Token)) { args.Add("--token"); args.Add(fb.Token); }

        string? notesFile = null;
        if (!string.IsNullOrWhiteSpace(fb.ReleaseNotes))
        {
            notesFile = Path.Combine(Path.GetTempPath(), $"acc_notes_{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(notesFile, fb.ReleaseNotes, ct);
            args.Add("--release-notes-file"); args.Add(notesFile);
        }

        Dictionary<string, string>? env = null;
        if (!string.IsNullOrEmpty(fb.CredentialsJsonPath) && string.IsNullOrEmpty(fb.Token))
        {
            if (!File.Exists(fb.CredentialsJsonPath))
                throw new FileNotFoundException("Service-account JSON not found", fb.CredentialsJsonPath);
            env = new() { ["GOOGLE_APPLICATION_CREDENTIALS"] = fb.CredentialsJsonPath };
        }

        LogMessage?.Invoke($"Uploading {Path.GetFileName(apkPath)} to app {fb.AppId}");
        LogMessage?.Invoke($"Groups: {fb.TesterGroups} | Testers: {fb.TesterEmails}");

        string? url = null;
        var tail = new Queue<string>();
        void capture(string line)
        {
            if (tail.Count >= 40) tail.Dequeue();
            tail.Enqueue(line);
            // firebase prints "View this release in the Firebase console: https://..."
            int i = line.IndexOf("https://console.firebase.google.com", StringComparison.Ordinal);
            if (i >= 0) url = line[i..].Trim();
        }
        _runner.OutputReceived += capture;
        try
        {
            int code = await _runner.RunAsync(CliPath, args,
                Path.GetDirectoryName(apkPath) ?? ".", ct, env);
            return new DistributeResult(code, url, string.Join('\n', tail));
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            throw new InvalidOperationException(
                $"Firebase CLI '{CliPath}' not found. Install with `sudo npm i -g firebase-tools` " +
                "or set the full path in Firebase Config.", ex);
        }
        finally
        {
            _runner.OutputReceived -= capture;
            if (notesFile != null) try { File.Delete(notesFile); } catch { }
        }
    }
}