using System;
using System.Threading;
using System.Threading.Tasks;

namespace AndroidCommandCenter.Services;

/// <summary>
/// Serializes everything that reads or writes application config: dialogs,
/// interactive runs, and the scheduler. A dialog holds the gate while open, so
/// a scheduled run can never snapshot a half-edited config.
///
/// The scheduler uses TryAcquire and skips (rather than queues) when busy —
/// a deploy that starts twenty minutes late because a dialog was open is worse
/// than one that's skipped and logged.
/// </summary>
public sealed class ConfigGate
{
    private readonly SemaphoreSlim _sem = new(1, 1);

    public string? HeldBy { get; private set; }
    public bool IsHeld => _sem.CurrentCount == 0;

    public async Task<IDisposable> AcquireAsync(string reason, CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct);
        HeldBy = reason;
        return new Releaser(this);
    }

    /// <summary>Returns null immediately if the gate is held.</summary>
    public IDisposable? TryAcquire(string reason)
    {
        if (!_sem.Wait(0)) return null;
        HeldBy = reason;
        return new Releaser(this);
    }

    private sealed class Releaser(ConfigGate gate) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            gate.HeldBy = null;
            gate._sem.Release();
        }
    }
}