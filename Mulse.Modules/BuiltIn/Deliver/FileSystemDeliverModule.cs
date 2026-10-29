using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Mulse.Modules.BuiltIn.Deliver;

public sealed class FileSystemDeliverModule(ILogger<FileSystemDeliverModule> logger, TimeProvider timeProvider) : IDeliverModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("path", "Path", "The local directory where rendered payloads should be written.", true),
        new("namingMode", "File naming", "Choose how output filenames are generated. Extensions are always appended after the generated name.", false, ModuleSettingInputKind.Select, "Execution",
            [new("Execution", "Execution-based (original behavior)"), new("Numeric", "Base name + number"),
             new("Random", "Base name + random hash"), new("Fixed", "Fixed name"), new("FixedDate", "Fixed name + date")]),
        new("fileName", "File name", "Required for Fixed and FixedDate. Optional base name for Numeric and Random; otherwise the payload name is used. May include an extension, but not a directory.", false),
        new("dateFormat", "Date format (UTC)", "For FixedDate: .NET date format appended before the extension. Must produce a valid filename; do not use slashes or colons.", false, ModuleSettingInputKind.Text, "yyyyMMdd-HHmmssfff"),
        new("overwriteExisting", "Overwrite existing", "Replace an existing target file. When off, a collision fails explicitly. Numeric always selects the first available positive counter and never overwrites. Multiple payloads with the same fixed name overwrite each other when enabled.", false, ModuleSettingInputKind.Boolean, "false"),
        new("extension", "Extension override", "Optional file extension override such as .json or .xml (the leading dot is optional). Otherwise use the configured filename's extension, then the payload extension, then .bin.", false, ModuleSettingInputKind.Text)
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Xml, ModuleDataFormat.Csv, ModuleDataFormat.Text],
        [ModuleProtocol.FileSystem],
        [ModuleCapability.Storage, ModuleCapability.Delivery]);

    public ModuleDescriptor Descriptor { get; } = new(
        "file-system-deliver",
        "File system deliver",
        ModuleKind.Deliver,
        "Writes rendered payloads to a target directory for archiving or downstream pickup.",
        SettingDescriptors,
        Recommendation);

    public async Task DeliverAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetFullPath(ModuleSettingReader.GetRequired(step.Settings, "path", Descriptor.Id));
        var mode = ModuleSettingReader.GetOptional(step.Settings, "namingMode") ?? "Execution";
        if (!new[] { "Execution", "Numeric", "Random", "Fixed", "FixedDate" }.Contains(mode, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Module '{Descriptor.Id}' has unsupported namingMode '{mode}'.");
        var configuredName = ModuleSettingReader.GetOptional(step.Settings, "fileName");
        var configuredExtension = ModuleSettingReader.GetOptional(step.Settings, "extension");
        var overwrite = ModuleSettingReader.GetBoolean(step.Settings, "overwriteExisting", false, Descriptor.Id);
        var fixedName = mode.Equals("Fixed", StringComparison.OrdinalIgnoreCase);
        var datedName = mode.Equals("FixedDate", StringComparison.OrdinalIgnoreCase);
        var numericName = mode.Equals("Numeric", StringComparison.OrdinalIgnoreCase);
        if ((fixedName || datedName) && configuredName is null)
            throw new ArgumentException($"Module '{Descriptor.Id}' requires fileName for {mode} naming.");
        if (configuredName is not null) ValidateFileName(configuredName);
        var date = datedName
            ? timeProvider.GetUtcNow().ToString(ModuleSettingReader.GetOptional(step.Settings, "dateFormat") ?? "yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)
            : null;
        if (date is not null) ValidateFileName(date);
        if (configuredExtension is not null)
        {
            configuredExtension = configuredExtension.StartsWith('.') ? configuredExtension : "." + configuredExtension;
            ValidateFileName("output" + configuredExtension);
        }

        Directory.CreateDirectory(directory);
        for (var index = 0; index < batch.Payloads.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = batch.Payloads[index];
            var executionName = mode.Equals("Execution", StringComparison.OrdinalIgnoreCase);
            var sourceName = executionName ? payload.Name : configuredName ?? payload.Name;
            var baseName = Path.GetFileNameWithoutExtension(sourceName);
            ValidateFileName(baseName);
            var extension = configuredExtension ?? Path.GetExtension(sourceName);
            if (string.IsNullOrEmpty(extension)) extension = Path.GetExtension(payload.Name);
            if (string.IsNullOrEmpty(extension)) extension = ".bin";
            ValidateFileName(baseName + extension);

            var stem = executionName
                ? $"{baseName}-{context.ExecutionId}-{timeProvider.GetUtcNow().ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)}-{index}"
                : numericName ? baseName
                : fixedName ? baseName
                : datedName ? $"{baseName}-{date}"
                : $"{baseName}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";
            ValidateFileName(stem + extension);
            var (targetPath, stream) = OpenTarget(directory, stem, extension, numericName, overwrite, cancellationToken);
            await using (stream)
            {
                await stream.WriteAsync(payload.Content.ToMemory(), cancellationToken).ConfigureAwait(false);
            }

            logger.LogInformation(
                "Delivered payload {PayloadName} for flow {FlowId} execution {ExecutionId} to {TargetPath} using module {ModuleId}",
                payload.Name,
                context.FlowId,
                context.ExecutionId,
                targetPath,
                Descriptor.Id);
        }
    }

    private static (string Path, FileStream Stream) OpenTarget(string directory, string stem, string extension, bool numeric, bool overwrite, CancellationToken cancellationToken)
    {
        for (long number = 1; ; number = checked(number + 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(directory, numeric ? $"{stem}-{number.ToString(CultureInfo.InvariantCulture)}{extension}" : stem + extension);
            try
            {
                return (target, new FileStream(target, numeric || !overwrite ? FileMode.CreateNew : FileMode.Create,
                    FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous));
            }
            catch (IOException exception) when (numeric && ((exception.HResult & 0xFFFF) is 17 or 80 or 183) && File.Exists(target))
            {
                // CreateNew reserves the filename atomically, including between concurrent deliveries.
            }
        }
    }

    private static void ValidateFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/') || name.Contains('\\') || name.Contains(':')
            || name.EndsWith('.') || name.EndsWith(' '))
            throw new ArgumentException($"Invalid output filename or date fragment '{name}'. Use a filename without directory separators or invalid characters.");
    }
}
