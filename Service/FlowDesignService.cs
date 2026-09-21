using System.Text.Json;
using System.Xml.Linq;
using Mulse.Modules;
using Service.Models;

namespace Service;

public sealed class FlowDesignService(IModuleCatalog moduleCatalog) : IFlowDesignService
{
    private const int MaxFields = 48;
    private const int MaxSampleLength = 200;
    private const int MaxPreviewLength = 1200;

    public Task<FlowDesignResponse> AnalyzeAsync(AnalyzeFlowDesignRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var text = request.TextContent?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Sample content is required.", nameof(request));
        }

        if (text.Length > 1_000_000)
        {
            throw new ArgumentException("Sample content is too large. Keep it under 1 MB.", nameof(request));
        }

        var format = DetectFormat(text, request.FileName, request.ContentType);
        var fields = ExtractFields(format, text)
            .Select(static sample => new FieldMappingSuggestionResponse(
                sample.Path,
                sample.SampleValue,
                RecommendPurpose(sample.Path),
                CreateSuggestedTargetField(sample.Path)))
            .ToArray();

        var availableModules = moduleCatalog.GetAll();
        var inputModules = CreateSuggestions(availableModules, ModuleKind.Input, format);
        var orchestrationAugmentModules = CreateSuggestions(availableModules, ModuleKind.OrchestrationAugment, format);
        var outputModules = CreateSuggestions(availableModules, ModuleKind.Output, format);

        var response = new FlowDesignResponse(
            format,
            request.FileName,
            text.Length <= MaxPreviewLength ? text : text[..MaxPreviewLength],
            inputModules,
            orchestrationAugmentModules,
            outputModules,
            fields);

