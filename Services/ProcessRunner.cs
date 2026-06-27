using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace AndroidCommandCenter.Services;

public class ProcessRunner
{
    public event Action<string>? OutputReceived;

    public async Task<int> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) OutputReceived?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) OutputReceived?.Invoke("[ERR] " + e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Kill the whole process tree if the user cancels
        using (ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already dead */ }
        }))
        {
            await process.WaitForExitAsync(ct);
        }

        return process.ExitCode;
    }

    /// <summary>
    /// Runs a process, streams output to OutputReceived AND returns the full text.
    /// Used when we need to parse the output (device list, AVD names, etc.)
    /// </summary>
    public async Task<(int ExitCode, string Output)> RunAndCaptureAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken ct = default)
    {
        var lines = new ConcurrentQueue<string>();
        void handler(string line) => lines.Enqueue(line);

        OutputReceived += handler;
        try
        {
            int code = await RunAsync(fileName, arguments, workingDirectory, ct);
            return (code, string.Join(Environment.NewLine, lines));
        }
        finally
        {
            OutputReceived -= handler;
        }
    }
}