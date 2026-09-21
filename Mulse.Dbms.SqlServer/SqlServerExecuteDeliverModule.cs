using System.Data;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Mulse.Modules;

namespace Mulse.Dbms.SqlServer;

/// <summary>
/// Executes a parameterized SQL Server command (statement or stored procedure) once per payload in
/// the batch, binding JSON fields from each payload to SQL parameters. Combine with an upstream
/// debatch-augment step to replicate common ETL "loop rows, upsert via stored proc" patterns
/// (e.g. legacy SSIS packages that call an insert-or-update proc per source row).
/// </summary>
public sealed class SqlServerExecuteDeliverModule(ILogger<SqlServerExecuteDeliverModule> logger) : IDeliverModule
{
    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json],
        [ModuleProtocol.SqlServer],
        [ModuleCapability.Delivery, ModuleCapability.Storage]);

    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("connectionString", "Connection string", "The SQL Server connection string used to execute the command.", true, ModuleSettingInputKind.TextArea),
        new("commandText", "Command text", "A SQL statement or stored procedure name to execute once per payload.", true, ModuleSettingInputKind.TextArea),
        new(
            "commandType",
            "Command type",
            "Whether commandText is a raw SQL statement or a stored procedure name.",
            false,
            ModuleSettingInputKind.Select,
            "Text",
            [new ModuleSettingOption("Text", "SQL text"), new ModuleSettingOption("StoredProcedure", "Stored procedure")]),
        new(
            "parameterMap",
            "Parameter map",
            "Optional comma-separated '@paramName=jsonPath' pairs mapping SQL parameters to JSON fields on each payload. When omitted, every top-level JSON property is bound to a same-named parameter.",
            false,
            ModuleSettingInputKind.TextArea)
    ];

    public ModuleDescriptor Descriptor { get; } = new(
        "sql-server-execute-deliver",
        "SQL Server execute deliver",
        ModuleKind.Deliver,
        "Executes a parameterized SQL statement or stored procedure once per payload, binding JSON fields to SQL parameters. Supports insert/update/upsert patterns.",
        SettingDescriptors,
        Recommendation);

    public async Task DeliverAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var connectionString = ModuleSettingReader.GetRequired(step.Settings, "connectionString", Descriptor.Id);
        var commandText = ModuleSettingReader.GetRequired(step.Settings, "commandText", Descriptor.Id);
        var commandType = ParseCommandType(ModuleSettingReader.GetOptional(step.Settings, "commandType"));
        var parameterMap = ParseParameterMap(ModuleSettingReader.GetOptional(step.Settings, "parameterMap"));

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceNode = JsonPayloadNavigator.Parse(payload.Content, Descriptor.Id, payload.Name);
            var parameters = BuildParameters(sourceNode, parameterMap, context, payload, commandType);

            var command = new CommandDefinition(
                commandText,
                parameters,
                commandType: commandType,
                cancellationToken: cancellationToken);

            var rowsAffected = await connection.ExecuteAsync(command).ConfigureAwait(false);

            logger.LogInformation(
                "Deliver module {ModuleId} executed command for flow {FlowId} execution {ExecutionId} payload {PayloadName}, {RowsAffected} row(s) affected.",
                Descriptor.Id,
                context.FlowId,
                context.ExecutionId,
                payload.Name,
                rowsAffected);
        }
    }

    private static DynamicParameters BuildParameters(
        JsonNode sourceNode,
        IReadOnlyList<(string ParameterName, string JsonPath)> parameterMap,
        FlowExecutionContext context,
        IntegrationPayload payload,
        CommandType commandType)
    {
        var parameters = new DynamicParameters();

        if (parameterMap.Count > 0)
        {
            foreach (var (parameterName, jsonPath) in parameterMap)
            {
                var node = JsonPayloadNavigator.ReadFirstNode(sourceNode, jsonPath);
                parameters.Add(parameterName, JsonPayloadNavigator.ExtractScalarText(node));
            }
        }
        else if (sourceNode is JsonObject sourceObject)
        {
            foreach (var property in sourceObject)
            {
                parameters.Add(NormalizeParameterName(property.Key), JsonPayloadNavigator.ExtractScalarText(property.Value));
            }
        }

        // Stored procedures have a fixed parameter signature: only append correlation parameters for
        // ad-hoc SQL text, where unused declared parameters are harmless. To correlate a stored
        // procedure call, add matching parameters to the procedure itself and map them explicitly
        // via parameterMap.
        if (commandType == CommandType.Text)
        {
            parameters.Add("@flowId", context.FlowId);
            parameters.Add("@executionId", context.ExecutionId);
            parameters.Add("@payloadName", payload.Name);
        }

        return parameters;
    }

    private static IReadOnlyList<(string ParameterName, string JsonPath)> ParseParameterMap(string? rawMap)
    {
        if (string.IsNullOrWhiteSpace(rawMap))
        {
            return [];
        }

        var entries = new List<(string, string)>();
        foreach (var pair in rawMap.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == pair.Length - 1)
            {
                throw new ArgumentException($"Module 'sql-server-execute-deliver' has an invalid parameterMap entry '{pair}'. Expected '@paramName=jsonPath'.", nameof(rawMap));
            }

            var parameterName = NormalizeParameterName(pair[..separatorIndex].Trim());
            var jsonPath = pair[(separatorIndex + 1)..].Trim();
            entries.Add((parameterName, jsonPath));
        }

        return entries;
    }

    private static string NormalizeParameterName(string parameterName)
    {
        return parameterName.StartsWith('@') ? parameterName : $"@{parameterName}";
    }

    private static CommandType ParseCommandType(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return CommandType.Text;
        }

        return rawValue.Trim().Equals("StoredProcedure", StringComparison.OrdinalIgnoreCase)
            ? CommandType.StoredProcedure
            : CommandType.Text;
    }
}
