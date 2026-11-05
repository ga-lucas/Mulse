using Mulse.Modules.ControlFlow;
using Mulse.Modules.Decisions;

namespace Mulse.Modules.BuiltIn.Augment;

public sealed class NativeControlFlowAugmentModule(IAugmentModuleInvoker augments, IRenderModuleInvoker renderers,
    IFlowOrchestrationStateStore? store = null, TimeProvider? clock = null, IDeliverModuleInvoker? deliveries = null) : IOrchestrationAugmentModule
{
    public ModuleDescriptor Descriptor { get; } = new(NativeFlowDefinition.ModuleId, "Native control flow", ModuleKind.OrchestrationAugment,
        "Source-independent sequences, branches and bounded loops; optional persisted waits, correlated receives, named messages and replies. Ambiguous external effects require reconciliation. Transactions are unsupported.",
        [new("flowJson", "Native control flow", "Version-1 native nodes. durable=true requires a state store and stable correlation/message ID metadata. Pending definitions are pinned; synchronous blocks retain whole-block retry behavior.", true, ModuleSettingInputKind.TextArea, "{\"version\":1,\"nodes\":[]}")]);

    public async Task<IntegrationBatch> AugmentAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        var definition = NativeFlowDefinition.Read(ModuleSettingReader.GetRequired(step.Settings, "flowJson", Descriptor.Id));
        if (definition.Durable)
            return await new NativeDurableExecutor(store ?? throw new NotSupportedException("Native durable control flow requires a configured state store."),
                clock ?? TimeProvider.System, augments, renderers, deliveries).ExecuteAsync(definition, context, batch, step, cancellationToken).ConfigureAwait(false);
        var executed = 0;
        var variables = new Dictionary<string, NativeScalar>(StringComparer.Ordinal);
        var variableTypes = new Dictionary<string, NativeScalarType>(StringComparer.Ordinal);
        NativeScalar Fact(NativeMessageFact fact, IntegrationBatch input) => fact.MessageName.Length == 0 ? fact.Read(input)
            : throw new InvalidOperationException("Named message facts require durable execution; no working-message substitution was made.");
        bool Matches(NativeFlowNode node, IntegrationBatch input)
        {
            if (node.Expression is not null) return NativeExpression.Boolean(node.Expression.Evaluate(variables, fact => Fact(fact, input)).Read());
            if (input.Count != 1) throw new InvalidOperationException($"Native condition '{node.Id}' requires exactly one payload. Use For each payload for a batch.");
            return DecisionRuntime.MatchesAll(input.Payloads[0], node.Conditions, node.Id);
        }
        async Task<IntegrationBatch> Run(IReadOnlyList<NativeFlowNode> nodes, IntegrationBatch input)
        {
            foreach (var node in nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++executed > 100000) throw new InvalidOperationException("Native control flow exceeded 100000 node executions.");
                switch (node.Kind)
                {
                    case NativeFlowNodeKind.DeclareVariable:
                    case NativeFlowNodeKind.SetVariable:
                        NativeVariables.Apply(node, variables, variableTypes, fact => Fact(fact, input));
                        break;
                    case NativeFlowNodeKind.Sequence:
                        input = await Run(node.Children, input).ConfigureAwait(false);
                        break;
                    case NativeFlowNodeKind.If:
                        input = await Run(Matches(node, input) ? node.Children : node.Otherwise, input).ConfigureAwait(false);
                        break;
                    case NativeFlowNodeKind.ForEach:
                        if (input.Count > node.MaxIterations) throw new InvalidOperationException($"Native loop '{node.Id}' exceeds its payload iteration limit.");
                        var outputs = new List<IntegrationPayload>();
                        foreach (var payload in input.Payloads)
                            outputs.AddRange((await Run(node.Children, new IntegrationBatch([payload])).ConfigureAwait(false)).Payloads);
                        input = new IntegrationBatch(outputs);
                        break;
                    case NativeFlowNodeKind.While:
                        var count = 0;
                        while (Matches(node, input))
                        {
                            if (++count > node.MaxIterations) throw new InvalidOperationException($"Native loop '{node.Id}' exceeded its iteration limit.");
                            input = await Run(node.Children, input).ConfigureAwait(false);
                        }
                        break;
                    case NativeFlowNodeKind.Augment:
                        input = await augments.AugmentAsync(node.Module, context, input, node.Settings, cancellationToken).ConfigureAwait(false);
                        break;
                    case NativeFlowNodeKind.Render:
                        input = await renderers.RenderAsync(node.Module, context, input, node.Settings, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new NotSupportedException($"Native node '{node.Id}' is not executable.");
                }
                if (context.Disposition != FlowExecutionDisposition.Continue)
                    throw new InvalidOperationException($"Native module node '{node.Id}' attempted to suspend a synchronous control-flow block. Use top-level durable nodes instead.");
            }
            return input;
        }
        return await Run(definition.Nodes, batch).ConfigureAwait(false);
    }
}
