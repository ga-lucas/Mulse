using System.Text.Json;
using System.Text.Json.Serialization;
using Mulse.Modules.Mapping;

namespace Mulse.Modules.Orchestration;

public sealed class OdxSendBinding
{
    public int NodeId { get; set; }
    public string Module { get; set; } = "";
    public Dictionary<string, string> Settings { get; set; } = [];
    public IReadOnlyList<OdxPipelineStage> Pipeline { get; set; } = [];
}

/// <summary>Explicit source-send adapters. Pipeline entries are reviewed renderer equivalents, not inferred BizTalk components.</summary>
public static class OdxSendBindings
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static IReadOnlyList<OdxSendBinding> Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 262144) throw new ArgumentException("ODX sendsJson requires at most 262144 characters.");
        OdxSendBinding[] bindings;
        try { bindings = JsonSerializer.Deserialize<OdxSendBinding[]>(json, Options) ?? throw new ArgumentException("ODX sendsJson must be an array, not null."); }
        catch (JsonException exception) { throw new ArgumentException("Invalid ODX sendsJson.", exception); }
        if (bindings.Length > 128) throw new ArgumentException("ODX sendsJson supports at most 128 source send bindings.");
        var ids = new HashSet<int>();
        foreach (var binding in bindings)
        {
            if (binding is null || binding.NodeId < 1 || !ids.Add(binding.NodeId) || string.IsNullOrWhiteSpace(binding.Module)
                || binding.Module.Length > 256 || binding.Module != binding.Module.Trim() || binding.Settings is null
                || binding.Settings.Count > 128 || binding.Settings.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                || binding.Pipeline is null || binding.Pipeline.Count > 32 || binding.Pipeline.Any(stage => stage is null || stage.Settings is null))
                throw new ArgumentException("ODX send bindings require unique positive node IDs, delivery module IDs, non-null settings and at most 32 pipeline stages.");
            binding.Settings = new(binding.Settings, StringComparer.OrdinalIgnoreCase);
            ModuleSettingReader.GetBoolean(binding.Settings, "expectsResponse", false, binding.Module);
            RenderPipelineDefinition.Read(JsonSerializer.Serialize(binding.Pipeline, Options), allowEmpty: true);
        }
        return bindings;
    }

    public static OdxActivity Bind(OdxNode node, OdxSendBinding binding, IRenderModuleInvoker renderers, IDeliverModuleInvoker deliveries)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(renderers);
        ArgumentNullException.ThrowIfNull(deliveries);
        if (node.Id != binding.NodeId) throw new ArgumentException("The outbound adapter must match its exact source Send node ID.");
        var frozen = Read(JsonSerializer.Serialize(new[] { binding }, Options)).Single();
        var port = node.Properties.GetValueOrDefault("PortName") ?? throw new ArgumentException("Send requires its source port.");
        return OdxMessageBindings.BindSend(node, new Dictionary<string, OdxPortSender>(StringComparer.Ordinal)
        {
            [port] = async (context, batch, token) =>
            {
                foreach (var stage in frozen.Pipeline)
                    batch = await renderers.RenderAsync(stage.Module, context, batch, stage.Settings, token).ConfigureAwait(false);
                return await deliveries.DeliverAsync(frozen.Module, context, batch, frozen.Settings, token).ConfigureAwait(false);
            }
        });
    }
}
