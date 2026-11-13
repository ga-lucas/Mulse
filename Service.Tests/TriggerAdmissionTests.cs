using Microsoft.Extensions.Logging.Abstractions;
using Service.Triggers;

namespace Service.Tests;

public sealed class TriggerAdmissionTests
{
    [Fact]
    public async Task Push_CapacityRejectionRetainsTheOriginalSignalUntilAdmitted()
    {
        var source = new TriggerSource();
        var runtime = new BusyOnceRuntime(PipelineTriggerMode.Push);
        var catalog = new FakeExecutableModuleCatalog().AddFetch(source);
        using var service = new PushTriggeredFlowService(runtime, catalog, NullLogger<PushTriggeredFlowService>.Instance);
        await service.StartAsync(default);
        try
        {
            await runtime.Admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, runtime.Attempts);
            Assert.Equal(1, source.Signals);
        }
        finally { await service.StopAsync(default); }
    }

    [Fact]
    public async Task Interval_AdmissionRejectionDoesNotPostponeDueWorkByAnEntireInterval()
    {
        var runtime = new BusyOnceRuntime(PipelineTriggerMode.Interval);
        using var service = new ScheduledFlowService(runtime, TimeProvider.System, NullLogger<ScheduledFlowService>.Instance);
        await service.StartAsync(default);
        try
        {
            await runtime.Admitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, runtime.Attempts);
        }
        finally { await service.StopAsync(default); }
    }

    private sealed class BusyOnceRuntime(PipelineTriggerMode mode) : IFlowRuntime
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);
        public TaskCompletionSource Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<PipelineDefinition> GetConfiguredFlows() => [new()
        {
            Id = "orders", Enabled = true,
            Trigger = new() { Mode = mode, RunOnStartup = true, Interval = TimeSpan.FromDays(1) },
            Fetches = [new() { Id = "source", Fetch = new() { Module = "push-source" } }]
        }];
        public Task<FlowExecutionResult> ExecuteAsync(string flowId, CancellationToken token)
        {
            if (Interlocked.Increment(ref _attempts) == 1) throw new AdmissionRejectedException("Simulated execution capacity.");
            Admitted.TrySetResult();
            return Task.FromResult(new FlowExecutionResult(flowId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, [], FlowExecutionOutcome.Completed, null));
        }
        public Task<FlowExecutionResult?> ReleaseDueBatchAsync(string flowId, CancellationToken token) => throw new NotSupportedException();
        public Task<FlowExecutionResult> ReleaseCollectedBatchAsync(string flowId, string instanceId, int count, CancellationToken token) => throw new NotSupportedException();
        public Task<FlowExecutionResult?> ResumeRetryAsync(FlowRetryState retry, CancellationToken token) => throw new NotSupportedException();
        public Task<FlowExecutionResult> RetrySourceOutcomesAsync(string flowId, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class TriggerSource : IPushTriggeredFetchModule, IAsyncDisposable
    {
        public ModuleDescriptor Descriptor { get; } = new("push-source", "Push source", ModuleKind.Fetch, "Single test signal");
        public int Signals { get; private set; }
        public Task<IAsyncDisposable> RegisterTriggerAsync(PushFlowTriggerContext context, ModuleStepDefinition step, CancellationToken token)
        {
            Signals++;
            context.Signal("one signal only");
            return Task.FromResult<IAsyncDisposable>(this);
        }
        public Task<IntegrationBatch> FetchAsync(FlowExecutionContext context, IntegrationBatch input, ModuleStepDefinition step, CancellationToken token)
            => throw new InvalidOperationException("The fake runtime must not execute a real fetch.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
