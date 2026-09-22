using System.Globalization;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.SqlClient;
using Mulse.Modules;

namespace Mulse.Dbms.SqlServer;

/// <summary>
/// Source-fetch module that queries SQL Server for reference data keyed off another source's parsed payloads.
/// <para>
/// Declare it as a non-root <see cref="SourceDefinition"/> whose <see cref="SourceDefinition.InputSourceIds"/>
/// name the upstream source(s) to key off: the merged parsed batch of those sources arrives as
/// <c>input</c>, and the configured <c>idPaths</c> are evaluated against every input payload to build the
/// distinct id list passed to the query.
/// </para>
/// <para>
/// Output convention: exactly ONE payload named <c>sql-lookup-rows.json</c> whose content is a top-level JSON
/// array of result rows (one object per row, column name to value). The join engine's row-set convention turns
/// that array into one row per element, so no envelope wrapping happens here - joining is the job of
/// <c>multi-source-join-map-augment</c>.
/// </para>
/// </summary>
public sealed class SqlServerLookupFetchModule : IFetchModule
{
    private const string OutputPayloadName = "sql-lookup-rows.json";

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json],
        [ModuleProtocol.SqlServer],
        [ModuleCapability.Lookup, ModuleCapability.Enrichment]);

    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("connectionString", "Connection string", "The SQL Server connection string used for lookup queries.", true, ModuleSettingInputKind.TextArea),
        new("idPaths", "ID paths", "A comma-separated list of JSON paths evaluated against this source's input payloads (the parsed output of its input sources) to extract lookup IDs.", true),
        new("queryText", "Query text", "The SQL query to execute. Use an @ids parameter and SQL Server STRING_SPLIT for the extracted IDs.", true, ModuleSettingInputKind.TextArea),
        new("idsParameterName", "IDs parameter name", "The SQL parameter that receives the comma-separated ID list.", false, ModuleSettingInputKind.Text, "@ids")
    ];

    public ModuleDescriptor Descriptor { get; } = new(
        "sql-server-lookup-fetch",
        "SQL Server lookup fetch",
        ModuleKind.Fetch,
        "Fetches SQL Server reference rows using IDs extracted from an upstream source's parsed payloads, emitting one JSON array payload of rows for a downstream join.",
        SettingDescriptors,
        Recommendation);

    public async Task<IntegrationBatch> FetchAsync(
        FlowExecutionContext context,
        IntegrationBatch input,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var connectionString = ModuleSettingReader.GetRequired(step.Settings, "connectionString", Descriptor.Id);
        var idPaths = ModuleSettingReader.GetRequired(step.Settings, "idPaths", Descriptor.Id)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var queryText = ModuleSettingReader.GetRequired(step.Settings, "queryText", Descriptor.Id);
        var idsParameterName = NormalizeParameterName(ModuleSettingReader.GetOptional(step.Settings, "idsParameterName") ?? "@ids");

        var ids = ExtractIds(input, idPaths);
        if (ids.Count == 0)
        {
            return new IntegrationBatch([CreatePayload(new JsonArray(), ids, context)]);
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await ExecuteLookupAsync(connection, queryText, idsParameterName, ids, context, cancellationToken).ConfigureAwait(false);
        return new IntegrationBatch([CreatePayload(rows, ids, context)]);
    }

    private IReadOnlyList<string> ExtractIds(IntegrationBatch input, IReadOnlyList<string> idPaths)
    {
        var ids = new List<string>();
        foreach (var payload in input.Payloads)
        {
            var node = JsonPayloadNavigator.Parse(payload.Content, Descriptor.Id, payload.Name);

            // Each input payload may itself be a row-set (a top-level array), so evaluate the id paths against
            // every logical row rather than only the document root.
            foreach (var row in JsonPayloadNavigator.ReadRows(node))
            {
                foreach (var idPath in idPaths)
                {
                    ids.AddRange(JsonPayloadNavigator.ReadStringValues(row, idPath));
                }
            }
        }

        return ids
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IntegrationPayload CreatePayload(JsonArray rows, IReadOnlyList<string> ids, FlowExecutionContext context)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["flowId"] = context.FlowId,
            ["executionId"] = context.ExecutionId,
            ["fetchedBy"] = Descriptor.Id,
            ["lookupIds"] = string.Join(',', ids),
            ["lookupRowCount"] = rows.Count.ToString(CultureInfo.InvariantCulture)
        };

        return new IntegrationPayload(
            OutputPayloadName,
            BinaryData.FromString(rows.ToJsonString()),
            "application/json",
            metadata);
    }

    private static async Task<JsonArray> ExecuteLookupAsync(
        SqlConnection connection,
        string queryText,
        string idsParameterName,
        IReadOnlyList<string> ids,
        FlowExecutionContext context,
        CancellationToken cancellationToken)
    {
        var rows = new JsonArray();

        var parameters = new DynamicParameters();
        parameters.Add(idsParameterName, string.Join(',', ids));
        parameters.Add("@flowId", context.FlowId);
        parameters.Add("@executionId", context.ExecutionId);

        var command = new CommandDefinition(queryText, parameters, cancellationToken: cancellationToken);
        var reader = await connection.ExecuteReaderAsync(command).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = new JsonObject();
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    row[reader.GetName(index)] = CreateJsonNode(reader.GetValue(index));
                }

                rows.Add(row);
            }
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
