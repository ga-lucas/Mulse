using System.Text.Json.Nodes;

namespace Mulse.Modules.BuiltIn.Transform;

/// <summary>
/// Rounds a single numeric input to <c>decimals</c> fractional digits (default 0, allowed range 0-28), with
/// midpoints rounded away from zero (so 2.5 becomes 3 and -2.5 becomes -3). Returns <c>null</c> when the input
/// is null or non-numeric.
/// </summary>
public sealed class RoundTransformModule : ITransformModule
{
    private const int MaxDecimals = 28;

    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("decimals", "Decimal places", "How many fractional digits to keep (0-28). Midpoints round away from zero.", false, ModuleSettingInputKind.Number, "0")
    ];

    public ModuleDescriptor Descriptor { get; } = new(
        "round-transform",
        "Round number",
        ModuleKind.Transform,
        "Rounds a single numeric input to a number of decimal places (midpoints round away from zero). Produces no value if the input isn't numeric.",
        SettingDescriptors);

    public Task<JsonNode?> InvokeAsync(
        FlowExecutionContext context,
        IReadOnlyList<JsonNode?> inputs,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TransformValues.RequireExactly(inputs, 1, Descriptor.Id);

        var decimals = ModuleSettingReader.GetInt32(step.Settings, "decimals", defaultValue: 0, Descriptor.Id);
        if (decimals is < 0 or > MaxDecimals)
        {
            throw new ArgumentException(
                $"Module '{Descriptor.Id}' requires the 'decimals' setting to be between 0 and {MaxDecimals}.",
                "decimals");
        }

        var number = TransformValues.ToNumber(inputs[0]);
        return Task.FromResult(number is null
            ? null
            : TransformValues.CreateNumber(Math.Round(number.Value, decimals, MidpointRounding.AwayFromZero)));
    }
}