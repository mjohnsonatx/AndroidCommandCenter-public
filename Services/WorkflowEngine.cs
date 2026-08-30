using System.Diagnostics;
using AndroidCommandCenter.Models;

namespace AndroidCommandCenter.Services;

public class WorkflowEngine
{
    public event Action<string>? LogMessage;
    public event Action<WorkflowProgress>? ProgressChanged;

    public async Task<bool> ExecuteAsync(
        string workflowName,
        List<WorkflowStep> steps,
        CancellationToken ct)
    {
        LogMessage?.Invoke("");
        LogMessage?.Invoke("========================================");
        LogMessage?.Invoke($"  WORKFLOW: {workflowName}");
        LogMessage?.Invoke($"  Steps:    {steps.Count}");
        LogMessage?.Invoke("========================================");

        var sw = Stopwatch.StartNew();
        int passed = 0, failed = 0;

        for (int i = 0; i < steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = steps[i];
            ProgressChanged?.Invoke(new WorkflowProgress(
                i + 1, steps.Count, step.Name, WorkflowProgressState.Running));

            LogMessage?.Invoke("");
            LogMessage?.Invoke($">>> [{i + 1}/{steps.Count}] {step.Name}");

            bool success = false;
            int maxAttempts = step.MaxRetries + 1;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                if (attempt > 1)
                {
                    LogMessage?.Invoke($">>> Retry {attempt - 1}/{step.MaxRetries}...");
                    await Task.Delay(3000, ct);
                }

                try
                {
                    int code = await step.Action(ct);
                    if (code == 0) { success = true; break; }
                    LogMessage?.Invoke($">>> Exit code {code}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($">>> Exception: {ex.Message}");
                }
            }

            if (success)
            {
                passed++;
                LogMessage?.Invoke($">>> PASSED: {step.Name}");
                ProgressChanged?.Invoke(new WorkflowProgress(
                    i + 1, steps.Count, step.Name, WorkflowProgressState.Passed));
            }
            else
            {
                failed++;
                LogMessage?.Invoke($">>> FAILED: {step.Name}");
                ProgressChanged?.Invoke(new WorkflowProgress(
                    i + 1, steps.Count, step.Name, WorkflowProgressState.Failed));
                if (step.StopOnFailure)
                {
                    LogMessage?.Invoke(">>> WORKFLOW ABORTED");
                    Summary(sw, passed, failed);
                    return false;
                }
            }
        }

        Summary(sw, passed, failed);
        return failed == 0;
    }

    private void Summary(Stopwatch sw, int passed, int failed)
    {
        sw.Stop();
        LogMessage?.Invoke("");
        LogMessage?.Invoke("========================================");
        LogMessage?.Invoke($"  RESULT: {passed} passed, {failed} failed");
        LogMessage?.Invoke($"  TIME:   {sw.Elapsed:mm\\:ss}");
        LogMessage?.Invoke("========================================");
    }
}

public enum WorkflowProgressState { Running, Passed, Failed }

public sealed record WorkflowProgress(
    int Current, int Total, string Step, WorkflowProgressState State);
