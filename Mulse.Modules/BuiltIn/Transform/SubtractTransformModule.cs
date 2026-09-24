using System.Text.Json.Nodes;

namespace Mulse.Modules.BuiltIn.Transform;

/// <summary>
/// Subtracts the sum of every remaining input from the first input. Requires at least one input; a single input
/// is returned as-is (as a number, not negated). Returns <c>null</c> when the first input isn't numeric (there is
/// no meaningful value to subtract from); null or non-numeric remaining inputs count as 0, like
/// <see cref="AddTransformModule"/>.
/// </summary>
public sealed class SubtractTransformModule : ITransformModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        "subtract-transform",
        "Subtract numbers",
        ModuleKind.Transform,
        "Returns the first input minus every remaining input. A single input is returned as-is. Produces no value if the first input isn't numeric; other missing or non-numeric inputs count as 0.");

    public Task<JsonNode?> InvokeAsync(
        FlowExecutionContext context,
        IReadOnlyList<JsonNode?> inputs,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TransformValues.RequireAtLeast(inputs, 1, Descriptor.Id);

        var first = TransformValues.ToNumber(inputs[0]);
        if (first is null)
        {
            return Task.FromResult<JsonNode?>(null);
        }

        var result = first.Value - inputs.Skip(1).Sum(static input => TransformValues.ToNumber(input) ?? 0m);
        return Task.FromResult<JsonNode?>(TransformValues.CreateNumber(result));
    }
}