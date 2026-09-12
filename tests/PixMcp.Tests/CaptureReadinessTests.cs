using Microsoft.PIX;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class CaptureReadinessTests
{
    private const PIX_PROCESS_UNSUPPORTED_REASON Waiting = PIX_PROCESS_UNSUPPORTED_REASON.PIX_PROCESS_UNSUPPORTED_REASON_NOT_USING_D3D12;
    private const PIX_PROCESS_UNSUPPORTED_REASON Ready = PIX_PROCESS_UNSUPPORTED_REASON.PIX_PROCESS_UNSUPPORTED_REASON_NONE;

    [Fact]
    public async Task CallbackBeforeLaunchReturnsWinsOverInitialResult()
    {
        var targets = new CaptureTargets();
        long before = targets.Revision;
        targets.Observe(42, Ready);
        CaptureTarget target = targets.Track(42, Waiting, before);
        await target.WaitReadyAsync(TimeSpan.Zero, default);
        Assert.True(target.Snapshot().Ready);
    }

    [Fact]
    public async Task TimeoutCancellationAndReplacementWakeWaiters()
    {
        var targets = new CaptureTargets();
        CaptureTarget target = targets.Track(42, Waiting, targets.Revision);
        PixToolException timeout = await Assert.ThrowsAsync<PixToolException>(() => target.WaitReadyAsync(TimeSpan.Zero, default));
        Assert.Equal("capture_target_not_ready", timeout.Detail.Code);
        using var cancellation = new CancellationTokenSource();
        Task cancelled = target.WaitReadyAsync(TimeSpan.FromMinutes(1), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Task replaced = target.WaitReadyAsync(TimeSpan.FromMinutes(1), default);
        targets.Track(42, Ready, targets.Revision);
        Assert.Equal("capture_target_changed", (await Assert.ThrowsAsync<PixToolException>(() => replaced)).Detail.Code);
        Assert.Throws<PixToolException>(() => targets.Validate(target));
    }

    [Fact]
    public async Task PermanentFailuresWakeWaitingCapture()
    {
        foreach (var (reason, code) in new[] {
            (PIX_PROCESS_UNSUPPORTED_REASON.PIX_PROCESS_UNSUPPORTED_REASON_TERMINATED, "capture_target_terminated"),
            (PIX_PROCESS_UNSUPPORTED_REASON.PIX_PROCESS_UNSUPPORTED_REASON_WRONG_ARCHITECTURE, "capture_target_unsupported") })
        {
            var target = new CaptureTarget(42, Waiting);
            Task waiting = target.WaitReadyAsync(TimeSpan.FromMinutes(1), default);
            target.Update(reason);
            Assert.Equal(code, (await Assert.ThrowsAsync<PixToolException>(() => waiting)).Detail.Code);
        }
    }

    [Fact]
    public async Task ClosingConnectionInterruptsWarmup()
    {
        var targets = new CaptureTargets();
        CaptureTarget target = targets.Track(42, Ready, 0);
        Task warmup = target.WarmupAsync(TimeSpan.FromMinutes(1), default);
        targets.Clear();
        Assert.Equal("capture_target_changed", (await Assert.ThrowsAsync<PixToolException>(() => warmup)).Detail.Code);
    }

    [Fact]
    public async Task TimingFinalizationRetriesAnExistingButUnreadableCapture()
    {
        int attempts = 0;
        string result = await PixMcp.Tools.DeviceTools.WaitForReadableCaptureAsync(() =>
            ++attempts < 2 ? Task.FromException<string>(new IOException("file exists but is incomplete")) : Task.FromResult("readable"),
            TimeSpan.FromSeconds(2), default);
        Assert.Equal("readable", result);
        Assert.Equal(2, attempts);
        var error = await Assert.ThrowsAsync<PixToolException>(() => PixMcp.Tools.DeviceTools.WaitForReadableCaptureAsync(
            () => Task.FromException<string>(new IOException("incomplete")), TimeSpan.Zero, default));
        Assert.Equal("capture_finalization_timeout", error.Detail.Code);
    }
}
