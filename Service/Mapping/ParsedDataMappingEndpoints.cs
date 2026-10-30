using Mulse.Modules;
using Mulse.Modules.Mapping;
using Service.Modules;

namespace Service.Mapping;

public sealed record ParsedDataSampleRequest(
    string Text, string Format = "Auto", string RecordPath = "", string? ParserModule = null,
    Dictionary<string, string>? ParserSettings = null, string? BinaryContent = null, string? FileName = null, string? ContentType = null);
public sealed record ParsedDataPreviewRequest(string MappingJson, string ParsedData, string Format = "Auto");
public sealed record ParsedDataPreviewResponse(string OutputJson);

public static class ParsedDataMappingEndpoints
{
    public static void MapParsedDataMappingEndpoints(this WebApplication app)
    {
        app.MapPost("/api/flow-designer/parsed-mapping/inspect", InspectAsync)
            .WithTags("Designer").WithSummary("Discover fields from supplied parsed data or an explicitly selected sample parser")
            .Produces<ParsedDataInspection>().ProducesProblem(StatusCodes.Status400BadRequest);
        app.MapPost("/api/flow-designer/parsed-mapping/preview", (ParsedDataPreviewRequest request, CancellationToken ct) =>
            TypedResults.Ok(new ParsedDataPreviewResponse(ParsedDataMapping.Map(request.MappingJson, request.ParsedData, request.Format, ct))))
            .WithTags("Designer").WithSummary("Preview a parsed-data map without fetching or delivering")
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> InspectAsync(ParsedDataSampleRequest request, IServiceProvider services, CancellationToken ct)
    {
        if (request.ParserModule is null)
            return TypedResults.Ok(ParsedDataMapping.Inspect(request.Text, request.Format, request.RecordPath, ct));
        if (request.Text is null || request.Text.Length > ParsedDataMapping.MaxCharacters)
            throw new ArgumentException("Sample text exceeds 1000000 characters.");
        if (request.BinaryContent is { Length: > 13_981_016 }) throw new ArgumentException("Binary samples are limited to 10 MiB.");
        byte[]? bytes = null;
        if (request.BinaryContent is not null)
        {
            try { bytes = Convert.FromBase64String(request.BinaryContent); }
            catch (FormatException ex) { throw new ArgumentException("Binary sample is not base64.", ex); }
            if (bytes.Length > 10 * 1024 * 1024) throw new ArgumentException("Binary samples are limited to 10 MiB.");
        }
        var catalog = services.GetRequiredService<IModuleCatalog>();
        await using var parser = await catalog.LeaseParseAsync(request.ParserModule, ct);
        var payload = new IntegrationPayload(Path.GetFileName(request.FileName ?? "sample"),
            bytes is null ? BinaryData.FromString(request.Text) : BinaryData.FromBytes(bytes),
            request.ContentType ?? (bytes is null ? "text/plain" : "application/octet-stream"), new Dictionary<string, string>());
        var parsed = await parser.Module.ParseAsync(new("mapping-sample", Guid.NewGuid().ToString("n"), DateTimeOffset.UtcNow),
            new IntegrationBatch([payload]), new ModuleStepDefinition { Module = request.ParserModule, Settings = request.ParserSettings ?? [] }, ct);
        if (parsed.Count == 0) throw new ArgumentException("The selected parser produced no sample records.");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var samples = new List<string>();
        var recordCount = 0;
        var totalCharacters = 0;
        foreach (var item in parsed.Payloads)
        {
            var inspection = ParsedDataMapping.Inspect(item.GetText(), request.Format, request.RecordPath, ct);
            foreach (var field in inspection.Fields) fields.Add(field);
            if (fields.Count > ParsedDataMapping.MaxFields) throw new ArgumentException("Sample exceeds 256 discoverable paths.");
            samples.Add(inspection.ParsedData);
            totalCharacters += inspection.ParsedData.Length;
            if (totalCharacters > ParsedDataMapping.MaxCharacters) throw new ArgumentException("Parsed sample output exceeds 1000000 characters. Use a smaller sample.");
            recordCount += inspection.RecordCount;
        }
        return TypedResults.Ok(new ParsedDataInspection(samples[0], fields.Order(StringComparer.Ordinal).ToArray(), recordCount, samples));
    }
}
