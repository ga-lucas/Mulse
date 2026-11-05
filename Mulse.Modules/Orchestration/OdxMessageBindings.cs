using System.Text.Json;
using System.Text.RegularExpressions;
using Mulse.Modules.Mapping;

namespace Mulse.Modules.Orchestration;

public sealed record OdxBoundMap(OdxActivity Activity, IReadOnlyList<string> RendererDependencies);
public sealed record OdxPipelineStage(string Module, IReadOnlyDictionary<string, string> Settings);
public delegate Task<IntegrationBatch> OdxPortSender(FlowExecutionContext context, IntegrationBatch message, CancellationToken cancellationToken);

/// <summary>Explicit native message/map/port adapters; never loads a CLR type named by source expressions.</summary>
public static partial class OdxMessageBindings
{
    [GeneratedRegex(@"\A\s*(?<type>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*System\.Type\.GetType\(\s*(?<selector>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*\)\s*;\s*transform\s*\(\s*(?<output>[A-Za-z_][A-Za-z0-9_]*)\s*\)\s*=\s*\k<type>\s*\(\s*(?<inputs>[A-Za-z_][A-Za-z0-9_]*(?:\s*,\s*[A-Za-z_][A-Za-z0-9_]*)*)\s*\)\s*;(?<properties>[\s\S]*)\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex DynamicTransform();
    [GeneratedRegex(@"\A\s*(?:(?://[^\n]*(?:\n|\z)|/\*[\s\S]*?\*/)\s*)*[A-Za-z_][A-Za-z0-9_]*\s*=\s*new\s+System\.Xml\.XmlDocument\s*\(", RegexOptions.CultureInvariant, 100)]
    private static partial Regex XmlConstructor();
    [GeneratedRegex(@"\A\s*(?<output>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<input>[A-Za-z_][A-Za-z0-9_]*)\s*;\s*\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex MessageCopy();
    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex MessageName();

    internal static bool IsDynamicTransform(OdxNode node) => node.ShapeType == "MessageAssignment"
        && node.Properties.GetValueOrDefault("Expression") is { Length: <= 65536 } expression
        && (DynamicTransform().IsMatch(expression) || XmlConstructor().IsMatch(expression) && expression.Contains("transform", StringComparison.Ordinal));
    internal static bool IsMessageCopy(OdxNode node, IReadOnlySet<string> messageNames)
    {
        var expression = node.Properties.GetValueOrDefault("Expression") ?? "";
        if (expression.Length > 65536) return false;
        var match = MessageCopy().Match(expression);
        return match.Success && messageNames.Contains(match.Groups["input"].Value);
    }

    public static OdxBoundMap BindDynamicTransform(OdxNode node, IReadOnlyList<RenderDispatchCase> choices, IRenderModuleInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        var expression = node.Properties.GetValueOrDefault("Expression") ?? "";
        if (expression.Length > 65536) throw new NotSupportedException("ODX map statement exceeds 65536 characters.");
        (string XmlName, string MessageName, string Transform, Func<OdxExecutionFrame, string> Document)? construction = null;
        if (XmlConstructor().IsMatch(expression))
        {
            construction = OdxExpressionCompiler.CompileXmlTransformPrefix(expression);
            expression = construction.Value.Transform;
        }
        var match = DynamicTransform().Match(expression);
        if (node.ShapeType != "MessageAssignment" || !match.Success)
            throw new NotSupportedException($"ODX node {node.Id} is not a supported complete GetType/transform statement. Nothing was bound.");
        var output = match.Groups["output"].Value;
        var properties = OdxExpressionCompiler.CompileMessageProperties(match.Groups["properties"].Value, output);
        var configured = RenderDispatchDefinition.Read(JsonSerializer.Serialize(choices, JsonSerializerOptions.Web)).ToDictionary(choice => choice.Value, StringComparer.Ordinal);
        var inputs = match.Groups["inputs"].Value.Split(',').Select(input => input.Trim()).ToArray();
        if (inputs.Length > 32) throw new NotSupportedException("ODX dynamic maps accept at most 32 named inputs.");
        var selector = match.Groups["selector"].Value.Split('.');
        if (construction is { } prefix && (prefix.MessageName != output || inputs.Length != 1 || inputs[0] != output
            || selector[0] == output || selector[0] == prefix.XmlName || match.Groups["type"].Value == prefix.XmlName))
            throw new NotSupportedException("XML construction must feed a single-input transform of that same message, without reading stale construction facts.");
        return new(async (_, frame, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!frame.Variables.TryGetValue(selector[0], out var value)) throw new InvalidOperationException($"ODX map selector root '{selector[0]}' is uninitialized.");
            foreach (var part in selector.Skip(1))
                value = value is System.Text.Json.Nodes.JsonObject record ? record[part] : throw new InvalidOperationException("ODX map selector requires typed object facts.");
            if (value is not System.Text.Json.Nodes.JsonValue scalar || !scalar.TryGetValue<string>(out var selection) || !configured.TryGetValue(selection, out var choice))
                throw new InvalidOperationException($"ODX map node {node.Id} selected an unconfigured identity. CLR type loading is prohibited.");
            IntegrationBatch? constructed = null;
            if (construction is { } xml)
            {
                var document = XmlMappingSchemas.ReadXml(xml.Document(frame));
                constructed = new([new(xml.MessageName + ".xml", BinaryData.FromString(document.ToString(System.Xml.Linq.SaveOptions.DisableFormatting)),
                    "application/xml", new Dictionary<string, string>(StringComparer.Ordinal))]);
            }
            var messages = constructed is null ? inputs.Select(input => RequiredMessage(frame, input)).ToArray() : new[] { constructed };
            var metadata = properties(frame);
            // Multi-message renderers receive the source arguments in order. Their schema/part adapter remains explicit.
            var rendered = await invoker.RenderAsync(choice.Module, frame.Context,
                new(messages.SelectMany(message => message.Payloads).ToArray()), choice.Settings, token).ConfigureAwait(false);
            if (metadata.Count > 0) rendered = rendered with { Payloads = rendered.Payloads.Select(payload =>
            {
                var values = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase);
                foreach (var property in metadata) values[property.Key] = property.Value;
                return payload with { Metadata = values };
            }).ToArray() };
            if (construction is { } completed && constructed is not null) SetMessage(frame, completed.XmlName, constructed);
            SetMessage(frame, output, rendered);
        }, configured.Values.Select(choice => choice.Module).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static OdxActivity BindMessageCopy(OdxNode node)
    {
        var expression = node.Properties.GetValueOrDefault("Expression") ?? "";
        if (expression.Length > 65536) throw new NotSupportedException("ODX copy statement exceeds 65536 characters.");
        var match = MessageCopy().Match(expression);
        if (node.ShapeType is not ("VariableAssignment" or "MessageAssignment") || !match.Success)
            throw new NotSupportedException($"ODX node {node.Id} is not a complete named-message copy.");
        var input = match.Groups["input"].Value; var output = match.Groups["output"].Value;
        return (_, frame, token) =>
        {
            token.ThrowIfCancellationRequested();
            var source = RequiredMessage(frame, input);
            var facts = frame.Variables.GetValueOrDefault(input)?.DeepClone();
            SetMessage(frame, output, new(source.Payloads.Select(payload => payload with { Metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.Ordinal) }).ToArray()));
            if (facts is not null) frame.Variables[output] = facts;
            return Task.CompletedTask;
        };
    }

    public static OdxBoundMap BindPipeline(string inputMessage, string outputMessage, IReadOnlyList<OdxPipelineStage> stages, IRenderModuleInvoker invoker)
    {
        ValidateName(inputMessage); ValidateName(outputMessage); ArgumentNullException.ThrowIfNull(invoker);
        if (stages.Count is < 1 or > 32) throw new ArgumentException("An explicit pipeline requires 1-32 ordered render-stage equivalents.");
        var frozen = stages.Select((stage, index) => new RenderDispatchCase { Value = index.ToString(System.Globalization.CultureInfo.InvariantCulture), Module = stage.Module, Settings = new(stage.Settings, StringComparer.OrdinalIgnoreCase) }).ToArray();
        var validated = RenderDispatchDefinition.Read(JsonSerializer.Serialize(frozen, JsonSerializerOptions.Web));
        return new(async (_, frame, token) =>
        {
            var message = RequiredMessage(frame, inputMessage);
            foreach (var stage in validated)
                message = await invoker.RenderAsync(stage.Module, frame.Context, message, stage.Settings, token).ConfigureAwait(false);
            SetMessage(frame, outputMessage, message);
        }, validated.Select(stage => stage.Module).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static OdxActivity BindSend(OdxNode node, IReadOnlyDictionary<string, OdxPortSender> ports)
    {
        var (port, message) = SendReferences(node);
        if (!ports.TryGetValue(port, out var sender) || sender is null) throw new NotSupportedException($"ODX port '{port}' has no installed native adapter.");
        if (node.Properties.GetValueOrDefault("OperationMessageName") == "Response")
            throw new NotSupportedException("Response sends require an explicit reply binding, not an ordinary outbound port.");
        return async (_, frame, token) =>
        {
            var response = await sender(frame.Context, RequiredMessage(frame, message), token).ConfigureAwait(false);
            if (response.Count > 0) frame.Context.CaptureResponse(response);
        };
    }

    public static OdxActivity BindReply(OdxNode node, string requestPort)
    {
        var (port, message) = SendReferences(node);
        if (port != requestPort || node.Properties.GetValueOrDefault("OperationMessageName") != "Response")
            throw new NotSupportedException("Reply must target the explicitly configured request port's Response operation message.");
        return (_, frame, token) => { token.ThrowIfCancellationRequested(); frame.Context.CaptureResponse(RequiredMessage(frame, message)); return Task.CompletedTask; };
    }

    private static (string Port, string Message) SendReferences(OdxNode node)
    {
        var port = node.Properties.GetValueOrDefault("PortName"); var message = node.Properties.GetValueOrDefault("MessageName");
        if (node.ShapeType != "Send" || string.IsNullOrWhiteSpace(port) || string.IsNullOrWhiteSpace(message)) throw new ArgumentException("Send requires source PortName and MessageName.");
        ValidateName(message); return (port, message);
    }
    private static void ValidateName(string name) { if (!MessageName().IsMatch(name)) throw new ArgumentException("A named ODX message requires an ASCII identifier."); }
    private static IntegrationBatch RequiredMessage(OdxExecutionFrame frame, string name)
        => frame.Messages.TryGetValue(name, out var message) ? message : throw new InvalidOperationException($"ODX message '{name}' is uninitialized.");
    private static void SetMessage(OdxExecutionFrame frame, string name, IntegrationBatch message)
    {
        frame.Messages[name] = message;
        frame.Variables.Remove(name);
    }
}
