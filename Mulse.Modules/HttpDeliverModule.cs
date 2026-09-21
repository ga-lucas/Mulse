using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Mulse.Modules;

public sealed class HttpDeliverModule(IHttpClientFactory httpClientFactory) : IRequestResponseDeliverModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("url", "URL", "The destination HTTP endpoint.", true),
        new("method", "Method", "The HTTP method used for outbound calls.", false, ModuleSettingInputKind.Select, "POST",
        [
            new ModuleSettingOption("POST", "POST"),
            new ModuleSettingOption("PUT", "PUT"),
            new ModuleSettingOption("PATCH", "PATCH")
        ]),
        new("contentType", "Content type override", "Optional outbound content type. Leave blank to use the rendered payload content type.", false, ModuleSettingInputKind.Text),
        new("headersJson", "Headers JSON", "An optional JSON object of additional request headers.", false, ModuleSettingInputKind.TextArea, "{}")
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Xml, ModuleDataFormat.Csv, ModuleDataFormat.Text],
        [ModuleProtocol.Http],
        [ModuleCapability.Delivery]);

    public ModuleDescriptor Descriptor { get; } = new(
        "http-deliver",
        "HTTP deliver",
        ModuleKind.Deliver,
        "Sends rendered payloads to a remote HTTP API.",
        SettingDescriptors,
        Recommendation);

    public async Task DeliverAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        await foreach (var _ in SendAsync(context, batch, step, captureResponse: false, cancellationToken).ConfigureAwait(false))
        {
            // Fire-and-forget: response bodies are not needed for one-way delivery.
        }
    }

    public async Task<IntegrationBatch> DeliverAndCaptureResponseAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var responses = new List<IntegrationPayload>(batch.Count);
        await foreach (var response in SendAsync(context, batch, step, captureResponse: true, cancellationToken).ConfigureAwait(false))
        {
            if (response is not null)
            {
                responses.Add(response);
            }
        }

        return new IntegrationBatch(responses);
    }

    private async IAsyncEnumerable<IntegrationPayload?> SendAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        bool captureResponse,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var url = ModuleSettingReader.GetRequired(step.Settings, "url", Descriptor.Id);
        var method = ModuleSettingReader.GetOptional(step.Settings, "method") ?? "POST";
        var configuredContentType = ModuleSettingReader.GetOptional(step.Settings, "contentType");
        var headersJson = ModuleSettingReader.GetOptional(step.Settings, "headersJson");
        var client = httpClientFactory.CreateClient(nameof(HttpDeliverModule));

        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            var content = new ByteArrayContent(payload.Content.ToArray());
            var resolvedContentType = string.IsNullOrWhiteSpace(configuredContentType)
                ? ResolvePayloadContentType(payload)
                : configuredContentType;
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(resolvedContentType);
            request.Content = content;
            request.Headers.TryAddWithoutValidation("X-Mulse-FlowId", context.FlowId);
            request.Headers.TryAddWithoutValidation("X-Mulse-ExecutionId", context.ExecutionId);
            request.Headers.TryAddWithoutValidation("X-Mulse-PayloadName", payload.Name);

            foreach (var header in ParseHeaders(headersJson))
            {
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    _ = request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            foreach (var metadataHeader in ResolveMetadataHeaders(payload.Metadata))
            {
                if (!request.Headers.TryAddWithoutValidation(metadataHeader.Key, metadataHeader.Value))
                {
                    _ = request.Content?.Headers.TryAddWithoutValidation(metadataHeader.Key, metadataHeader.Value);
                }
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (!captureResponse)
            {
                yield return null;
                continue;
            }

            var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var responseContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            yield return new IntegrationPayload(
                payload.Name,
                BinaryData.FromBytes(responseBytes),
                responseContentType,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["requestPayloadName"] = payload.Name,
                    ["statusCode"] = ((int)response.StatusCode).ToString()
                });
        }
    }

    private static IReadOnlyDictionary<string, string> ParseHeaders(string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var node = JsonNode.Parse(headersJson) as JsonObject;
        if (node is null)
        {
            throw new ArgumentException("The HTTP deliver headersJson setting must be a JSON object.", nameof(headersJson));
        }

        return node.ToDictionary(
            static entry => entry.Key,
            static entry => JsonPayloadNavigator.ExtractScalarText(entry.Value) ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolvePayloadContentType(IntegrationPayload payload)
    {
        if (payload.Metadata.TryGetValue("contentTypeOverride", out var overrideContentType)
            && !string.IsNullOrWhiteSpace(overrideContentType))
        {
            return overrideContentType;
        }

        return payload.ContentType;
    }

    private static IEnumerable<KeyValuePair<string, string>> ResolveMetadataHeaders(IReadOnlyDictionary<string, string> metadata)
    {
        foreach (var entry in metadata)
        {
            if (!entry.Key.StartsWith("httpHeader.", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(entry.Value))
            {
                continue;
            }

            yield return new KeyValuePair<string, string>(entry.Key[11..], entry.Value);
        }
    }
}
