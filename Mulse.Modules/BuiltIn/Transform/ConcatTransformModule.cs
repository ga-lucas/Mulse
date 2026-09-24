using System.Text.Json.Nodes;

namespace Mulse.Modules.BuiltIn.Transform;

/// <summary>
/// Joins the string representation of every input, in order, with an optional <c>separator</c>. Accepts any
/// number of inputs (zero inputs yield an empty string). Null (missing) inputs are skipped entirely when
/// <c>skipNulls</c> is true (the default), so they don't produce doubled separators; otherwise they contribute an
/// empty string.
/// </summary>
public sealed class ConcatTransformModule : ITransformModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("separator", "Separator", "Text inserted between each input value, e.g. ' ' or ', '. Leave blank to join the values directly.", false, ModuleSettingInputKind.Text, ""),
        new("skipNulls", "Skip missing values", "Skips inputs that resolved to no value instead of treating them as empty text.", false, ModuleSettingInputKind.Boolean, "true")
    ];

    public ModuleDescriptor Descriptor { get; } = new(
        "concat-transform",
        "Concatenate text",
        ModuleKind.Transform,
        "Joins all inputs as text, in order, with an optional separator. Accepts any number of inputs.",
        SettingDescriptors);

    public Task<JsonNode?> InvokeAsync(
        FlowExecutionContext context,
        IReadOnlyList<JsonNode?> inputs,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Read the separator raw (not via GetOptional) so whitespace-only separators such as " " are honored.
        var separator = step.Settings.TryGetValue("separator", out var configuredSeparator) ? configuredSeparator ?? string.Empty : string.Empty;
        var skipNulls = ModuleSettingReader.GetBoolean(step.Settings, "skipNulls", defaultValue: true, Descriptor.Id);

        var parts = inputs
            .Where(input => !skipNulls || input is not null)
            .Select(static input => TransformValues.ToText(input) ?? string.Empty);

        return Task.FromResult<JsonNode?>(TransformValues.CreateText(string.Join(separator, parts)));
    }
}