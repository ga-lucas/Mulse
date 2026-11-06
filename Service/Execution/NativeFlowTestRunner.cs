using Mulse.Modules;
using System.Text.Json.Nodes;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.ControlFlow;
using Service.Models;

namespace Service.Execution;

public static class NativeFlowTestRunner
{
    public static async Task<NativeFlowTestResponse> RunAsync(NativeFlowTestRequest request, CancellationToken cancellationToken)
    {
        if (request is null || request.FlowJson is null || request.Input is null || request.Mocks is null || request.Mocks.Count > 128)
            throw new ArgumentException("Supply a native definition, one synthetic input and at most 128 mock responses.");
        var definition = NativeFlowDefinition.Read(request.FlowJson);
        var input = ReadPayload(request.Input);
        var mocks = new Dictionary<(string Kind, string Module), NativeFlowTestPayload?>();
        foreach (var mock in request.Mocks)
        {
            if (mock is null || mock.Kind is not ("Augment" or "Render" or "Deliver" or "Transform") || string.IsNullOrWhiteSpace(mock.Module)
                || mock.Module.Length > 256 || mock.Module != mock.Module.Trim() || mock.Kind != "Deliver" && mock.Output is null)
                throw new ArgumentException("Each mock requires a kind, module and explicit output (delivery output may be empty).");
            if (mock.Output is not null) _ = ReadPayload(mock.Output);
            if (!mocks.TryAdd((mock.Kind, mock.Module), mock.Output)) throw new ArgumentException("Duplicate mock kind and module.");
        }
        var invoker = new MockInvoker(mocks);
        var clock = new VirtualClock();
        var store = new MemoryStore();
        var context = new FlowExecutionContext("isolated-native-test", Guid.NewGuid().ToString("N"), clock.Now);
        var step = new ModuleStepDefinition { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = request.FlowJson } };
        var module = new NativeControlFlowAugmentModule(invoker, invoker, store, clock, invoker, invoker);
        try
        {
            var output = await module.AugmentAsync(context, new([input]), step, cancellationToken).ConfigureAwait(false);
            var resumes = 0;
            // Only timer waits are resumed; a receive needs a real arrival and ends the test as suspended.
            while (request.AdvanceWaits && context.Disposition == FlowExecutionDisposition.Suspended && resumes < 32)
            {
                var saved = (await store.GetAllAsync(context.FlowId, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
                if (saved?.Native?.DeadlineUtc is not { } due) break;
                if (due > clock.Now) clock.Now = due;
                context = new FlowExecutionContext(context.FlowId, Guid.NewGuid().ToString("N"), clock.Now);
                context.SetOrchestrationInstance(saved.InstanceId, saved.CorrelationKey, saved.Bookmark, true);
                output = await module.AugmentAsync(context, IntegrationBatch.Empty, step, cancellationToken).ConfigureAwait(false);
                resumes++;
            }
            return new(context.Disposition.ToString(), context.SuspensionReason, Capture(output), Capture(new(context.CapturedResponses)), invoker.Calls, resumes);
        }
        catch (NativeAmbiguousEffectException exception)
        {
            throw new InvalidOperationException("Isolated mocked effect failed; no real module or destination was invoked: " + (exception.InnerException?.Message ?? exception.Message), exception);
        }
    }

    private static IntegrationPayload ReadPayload(NativeFlowTestPayload payload)
    {
        if (payload.Name is null || payload.Content is null || payload.ContentType is null || payload.Metadata is null
            || payload.Name.Length is 0 or > 256 || payload.ContentType.Length is 0 or > 256 || payload.Content.Length > 1_000_000
            || payload.Metadata.Count > 128 || payload.Metadata.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 || pair.Value is null || pair.Value.Length > 2048))
            throw new ArgumentException("Synthetic payload needs a bounded name, content type, text (up to 1,000,000 characters) and metadata.");
        return new(payload.Name, BinaryData.FromString(payload.Content), payload.ContentType, new Dictionary<string, string>(payload.Metadata, StringComparer.Ordinal));
    }

    private static List<NativeFlowTestPayload> Capture(IntegrationBatch batch)
        => batch.Payloads.Select(payload => new NativeFlowTestPayload(payload.Name, payload.GetText(), payload.ContentType,
            new Dictionary<string, string>(payload.Metadata, StringComparer.Ordinal))).ToList();

