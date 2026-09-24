using System.Text.Json.Nodes;

namespace Mulse.Modules.BuiltIn.Transform;

/// <summary>
/// Multiplies every numeric input together. Accepts any number of inputs. Null or non-numeric inputs are
/// skipped (neither treated as 0, which would silently zero the result, nor as 1, which would hide a missing
/// operand behind a plausible-looking value). Returns <c>null</c> when there are no numeric inputs at all
/// (including an empty input list).
/// </summary>
public sealed class MultiplyTransformModule : ITransformModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        "multiply-transform",
        "Multiply numbers",
        ModuleKind.Transform,
        "Multiplies all numeric inputs together. Missing or non-numeric inputs are skipped; produces no value if no input is numeric.");

    public Task<JsonNode?> InvokeAsync(
        FlowExecutionContext context,
        IReadOnlyList<JsonNode?> inputs,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        decimal? product = null;
        foreach (var input in inputs)
        {
            var number = TransformValues.ToNumber(input);
            if (number is null)
            {
                continue;
            }

            product = (product ?? 1m) * number.Value;
        }

        return Task.FromResult(product is null ? null : TransformValues.CreateNumber(product.Value));
    }
}