using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Mulse.Ui.Models;

namespace Mulse.Ui.Components.Pages;

// This page's logic is split across several partial-class files by concern:
//   Designer.razor.cs         - state, lifecycle, load/save, and shared helpers/nested types (this file)
//   Designer.razor.Graph.cs   - canvas graph editing (sources, augments, routes, layout, edges)
//   Designer.razor.Sample.cs  - "seed from sample data" panel
//   Designer.razor.BizTalk.cs - "import from BizTalk" panel
//   Designer.razor.Join.cs    - multi-source join/map augment editor + generic settings editor
public partial class Designer : IAsyncDisposable
{
    private const double NodeWidth = 210;
    private const double NodeHeight = 96;
    private const double ColumnSpacing = 270;
    private const double RowSpacing = 150;
    private const double CanvasPadding = 40;
    private const double LanePaddingX = 18;
    private const double LaneHeaderHeight = 38;
    private const double LaneGap = 30;

    private const string JoinModuleId = "multi-source-join-map-augment";
    private const string WorkingDocumentSelector = "*";
    private const string LeftSourceKey = "leftSourceId";
    private const string RightSourceKey = "rightSourceId";
    private const string JoinKindKey = "joinKind";
    private const string CardinalityKey = "resultCardinality";
    private const string NestedPathKey = "nestedTargetPath";
    private const string JoinKeysKey = "joinKeysJson";
    private const string MappingsKey = "mappingJson";

    private static readonly string[] TriggerModeOptions = ["Disabled", "OnDemand", "Interval", "Push"];
    private static readonly string[] RetryBackoffOptions = ["Fixed", "Linear", "Exponential"];
    private static readonly string[] SourceKindOptions = ["Left", "Right", "Literal"];
    private static readonly string[] ConditionOptions = ["Always", "WhenMatched", "WhenUnmatched"];
    private static readonly string[] JoinKindOptions = ["Inner", "Left", "Right", "Full"];
    private static readonly string[] CardinalityOptions = ["FanOut", "Nested"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Parameter]
    public string? FlowId { get; set; }

    private readonly List<FlowNode> _nodes = [];
    private readonly List<SourceLane> _sources = [];
    private readonly Dictionary<string, JoinEditorState> _joinEditors = new(StringComparer.Ordinal);
    private readonly List<FieldMappingRow> _mappingRows = [];

    private IReadOnlyList<ModuleViewModel> _modules = [];
    private IReadOnlyList<ConfigReferenceUsageViewModel> _configUsages = [];
    private IReadOnlyList<BizTalkSettingRequirementViewModel> _bizTalkRequirements = [];
    private IReadOnlyList<string> _bizTalkWarnings = [];

    private ElementReference _canvasElement;
    private IJSObjectReference? _canvasModule;
    private DotNetObjectReference<Designer>? _selfReference;
    private bool _canvasInitialized;

    private string? _loadedFlowId;
    private bool _graphInitialized;
    private bool _isLoading = true;
    private bool _isSaving;
    private string? _statusMessage;
    private string? _errorMessage;
    private string? _selectedNodeId;
    private string _newSettingKey = string.Empty;
    private int _unresolvedConfigCount;
    private SeedSource _seedSource = SeedSource.Manual;

    private string _flowId = string.Empty;
    private bool _enabled = true;
    private string _triggerMode = "OnDemand";
    private string _intervalText = "00:05:00";
    private bool _runOnStartup;

    private bool _hasDefaultRetry;
    private int? _retryMaxAttempts;
    private double _retryDelaySeconds = 1;
    private string _retryBackoff = "Fixed";
    private double? _retryMaxDelaySeconds;

    private bool _showSamplePanel;
    private bool _isAnalyzingSample;
    private string _sampleText = string.Empty;
    private string? _fileName;
    private string? _contentType;
    private FlowDesignAnalysisViewModel? _analysis;

    private bool _showBizTalkPanel;
    private bool _isAnalyzingBizTalk;
    private string _sourcePath = string.Empty;
    private string _additionalSourcePaths = string.Empty;
    private string _additionalBindingPaths = string.Empty;
    private BizTalkSolutionAnalysisViewModel? _bizTalkAnalysis;

    private bool IsExistingFlow => !string.IsNullOrWhiteSpace(FlowId);

    private FlowNode? SelectedNode => _nodes.FirstOrDefault(node => string.Equals(node.Id, _selectedNodeId, StringComparison.Ordinal));

