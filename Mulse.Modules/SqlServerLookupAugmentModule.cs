using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace Mulse.Modules;

public sealed class SqlServerLookupAugmentModule : IOrchestrationAugmentModule
{
    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json],
        [ModuleProtocol.SqlServer],
        [ModuleCapability.Lookup, ModuleCapability.Enrichment]);

    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("connectionString", "Connection string", "The SQL Server connection string used for lookup queries.", true, ModuleSettingInputKind.TextArea),
        new("idPaths", "ID paths", "A comma-separated list of JSON paths used to extract lookup IDs from the source payload.", true),
        new("queryText", "Query text", "The SQL query to execute. Use an @ids parameter and SQL Server STRING_SPLIT for the extracted IDs.", true, ModuleSettingInputKind.TextArea),
        new("idsParameterName", "IDs parameter name", "The SQL parameter that receives the comma-separated ID list.", false, ModuleSettingInputKind.Text, "@ids")
    ];

    public ModuleDescriptor Descriptor { get; } = new(
        "sql-server-lookup-augment",
        "SQL Server lookup augment",
        ModuleKind.OrchestrationAugment,
        "Executes a SQL Server lookup based on IDs extracted from the source JSON payload and attaches the result set to the working document.",
        SettingDescriptors,
        Recommendation);

    public async Task<IntegrationBatch> AugmentAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var connectionString = ModuleSettingReader.GetRequired(step.Settings, "connectionString", Descriptor.Id);
        var idPaths = ModuleSettingReader.GetRequired(step.Settings, "idPaths", Descriptor.Id)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var queryText = ModuleSettingReader.GetRequired(step.Settings, "queryText", Descriptor.Id);
        var idsParameterName = NormalizeParameterName(ModuleSettingReader.GetOptional(step.Settings, "idsParameterName") ?? "@ids");

        var transformedPayloads = new List<IntegrationPayload>(batch.Payloads.Count);

        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceNode = JsonPayloadNavigator.Parse(payload.Content, Descriptor.Id, payload.Name);
            var ids = idPaths
                .SelectMany(path => JsonPayloadNavigator.ReadStringValues(sourceNode, path))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var rows = await ExecuteLookupAsync(connectionString, queryText, idsParameterName, ids, context, payload, cancellationToken).ConfigureAwait(false);
            var envelope = new JsonObject
            {
                ["source"] = sourceNode.DeepClone(),
                ["lookup"] = new JsonObject
                {
                    ["ids"] = new JsonArray(ids.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                    ["count"] = rows.Count,
                    ["rows"] = rows
                }
            };

            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["lookupIds"] = string.Join(',', ids),
                ["lookupRowCount"] = rows.Count.ToString(CultureInfo.InvariantCulture)
            };

            transformedPayloads.Add(new IntegrationPayload(
                Path.ChangeExtension(payload.Name, ".json"),
                BinaryData.FromString(envelope.ToJsonString()),
                "application/json",
                metadata));
        }

        return new IntegrationBatch(transformedPayloads);
    }

    private static async Task<JsonArray> ExecuteLookupAsync(
        string connectionString,
        string queryText,
        string idsParameterName,
        IReadOnlyList<string> ids,
        FlowExecutionContext context,
        IntegrationPayload payload,
        CancellationToken cancellationToken)
    {
        var rows = new JsonArray();
        if (ids.Count == 0)
        {
            return rows;
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.Text;
        command.CommandText = queryText;
        command.Parameters.AddWithValue(idsParameterName, string.Join(',', ids));
        command.Parameters.AddWithValue("@flowId", context.FlowId);
        command.Parameters.AddWithValue("@executionId", context.ExecutionId);
        command.Parameters.AddWithValue("@payloadName", payload.Name);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new JsonObject();
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[reader.GetName(index)] = CreateJsonNode(reader.GetValue(index));
            }

            rows.Add(row);
        }

        return rows;
    }

    private static string NormalizeParameterName(string parameterName)
    {
        return parameterName.StartsWith('@') ? parameterName : $"@{parameterName}";
    }

    private static JsonNode? CreateJsonNode(object value)
    {
        return value switch
        {
            null or DBNull => null,
            string stringValue => JsonValue.Create(stringValue),
            bool booleanValue => JsonValue.Create(booleanValue),
            byte byteValue => JsonValue.Create(byteValue),
            short int16Value => JsonValue.Create(int16Value),
            int int32Value => JsonValue.Create(int32Value),
            long int64Value => JsonValue.Create(int64Value),
            float singleValue => JsonValue.Create(singleValue),
            double doubleValue => JsonValue.Create(doubleValue),
            decimal decimalValue => JsonValue.Create(decimalValue),
            Guid guidValue => JsonValue.Create(guidValue.ToString()),
            DateTimeOffset dateTimeOffsetValue => JsonValue.Create(dateTimeOffsetValue.ToString("O")),
            DateTime dateTimeValue => JsonValue.Create(dateTimeValue.ToString("O")),
            byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
            _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture))
        };
    }
}
