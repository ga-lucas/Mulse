using System.Text.Json.Nodes;

namespace Mulse.Modules.Abstractions;

/// <summary>
/// A lightweight, hot-loadable value function used by field mappings (e.g. inside a join/map augment) to derive
/// one output value from N input values - simple math, string concatenation, combining fields, etc. Unlike the
/// other module kinds, a transform never appears as its own node on a flow's graph and is never leased/invoked
/// directly by FlowRuntime; it's invoked by another module (via <see cref="IModuleInvoker"/>) as part of that
/// module's own execution.
/// </summary>
public interface ITransformModule : IModule
{
    /// <summary>
    /// Computes one output value from the ordered <paramref name="inputs"/>. Returning <c>null</c> means "no value"
    /// (callers such as field mappings then skip writing the target field).
    /// </summary>
    Task<JsonNode?> InvokeAsync(
        FlowExecutionContext context,
        IReadOnlyList<JsonNode?> inputs,
        ModuleStepDefinition step,
        CancellationToken cancellationToken);
}
