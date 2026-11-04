using System.Text.Json.Nodes;
using Mulse.Modules.Execution;
using Mulse.Modules.Payloads;
using Mulse.Modules.Pipeline;

namespace Mulse.Modules.Orchestration;

public sealed class OdxExecutionFrame(OdxProgram program, FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step)
{
    public OdxProgram Program { get; } = program;
    public FlowExecutionContext Context { get; } = context;
    public ModuleStepDefinition Step { get; } = step;
    public IntegrationBatch Batch { get; set; } = batch;
    public Dictionary<string, IntegrationBatch> Messages { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, JsonNode?> Variables { get; } = new(StringComparer.Ordinal);
    public Exception? CurrentException { get; internal set; }
}

public delegate Task OdxActivity(OdxNode node, OdxExecutionFrame frame, CancellationToken cancellationToken);
public delegate Task<bool> OdxCondition(OdxNode node, OdxExecutionFrame frame, CancellationToken cancellationToken);
public delegate Task OdxTransactionScope(OdxNode node, OdxExecutionFrame frame, Func<CancellationToken, Task> body, CancellationToken cancellationToken);

public sealed class OdxExecutionBindings
{
    public Dictionary<int, OdxActivity> Activities { get; } = [];
    public Dictionary<int, OdxCondition> Conditions { get; } = [];
    public Dictionary<int, OdxTransactionScope> Transactions { get; } = [];
}

public sealed class OdxExecutionLimitException(string message) : InvalidOperationException(message);