    private SourceLane? FindSource(string? sourceId)
        => string.IsNullOrWhiteSpace(sourceId)
            ? null
            : _sources.FirstOrDefault(source => string.Equals(source.Id, sourceId, StringComparison.OrdinalIgnoreCase));

    private SourceLane? SourceForNode(FlowNode node)
        => node.Stage is NodeStage.Fetch or NodeStage.Parse ? FindSource(node.SourceId) : null;

    protected override async Task OnInitializedAsync()
    {
        await LoadCatalogAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_graphInitialized && string.Equals(_loadedFlowId, FlowId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _graphInitialized = true;
        _loadedFlowId = FlowId;

        if (IsExistingFlow)
        {
            await LoadFlowAsync(FlowId!);
        }
        else
        {
            ResetGraph();
            _isLoading = false;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_canvasInitialized)
        {
            return;
        }

        _canvasInitialized = true;
        _selfReference = DotNetObjectReference.Create(this);
        _canvasModule = await JS.InvokeAsync<IJSObjectReference>("import", "./js/flow-canvas.js");
        await _canvasModule.InvokeVoidAsync("initialize", _canvasElement, _selfReference);
    }

    public async ValueTask DisposeAsync()
    {
        if (_canvasModule is not null)
        {
            try
            {
                await _canvasModule.InvokeVoidAsync("dispose", _canvasElement);
                await _canvasModule.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The circuit is already gone, so there is nothing left to clean up on the client.
            }
        }

        _selfReference?.Dispose();
    }

    [JSInvokable]
    public Task OnNodeMoved(string nodeId, double x, double y)
    {
        var node = _nodes.FirstOrDefault(candidate => string.Equals(candidate.Id, nodeId, StringComparison.Ordinal));
        if (node is not null)
        {
            node.X = x;
            node.Y = y;
            StateHasChanged();
        }

        return Task.CompletedTask;
    }

    private async Task LoadCatalogAsync()
    {
        try
        {
            _modules = await ApiClient.GetModulesAsync(CancellationToken.None);
            _configUsages = await ApiClient.GetConfigValueUsagesAsync(CancellationToken.None);
            UpdateUnresolvedConfigCount();
        }
        catch (Exception exception)
        {
            _errorMessage = $"Unable to load the module catalog: {exception.Message}";
        }
    }

    private async Task LoadFlowAsync(string flowId)
    {
        _isLoading = true;
        _errorMessage = null;

        try
        {
            var flow = await ApiClient.GetFlowAsync(flowId, CancellationToken.None);
            BuildFromFlow(flow);
            _seedSource = SeedSource.ExistingFlow;
            UpdateUnresolvedConfigCount();
        }
        catch (Exception exception)
        {
            ResetGraph();
            _errorMessage = $"Unable to load flow '{flowId}': {exception.Message}";
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void ResetGraph()
    {
        _nodes.Clear();
        _sources.Clear();
        _joinEditors.Clear();
        _mappingRows.Clear();
        _bizTalkRequirements = [];
        _bizTalkWarnings = [];
        _selectedNodeId = null;
        _seedSource = SeedSource.Manual;
        _flowId = string.Empty;
        _enabled = true;
        _triggerMode = "OnDemand";
        _intervalText = "00:05:00";
        _runOnStartup = false;
        _hasDefaultRetry = false;
        _retryMaxAttempts = null;
        _retryDelaySeconds = 1;
        _retryBackoff = "Fixed";
        _retryMaxDelaySeconds = null;
    }

    private void ClearCanvas()
    {
        var flowId = _flowId;
        ResetGraph();
        _flowId = flowId;
        _seedSource = IsExistingFlow ? SeedSource.ExistingFlow : SeedSource.Manual;
        _statusMessage = "Canvas cleared.";
    }

    private void BuildFromFlow(FlowViewModel flow)
    {
        _nodes.Clear();
        _sources.Clear();
        _joinEditors.Clear();
        _mappingRows.Clear();
        _bizTalkRequirements = [];
        _bizTalkWarnings = [];
        _selectedNodeId = null;

        _flowId = flow.Id;
        _enabled = flow.Enabled;
        _triggerMode = flow.TriggerMode;
        _intervalText = flow.Interval?.ToString() ?? "00:05:00";
        _runOnStartup = flow.RunOnStartup;

        if (flow.Retry is { } retry)
        {
            _hasDefaultRetry = true;
            _retryMaxAttempts = retry.MaxAttempts;
            _retryDelaySeconds = retry.DelaySeconds;
            _retryBackoff = retry.Backoff;
            _retryMaxDelaySeconds = retry.MaxDelaySeconds;
        }
        else
        {
            _hasDefaultRetry = false;
            _retryMaxAttempts = null;
            _retryDelaySeconds = 1;
            _retryBackoff = "Fixed";
            _retryMaxDelaySeconds = null;
        }

        foreach (var source in flow.Sources)
        {
            CreateSourceLane(
                source.Id,
                source.Fetch.Module,
                source.Fetch.Settings,
                source.Parse.Module,
                source.Parse.Settings,
                source.InputSourceIds);
        }

        var order = 0;
        foreach (var augment in flow.Augments)
        {
            CreateNode(NodeStage.Augment, augment.Module, augment.Settings, order: order++);
        }

        var routeIndex = 0;
        foreach (var route in flow.Deliveries)
        {
            var routeId = Guid.NewGuid().ToString("n");
            CreateNode(NodeStage.Render, route.Render.Module, route.Render.Settings, order: routeIndex, routeId: routeId);
            CreateNode(NodeStage.Deliver, route.Deliver.Module, route.Deliver.Settings, order: routeIndex, routeId: routeId);
            routeIndex++;
        }

        AutoLayout();
    }

    private FlowNode CreateNode(NodeStage stage, string moduleId, IReadOnlyDictionary<string, string>? settings = null, int order = 0, string? routeId = null, string? sourceId = null)
    {
        var node = new FlowNode
        {
            Stage = stage,
            ModuleId = moduleId,
            Order = order,
            RouteId = routeId ?? string.Empty,
            SourceId = sourceId ?? string.Empty,
            Settings = settings is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase)
        };

        _nodes.Add(node);
        return node;
    }

    private SourceLane CreateSourceLane(
        string sourceId,
        string fetchModuleId,
        IReadOnlyDictionary<string, string>? fetchSettings,
        string parseModuleId,
        IReadOnlyDictionary<string, string>? parseSettings,
        IEnumerable<string>? inputSourceIds)
    {
        var id = string.IsNullOrWhiteSpace(sourceId) ? NextSourceId() : sourceId.Trim();
        var lane = new SourceLane
        {
            Id = id,
            Fetch = CreateNode(NodeStage.Fetch, fetchModuleId, fetchSettings, sourceId: id),
            Parse = CreateNode(NodeStage.Parse, parseModuleId, parseSettings, sourceId: id)
        };

        if (inputSourceIds is not null)
        {
            foreach (var inputSourceId in inputSourceIds.Where(static value => !string.IsNullOrWhiteSpace(value)))
            {
                lane.InputSourceIds.Add(inputSourceId.Trim());
            }
        }

        _sources.Add(lane);
        return lane;
    }

    private string NextSourceId()
    {
        if (FindSource("primary") is null)
        {
            return "primary";
        }

        for (var index = 2; ; index++)
        {
            var candidate = $"source-{index}";
            if (FindSource(candidate) is null)
            {
                return candidate;
            }
        }
    }

    private async Task SaveAsync()
    {
        _isSaving = true;
        _statusMessage = null;
        _errorMessage = null;

        try
        {
            var flowId = IsExistingFlow ? FlowId! : _flowId.Trim();
            if (string.IsNullOrWhiteSpace(flowId))
            {
                throw new InvalidOperationException("Enter a flow id.");
            }

            if (_sources.Count == 0)
            {
                throw new InvalidOperationException("Add at least one source (a fetch and parse pair) to the canvas.");
            }

            if (DescribeSourceGraphError() is { } graphError)
            {
                throw new InvalidOperationException(graphError);
            }

            foreach (var source in _sources)
            {
                if (string.IsNullOrWhiteSpace(source.Fetch.ModuleId) || string.IsNullOrWhiteSpace(source.Parse.ModuleId))
                {
                    throw new InvalidOperationException($"Select a fetch module and a parse module for source '{source.Id}'.");
                }
            }

            var routes = Routes;
            if (routes.Count == 0)
            {
                throw new InvalidOperationException("At least one delivery route is required.");
            }

            foreach (var route in routes)
            {
                if (route.Render is null || route.Deliver is null
                    || string.IsNullOrWhiteSpace(route.Render.ModuleId)
                    || string.IsNullOrWhiteSpace(route.Deliver.ModuleId))
                {
                    throw new InvalidOperationException("Every delivery route needs a render and a deliver module.");
                }
            }

            var augments = AugmentNodes;
            if (augments.Any(static node => string.IsNullOrWhiteSpace(node.ModuleId)))
            {
                throw new InvalidOperationException("Select a module for every augment node.");
            }

            if (_enabled && _unresolvedConfigCount > 0)
            {
                throw new InvalidOperationException($"This flow has {_unresolvedConfigCount} unresolved config/secret reference(s). Resolve them on the Config values page before enabling it.");
            }

            TimeSpan? interval = null;
            if (string.Equals(_triggerMode, "Interval", StringComparison.OrdinalIgnoreCase))
            {
                if (!TimeSpan.TryParse(_intervalText, out var parsedInterval) || parsedInterval <= TimeSpan.Zero)
                {
                    throw new InvalidOperationException("Enter a valid interval such as 00:05:00.");
                }

                interval = parsedInterval;
            }

            var trigger = new FlowTriggerRequestViewModel(_triggerMode, interval, _runOnStartup);
            var retry = _hasDefaultRetry
                ? new RetryPolicyRequestViewModel(_retryMaxAttempts, _retryDelaySeconds, _retryBackoff, _retryMaxDelaySeconds)
                : null;
            var sourceSteps = _sources
                .Select(static source => new FlowSourceRequestViewModel(
                    source.Id.Trim(),
                    ToStepRequest(source.Fetch),
                    ToStepRequest(source.Parse),
                    source.InputSourceIds.ToArray()))
                .ToArray();
            var augmentSteps = augments.Select(ToStepRequest).ToArray();
            var deliverySteps = routes
                .Select(static route => new DeliveryRouteRequestViewModel(ToStepRequest(route.Render!), ToStepRequest(route.Deliver!)))
                .ToArray();

            FlowViewModel saved;
            if (IsExistingFlow)
            {
                saved = await ApiClient.UpdateFlowAsync(
                    new UpdateFlowRequestViewModel(flowId, _enabled, trigger, sourceSteps, augmentSteps, deliverySteps, retry),
                    CancellationToken.None);
                _statusMessage = $"Saved flow '{saved.Id}' with {sourceSteps.Length} source(s).";
            }
            else
            {
                var mappings = _mappingRows
                    .Where(static row => !string.IsNullOrWhiteSpace(row.TargetField))
                    .Select(static row => new FlowDesignFieldMappingRequestViewModel(row.SourceKind, row.SourcePath, row.TargetField, row.Condition, row.LiteralValue))
                    .ToArray();
                var hasJoinAugment = augments.Any(static node => string.Equals(node.ModuleId, JoinModuleId, StringComparison.OrdinalIgnoreCase));

                if (mappings.Length > 0 && hasJoinAugment)
                {
                    saved = await ApiClient.CreateDesignedFlowAsync(
                        new CreateDesignedFlowRequestViewModel(flowId, _enabled, trigger, sourceSteps, augmentSteps, deliverySteps, mappings),
                        CancellationToken.None);

                    if (retry is not null)
                    {
                        saved = await ApiClient.UpdateFlowAsync(
                            new UpdateFlowRequestViewModel(saved.Id, _enabled, trigger, sourceSteps, augmentSteps, deliverySteps, retry),
                            CancellationToken.None);
                    }
                }
                else
                {
                    saved = await ApiClient.CreateFlowAsync(
                        new CreateFlowRequestViewModel(flowId, _enabled, trigger, sourceSteps, augmentSteps, deliverySteps, retry),
                        CancellationToken.None);
                }

                _statusMessage = $"Created flow '{saved.Id}' with {sourceSteps.Length} source(s), {augmentSteps.Length} augment(s), and {deliverySteps.Length} delivery route(s).";
                _loadedFlowId = saved.Id;
                FlowId = saved.Id;
                _flowId = saved.Id;
                _seedSource = SeedSource.ExistingFlow;
                Navigation.NavigateTo($"/designer/{Uri.EscapeDataString(saved.Id)}");
            }

            _enabled = saved.Enabled;
            await RefreshConfigUsagesAsync();
        }
        catch (Exception exception)
        {
            _errorMessage = $"Unable to save the flow: {exception.Message}";
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async Task RefreshConfigUsagesAsync()
    {
        try
        {
            _configUsages = await ApiClient.GetConfigValueUsagesAsync(CancellationToken.None);
            UpdateUnresolvedConfigCount();
        }
        catch (Exception exception)
        {
            _errorMessage = $"Unable to refresh config references: {exception.Message}";
        }
    }

    private void UpdateUnresolvedConfigCount()
    {
        var flowId = IsExistingFlow ? FlowId : _flowId;
        _unresolvedConfigCount = string.IsNullOrWhiteSpace(flowId)
            ? 0
            : _configUsages.Count(usage => !usage.IsResolved && string.Equals(usage.FlowId, flowId, StringComparison.OrdinalIgnoreCase));
    }

    private void AddMappingRow() => _mappingRows.Add(new FieldMappingRow());

    private IReadOnlyList<ModuleViewModel> ModulesForStage(NodeStage stage)
    {
        var kind = ModuleKindFor(stage);
        return _modules.Where(module => string.Equals(module.Kind, kind, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private ModuleViewModel? FindCatalogModule(string? moduleId)
    {
        if (string.IsNullOrWhiteSpace(moduleId))
        {
            return null;
        }

        return _modules.FirstOrDefault(module => string.Equals(module.Id, moduleId, StringComparison.OrdinalIgnoreCase));
    }

    private static string ModuleKindFor(NodeStage stage) => stage switch
    {
        NodeStage.Fetch => "Fetch",
        NodeStage.Parse => "Parse",
        NodeStage.Augment => "OrchestrationAugment",
        NodeStage.Render => "Render",
        _ => "Deliver"
    };

    private static string StageDescription(NodeStage stage) => stage switch
    {
        NodeStage.Fetch => "Acquires this source's raw content. It can consume other sources' parsed output as its input.",
        NodeStage.Parse => "Converts this source's raw content into the working payload shape.",
        NodeStage.Augment => "Join, decision, and mapping steps executed once every source has been fetched and parsed.",
        NodeStage.Render => "Prepares outbound content for one delivery route.",
        _ => "Dispatches the rendered payload for one delivery route."
    };

    private string NodeHeading(FlowNode node) => node.Stage switch
    {
        NodeStage.Fetch => string.IsNullOrWhiteSpace(node.SourceId) ? "Fetch" : $"Fetch - {node.SourceId}",
        NodeStage.Parse => string.IsNullOrWhiteSpace(node.SourceId) ? "Parse" : $"Parse - {node.SourceId}",
        NodeStage.Augment => $"Augment {AugmentNodes.IndexOf(node) + 1}",
        NodeStage.Render => $"Render {RouteNumber(node)}",
        _ => $"Deliver {RouteNumber(node)}"
    };

    private int RouteNumber(FlowNode node)
    {
        var index = Routes.FindIndex(route => string.Equals(route.RouteId, node.RouteId, StringComparison.Ordinal));
        return index < 0 ? 1 : index + 1;
    }

    private string NodeTitle(FlowNode node)
    {
        if (string.IsNullOrWhiteSpace(node.ModuleId))
        {
            return "(no module selected)";
        }

        return FindCatalogModule(node.ModuleId)?.DisplayName ?? node.ModuleId;
    }

    private static string NodeSubtitle(FlowNode node)
    {
        var settings = node.Settings.Count(static entry => !string.IsNullOrWhiteSpace(entry.Value));
        return string.IsNullOrWhiteSpace(node.ModuleId)
            ? "Click to configure"
            : $"{Truncate(node.ModuleId, 20)} - {settings} setting(s)";
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, Math.Max(1, maxLength - 1)), "\u2026");
    }

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static FlowStepRequestViewModel ToStepRequest(FlowNode node)
        => new(node.ModuleId, CreateSettingsCopy(node.Settings));

    private static Dictionary<string, string> CreateSettingsCopy(Dictionary<string, string> settings)
    {
        return settings
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Value))
            .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string[] SplitPaths(string value)
        => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string FormatList(IReadOnlyList<string> values)
        => values.Count == 0 ? "None" : string.Join(", ", values);

    private static string? ChooseModule(
        IReadOnlyList<ModuleSuggestionViewModel> modules,
        string? excludedId = null,
        string? requiredCapability = null,
        string? requiredProtocol = null,
        string? requiredFormat = null)
    {
        static bool Matches(ModuleSuggestionViewModel module, string? excludedId, string? requiredCapability, string? requiredProtocol, string? requiredFormat)
        {
            return !string.Equals(module.Id, excludedId, StringComparison.OrdinalIgnoreCase)
                && (requiredCapability is null || module.Capabilities.Contains(requiredCapability, StringComparer.OrdinalIgnoreCase))
                && (requiredProtocol is null || module.Protocols.Contains(requiredProtocol, StringComparer.OrdinalIgnoreCase))
                && (requiredFormat is null || module.SupportedFormats.Contains(requiredFormat, StringComparer.OrdinalIgnoreCase));
        }

        return modules.FirstOrDefault(module => module.IsRecommended && Matches(module, excludedId, requiredCapability, requiredProtocol, requiredFormat))?.Id
            ?? modules.FirstOrDefault(module => Matches(module, excludedId, requiredCapability, requiredProtocol, requiredFormat))?.Id
            ?? modules.FirstOrDefault(module => module.IsRecommended && !string.Equals(module.Id, excludedId, StringComparison.OrdinalIgnoreCase))?.Id
            ?? modules.FirstOrDefault(module => !string.Equals(module.Id, excludedId, StringComparison.OrdinalIgnoreCase))?.Id
            ?? modules.FirstOrDefault()?.Id;
    }

    private static string CreateDefaultFlowId(string? fileName, string detectedFormat)
    {
        var seed = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(seed))
        {
            seed = detectedFormat;
        }

        var characters = seed
            .ToLowerInvariant()
            .Select(static character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        var normalized = string.Join(string.Empty, characters).Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "flow-designer-workflow" : $"{normalized}-workflow";
    }

    private static int? ParseNullableInt(string? value) => int.TryParse(value, out var parsed) ? parsed : null;

    private static double ParseDouble(string? value, double fallback) => double.TryParse(value, out var parsed) ? parsed : fallback;

    private static double? ParseNullableDouble(string? value) => double.TryParse(value, out var parsed) ? parsed : null;

    private enum NodeStage
    {
        Fetch,
        Parse,
        Augment,
        Render,
        Deliver
    }

    private enum SeedSource
    {
        Manual,
        Sample,
        BizTalk,
        ExistingFlow
    }

    private sealed class FlowNode
    {
        public string Id { get; } = Guid.NewGuid().ToString("n");

        public NodeStage Stage { get; init; }

        public string ModuleId { get; set; } = string.Empty;

        public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public int Order { get; set; }

        public string RouteId { get; init; } = string.Empty;

        public string SourceId { get; set; } = string.Empty;

        public double X { get; set; }

        public double Y { get; set; }
    }

    private sealed class SourceLane
    {
        public required string Id { get; set; }

        public required FlowNode Fetch { get; init; }

        public required FlowNode Parse { get; init; }

        public List<string> InputSourceIds { get; } = [];
    }

    private sealed record FlowEdge(FlowNode From, FlowNode To, bool IsDependency);

    private sealed record RouteView(string RouteId, int Order, FlowNode? Render, FlowNode? Deliver);

    private enum JoinMappingField
    {
        SourceKind,
        SourcePath,
        TargetField,
        Condition,
        LiteralValue
    }

    private sealed class JoinEditorState
    {
        public List<JoinKeyRow> Keys { get; } = [];

        public List<FieldMappingRow> Mappings { get; } = [];
    }

    private sealed class JoinKeyRow
    {
        public string LeftPath { get; set; } = string.Empty;

        public string RightPath { get; set; } = string.Empty;
    }

    private sealed record JoinKeyDto
    {
        public string? LeftPath { get; init; }

        public string? RightPath { get; init; }
    }

    private sealed record JoinMappingDto
    {
        public string? SourceKind { get; init; }

        public string? SourcePath { get; init; }

        public string? TargetField { get; init; }

        public string? Condition { get; init; }

        public string? LiteralValue { get; init; }
    }

    private sealed class FieldMappingRow
    {
        public string SourceKind { get; set; } = "Left";

        public string SourcePath { get; set; } = string.Empty;

        public string Condition { get; set; } = "Always";

        public string TargetField { get; set; } = string.Empty;

        public string LiteralValue { get; set; } = string.Empty;
    }
}
