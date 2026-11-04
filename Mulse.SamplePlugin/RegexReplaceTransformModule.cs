using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mulse.Modules;

namespace Mulse.SamplePlugin;

public sealed class RegexReplaceTransformModule : ITransformModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        "regex-replace-transform", "Replace text by pattern", ModuleKind.Transform,
        "Replaces matches in one text input using a bounded regular expression. Supplied by a hot-loadable plugin.",
        [
            new("pattern", "Pattern", "Regular expression to match. Each match search is limited to 250 milliseconds.", true),
            new("replacement", "Replacement", "Literal replacement text (no capture substitution), at most 4096 characters.", false, ModuleSettingInputKind.Text, "")
        ]);

    public Task<JsonNode?> InvokeAsync(FlowExecutionContext context, IReadOnlyList<JsonNode?> inputs,
        ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (inputs.Count != 1 || inputs[0] is not JsonValue value || !value.TryGetValue<string>(out var text))
            throw new ArgumentException($"Module '{Descriptor.Id}' requires exactly one text input.");
        var pattern = ModuleSettingReader.GetRequired(step.Settings, "pattern", Descriptor.Id);
        if (text.Length > 1_000_000 || pattern.Length > 4096)
            throw new ArgumentException($"Module '{Descriptor.Id}' limits input to 1000000 characters and patterns to 4096 characters.");
        var replacement = step.Settings.GetValueOrDefault("replacement", "");
        if (replacement.Length > 4096) throw new ArgumentException($"Module '{Descriptor.Id}' replacement exceeds 4096 characters.");
        var outputLength = text.Length;
        var regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        var result = regex.Replace(text, match =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            outputLength += replacement.Length - match.Length;
            if (outputLength > 4_000_000) throw new ArgumentException($"Module '{Descriptor.Id}' output exceeds 4000000 characters.");
            return replacement;
        });
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<JsonNode?>(JsonValue.Create(result));
    }
}
