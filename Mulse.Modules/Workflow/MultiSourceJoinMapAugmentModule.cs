using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Mulse.Modules.Workflow;

/// <summary>
/// General-purpose N-way join/map augment. It operates on the merged, <c>sourceId</c>-tagged batch produced by a
/// flow's source graph and joins two named inputs into mapped output documents.
/// <para>
/// <b>Row-set convention.</b> Each named input is turned into a logical row-set from the payloads tagged with
/// that <c>sourceId</c>: a payload whose JSON root is an array contributes one row per array element, any other
/// payload contributes exactly one row. A source that emits many single-object payloads and a source that emits
/// one array payload therefore behave identically.
/// </para>
/// <para>
/// <b>Chaining.</b> Leaving <c>leftSourceId</c> empty (or setting it to <c>*</c>) selects "the accumulated working
/// document so far": every payload in the batch that is not part of the right input. That makes it possible to
/// chain several joins, each one folding another source into the working document.
/// </para>
/// <para>
/// <b>Cardinality.</b> <c>FanOut</c> emits one output record per matched left x right pair (classic relational
/// fan-out). <c>Nested</c> emits one output record per left row, with all of its matched right rows mapped into a
/// JSON array written at <c>nestedTargetPath</c> (which may itself be a nested/array path such as
/// <c>order.lineItems</c> or <c>orders[].lineItems</c>).
/// </para>
/// </summary>
public sealed class MultiSourceJoinMapAugmentModule : IOrchestrationAugmentModule
{
    private const string WorkingDocumentSelector = "*";
    private const string SourceIdMetadataKey = "sourceId";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json],
        capabilities: [ModuleCapability.Mapping, ModuleCapability.Transformation, ModuleCapability.Enrichment]);

    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("leftSourceId", "Left source id", "The sourceId whose payloads form the left row-set. Leave blank (or use '*') to join against the accumulated working document - every payload that isn't part of the right source - so several joins can be chained.", false),
        new("rightSourceId", "Right source id", "The sourceId whose payloads form the right row-set.", true),
        new("joinKeysJson", "Join keys", "A JSON array of composite key pairs, e.g. [{\"leftPath\":\"$.customerId\",\"rightPath\":\"CustomerId\"}]. Every pair must match for two rows to be considered joined. Paths may traverse nested arrays using the trailing [] convention.", true, ModuleSettingInputKind.TextArea, "[]"),
        new("joinKind", "Join kind", "Relational join semantics applied to the two row-sets.", false, ModuleSettingInputKind.Select, nameof(WorkflowJoinMode.Left),
        [
            new ModuleSettingOption(nameof(WorkflowJoinMode.Inner), "Inner join"),
            new ModuleSettingOption(nameof(WorkflowJoinMode.Left), "Left join"),
            new ModuleSettingOption(nameof(WorkflowJoinMode.Right), "Right join"),
            new ModuleSettingOption(nameof(WorkflowJoinMode.Full), "Full outer join")
        ]),
        new("resultCardinality", "Result cardinality", "FanOut emits one record per matched left/right pair. Nested emits one record per left row with its matched right rows attached as an array.", false, ModuleSettingInputKind.Select, nameof(WorkflowJoinCardinality.FanOut),
        [
            new ModuleSettingOption(nameof(WorkflowJoinCardinality.FanOut), "Fan out (one record per matched pair)"),
            new ModuleSettingOption(nameof(WorkflowJoinCardinality.Nested), "Nested (one record per left row)")
        ]),
        new("nestedTargetPath", "Nested target path", "Where the array of mapped right rows is written when result cardinality is Nested, e.g. 'order.lineItems'.", false),
        new("mappingJson", "Field mappings", "A JSON array of visual field mappings that builds the output document. Each mapping reads from the Left row, the Right row, or a Literal value.", true, ModuleSettingInputKind.TextArea, "[]")
    ];

    public ModuleDescriptor Descriptor { get; } = new(
        "multi-source-join-map-augment",
        "Multi-source join and map augment",
        ModuleKind.OrchestrationAugment,
        "Joins two named flow sources (by composite key, with Inner/Left/Right/Full semantics and either fan-out or nested cardinality) and maps the matched rows into a new JSON document. Each source's parsed payloads form a row-set: an array payload yields one row per element, any other payload yields a single row.",
        SettingDescriptors,
        Recommendation);

    public Task<IntegrationBatch> AugmentAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var leftSourceId = ModuleSettingReader.GetOptional(step.Settings, "leftSourceId") ?? WorkingDocumentSelector;
        var rightSourceId = ModuleSettingReader.GetRequired(step.Settings, "rightSourceId", Descriptor.Id);
        var joinKeys = ParseJoinKeys(ModuleSettingReader.GetRequired(step.Settings, "joinKeysJson", Descriptor.Id));
        var joinKind = ParseEnum(ModuleSettingReader.GetOptional(step.Settings, "joinKind"), WorkflowJoinMode.Left);
        var cardinality = ParseEnum(ModuleSettingReader.GetOptional(step.Settings, "resultCardinality"), WorkflowJoinCardinality.FanOut);
        var nestedTargetPath = ModuleSettingReader.GetOptional(step.Settings, "nestedTargetPath");
        var mappings = ParseMappings(ModuleSettingReader.GetRequired(step.Settings, "mappingJson", Descriptor.Id));

        if (cardinality == WorkflowJoinCardinality.Nested && string.IsNullOrWhiteSpace(nestedTargetPath))
        {
            throw new ArgumentException(
                $"Module '{Descriptor.Id}' requires the 'nestedTargetPath' setting when 'resultCardinality' is Nested.",
                "nestedTargetPath");
        }

        var rightRows = ReadRows(batch, payload => IsFromSource(payload, rightSourceId));
        var leftRows = IsWorkingDocumentSelector(leftSourceId)
            ? ReadRows(batch, payload => !IsFromSource(payload, rightSourceId))
            : ReadRows(batch, payload => IsFromSource(payload, leftSourceId));

        var matchedRightRows = new HashSet<int>();
        var outputs = new List<JoinOutput>();
        var matchedPairCount = 0;
        var unmatchedLeftCount = 0;

        foreach (var leftRow in leftRows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var matches = new List<JoinRow>();
            for (var rightIndex = 0; rightIndex < rightRows.Count; rightIndex++)
            {
                if (!RowsMatch(leftRow.Node, rightRows[rightIndex].Node, joinKeys))
                {
                    continue;
                }

                matches.Add(rightRows[rightIndex]);
                matchedRightRows.Add(rightIndex);
            }

            matchedPairCount += matches.Count;
            if (matches.Count == 0)
            {
                unmatchedLeftCount++;
            }

            // Which left rows survive: Inner and Right only keep left rows that matched something.
            var emitsUnmatchedLeft = joinKind is WorkflowJoinMode.Left or WorkflowJoinMode.Full;
            if (matches.Count == 0 && !emitsUnmatchedLeft)
            {
                continue;
            }

            if (cardinality == WorkflowJoinCardinality.Nested)
            {
                outputs.Add(BuildNestedRecord(leftRow, matches, mappings, nestedTargetPath!));
                continue;
            }

            if (matches.Count == 0)
            {
                outputs.Add(BuildFlatRecord(leftRow, rightRow: null, mappings, isMatched: false));
                continue;
            }

            foreach (var match in matches)
            {
                outputs.Add(BuildFlatRecord(leftRow, match, mappings, isMatched: true));
            }
        }

        // Right/Full pass: emit only right rows that matched NOTHING, so rows already covered by the left pass
        // are never duplicated.
        var unmatchedRightCount = 0;
        for (var rightIndex = 0; rightIndex < rightRows.Count; rightIndex++)
        {
            if (matchedRightRows.Contains(rightIndex))
            {
                continue;
            }

            unmatchedRightCount++;
            if (joinKind is not (WorkflowJoinMode.Right or WorkflowJoinMode.Full))
            {
                continue;
            }

            var rightRow = rightRows[rightIndex];
            outputs.Add(cardinality == WorkflowJoinCardinality.Nested
                ? BuildNestedRecord(leftRow: null, [rightRow], mappings, nestedTargetPath!)
                : BuildFlatRecord(leftRow: null, rightRow, mappings, isMatched: false));
        }

        var payloads = MaterializePayloads(
            outputs, leftSourceId, rightSourceId, joinKind, cardinality, matchedPairCount, unmatchedLeftCount, unmatchedRightCount);

        return Task.FromResult(new IntegrationBatch(payloads));
    }

    private static JoinOutput BuildFlatRecord(
        JoinRow? leftRow,
        JoinRow? rightRow,
        IReadOnlyList<WorkflowFieldMappingDefinition> mappings,
        bool isMatched)
    {
        var document = new JsonObject();
        ApplyMappings(document, mappings, leftRow?.Node, rightRow?.Node, isMatched);
        return new JoinOutput(document, leftRow ?? rightRow, isMatched, isMatched ? 1 : 0);
    }

    private static JoinOutput BuildNestedRecord(
        JoinRow? leftRow,
        IReadOnlyList<JoinRow> rightRows,
        IReadOnlyList<WorkflowFieldMappingDefinition> mappings,
        string nestedTargetPath)
    {
        var isMatched = rightRows.Count > 0 && leftRow is not null;
        var document = new JsonObject();

        // Left- and literal-scoped mappings build the parent record once...
        ApplyMappings(
            document,
            mappings.Where(static mapping => mapping.SourceKind != WorkflowFieldSourceKind.Right).ToArray(),
            leftRow?.Node,
            rightNode: null,
            isMatched);

        // ...and right-scoped mappings build one nested object per matched right row.
        var nestedItems = new JsonArray();
        foreach (var rightRow in rightRows)
        {
            var item = new JsonObject();
            ApplyMappings(
                item,
                mappings.Where(static mapping => mapping.SourceKind == WorkflowFieldSourceKind.Right).ToArray(),
                leftRow?.Node,
                rightRow.Node,
                isMatched);
            nestedItems.Add(item);
        }

        JsonPayloadNavigator.SetNode(document, nestedTargetPath, nestedItems);
        return new JoinOutput(document, leftRow ?? rightRows.FirstOrDefault(), isMatched, rightRows.Count);
    }

    private static void ApplyMappings(
        JsonObject document,
        IReadOnlyList<WorkflowFieldMappingDefinition> mappings,
        JsonNode? leftNode,
        JsonNode? rightNode,
        bool isMatched)
    {
        foreach (var mapping in mappings)
        {
            if (!ShouldApply(mapping.Condition, isMatched))
            {
                continue;
            }

            var value = ResolveValue(mapping, leftNode, rightNode);
            if (value is null)
            {
                continue;
            }

            JsonPayloadNavigator.SetNode(document, mapping.TargetField, value);
        }
    }

    private List<IntegrationPayload> MaterializePayloads(
        IReadOnlyList<JoinOutput> outputs,
        string leftSourceId,
        string rightSourceId,
        WorkflowJoinMode joinKind,
        WorkflowJoinCardinality cardinality,
        int matchedPairCount,
        int unmatchedLeftCount,
        int unmatchedRightCount)
    {
        var payloads = new List<IntegrationPayload>(outputs.Count);
        var nameUsage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var output in outputs)
        {
            var originPayload = output.Origin?.Payload;
            var baseName = Path.ChangeExtension(originPayload?.Name ?? $"{Descriptor.Id}.json", ".json");
            nameUsage.TryGetValue(baseName, out var used);
            nameUsage[baseName] = used + 1;
            var name = used == 0
                ? baseName
                : $"{Path.GetFileNameWithoutExtension(baseName)}-{used}.json";

            var metadata = originPayload is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(originPayload.Metadata, StringComparer.OrdinalIgnoreCase);

            metadata["joinKind"] = joinKind.ToString();
            metadata["joinCardinality"] = cardinality.ToString();
            metadata["joinLeftSourceId"] = leftSourceId;
            metadata["joinRightSourceId"] = rightSourceId;
            metadata["joinMatched"] = output.IsMatched.ToString();
            metadata["joinMatchedRowCount"] = output.MatchedRowCount.ToString(CultureInfo.InvariantCulture);
            metadata["joinMatchedPairCount"] = matchedPairCount.ToString(CultureInfo.InvariantCulture);
            metadata["joinUnmatchedLeftRowCount"] = unmatchedLeftCount.ToString(CultureInfo.InvariantCulture);
            metadata["joinUnmatchedRightRowCount"] = unmatchedRightCount.ToString(CultureInfo.InvariantCulture);

            // The joined result becomes the new working document, so it must no longer look like it belongs to
            // either input source; a later chained join picks it up through the '*' (working document) selector.
            metadata[SourceIdMetadataKey] = Descriptor.Id;

            payloads.Add(new IntegrationPayload(
                name,
                BinaryData.FromString(output.Document.ToJsonString(SerializerOptions)),
                "application/json",
                metadata));
        }

        return payloads;
    }

    private IReadOnlyList<JoinRow> ReadRows(IntegrationBatch batch, Func<IntegrationPayload, bool> selector)
    {
        var rows = new List<JoinRow>();
        foreach (var payload in batch.Payloads)
        {
            if (!selector(payload))
            {
                continue;
            }

            var root = JsonPayloadNavigator.Parse(payload.Content, Descriptor.Id, payload.Name);
            foreach (var row in JsonPayloadNavigator.ReadRows(root))
            {
                rows.Add(new JoinRow(row, payload));
            }
        }

        return rows;
    }

    private static bool RowsMatch(JsonNode leftNode, JsonNode rightNode, IReadOnlyList<WorkflowJoinKeyDefinition> joinKeys)
    {
        if (joinKeys.Count == 0)
        {
            // No keys configured means a cross join: every left row matches every right row.
            return true;
        }

        foreach (var joinKey in joinKeys)
        {
            // Null/absent key values never match anything, mirroring SQL null semantics.
            var leftValues = JsonPayloadNavigator.ReadAllScalarTexts(leftNode, joinKey.LeftPath)
                .OfType<string>()
                .ToArray();
            if (leftValues.Length == 0)
            {
                return false;
            }

            var rightValues = JsonPayloadNavigator.ReadAllScalarTexts(rightNode, joinKey.RightPath)
                .OfType<string>()
                .ToArray();
            if (rightValues.Length == 0)
            {
                return false;
            }

            var matched = leftValues.Any(leftValue =>
                rightValues.Any(rightValue => string.Equals(leftValue, rightValue, StringComparison.OrdinalIgnoreCase)));

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsWorkingDocumentSelector(string sourceId)
        => string.IsNullOrWhiteSpace(sourceId) || string.Equals(sourceId.Trim(), WorkingDocumentSelector, StringComparison.Ordinal);

    private static bool IsFromSource(IntegrationPayload payload, string sourceId)
        => payload.Metadata.TryGetValue(SourceIdMetadataKey, out var value)
            && string.Equals(value, sourceId, StringComparison.OrdinalIgnoreCase);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum defaultValue) where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : defaultValue;

    private static IReadOnlyList<WorkflowJoinKeyDefinition> ParseJoinKeys(string joinKeysJson)
        => JsonSerializer.Deserialize<WorkflowJoinKeyDefinition[]>(joinKeysJson, SerializerOptions) ?? [];

    private static IReadOnlyList<WorkflowFieldMappingDefinition> ParseMappings(string mappingJson)
        => JsonSerializer.Deserialize<WorkflowFieldMappingDefinition[]>(mappingJson, SerializerOptions) ?? [];

    private static bool ShouldApply(WorkflowFieldCondition condition, bool isMatched)
        => condition switch
        {
            WorkflowFieldCondition.Always => true,
            WorkflowFieldCondition.WhenMatched => isMatched,
            WorkflowFieldCondition.WhenUnmatched => !isMatched,
            _ => true
        };

    private static JsonNode? ResolveValue(WorkflowFieldMappingDefinition mapping, JsonNode? leftNode, JsonNode? rightNode)
        => mapping.SourceKind switch
        {
            WorkflowFieldSourceKind.Left => JsonPayloadNavigator.ReadFirstNode(leftNode, mapping.SourcePath),
            WorkflowFieldSourceKind.Right => JsonPayloadNavigator.ReadFirstNode(rightNode, mapping.SourcePath),
            WorkflowFieldSourceKind.Literal => CreateLiteralNode(mapping.LiteralValue),
            _ => null
        };

    private static JsonNode? CreateLiteralNode(string? literalValue)
    {
        if (literalValue is null)
        {
            return null;
        }

        var trimmed = literalValue.Trim();
        if (trimmed.Length == 0)
        {
            return JsonValue.Create(string.Empty);
        }

        try
        {
            return JsonNode.Parse(trimmed);
        }
        catch (JsonException)
        {
            return JsonValue.Create(literalValue);
        }
    }

    /// <summary>One logical row of a named input, plus the payload it was read from (for name/metadata provenance).</summary>
    private sealed record JoinRow(JsonNode Node, IntegrationPayload Payload);

    /// <summary>One emitted output document and the join outcome that produced it.</summary>
    private sealed record JoinOutput(JsonObject Document, JoinRow? Origin, bool IsMatched, int MatchedRowCount);
}
