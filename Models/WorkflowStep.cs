namespace AndroidCommandCenter.Models;

public class WorkflowStep
{
    public required string Name { get; init; }
    public required Func<CancellationToken, Task<int>> Action { get; init; }
    public int MaxRetries { get; init; } = 0;
    public bool StopOnFailure { get; init; } = true;
}