using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mulse.Modules;

public static class DecisionRuntime
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static IReadOnlyList<DecisionConditionDefinition> ParseConditions(string decisionJson, string ownerId)
    {
        try
        {
            return JsonSerializer.Deserialize<DecisionConditionDefinition[]>(decisionJson, SerializerOptions)
                ?? [];
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"Module or step '{ownerId}' has invalid conditionJson content.", nameof(decisionJson), exception);
        }
    }

    public static IReadOnlyList<DecisionRuleDefinition> ParseRules(string decisionJson, string ownerId)
    {
        try
        {
            return JsonSerializer.Deserialize<DecisionRuleDefinition[]>(decisionJson, SerializerOptions)
                ?? [];
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"Module '{ownerId}' has invalid decisionJson content.", nameof(decisionJson), exception);
        }
    }

    public static bool MatchesAll(IntegrationPayload payload, IReadOnlyList<DecisionConditionDefinition> conditions, string ownerId)
    {
        return conditions.All(condition => Matches(payload, condition, ownerId));
    }

    public static string? ResolveActionValue(IntegrationPayload payload, DecisionActionDefinition action, string ownerId)
    {
        return action.ValueSource switch
        {
            DecisionValueSourceKind.Literal => action.Value,
            DecisionValueSourceKind.Metadata => ResolveValues(payload, DecisionValueSourceKind.Metadata, action.SourcePath, ownerId).FirstOrDefault(),
            DecisionValueSourceKind.Payload => ResolveValues(payload, DecisionValueSourceKind.Payload, action.SourcePath, ownerId).FirstOrDefault(),
            DecisionValueSourceKind.PayloadName => payload.Name,
            DecisionValueSourceKind.ContentType => payload.ContentType,
            _ => action.Value
        };
    }

    public static JsonNode? ResolveActionNode(IntegrationPayload payload, DecisionActionDefinition action, string ownerId)
    {
        if (action.ValueSource == DecisionValueSourceKind.Payload)
        {
            var sourcePath = action.SourcePath ?? "$";
            return JsonPayloadNavigator.ReadFirstNode(JsonPayloadNavigator.Parse(payload.Content, ownerId, payload.Name), sourcePath);
        }

        var scalarValue = ResolveActionValue(payload, action, ownerId);
        return CreateLiteralNode(scalarValue);
    }

    private static bool Matches(IntegrationPayload payload, DecisionConditionDefinition condition, string ownerId)
    {
        var values = ResolveValues(payload, condition.Source, condition.Path, ownerId);
        return condition.Operator switch
        {
            DecisionComparisonOperator.Exists => values.Any(static value => !string.IsNullOrWhiteSpace(value)),
            DecisionComparisonOperator.Equals => values.Any(value => string.Equals(value, condition.Value, StringComparison.OrdinalIgnoreCase)),
            DecisionComparisonOperator.NotEquals => values.Any(value => !string.Equals(value, condition.Value, StringComparison.OrdinalIgnoreCase)),
            DecisionComparisonOperator.Contains => values.Any(value => Contains(value, condition.Value)),
            DecisionComparisonOperator.StartsWith => values.Any(value => StartsWith(value, condition.Value)),
            DecisionComparisonOperator.EndsWith => values.Any(value => EndsWith(value, condition.Value)),
            DecisionComparisonOperator.In => values.Any(value => IsInSet(value, condition.Value)),
            DecisionComparisonOperator.RegexMatch => values.Any(value => Regex.IsMatch(value, condition.Value ?? string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
            DecisionComparisonOperator.IsTrue => values.Any(IsTrue),
            DecisionComparisonOperator.IsFalse => values.Any(IsFalse),
            _ => false
        };
    }

    private static IReadOnlyList<string> ResolveValues(IntegrationPayload payload, DecisionValueSourceKind source, string? path, string ownerId)
    {
        return source switch
        {
            DecisionValueSourceKind.Literal => string.IsNullOrWhiteSpace(path) ? [] : [path],
            DecisionValueSourceKind.Metadata => ResolveMetadataValues(payload, path),
            DecisionValueSourceKind.Payload => ResolvePayloadValues(payload, path, ownerId),
            DecisionValueSourceKind.PayloadName => [payload.Name],
            DecisionValueSourceKind.ContentType => [payload.ContentType],
            _ => []
        };
    }

    private static IReadOnlyList<string> ResolveMetadataValues(IntegrationPayload payload, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        return payload.Metadata.TryGetValue(path, out var value) && !string.IsNullOrWhiteSpace(value)
            ? [value]
            : [];
    }

    private static IReadOnlyList<string> ResolvePayloadValues(IntegrationPayload payload, string? path, string ownerId)
    {
        var jsonPath = string.IsNullOrWhiteSpace(path) ? "$" : path;
        var payloadNode = JsonPayloadNavigator.Parse(payload.Content, ownerId, payload.Name);
        return JsonPayloadNavigator.ReadStringValues(payloadNode, jsonPath);
    }

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

    private static bool Contains(string candidate, string? value)
    {
        return value is not null && candidate.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartsWith(string candidate, string? value)
    {
        return value is not null && candidate.StartsWith(value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EndsWith(string candidate, string? value)
    {
        return value is not null && candidate.EndsWith(value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInSet(string candidate, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return values.Any(item => string.Equals(item, candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTrue(string candidate)
    {
        return bool.TryParse(candidate, out var value) && value;
    }

    private static bool IsFalse(string candidate)
    {
        return bool.TryParse(candidate, out var value) && !value;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
