namespace Service.Tests;

/// <summary>
/// Delegate-backed fake module implementations used to drive <see cref="FlowRuntime"/> tests without any real
/// I/O. Each fake wraps a plain async delegate so a test can plug in whatever behavior it needs (pass-through,
/// a canned output batch, a call counter, or a failure to inject for retry/compensation tests).
/// </summary>
internal sealed class DelegateFetchModule(string id, Func<FlowExecutionContext, IntegrationBatch, ModuleStepDefinition, CancellationToken, Task<IntegrationBatch>> fetch) : IFetchModule
{
    public ModuleDescriptor Descriptor { get; } = new(id, id, ModuleKind.Fetch, "test fake");

    public Task<IntegrationBatch> FetchAsync(FlowExecutionContext context, IntegrationBatch input, ModuleStepDefinition step, CancellationToken cancellationToken)
        => fetch(context, input, step, cancellationToken);
}

internal sealed class DelegateParseModule(string id, Func<FlowExecutionContext, IntegrationBatch, ModuleStepDefinition, CancellationToken, Task<IntegrationBatch>> parse) : IParseModule
{
    public ModuleDescriptor Descriptor { get; } = new(id, id, ModuleKind.Parse, "test fake");

    public Task<IntegrationBatch> ParseAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
        => parse(context, batch, step, cancellationToken);
}

internal sealed class DelegateAugmentModule(string id, Func<FlowExecutionContext, IntegrationBatch, ModuleStepDefinition, CancellationToken, Task<IntegrationBatch>> augment) : IOrchestrationAugmentModule
{
    public ModuleDescriptor Descriptor { get; } = new(id, id, ModuleKind.OrchestrationAugment, "test fake");

    public Task<IntegrationBatch> AugmentAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
        => augment(context, batch, step, cancellationToken);
}

internal sealed class DelegateRenderModule(string id, Func<FlowExecutionContext, IntegrationBatch, ModuleStepDefinition, CancellationToken, Task<IntegrationBatch>> render) : IRenderModule
{
    public ModuleDescriptor Descriptor { get; } = new(id, id, ModuleKind.Render, "test fake");

    public Task<IntegrationBatch> RenderAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
        => render(context, batch, step, cancellationToken);
}

internal sealed class DelegateDeliverModule(string id, Func<FlowExecutionContext, IntegrationBatch, ModuleStepDefinition, CancellationToken, Task> deliver) : IDeliverModule
{
    public ModuleDescriptor Descriptor { get; } = new(id, id, ModuleKind.Deliver, "test fake");

    public Task DeliverAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
        => deliver(context, batch, step, cancellationToken);
}

/// <summary>Helpers for the common "just pass the batch through, but count calls / optionally fail" fake shape.</summary>
internal static class DelegateModuleFactory
{
    public static DelegateFetchModule PassthroughFetch(string id, Action? onCall = null, Func<int, bool>? failOnCall = null)
    {
        var callCount = 0;
        return new DelegateFetchModule(id, (_, input, _, _) =>
        {
            callCount++;
            onCall?.Invoke();
            if (failOnCall?.Invoke(callCount) == true)
            {
                throw new InvalidOperationException($"Fake fetch module '{id}' failed on call {callCount}.");
            }

            return Task.FromResult(input);
        });
    }

    public static DelegateFetchModule Fetch(string id, IntegrationBatch output, Func<int, bool>? failOnCall = null)
    {
        var callCount = 0;
        return new DelegateFetchModule(id, (_, _, _, _) =>
        {
            callCount++;
            if (failOnCall?.Invoke(callCount) == true)
            {
                throw new InvalidOperationException($"Fake fetch module '{id}' failed on call {callCount}.");
            }

            return Task.FromResult(output);
        });
    }

    public static DelegateParseModule PassthroughParse(string id, Func<int, bool>? failOnCall = null)
    {
        var callCount = 0;
        return new DelegateParseModule(id, (_, batch, _, _) =>
        {
            callCount++;
            if (failOnCall?.Invoke(callCount) == true)
            {
                throw new InvalidOperationException($"Fake parse module '{id}' failed on call {callCount}.");
            }

            return Task.FromResult(batch);
        });
    }

    public static DelegateAugmentModule PassthroughAugment(string id, Func<int, bool>? failOnCall = null)
    {
        var callCount = 0;
        return new DelegateAugmentModule(id, (_, batch, _, _) =>
        {
            callCount++;
            if (failOnCall?.Invoke(callCount) == true)
            {
                throw new InvalidOperationException($"Fake augment module '{id}' failed on call {callCount}.");
            }

            return Task.FromResult(batch);
        });
    }

    public static DelegateRenderModule PassthroughRender(string id, Func<int, bool>? failOnCall = null)
    {
        var callCount = 0;
        return new DelegateRenderModule(id, (_, batch, _, _) =>
        {
            callCount++;
            if (failOnCall?.Invoke(callCount) == true)
            {
                throw new InvalidOperationException($"Fake render module '{id}' failed on call {callCount}.");
            }

            return Task.FromResult(batch);
        });
    }

    public static DelegateDeliverModule Deliver(string id, List<IntegrationBatch>? deliveredBatches = null, Func<int, bool>? failOnCall = null)
    {
        var callCount = 0;
        return new DelegateDeliverModule(id, (_, batch, _, _) =>
        {
            callCount++;
            if (failOnCall?.Invoke(callCount) == true)
            {
                throw new InvalidOperationException($"Fake deliver module '{id}' failed on call {callCount}.");
            }

            deliveredBatches?.Add(batch);
            return Task.CompletedTask;
        });
    }
}
