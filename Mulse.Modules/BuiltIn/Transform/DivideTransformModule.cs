using System.Text.Json.Nodes;

namespace Mulse.Modules.BuiltIn.Transform;

/// <summary>
/// Divides the first input by each remaining input in turn. Requires at least one input; a single input is
/// returned as-is (as a number). Returns <c>null</c> - which makes a field mapping skip its target field, the
/// same way an unresolved source path does - when any input is null/non-numeric or when dividing by zero, rather
/// than producing NaN/Infinity or failing the whole flow.
/// </summary>
public sealed class DivideTransformModule : ITransformModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        "divide-transform",
        "Divide numbers",
        ModuleKind.Transform,
        "Divides the first input by each remaining input in turn. A single input is returned as-is. Produces no value on division by zero or when any input is missing or non-numeric.");

    public Task<JsonNode?> InvokeAsync(
        FlowExecutionContext context,
        IReadOnlyList<JsonNode?> inputs,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TransformValues.RequireAtLeast(inputs, 1, Descriptor.Id);

        var result = TransformValues.ToNumber(inputs[0]);
        if (result is null)
        {
            return Task.FromResult<JsonNode?>(null);
        }

        foreach (var input in inputs.Skip(1))
        {
            var divisor = TransformValues.ToNumber(input);
            if (divisor is null || divisor.Value == 0m)
            {
                return Task.FromResult<JsonNode?>(null);
            }

            result /= divisor.Value;
        }

        return Task.FromResult<JsonNode?>(TransformValues.CreateNumber(result.Value));
    }
}