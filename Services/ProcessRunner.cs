using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace AndroidCommandCenter.Services;

/// <summary>
/// Thrown when the executable itself cannot be launched, as opposed to running
/// and returning non-zero. The two need different messages in the UI: "adb not
/// installed" vs "adb said no".
/// </summary>
public sealed class ToolLaunchException(string tool, string reason, Exception? inner = null)
    : Exception($"Cannot run '{tool}': {reason}", inner)
{
    public string Tool { get; } = tool;
}

public sealed class ProcessRunner
{
    public event Action<string>? OutputReceived;

    /// <summary>Preferred overload — no shell, no quoting hazards.</summary>
    public Task<int> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        CancellationToken ct = default,
        Dictionary<string, string>? envVars = null,
        bool echoCommand = true)
    {
        var psi = BasePsi(fileName, workingDirectory, envVars);
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (echoCommand)
            OutputReceived?.Invoke($"$ {fileName} {string.Join(' ', args.Select(Quote))}");
        return RunCore(psi, fileName, ct);
    }

    /// <summary>
    /// Legacy string-args overload, kept for call sites that pass pre-built
    /// Gradle task lists. Do NOT use it for anything containing a secret or a
    /// user-supplied path — use the IReadOnlyList overload.
    /// </summary>
    public Task<int> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken ct = default,
        Dictionary<string, string>? envVars = null)
    {
        var psi = BasePsi(fileName, workingDirectory, envVars);
        psi.Arguments = arguments;
        OutputReceived?.Invoke($"$ {fileName} {Redact(arguments)}");
        return RunCore(psi, fileName, ct);
    }

    public async Task<(int ExitCode, string Output)> RunAndCaptureAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        CancellationToken ct = default,
        Dictionary<string, string>? envVars = null,
        bool echoCommand = false)
    {
        var lines = new ConcurrentQueue<string>();
        void handler(string line) => lines.Enqueue(line);
        OutputReceived += handler;
        try
        {
            int code = await RunAsync(fileName, args, workingDirectory, ct, envVars, echoCommand);
            return (code, string.Join(Environment.NewLine, lines));
        }
        finally { OutputReceived -= handler; }
    }

    private static ProcessStartInfo BasePsi(
        string fileName, string workingDirectory, Dictionary<string, string>? envVars)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory : workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // Gradle and adb emit ANSI colour codes when they think they're on a
        // terminal; they'd render as escape garbage in the log TextBox.
        psi.Environment["TERM"] = "dumb";
        psi.Environment["NO_COLOR"] = "1";
        if (envVars != null)
            foreach (var (k, v) in envVars) psi.Environment[k] = v;
        return psi;
    }

    private async Task<int> RunCore(ProcessStartInfo psi, string tool, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        // Completion sources let us wait for the output pumps to drain. Without
        // this, WaitForExitAsync can return before the last lines are delivered
        // and a failing build's final error message gets lost.
        var stdoutDone = new TaskCompletionSource();
        var stderrDone = new TaskCompletionSource();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) stdoutDone.TrySetResult();
            else OutputReceived?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) stderrDone.TrySetResult();
            else OutputReceived?.Invoke("[ERR] " + e.Data);
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ToolLaunchException(tool, ex.NativeErrorCode switch
            {
                2  => "executable not found. Check the tool path / that it is installed.",
                13 => "permission denied. The file may not be executable (chmod +x).",
                8  => "not a valid executable for this platform.",
                _  => ex.Message
            }, ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (ct.Register(() => TryKill(process)))
        {
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                // Give the tree a moment to actually die so the next Gradle
                // invocation doesn't hit a stale lock.
                try { process.WaitForExit(5000); } catch { }
                throw;
            }
        }

        // Drain, but never hang forever on a child that inherited the pipe.
        await Task.WhenAny(
            Task.WhenAll(stdoutDone.Task, stderrDone.Task),
            Task.Delay(2000, CancellationToken.None));

        return process.ExitCode;
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    private static string Quote(string a) =>
        a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a;

    /// <summary>Strip injected signing passwords out of echoed command lines.</summary>
    public static string Redact(string args)
    {
        foreach (var key in new[]
        {
            "android.injected.signing.store.password",
            "android.injected.signing.key.password",
            "--token"
        })
        {
            int i;
            while ((i = args.IndexOf(key, StringComparison.Ordinal)) >= 0)
            {
                int start = i + key.Length;
                if (start < args.Length && (args[start] == '=' || args[start] == ' ')) start++;
                int end = start;
                while (end < args.Length && args[end] != ' ') end++;
                args = args[..start] + "***" + args[end..];
                if (args.IndexOf(key, end, StringComparison.Ordinal) < 0) break;
            }
        }
        return args;
    }
}