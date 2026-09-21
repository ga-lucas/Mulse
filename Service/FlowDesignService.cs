using System.Text.Json;
using System.Text.Json.Serialization;
using Mulse.Modules;
using Service.Models;
using System.Xml.Linq;

namespace Service;

public sealed class FlowDesignService(IModuleCatalog moduleCatalog, IFlowDefinitionService flowDefinitionService) : IFlowDesignService
{
    private const int MaxFields = 48;
    private const int MaxSampleLength = 200;
    private const int MaxPreviewLength = 1200;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

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
        var response = new FlowDesignResponse(
            format,
            request.FileName,
            text.Length <= MaxPreviewLength ? text : text[..MaxPreviewLength],
            CreateSuggestions(availableModules, ModuleKind.Fetch, format),
            CreateSuggestions(availableModules, ModuleKind.Parse, format),
            CreateSuggestions(availableModules, ModuleKind.OrchestrationAugment, format),
            CreateSuggestions(availableModules, ModuleKind.Render, format),
            CreateSuggestions(availableModules, ModuleKind.Deliver, format),
            fields);

        return Task.FromResult(response);
    }

    public async Task<PipelineDefinition> CreateFlowAsync(CreateDesignedFlowRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Deliveries.Count == 0)
        {
            throw new ArgumentException("At least one delivery route is required.", nameof(request));
        }

        var pipeline = new PipelineDefinition
        {
            Id = request.Id,
            Enabled = request.Enabled,
            Trigger = new PipelineTriggerOptions
            {
                Mode = request.Trigger.Mode,
                Interval = request.Trigger.Interval,
                RunOnStartup = request.Trigger.RunOnStartup
            },
            Fetch = MapStep(request.Fetch),
            Parse = MapStep(request.Parse),
            Augments = request.Augments.Select(MapStep).ToList(),
            Deliveries = request.Deliveries.Select(MapDelivery).ToList()
        };

        ApplyDesignerMappings(pipeline.Augments, request.Mappings);
        return await flowDefinitionService.CreateAsync(pipeline, cancellationToken).ConfigureAwait(false);
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
            .Select(module =>
            {
                var score = GetRecommendationScore(module.Descriptor, kind, format);
                return new RankedModuleSuggestion(
                    new ModuleSuggestionResponse(
                        module.Descriptor.Id,
                        module.Descriptor.DisplayName,
                        module.Descriptor.Kind,
                        module.Descriptor.Description,
                        score > 0,
                        MapSettings(module.Descriptor.Settings),
                        MapFormats(module.Descriptor.Recommendation),
                        MapProtocols(module.Descriptor.Recommendation),
                        MapCapabilities(module.Descriptor.Recommendation)),
                    score);
            })
            .OrderByDescending(static module => module.Score)
            .ThenByDescending(static module => module.Response.IsRecommended)
            .ThenBy(static module => module.Response.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static module => module.Response)
            .ToArray();
    }

    private static IReadOnlyList<ModuleSettingResponse> MapSettings(IReadOnlyList<ModuleSettingDescriptor> settings)
    {
        return settings
            .Select(static setting => new ModuleSettingResponse(
                setting.Key,
                setting.Label,
                setting.Description,
                setting.IsRequired,
                setting.InputKind.ToString(),
                setting.DefaultValue,
                setting.Options?.Select(static option => new ModuleSettingOptionResponse(option.Value, option.Label)).ToArray() ?? []))
            .ToArray();
    }

    private static IReadOnlyList<string> MapFormats(ModuleRecommendationProfile? recommendation)
    {
        return recommendation?.SupportedFormats.Select(static format => format.ToString()).ToArray() ?? [];
    }

    private static IReadOnlyList<string> MapProtocols(ModuleRecommendationProfile? recommendation)
    {
        return recommendation?.Protocols.Select(static protocol => protocol.ToString()).ToArray() ?? [];
    }

    private static IReadOnlyList<string> MapCapabilities(ModuleRecommendationProfile? recommendation)
    {
        return recommendation?.Capabilities.Select(static capability => capability.ToString()).ToArray() ?? [];
    }

    private static int GetRecommendationScore(ModuleDescriptor descriptor, ModuleKind kind, DetectedDataFormat format)
    {
        var recommendation = descriptor.Recommendation;
        if (recommendation is null)
        {
            return 0;
        }

        var moduleFormat = MapFormat(format);
        if (recommendation.SupportedFormats.Count > 0 && !recommendation.SupportedFormats.Contains(moduleFormat))
        {
            return 0;
        }

        var score = recommendation.SupportedFormats.Count > 0 ? 4 : 1;
        score += recommendation.Capabilities.Sum(capability => GetCapabilityWeight(kind, capability));
        score += recommendation.Protocols.Count(protocol => GetPreferredProtocols(kind).Contains(protocol)) * 2;
        score += recommendation.Capabilities.Count(capability => GetFormatSpecificCapabilities(moduleFormat).Contains(capability));
        return score;
    }

    private static ModuleDataFormat MapFormat(DetectedDataFormat format)
    {
        return format switch
        {
            DetectedDataFormat.Json => ModuleDataFormat.Json,
            DetectedDataFormat.Xml => ModuleDataFormat.Xml,
            DetectedDataFormat.Csv => ModuleDataFormat.Csv,
            _ => ModuleDataFormat.Text
        };
    }

    private static int GetCapabilityWeight(ModuleKind kind, ModuleCapability capability)
    {
        return (kind, capability) switch
        {
            (ModuleKind.Fetch, ModuleCapability.Ingestion) => 5,
            (ModuleKind.Fetch, ModuleCapability.Polling) => 3,
            (ModuleKind.Parse, ModuleCapability.Parsing) => 5,
            (ModuleKind.OrchestrationAugment, ModuleCapability.Lookup) => 5,
            (ModuleKind.OrchestrationAugment, ModuleCapability.Mapping) => 5,
            (ModuleKind.OrchestrationAugment, ModuleCapability.Enrichment) => 4,
            (ModuleKind.OrchestrationAugment, ModuleCapability.Decision) => 3,
            (ModuleKind.OrchestrationAugment, ModuleCapability.Transformation) => 2,
            (ModuleKind.OrchestrationAugment, ModuleCapability.Envelope) => 1,
            (ModuleKind.Render, ModuleCapability.Serialization) => 5,
            (ModuleKind.Deliver, ModuleCapability.Delivery) => 5,
            (ModuleKind.Deliver, ModuleCapability.Storage) => 3,
            (ModuleKind.Deliver, ModuleCapability.Logging) => 1,
            _ => 0
        };
    }

    private static IReadOnlySet<ModuleProtocol> GetPreferredProtocols(ModuleKind kind)
    {
        return kind switch
        {
            ModuleKind.Fetch => new HashSet<ModuleProtocol> { ModuleProtocol.FileSystem, ModuleProtocol.Sftp },
            ModuleKind.Parse => new HashSet<ModuleProtocol>(),
            ModuleKind.OrchestrationAugment => new HashSet<ModuleProtocol> { ModuleProtocol.SqlServer, ModuleProtocol.Plugin },
            ModuleKind.Render => new HashSet<ModuleProtocol>(),
            ModuleKind.Deliver => new HashSet<ModuleProtocol> { ModuleProtocol.Http, ModuleProtocol.FileSystem },
            _ => new HashSet<ModuleProtocol>()
        };
    }

    private static IReadOnlySet<ModuleCapability> GetFormatSpecificCapabilities(ModuleDataFormat format)
    {
        return format switch
        {
            ModuleDataFormat.Json => new HashSet<ModuleCapability> { ModuleCapability.Envelope, ModuleCapability.Mapping, ModuleCapability.Transformation },
            ModuleDataFormat.Xml => new HashSet<ModuleCapability> { ModuleCapability.Serialization },
            _ => new HashSet<ModuleCapability>()
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

    private static ModuleStepDefinition MapStep(FlowStepRequest step)
    {
        return new ModuleStepDefinition
        {
            Module = step.Module,
            Settings = new Dictionary<string, string>(step.Settings, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static DeliveryRouteDefinition MapDelivery(DeliveryRouteRequest route)
    {
        return new DeliveryRouteDefinition
        {
            Render = MapStep(route.Render),
            Deliver = MapStep(route.Deliver)
        };
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static void ApplyDesignerMappings(IReadOnlyList<ModuleStepDefinition> augments, IReadOnlyList<FlowDesignFieldMappingRequest> mappings)
    {
        if (mappings.Count == 0)
        {
            return;
        }

        var mappingSteps = augments
            .Where(static step => string.Equals(step.Module, "conditional-join-map-augment", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (mappingSteps.Length == 0)
        {
            throw new ArgumentException("Designer mappings require the conditional-join-map-augment module to be configured.", nameof(mappings));
        }

        var mappingDefinitions = mappings.Select(static mapping => new WorkflowFieldMappingDefinition
        {
            SourceKind = mapping.SourceKind,
            SourcePath = mapping.SourcePath,
            TargetField = mapping.TargetField,
            Condition = mapping.Condition,
            LiteralValue = mapping.LiteralValue
        }).ToArray();

        var mappingJson = JsonSerializer.Serialize(mappingDefinitions, SerializerOptions);
        foreach (var mappingStep in mappingSteps)
        {
            mappingStep.Settings["mappingJson"] = mappingJson;
        }
    }

    private sealed record FieldSample(string Path, string SampleValue);

    private sealed record RankedModuleSuggestion(ModuleSuggestionResponse Response, int Score);
}
