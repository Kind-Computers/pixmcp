using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.PIX;
using ModelContextProtocol;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public sealed class LifecycleTests
{
    [Fact]
    public async Task CancellationWhileQueuedSkipsNativeTokenAndWork()
    {
        using var fixture = new Fixture();
        using var gate = new Gate();
        Task blocker = fixture.Worker.Run(gate.Block);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool executed = false;
        Job job = fixture.Jobs.Start("test", "queued", _ => executed = true);
        fixture.Jobs.Cancel(job);
        gate.Release.Set();
        await blocker;
        await Finished(job);
        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.False(executed);
        Assert.Equal(0, fixture.TokensCreated);
        Assert.True(job.ToDto().CancellationRequested);
        Assert.Null(job.StartedAt);
    }

    [Fact]
    public async Task CancellationDuringNativeTokenCreationReachesTheToken()
    {
        using var gate = new Gate();
        var token = new FakeToken();
        using var fixture = new Fixture(() => { gate.Block(); return token; });
        bool executed = false;
        Job job = fixture.Jobs.Start("test", "creating token", _ => executed = true);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Jobs.Cancel(job);
        gate.Release.Set();
        await Finished(job);
        Assert.Equal(1, token.Calls);
        Assert.False(executed);
        Assert.Equal(JobStatus.Cancelled, job.Status);
    }

    [Fact]
    public async Task CompletedWorkKeepsItsResultAfterCancellationRequest()
    {
        using var fixture = new Fixture();
        using var gate = new Gate();
        Job job = fixture.Jobs.Start("test", "uncancellable work", _ => { gate.Block(); return "saved.wpix"; });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Jobs.Cancel(job);
        gate.Release.Set();
        await Finished(job);
        JobDto result = job.ToDto();
        Assert.Equal("succeeded", result.Status);
        Assert.NotNull(result.ResultRef);
        Assert.Equal("saved.wpix", fixture.Session.Results.Read(result.ResultRef).Value);
        Assert.True(result.CancellationRequested);
        Assert.NotNull(result.FinishedAt);
        Assert.Equal(1, result.Progress);
        Assert.Equal(PixErrors.Codes.JobAlreadyFinished, Assert.Throws<PixToolException>(() => fixture.Jobs.Cancel(job)).Detail.Code);
    }

    [Theory]
    [InlineData(unchecked((int)0x80004005), JobStatus.Failed)]
    [InlineData(unchecked((int)0x80004004), JobStatus.Cancelled)]
    [InlineData(unchecked((int)0x800704C7), JobStatus.Cancelled)]
    public async Task NativeFailuresAreOnlyCancellationWhenTheErrorIndicatesInterruption(int hresult, JobStatus expected)
    {
        using var fixture = new Fixture();
        using var gate = new Gate();
        Job job = fixture.Jobs.Start("test", "failing", _ => { gate.Block(); throw new COMException("native failure", hresult); });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Jobs.Cancel(job);
        gate.Release.Set();
        await Finished(job);
        Assert.Equal(expected, job.Status);
        Assert.Contains(PixErrors.Hex(hresult), job.Error);
    }

    [Fact]
    public async Task CancellationErrorWithoutRequestRemainsFailure()
    {
        using var fixture = new Fixture();
        Job job = fixture.Jobs.Start("test", "aborted elsewhere", _ => throw new COMException("aborted", unchecked((int)0x80004004)));
        await Finished(job);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.False(job.CancellationRequested);
    }

    [Fact]
    public async Task CaptureDelayIsCancellableBeforeConfiguringOrCapturing()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool configured = false, captured = false;
        Job job = fixture.Jobs.StartAfter("gpu-capture", "delayed capture", async j =>
        {
            entered.SetResult();
            await Task.Delay(TimeSpan.FromMinutes(1), j.Cancellation.Token);
        }, j =>
        {
            return DeviceTools.CaptureWithOptions(42, 1, _ => configured = true, () => captured = true,
                j.Cancellation.Token);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Jobs.Cancel(job);
        await Finished(job);
        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.False(configured);
        Assert.False(captured);
    }

    [Fact]
    public void CancellationAfterCaptureConfigurationPreventsCapture()
    {
        using var cancellation = new CancellationTokenSource();
        bool captured = false;
        Assert.Throws<OperationCanceledException>(() => DeviceTools.CaptureWithOptions(42, 1,
            _ => cancellation.Cancel(), () => captured = true, cancellation.Token));
        Assert.False(captured);
    }

    [Fact]
    public async Task HandleLookupRunsAfterPreviouslyQueuedClose()
    {
        using var fixture = new Fixture();
        FakeHandle handle = await fixture.Worker.Run(() => fixture.Session.Register(new FakeHandle()));
        using var gate = new Gate();
        Task blocker = fixture.Worker.Run(gate.Block);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<object> closing = fixture.Worker.Run(() => fixture.Session.Close(handle.Id));
        bool executed = false;
        Job job = fixture.Jobs.StartForHandle<FakeHandle>("test", "after close", handle.Id, (_, _) => executed = true);
        gate.Release.Set();
        await blocker;
        await closing;
        await Finished(job);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("Unknown handle", job.Error);
        Assert.False(executed);
        Assert.True(handle.Closed);
    }

    [Fact]
    public async Task ConflictingQueuedAnalysisRequestsDoNotReplaceFirstConfiguration()
    {
        using var fixture = new Fixture();
        FakeHandle handle = await fixture.Worker.Run(() => fixture.Session.Register(new FakeHandle()));
        using var gate = new Gate();
        Task blocker = fixture.Worker.Run(gate.Block);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var firstOptions = new AnalysisOptions(Adapter: 1);
        var secondOptions = new AnalysisOptions(Adapter: 2);
        Job Start(AnalysisOptions options) => fixture.Jobs.StartForHandle<FakeHandle>("analysis", "start", handle.Id, (_, h) =>
        {
            Assert.True(fixture.Worker.IsOnWorkerThread);
            if (h.RunningOptions is AnalysisOptions running) running.ValidateRunningRequest(options);
            else h.RunningOptions = options;
            return h.RunningOptions;
        });
        Job first = Start(firstOptions), second = Start(secondOptions);
        gate.Release.Set();
        await blocker;
        await Finished(first);
        await Finished(second);
        Assert.Equal(JobStatus.Succeeded, first.Status);
        Assert.Equal(JobStatus.Failed, second.Status);
        Assert.Equal(firstOptions, handle.RunningOptions);
        Assert.Contains("pix_gpu_analysis_stop", second.Error);
    }

    [Fact]
    public void RunningAnalysisAllowsUnspecifiedOrMatchingSettings()
    {
        var options = new AnalysisOptions(1, 2, PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_ENABLE_DEBUG_LAYER);
        options.ValidateRunningRequest(new());
        options.ValidateRunningRequest(new(Adapter: 1));
        options.ValidateRunningRequest(options);
        Assert.Equal(PixErrors.Codes.AnalysisSettingsConflict, Assert.Throws<PixToolException>(() => options.ValidateRunningRequest(new(PowerState: 3))).Detail.Code);
        Assert.Equal(PixErrors.Codes.AnalysisSettingsConflict, Assert.Throws<PixToolException>(() => new AnalysisOptions().ValidateRunningRequest(new(Adapter: 1))).Detail.Code);
    }

    [Fact]
    public void DefaultAnalysisPreservesCancellationAndNotificationsWithoutRetryingFailures()
    {
        var job = new Job("test", "analysis", "default startup");
        var token = new FakeToken();
        job.AttachPixToken(token);
        int calls = 0;
        var failure = new COMException("replay failed", unchecked((int)0x80004005));
        COMException actual = Assert.Throws<COMException>(() => GpuCaptureHandle.InvokeStartAnalysis(null, job, (parameters, notifications, cancellation) =>
        {
            calls++;
            Assert.Null(parameters);
            Assert.NotNull(notifications);
            Assert.Same(token, cancellation);
            throw failure;
        }));
        Assert.Same(failure, actual);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CancelledAnalysisDoesNotInvokeReplay()
    {
        var job = new Job("test", "analysis", "cancelled startup");
        job.RequestCancellation();
        bool invoked = false;
        Assert.Throws<OperationCanceledException>(() => GpuCaptureHandle.InvokeStartAnalysis(null, job, (_, _, _) => invoked = true));
        Assert.False(invoked);
    }

    private static Task Finished(Job job) => job.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

    private sealed class FakeToken : IPixCancellationToken
    {
        public int Calls;
        public void Cancel() => Interlocked.Increment(ref Calls);
    }

    private sealed class FakeHandle : PixHandle
    {
        public FakeHandle() : base("fake") { }
        public override string Kind => "fake";
        public bool Closed { get; private set; }
        public AnalysisOptions? RunningOptions { get; set; }
        public override void Close(List<string> warnings) => Closed = true;
    }

    private sealed class Gate : IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public void Block()
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test gate was not released.");
        }
        public void Dispose() => Release.Set();
    }

    private sealed class Fixture : IDisposable
    {
        public PixWorker Worker { get; } = new();
        public PixSession Session { get; }
        public JobManager Jobs { get; }
        public int TokensCreated;
        public Fixture(Func<IPixCancellationToken?>? createToken = null)
        {
            Session = new PixSession(Worker, NullLogger<PixSession>.Instance);
            Jobs = new JobManager(Worker, Session, () => { Interlocked.Increment(ref TokensCreated); return createToken?.Invoke(); });
        }
        public void Dispose() { Session.Dispose(); Worker.Dispose(); }
    }
}