        return Task.FromResult(response);
    }

    private static DetectedDataFormat DetectFormat(string text, string? fileName, string? contentType)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);
        if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith('{')
            || text.StartsWith('['))
        {
            return DetectedDataFormat.Json;
        }

        if (string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contentType, "application/xml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contentType, "text/xml", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith('<'))
        {
            return DetectedDataFormat.Xml;
        }

        var firstLine = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).FirstOrDefault() ?? string.Empty;
        if (string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase)
            || firstLine.Count(static character => character == ',') >= 1
            || firstLine.Count(static character => character == ';') >= 1
            || firstLine.Count(static character => character == '\t') >= 1)
        {
            return DetectedDataFormat.Csv;
        }

        return DetectedDataFormat.Text;
    }

    private static IReadOnlyList<FieldSample> ExtractFields(DetectedDataFormat format, string text)
    {
        return format switch
        {
            DetectedDataFormat.Json => ExtractJsonFields(text),
            DetectedDataFormat.Xml => ExtractXmlFields(text),
            DetectedDataFormat.Csv => ExtractCsvFields(text),
            _ => [new FieldSample("content", CreateSampleValue(text))]
        };
    }

    private static IReadOnlyList<FieldSample> ExtractJsonFields(string text)
    {
        using var document = JsonDocument.Parse(text);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddJsonElement(document.RootElement, "$", fields);
        return fields.Select(static entry => new FieldSample(entry.Key, entry.Value)).Take(MaxFields).ToArray();
    }

    private static void AddJsonElement(JsonElement element, string path, IDictionary<string, string> fields)
    {
        if (fields.Count >= MaxFields)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AddJsonElement(property.Value, path == "$" ? property.Name : $"{path}.{property.Name}", fields);
                    if (fields.Count >= MaxFields)
                    {
                        break;
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray().Take(3))
                {
                    AddJsonElement(item, $"{path}[]", fields);
                    if (fields.Count >= MaxFields)
                    {
                        break;
                    }
                }
                break;
            default:
                fields.TryAdd(path, CreateSampleValue(element.ToString()));
                break;
        }
    }

    private static IReadOnlyList<FieldSample> ExtractXmlFields(string text)
    {
        var document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (document.Root is not null)
        {
            AddXmlElement(document.Root, document.Root.Name.LocalName, fields);
        }

        return fields.Select(static entry => new FieldSample(entry.Key, entry.Value)).Take(MaxFields).ToArray();
    }

    private static void AddXmlElement(XElement element, string path, IDictionary<string, string> fields)
    {
        if (fields.Count >= MaxFields)
        {
            return;
        }

        foreach (var attribute in element.Attributes())
        {
            fields.TryAdd($"{path}.@{attribute.Name.LocalName}", CreateSampleValue(attribute.Value));
        }

        if (!element.Elements().Any())
        {
            fields.TryAdd(path, CreateSampleValue(element.Value));
            return;
        }

        foreach (var child in element.Elements())
        {
            AddXmlElement(child, $"{path}.{child.Name.LocalName}", fields);
            if (fields.Count >= MaxFields)
            {
                break;
            }
        }
    }

    private static IReadOnlyList<FieldSample> ExtractCsvFields(string text)
    {
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return [];
        }

        var delimiter = DetectDelimiter(lines[0]);
        var headers = lines[0].Split(delimiter).Select(static value => value.Trim()).ToArray();
        var values = lines.Length > 1 ? lines[1].Split(delimiter) : [];
        var fields = new List<FieldSample>(headers.Length);

        for (var index = 0; index < headers.Length && index < MaxFields; index++)
        {
            var header = string.IsNullOrWhiteSpace(headers[index]) ? $"Column{index + 1}" : headers[index];
            var sampleValue = index < values.Length ? values[index] : string.Empty;
            fields.Add(new FieldSample(header, CreateSampleValue(sampleValue)));
        }

        return fields;
    }

    private static char DetectDelimiter(string firstLine)
    {
        var candidates = new[] { ',', ';', '\t' };
        return candidates
            .OrderByDescending(candidate => firstLine.Count(character => character == candidate))
            .First();
    }

    private IReadOnlyList<ModuleSuggestionResponse> CreateSuggestions(IReadOnlyList<ModuleCatalogEntry> modules, ModuleKind kind, DetectedDataFormat format)
    {
        return modules
            .Where(module => module.Descriptor.Kind == kind)
            .Select(module => new ModuleSuggestionResponse(
                module.Descriptor.Id,
                module.Descriptor.DisplayName,
                module.Descriptor.Kind,
                module.Descriptor.Description,
                IsRecommended(module.Descriptor, kind, format)))
            .OrderByDescending(static module => module.IsRecommended)
            .ThenBy(static module => module.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsRecommended(ModuleDescriptor descriptor, ModuleKind kind, DetectedDataFormat format)
    {
        return kind switch
        {
            ModuleKind.Input => descriptor.Id.Contains("file-system", StringComparison.OrdinalIgnoreCase),
            ModuleKind.OrchestrationAugment when format == DetectedDataFormat.Json => descriptor.Id.Contains("json", StringComparison.OrdinalIgnoreCase) || descriptor.Id.Contains("augment", StringComparison.OrdinalIgnoreCase),
            ModuleKind.Output => descriptor.Id.Contains("storage", StringComparison.OrdinalIgnoreCase) || descriptor.Id.Contains("logging", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static FieldMappingPurpose RecommendPurpose(string path)
    {
        var normalized = path.ToLowerInvariant();
        if (normalized.Contains("id") || normalized.Contains("key") || normalized.Contains("code"))
        {
            return FieldMappingPurpose.Merge;
        }

        if (normalized.Contains("date") || normalized.Contains("time") || normalized.Contains("status") || normalized.Contains("priority") || normalized.Contains("type"))
        {
            return FieldMappingPurpose.Orchestration;
        }

        return FieldMappingPurpose.Output;
    }

    private static string CreateSuggestedTargetField(string path)
    {
        var segments = path.Split(['.', '[', ']', '@'], StringSplitOptions.RemoveEmptyEntries);
        var lastSegment = segments.LastOrDefault(static segment => segment != "$") ?? "field";
        return string.Concat(lastSegment.Where(static character => char.IsLetterOrDigit(character) || character == '_'));
    }

    private static string CreateSampleValue(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length <= MaxSampleLength)
        {
            return normalized;
        }

        return normalized[..MaxSampleLength];
    }

    private sealed record FieldSample(string Path, string SampleValue);
}
