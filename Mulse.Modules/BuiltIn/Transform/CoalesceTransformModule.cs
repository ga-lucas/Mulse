using System.Text.Json.Nodes;

namespace Mulse.Modules.BuiltIn.Transform;

/// <summary>
/// Returns (a copy of) the first input that has a value: not null and not an empty/whitespace-only JSON string.
/// Accepts any number of inputs; returns <c>null</c> if none qualifies. Non-string values (numbers, booleans,
/// objects, arrays) always qualify.
/// </summary>
public sealed class CoalesceTransformModule : ITransformModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        "coalesce-transform",
        "First non-empty value",
        ModuleKind.Transform,
        "Returns the first input that is present and not empty text. Accepts any number of inputs; produces no value if every input is missing or empty.");

    public Task<JsonNode?> InvokeAsync(
        FlowExecutionContext context,
        IReadOnlyList<JsonNode?> inputs,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var input in inputs)
        {
            if (input is null)
            {
                continue;
            }

            if (input is JsonValue value && value.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            return Task.FromResult<JsonNode?>(input.DeepClone());
        }

        return Task.FromResult<JsonNode?>(null);
    }
}