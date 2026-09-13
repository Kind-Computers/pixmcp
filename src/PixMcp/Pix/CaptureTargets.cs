using System.Diagnostics;
using Microsoft.PIX;

namespace PixMcp.Pix;

public sealed record CaptureTargetDto(uint ProcessId, bool Ready, string UnsupportedReason);

/// <summary>Copies target notifications into managed state so readiness waits never hold the PIX worker.</summary>
public sealed class CaptureTargets
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, CaptureTarget> _targets = new();
    private readonly Dictionary<uint, (long Revision, PIX_PROCESS_UNSUPPORTED_REASON Reason)> _observations = new();
    private long _revision;
    public long Revision { get { lock (_gate) return _revision; } }

    public CaptureTarget Track(uint pid, PIX_PROCESS_UNSUPPORTED_REASON reason, long beforeOperation)
    {
        lock (_gate)
        {
            if (_observations.TryGetValue(pid, out var observed) && observed.Revision > beforeOperation) reason = observed.Reason;
            if (_targets.Remove(pid, out CaptureTarget? old)) old.Invalidate();
            var target = new CaptureTarget(pid, reason);
            _targets[pid] = target;
            return target;
        }
    }

    public void Observe(uint pid, PIX_PROCESS_UNSUPPORTED_REASON reason)
    {
        lock (_gate)
        {
            _observations[pid] = (++_revision, reason);
            if (_targets.TryGetValue(pid, out CaptureTarget? target)) target.Update(reason);
        }
    }

    public CaptureTarget Get(uint pid)
    {
        lock (_gate) return _targets.TryGetValue(pid, out CaptureTarget? target) ? target
            : throw new PixToolException(PixErrors.Codes.CaptureTargetChanged, $"Process {pid} is not a capture target on this connection. Launch or attach it first.");
    }

    public void Validate(CaptureTarget expected)
    {
        lock (_gate)
        {
            if (!_targets.TryGetValue(expected.ProcessId, out CaptureTarget? current) || !ReferenceEquals(current, expected))
                throw new PixToolException(PixErrors.Codes.CaptureTargetChanged, "The capture target was detached or replaced while waiting.");
            current.RequireReady();
        }
    }

    public CaptureTargetDto[] Snapshot() { lock (_gate) return _targets.Values.OrderBy(t => t.ProcessId).Select(t => t.Snapshot()).ToArray(); }
    public void TerminateAll()
    {
        lock (_gate) foreach (CaptureTarget target in _targets.Values)
            target.Update(PIX_PROCESS_UNSUPPORTED_REASON.PIX_PROCESS_UNSUPPORTED_REASON_TERMINATED);
    }
    public void Clear()
    {
        lock (_gate)
        {
            foreach (CaptureTarget target in _targets.Values) target.Invalidate();
            _targets.Clear(); _observations.Clear(); ++_revision;
        }
    }
}

public sealed class CaptureTarget(uint processId, PIX_PROCESS_UNSUPPORTED_REASON reason)
{
    private readonly object _gate = new();
    private PIX_PROCESS_UNSUPPORTED_REASON _reason = reason;
    private bool _invalid;
    private TaskCompletionSource _changed = NewSignal();
    public uint ProcessId { get; } = processId;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CaptureTargetDto Snapshot() { lock (_gate) return new(ProcessId, !_invalid && Ready, Json.EnumName(_reason)); }
    private bool Ready => _reason == PIX_PROCESS_UNSUPPORTED_REASON.PIX_PROCESS_UNSUPPORTED_REASON_NONE;
    public void Update(PIX_PROCESS_UNSUPPORTED_REASON value) { lock (_gate) { _reason = value; Changed(); } }
    public void Invalidate() { lock (_gate) { _invalid = true; Changed(); } }
    private void Changed() { TaskCompletionSource old = _changed; _changed = NewSignal(); old.TrySetResult(); }

    public void RequireReady()
    {
        lock (_gate)
        {
            CheckPermanentFailure();
            if (!Ready) throw new PixToolException(PixErrors.Codes.CaptureTargetNotReady, $"Process {ProcessId} has not created a capturable D3D12 device.", true);
        }
    }

    private void CheckPermanentFailure()
    {
        if (_invalid) throw new PixToolException(PixErrors.Codes.CaptureTargetChanged, "The capture target was detached, replaced, or closed while waiting.");
        if (_reason == PIX_PROCESS_UNSUPPORTED_REASON.PIX_PROCESS_UNSUPPORTED_REASON_TERMINATED)
            throw new PixToolException(PixErrors.Codes.CaptureTargetTerminated, $"Process {ProcessId} terminated before capture.");
        if (_reason is not (PIX_PROCESS_UNSUPPORTED_REASON.PIX_PROCESS_UNSUPPORTED_REASON_NONE or PIX_PROCESS_UNSUPPORTED_REASON.PIX_PROCESS_UNSUPPORTED_REASON_NOT_USING_D3D12))
            throw new PixToolException(PixErrors.Codes.CaptureTargetUnsupported, $"Process {ProcessId} is unsupported: {Json.EnumName(_reason)}.");
    }

    internal async Task WaitReadyAsync(TimeSpan timeout, CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task changed;
            lock (_gate) { CheckPermanentFailure(); if (Ready) return; changed = _changed.Task; }
            TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) { RequireReady(); return; }
            try { await changed.WaitAsync(remaining, token).ConfigureAwait(false); }
            catch (TimeoutException) { RequireReady(); return; }
        }
    }

    internal async Task WarmupAsync(TimeSpan duration, CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task changed;
            lock (_gate) { RequireReady(); changed = _changed.Task; }
            TimeSpan remaining = duration - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return;
            try { await changed.WaitAsync(remaining, token).ConfigureAwait(false); }
            catch (TimeoutException) { RequireReady(); return; }
        }
    }
}
