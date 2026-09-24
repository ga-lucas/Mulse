using System.Text.Json.Nodes;

namespace Mulse.Modules.Abstractions;

/// <summary>
/// Lets one module invoke a different kind of module (currently a <see cref="ITransformModule"/>) as part of its
/// own execution, without depending on the host's module catalog/lease types. The host registers an implementation
/// in DI, so modules can simply take it as a constructor dependency.
/// </summary>
public interface IModuleInvoker
{
    /// <summary>
    /// Leases the transform module <paramref name="moduleId"/>, invokes it with <paramref name="inputs"/> and
    /// <paramref name="settings"/>, and releases it again.
    /// </summary>
    Task<JsonNode?> InvokeTransformAsync(
        string moduleId,
        FlowExecutionContext context,
        IReadOnlyList<JsonNode?> inputs,
        IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellationToken);
}