    private sealed class MockInvoker(Dictionary<(string Kind, string Module), NativeFlowTestPayload?> mocks)
        : IAugmentModuleInvoker, IRenderModuleInvoker, IDeliverModuleInvoker, IModuleInvoker
    {
        public List<NativeFlowTestCall> Calls { get; } = [];
        private long _capturedBytes;
        private Task<IntegrationBatch> Invoke(string kind, string module, IntegrationBatch input, CancellationToken token, IReadOnlyDictionary<string, string>? settings = null)
        {
            token.ThrowIfCancellationRequested();
            if (Calls.Count >= 128) throw new InvalidOperationException("Isolated native test exceeds 128 mock calls.");
            if (!mocks.TryGetValue((kind, module), out var output)) throw new InvalidOperationException($"No explicit {kind} mock for module '{module}'. No real module was invoked.");
            _capturedBytes += input.Payloads.Sum(payload => (long)payload.Content.ToMemory().Length);
            if (_capturedBytes > 4_000_000) throw new InvalidOperationException("Isolated native test captured inputs exceed 4,000,000 bytes.");
            Calls.Add(new(kind, module, Capture(input), settings is null ? null : Redact(settings)));
            return Task.FromResult(output is null ? IntegrationBatch.Empty : new IntegrationBatch([ReadPayload(output)]));
        }
        private static Dictionary<string, string> Redact(IReadOnlyDictionary<string, string> settings)
            => settings.ToDictionary(pair => pair.Key, pair => IsSensitive(pair.Key) ? "***" : pair.Value, StringComparer.OrdinalIgnoreCase);
        private static bool IsSensitive(string key) => new[] { "password", "secret", "token", "key", "credential", "passphrase" }
            .Any(word => key.Contains(word, StringComparison.OrdinalIgnoreCase));
        public Task<JsonNode?> InvokeTransformAsync(string id, FlowExecutionContext context, IReadOnlyList<JsonNode?> inputs, IReadOnlyDictionary<string, string> settings, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Calls.Count >= 128) throw new InvalidOperationException("Isolated native test exceeds 128 mock calls.");
            if (!mocks.TryGetValue(("Transform", id), out var output) || output is null) throw new InvalidOperationException($"No explicit Transform mock for module '{id}'. No real module was invoked.");
            Calls.Add(new("Transform", id, inputs.Select((value, index) => new NativeFlowTestPayload("argument-" + (index + 1), value?.ToJsonString() ?? "null", "application/json", new Dictionary<string, string>())).ToList()));
            return Task.FromResult<JsonNode?>(JsonValue.Create(output.Content));
        }
        public Task<IntegrationBatch> AugmentAsync(string id, FlowExecutionContext context, IntegrationBatch input, IReadOnlyDictionary<string, string> settings, CancellationToken token) => Invoke("Augment", id, input, token);
        public Task<IntegrationBatch> RenderAsync(string id, FlowExecutionContext context, IntegrationBatch input, IReadOnlyDictionary<string, string> settings, CancellationToken token) => Invoke("Render", id, input, token);
        public Task<IntegrationBatch> DeliverAsync(string id, FlowExecutionContext context, IntegrationBatch input, IReadOnlyDictionary<string, string> settings, CancellationToken token) => Invoke("Deliver", id, input, token, settings);
    }

    private sealed class VirtualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryStore : IFlowOrchestrationStateStore
    {
        private readonly Dictionary<(string Flow, string Key), FlowOrchestrationCheckpoint> _items = new();
        public Task<FlowOrchestrationCheckpoint?> GetAsync(string flowId, string correlationKey, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(_items.GetValueOrDefault((flowId, correlationKey))); }
        public Task<IReadOnlyList<FlowOrchestrationCheckpoint>> GetAllAsync(string flowId, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<FlowOrchestrationCheckpoint>>(_items.Values.Where(item => item.FlowId == flowId).ToArray()); }
        public Task<IReadOnlyList<FetchProcessingReceipt>> GetPendingSourceReceiptsAsync(string flowId, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<FetchProcessingReceipt>>([]); }
        public Task UpsertAsync(FlowOrchestrationCheckpoint checkpoint, CancellationToken token)
        { token.ThrowIfCancellationRequested(); _items[(checkpoint.FlowId, checkpoint.CorrelationKey)] = checkpoint; return Task.CompletedTask; }
        public Task DeleteAsync(string flowId, string correlationKey, CancellationToken token)
        { token.ThrowIfCancellationRequested(); _items.Remove((flowId, correlationKey)); return Task.CompletedTask; }
    }
}
