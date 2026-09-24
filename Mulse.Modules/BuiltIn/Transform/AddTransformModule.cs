using System.Text.Json.Nodes;

namespace Mulse.Modules.BuiltIn.Transform;

/// <summary>
/// Sums every input as a number. Accepts any number of inputs; null or non-numeric inputs count as 0, so zero
/// inputs yield 0. Numeric strings (e.g. <c>"12.5"</c>) are parsed using the invariant culture.
/// </summary>
public sealed class AddTransformModule : ITransformModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        "add-transform",
        "Add numbers",
        ModuleKind.Transform,
        "Sums all inputs as numbers. Accepts any number of inputs; missing or non-numeric inputs count as 0.");

    public Task<JsonNode?> InvokeAsync(
        FlowExecutionContext context,
        IReadOnlyList<JsonNode?> inputs,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var total = inputs.Sum(static input => TransformValues.ToNumber(input) ?? 0m);
        return Task.FromResult<JsonNode?>(TransformValues.CreateNumber(total));
    }
}