using System.Text.Json.Nodes;

namespace Mulse.Modules.BuiltIn.Transform;

/// <summary>
/// Extracts part of a single text input, starting at the zero-based <c>start</c> index and spanning
/// <c>length</c> characters (or to the end of the text when <c>length</c> is blank). Out-of-range values are
/// clamped rather than rejected: a negative start is treated as 0, a start past the end yields an empty string,
/// and a length running past the end stops at the end. Returns <c>null</c> for a null input.
/// </summary>
public sealed class SubstringTransformModule : ITransformModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("start", "Start index", "Zero-based index of the first character to keep.", true, ModuleSettingInputKind.Number, "0"),
        new("length", "Length", "How many characters to keep. Leave blank to keep everything to the end of the text.", false, ModuleSettingInputKind.Number)
    ];

    public ModuleDescriptor Descriptor { get; } = new(
        "substring-transform",
        "Substring",
        ModuleKind.Transform,
        "Extracts part of a single text input by start index and optional length. Out-of-range values are clamped.",
        SettingDescriptors);

    public Task<JsonNode?> InvokeAsync(
        FlowExecutionContext context,
        IReadOnlyList<JsonNode?> inputs,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TransformValues.RequireExactly(inputs, 1, Descriptor.Id);

        var start = TransformValues.GetRequiredInt32(step.Settings, "start", Descriptor.Id);
        var length = TransformValues.GetOptionalInt32(step.Settings, "length", Descriptor.Id);

        var text = TransformValues.ToText(inputs[0]);
        if (text is null)
        {
            return Task.FromResult<JsonNode?>(null);
        }

        var clampedStart = Math.Clamp(start, 0, text.Length);
        var available = text.Length - clampedStart;
        var clampedLength = length is null ? available : Math.Clamp(length.Value, 0, available);

        return Task.FromResult<JsonNode?>(TransformValues.CreateText(text.Substring(clampedStart, clampedLength)));
    }
}