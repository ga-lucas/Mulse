using System.Text.Json.Nodes;

namespace Mulse.Modules.BuiltIn.Transform;

/// <summary>Removes leading and trailing whitespace from a single text input. Returns <c>null</c> for a null input.</summary>
public sealed class TrimTransformModule : ITransformModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        "trim-transform",
        "Trim whitespace",
        ModuleKind.Transform,
        "Removes leading and trailing whitespace from a single text input.");

    public Task<JsonNode?> InvokeAsync(
        FlowExecutionContext context,
        IReadOnlyList<JsonNode?> inputs,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TransformValues.RequireExactly(inputs, 1, Descriptor.Id);

        var text = TransformValues.ToText(inputs[0]);
        return Task.FromResult(text is null ? null : TransformValues.CreateText(text.Trim()));
    }
}