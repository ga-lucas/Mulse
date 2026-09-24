using System.Text.Json.Nodes;

namespace Mulse.Modules.BuiltIn.Transform;

/// <summary>Converts a single text input to lower case using the invariant culture. Returns <c>null</c> for a null input.</summary>
public sealed class LowerCaseTransformModule : ITransformModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        "lowercase-transform",
        "Lower case",
        ModuleKind.Transform,
        "Converts a single text input to lower case (culture-invariant).");

    public Task<JsonNode?> InvokeAsync(
        FlowExecutionContext context,
        IReadOnlyList<JsonNode?> inputs,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TransformValues.RequireExactly(inputs, 1, Descriptor.Id);

        var text = TransformValues.ToText(inputs[0]);
        return Task.FromResult(text is null ? null : TransformValues.CreateText(text.ToLowerInvariant()));
    }
}