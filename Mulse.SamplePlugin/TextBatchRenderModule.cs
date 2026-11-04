using System.Globalization;
using System.Text;
using Mulse.Modules;

namespace Mulse.SamplePlugin;

public sealed class TextBatchRenderModule : IRenderModule
{
    private const int MaxCharacters = 4000000;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public ModuleDescriptor Descriptor { get; } = new(
        "sample-text-batch-render", "Combine UTF-8 text with header / trailer", ModuleKind.Render,
        "Combines the current input batch into one UTF-8 text document. No cross-run buffering or automatic counting/totals in the header/trailer. Output retains only metadata shared identically by all inputs.",
        [
            new("header", "Header", "Literal text before the first body; include any required line ending. At most 65536 characters.", false, ModuleSettingInputKind.TextArea, ""),
            new("separator", "Body separator", "Literal text between bodies, not after the last body. At most 65536 characters.", false, ModuleSettingInputKind.TextArea, "\n"),
            new("trailer", "Trailer", "Literal text after the last body; include any required line ending. At most 65536 characters.", false, ModuleSettingInputKind.TextArea, ""),
            new("outputFileName", "Output filename", "Optional single filename. Default: first input filename with .batch.txt extension.", false)
        ]);

    public Task<IntegrationBatch> RenderAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var header = step.Settings.GetValueOrDefault("header", "");
        var separator = step.Settings.GetValueOrDefault("separator", "\n");
        var trailer = step.Settings.GetValueOrDefault("trailer", "");
        if (header.Length > 65536 || separator.Length > 65536 || trailer.Length > 65536)
            throw new ArgumentException($"Module '{Descriptor.Id}' limits each header, separator and trailer to 65536 characters.");
        if (batch.Count == 0) return Task.FromResult(IntegrationBatch.Empty);
        var name = ModuleSettingReader.GetOptional(step.Settings, "outputFileName") ?? Path.ChangeExtension(Path.GetFileName(batch.Payloads[0].Name), ".batch.txt");
        if (name.Length > 256 || name is "." or ".." || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Module '{Descriptor.Id}' requires a single output filename of at most 256 characters.");
        var output = new StringBuilder(header);
        for (var index = 0; index < batch.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = batch.Payloads[index].Content.ToMemory();
            var separatorLength = index == 0 ? 0 : separator.Length;
            if (bytes.Length > MaxCharacters * 4 || output.Length + (long)separatorLength + Utf8.GetCharCount(bytes.Span) + trailer.Length > MaxCharacters)
                throw new ArgumentException($"Module '{Descriptor.Id}' output exceeds {MaxCharacters} characters.");
            if (index > 0) output.Append(separator);
            output.Append(Utf8.GetString(bytes.Span));
        }
        output.Append(trailer);
        var first = batch.Payloads[0];
        var metadata = first.Metadata.Where(entry => batch.Payloads.All(payload => payload.Metadata.TryGetValue(entry.Key, out var value) && value == entry.Value))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        metadata["renderedBy"] = Descriptor.Id;
        metadata["batch.count"] = batch.Count.ToString(CultureInfo.InvariantCulture);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new IntegrationPayload(name, BinaryData.FromBytes(Utf8.GetBytes(output.ToString())), "text/plain; charset=utf-8", metadata);
        return Task.FromResult(new IntegrationBatch([result]));
    }
}
