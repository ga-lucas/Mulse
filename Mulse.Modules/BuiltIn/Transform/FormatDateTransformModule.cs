using System.Globalization;
using System.Text.Json.Nodes;

namespace Mulse.Modules.BuiltIn.Transform;

/// <summary>
/// Parses a single input as a date/time (culture-invariant; values without an explicit offset are treated as UTC
/// so results don't depend on the host's time zone) and formats it with the <c>outputFormat</c> standard or
/// custom .NET date format string, e.g. <c>yyyy-MM-dd</c>. Returns <c>null</c> when the input is null or can't be
/// parsed as a date.
/// </summary>
public sealed class FormatDateTransformModule : ITransformModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("outputFormat", "Output format", "A .NET date format string, e.g. 'yyyy-MM-dd', 'dd/MM/yyyy HH:mm' or 'O' (ISO 8601).", true, ModuleSettingInputKind.Text, "yyyy-MM-dd")
    ];

    public ModuleDescriptor Descriptor { get; } = new(
        "format-date-transform",
        "Format date",
        ModuleKind.Transform,
        "Parses a single input as a date/time (culture-invariant, UTC when no offset is given) and formats it with a .NET date format string. Produces no value if the input isn't a date.",
        SettingDescriptors);

    public Task<JsonNode?> InvokeAsync(
        FlowExecutionContext context,
        IReadOnlyList<JsonNode?> inputs,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TransformValues.RequireExactly(inputs, 1, Descriptor.Id);

        var outputFormat = ModuleSettingReader.GetRequired(step.Settings, "outputFormat", Descriptor.Id);

        var text = TransformValues.ToText(inputs[0]);
        if (text is null
            || !DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
        {
            return Task.FromResult<JsonNode?>(null);
        }

        try
        {
            return Task.FromResult<JsonNode?>(TransformValues.CreateText(value.ToString(outputFormat, CultureInfo.InvariantCulture)));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                $"Module '{Descriptor.Id}' has an invalid date format for 'outputFormat': '{outputFormat}'.",
                "outputFormat",
                exception);
        }
    }
}