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
//   Designer.razor.Graph.cs   - canvas graph editing (fetch/parse nodes, augments, routes, layout, edges)
//   Designer.razor.Sample.cs  - sample-data analysis and source-lane drafting
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
    private static readonly string[] SourceKindOptions = ["Left", "Right", "Literal", "Transform"];
    private static readonly string[] ScalarSourceKindOptions = ["Left", "Right", "Literal"];
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
    private readonly Dictionary<string, JoinEditorState> _joinEditors = new(StringComparer.Ordinal);

    private IReadOnlyList<ModuleViewModel> _modules = [];
    private IReadOnlyList<string> _configReferences = [];
    private IReadOnlyList<ConfigReferenceUsageViewModel> _configUsages = [];
    private IReadOnlyList<BizTalkSettingRequirementViewModel> _bizTalkRequirements = [];
    private IReadOnlyList<string> _bizTalkWarnings = [];
    private FlowMigrationReportViewModel? _migration;

    private ElementReference _canvasElement;
    private ElementReference _toolboxElement;
    private Microsoft.AspNetCore.Components.Forms.InputFile? _canvasFileInput;
    private IJSObjectReference? _canvasModule;
    private DotNetObjectReference<Designer>? _selfReference;
    private bool _canvasInitialized;

    // The SVG viewBox doubles as the pan/zoom "camera": JS mutates it live during a gesture and only
    // reports the final value back here once per gesture (see wwwroot/js/flow-canvas.js), the same
    // pattern already used for node-drag positions, so a mid-gesture Blazor re-render can't cause a jump.
    private double _viewX;
    private double _viewY;
    private double _viewW = 1080;
    private double _viewH = 520;

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
    private string _flowName = string.Empty;
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
    private bool _showMappingDesigner;
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

    private void ToggleMappingDesigner() => _showMappingDesigner = !_showMappingDesigner;

    private void AddXmlMappingRender()
    {
        if (FindCatalogModule("xml-mapping-render") is null)
        {
            _errorMessage = "Load the compatibility package to add an XML mapping render.";
            return;
        }
        var route = Routes.FirstOrDefault(candidate => candidate.Render is null);
        var render = CreateNode(NodeStage.Render, "xml-mapping-render",
            new Dictionary<string, string> { ["mappingJson"] = JsonSerializer.Serialize(new XmlMappingViewModel(), JsonOptions) },
            order: route?.Order ?? Routes.Count, routeId: route?.RouteId ?? Guid.NewGuid().ToString("n"));
        _selectedNodeId = render.Id;
        AutoLayout();
        _statusMessage = "Added XML mapping render. Edit its fields in Mapping Designer and add a deliver node before saving.";
    }

    private FlowNode? SelectedNode => _nodes.FirstOrDefault(node => string.Equals(node.Id, _selectedNodeId, StringComparison.Ordinal));

    private List<FlowNode> FetchNodes => _nodes.Where(static node => node.Stage == NodeStage.Fetch).ToList();

    private List<FlowNode> ParseNodes => _nodes.Where(static node => node.Stage == NodeStage.Parse).ToList();

    private FlowNode? FindFetch(string? fetchId) => FindGraphNode(NodeStage.Fetch, fetchId);

    private FlowNode? FindParse(string? parseId) => FindGraphNode(NodeStage.Parse, parseId);

    private FlowNode? FindGraphNode(NodeStage stage, string? graphId)
        => string.IsNullOrWhiteSpace(graphId)
            ? null
            : _nodes.FirstOrDefault(node => node.Stage == stage && string.Equals(node.GraphId, graphId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Fetch and parse node ids share one namespace (the backend rejects a fetch and a parse with the same id).</summary>
    private bool IsGraphIdTaken(string graphId, FlowNode? except = null)
        => _nodes.Any(node => node.Stage is NodeStage.Fetch or NodeStage.Parse
            && !ReferenceEquals(node, except)
            && string.Equals(node.GraphId, graphId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The fetch nodes whose raw output <paramref name="parse"/> parses, in its <see cref="FlowNode.FetchNodeIds"/> order.</summary>
    private List<FlowNode> FetchesForParse(FlowNode parse)
        => parse.FetchNodeIds.Select(FindFetch).OfType<FlowNode>().Distinct().ToList();

    /// <summary>The parse nodes that list <paramref name="fetch"/> in their <see cref="FlowNode.FetchNodeIds"/>.</summary>
    private List<FlowNode> ParsesForFetch(FlowNode fetch)
        => ParseNodes.Where(parse => parse.FetchNodeIds.Contains(fetch.GraphId, StringComparer.OrdinalIgnoreCase)).ToList();

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
        await _canvasModule.InvokeVoidAsync("initializeToolbox", _toolboxElement, _canvasElement, _selfReference);
        await _canvasModule.InvokeVoidAsync("initializeFileDrop", _canvasElement, _canvasFileInput!.Element, _selfReference);
    }

    public async ValueTask DisposeAsync()
    {
        if (_canvasModule is not null)
        {
            try
            {
                await _canvasModule.InvokeVoidAsync("disposeToolbox", _toolboxElement);
                await _canvasModule.InvokeVoidAsync("disposeFileDrop", _canvasElement);
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

    public sealed record NodeMove(string Id, double X, double Y);

    [JSInvokable]
    public Task OnNodesMoved(NodeMove[] moves)
    {
        foreach (var move in moves)
        {
            var node = _nodes.FirstOrDefault(candidate => string.Equals(candidate.Id, move.Id, StringComparison.Ordinal));
            if (node is not null)
            {
                node.X = move.X;
                node.Y = move.Y;
            }
        }

        StateHasChanged();
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnViewChanged(double x, double y, double w, double h)
    {
        _viewX = x;
        _viewY = y;
        _viewW = w;
        _viewH = h;
        StateHasChanged();
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnToolboxDrop(string nodeType, double x, double y)
    {
        switch (nodeType)
        {
            case "source":
                AddSourceLaneAt(x, y);
                break;
            case "fetch":
                AddFetchNodeAt(x, y);
                break;
            case "parse":
                AddParseNodeAt(x, y);
                break;
            case "augment":
                AddAugmentNodeAt(x, y);
                break;
            case "join":
                AddJoinAugmentNodeAt(x, y);
                break;
            case "route":
                AddDeliveryRouteAt(x, y);
                break;
            case "render":
                AddRenderNodeAt(x, y);
                break;
            case "deliver":
                AddDeliverNodeAt(x, y);
                break;
        }

        StateHasChanged();
        return Task.CompletedTask;
    }

    private async Task ZoomInAsync() => await ZoomByAsync(1.25);

    private async Task ZoomOutAsync() => await ZoomByAsync(0.8);

    private async Task ZoomByAsync(double factor)
    {
        if (_canvasModule is not null)
        {
            await _canvasModule.InvokeVoidAsync("zoomBy", _canvasElement, factor, _selfReference);
        }
    }

    private void FitView()
    {
        _viewX = CanvasX;
        _viewY = CanvasY;
        _viewW = CanvasWidth / 0.8;
        _viewH = CanvasHeight / 0.8;
    }

    private string ZoomPercentLabel
    {
        get
        {
            var reference = Math.Max(CanvasWidth, 1);
            var percent = Math.Round(reference / Math.Max(_viewW, 1) * 100);
            return $"{percent}%";
        }
    }

    private async Task RefreshCatalogAsync()
    {
        _errorMessage = null;
        await LoadCatalogAsync();
    }

    private async Task LoadCatalogAsync()
    {
        try
        {
            _modules = await ApiClient.GetModulesAsync(CancellationToken.None);
            _configReferences = await ApiClient.GetConfigReferencesAsync(CancellationToken.None);
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
        _joinEditors.Clear();
        _bizTalkRequirements = [];
        _bizTalkWarnings = [];
        _migration = null;
        _selectedNodeId = null;
        _seedSource = SeedSource.Manual;
        _flowId = string.Empty;
        _flowName = string.Empty;
        _enabled = true;
        _triggerMode = "OnDemand";
        _intervalText = "00:05:00";
        _runOnStartup = false;
        _hasDefaultRetry = false;
        _retryMaxAttempts = null;
        _retryDelaySeconds = 1;
        _retryBackoff = "Fixed";
        _retryMaxDelaySeconds = null;
        FitView();
    }

    private void ClearCanvas()
    {
        var flowId = _flowId;
        var flowName = _flowName;
        var migration = _migration;
        ResetGraph();
        _flowId = flowId;
        _flowName = flowName;
        _migration = migration;
        _seedSource = IsExistingFlow ? SeedSource.ExistingFlow : SeedSource.Manual;
        _statusMessage = "Canvas cleared.";
    }

    private void BuildFromFlow(FlowViewModel flow)
    {
        _nodes.Clear();
        _joinEditors.Clear();
        _bizTalkRequirements = [];
        _bizTalkWarnings = [];
        _migration = flow.Migration;
        _selectedNodeId = null;

        _flowId = flow.Id;
        _flowName = flow.Name ?? string.Empty;
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

        foreach (var fetch in flow.Fetches)
        {
            CreateFetchNode(fetch.Id, fetch.Fetch.Module, fetch.Fetch.Settings, fetch.InputParseIds, fetch.Fetch.Name);
        }

        foreach (var parse in flow.Parses)
        {
            CreateParseNode(parse.Id, parse.Parse.Module, parse.Parse.Settings, parse.FetchNodeIds, parse.Parse.Name);
        }

        var order = 0;
        foreach (var augment in flow.Augments)
        {
            CreateNode(NodeStage.Augment, augment.Module, augment.Settings, order: order++, name: augment.Name);
        }

        var routeIndex = 0;
        foreach (var route in flow.Deliveries)
        {
            var routeId = Guid.NewGuid().ToString("n");
            var render = CreateNode(NodeStage.Render, route.Render.Module, route.Render.Settings, order: routeIndex, routeId: routeId, name: route.Render.Name);
            render.AtomicScope = NormalizeAtomicScope(route.AtomicScope);
            foreach (var action in route.Delivers ?? [])
            {
                var deliver = CreateNode(NodeStage.Deliver, action.Deliver.Module, action.Deliver.Settings, order: routeIndex, routeId: routeId, name: action.Deliver.Name);
                deliver.Compensation = action.Compensation is { } compensation
                    ? new FlowStepRequestViewModel(compensation.Module, new Dictionary<string, string>(compensation.Settings, StringComparer.OrdinalIgnoreCase), Name: compensation.Name)
                    : null;
            }

            routeIndex++;
        }

        AutoLayout();
        FitView();
    }

    private FlowNode CreateNode(NodeStage stage, string moduleId, IReadOnlyDictionary<string, string>? settings = null, int order = 0, string? routeId = null, string? graphId = null, string? name = null)
    {
        var node = new FlowNode
        {
            Stage = stage,
            ModuleId = moduleId,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Order = order,
            RouteId = routeId ?? string.Empty,
            GraphId = graphId ?? string.Empty,
            Settings = settings is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase)
        };

        _nodes.Add(node);
        return node;
    }

    private FlowNode CreateFetchNode(
        string? fetchId,
        string moduleId,
        IReadOnlyDictionary<string, string>? settings,
        IEnumerable<string>? inputParseIds,
        string? name = null)
    {
        var id = string.IsNullOrWhiteSpace(fetchId) ? NextFetchId("fetch") : fetchId.Trim();
        var node = CreateNode(NodeStage.Fetch, moduleId, settings, graphId: id, name: name);
        AddDistinctIds(node.InputParseIds, inputParseIds);
        return node;
    }

    private FlowNode CreateParseNode(
        string? parseId,
        string moduleId,
        IReadOnlyDictionary<string, string>? settings,
        IEnumerable<string>? fetchNodeIds,
        string? name = null)
    {
        var id = string.IsNullOrWhiteSpace(parseId) ? NextParseId() : parseId.Trim();
        var node = CreateNode(NodeStage.Parse, moduleId, settings, graphId: id, name: name);
        AddDistinctIds(node.FetchNodeIds, fetchNodeIds);
        return node;
    }

    /// <summary>
    /// Creates the common "source" shape: one fetch node plus one parse node that parses it. The parse node
    /// takes <paramref name="parseId"/> (the id join augments and downstream fetches reference), and the fetch
    /// node gets a derived "{parseId}-fetch" id, because fetch and parse ids share one namespace.
    /// </summary>
    private (FlowNode Fetch, FlowNode Parse) CreateSourcePair(
        string? parseId,
        string fetchModuleId,
        IReadOnlyDictionary<string, string>? fetchSettings,
        string parseModuleId,
        IReadOnlyDictionary<string, string>? parseSettings)
    {
        var id = string.IsNullOrWhiteSpace(parseId) ? NextParseId() : parseId.Trim();
        var fetch = CreateFetchNode(NextFetchId($"{id}-fetch"), fetchModuleId, fetchSettings, null);
        var parse = CreateParseNode(id, parseModuleId, parseSettings, [fetch.GraphId]);
        return (fetch, parse);
    }

    private static void AddDistinctIds(List<string> target, IEnumerable<string>? ids)
    {
        if (ids is null)
        {
            return;
        }

        foreach (var id in ids.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value.Trim()))
        {
            if (!target.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(id);
            }
        }
    }

    private string NextParseId()
    {
        if (!IsGraphIdTaken("primary") && !IsGraphIdTaken("primary-fetch"))
        {
            return "primary";
        }

        for (var index = 2; ; index++)
        {
            var candidate = $"source-{index}";
            if (!IsGraphIdTaken(candidate) && !IsGraphIdTaken($"{candidate}-fetch"))
            {
                return candidate;
            }
        }
    }

    private string NextFetchId(string baseId)
    {
        if (!IsGraphIdTaken(baseId))
        {
            return baseId;
        }

        for (var index = 2; ; index++)
        {
            var candidate = $"{baseId}-{index}";
            if (!IsGraphIdTaken(candidate))
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

            var fetchNodes = FetchNodes;
            var parseNodes = ParseNodes;
            if (fetchNodes.Count == 0 || parseNodes.Count == 0)
            {
                throw new InvalidOperationException("Add at least one source (a fetch node parsed by a parse node) to the canvas.");
            }

            if (DescribeSourceGraphError() is { } graphError)
            {
                throw new InvalidOperationException(graphError);
            }

            if (fetchNodes.FirstOrDefault(static node => string.IsNullOrWhiteSpace(node.ModuleId)) is { } fetchWithoutModule)
            {
                throw new InvalidOperationException($"Select a fetch module for fetch node '{fetchWithoutModule.GraphId}'.");
            }

            if (parseNodes.FirstOrDefault(static node => string.IsNullOrWhiteSpace(node.ModuleId)) is { } parseWithoutModule)
            {
                throw new InvalidOperationException($"Select a parse module for parse node '{parseWithoutModule.GraphId}'.");
            }

            var routes = Routes;
            if (routes.Count == 0)
            {
                throw new InvalidOperationException("At least one delivery route is required.");
            }

            foreach (var route in routes)
            {
                if (route.Render is null || route.Delivers.Count == 0
                    || string.IsNullOrWhiteSpace(route.Render.ModuleId)
                    || route.Delivers.Any(static deliver => string.IsNullOrWhiteSpace(deliver.ModuleId)))
                {
                    throw new InvalidOperationException("Every delivery route needs a render module and at least one deliver node, and every deliver node needs a module.");
                }
            }

            var augments = AugmentNodes;
            if (augments.Any(static node => string.IsNullOrWhiteSpace(node.ModuleId)))
            {
                throw new InvalidOperationException("Select a module for every augment node.");
            }

            if (_enabled && _migration is not null)
                throw new InvalidOperationException(FlowMigrationReportViewModel.ExecutionBlockReason);

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
            var fetchSteps = fetchNodes
                .Select(static node => new FlowFetchNodeRequestViewModel(
                    node.GraphId.Trim(),
                    ToStepRequest(node),
                    node.InputParseIds.ToArray()))
                .ToArray();
            var parseSteps = parseNodes
                .Select(static node => new FlowParseNodeRequestViewModel(
                    node.GraphId.Trim(),
                    ToStepRequest(node),
                    node.FetchNodeIds.ToArray()))
                .ToArray();
            var augmentSteps = augments.Select(ToStepRequest).ToArray();
            var deliverySteps = routes
                .Select(static route => new DeliveryRouteRequestViewModel(
                    ToStepRequest(route.Render!),
                    route.Delivers
                        .Select(static deliver => new DeliveryActionRequestViewModel(ToStepRequest(deliver), ToCompensationRequest(deliver)))
                        .ToArray(),
                    NormalizeAtomicScope(route.Render!.AtomicScope)))
                .ToArray();

            FlowViewModel saved;
            if (IsExistingFlow)
            {
                var existing = await ApiClient.GetFlowAsync(flowId, CancellationToken.None);
                saved = await ApiClient.UpdateFlowAsync(
                    new UpdateFlowRequestViewModel(flowId, _enabled, trigger, fetchSteps, parseSteps, augmentSteps, deliverySteps, retry, existing.Category, _flowName, _migration),
                    CancellationToken.None);
                _statusMessage = $"Saved flow '{saved.Id}' with {parseSteps.Length} parse node(s) fed by {fetchSteps.Length} fetch node(s).";
            }
            else
            {
                saved = await ApiClient.CreateFlowAsync(
                    new CreateFlowRequestViewModel(flowId, _enabled, trigger, fetchSteps, parseSteps, augmentSteps, deliverySteps, retry, _flowName, _migration),
                    CancellationToken.None);

                _statusMessage = $"Created flow '{saved.Id}' with {parseSteps.Length} parse node(s), {fetchSteps.Length} fetch node(s), {augmentSteps.Length} augment(s), and {deliverySteps.Length} delivery route(s).";
                _loadedFlowId = saved.Id;
                FlowId = saved.Id;
                _flowId = saved.Id;
                _seedSource = SeedSource.ExistingFlow;
                Navigation.NavigateTo($"/designer/{Uri.EscapeDataString(saved.Id)}");
            }

            _enabled = saved.Enabled;
            _flowName = saved.Name ?? string.Empty;
            _migration = saved.Migration;
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

    private IReadOnlyList<ModuleViewModel> ModulesForStage(NodeStage stage)
    {
        var kind = ModuleKindFor(stage);
        return _modules.Where(module => string.Equals(module.Kind, kind, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>Transform modules aren't tied to a <see cref="NodeStage"/>; they're picked per field mapping row.</summary>
    private IReadOnlyList<ModuleViewModel> TransformModules
        => _modules.Where(static module => string.Equals(module.Kind, "Transform", StringComparison.OrdinalIgnoreCase)).ToArray();

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

    private FlowNode? PushTriggerFetch => FetchNodes.FirstOrDefault(fetch => FindCatalogModule(fetch.ModuleId)?.SupportsPushTrigger == true);

    private string TriggerGuidance => _triggerMode switch
    {
        "Interval" => "The flow owns the schedule: each run executes the source graph once, including one SFTP scan. There is no separate per-module polling timer. Run on startup controls the initial interval run.",
        "Push" => PushTriggerFetch is { } fetch
            ? $"Fetch '{fetch.GraphId}' ({fetch.ModuleId}) is the first push-capable fetch and will register before the first run when the flow is saved and enabled (allow about five seconds). Its events initiate the whole flow; other fetches run normally, not as additional triggers."
            : "No currently loaded fetch in this flow supports Push. It will not start automatically from source events. Add a push-capable fetch such as File system watcher, or use Interval for SFTP polling.",
        "OnDemand" => "Runs only when explicitly invoked. SFTP scans once per run; watcher events do not start an OnDemand flow. Run on startup applies only to Interval.",
        _ => "Automatic triggering is disabled. Run on startup applies only to Interval."
    };

    private string FetchTriggerGuidance(FlowNode fetch)
    {
        if (string.Equals(fetch.ModuleId, "sftp-fetch", StringComparison.OrdinalIgnoreCase))
            return "SFTP scans once when this fetch executes. Use Flow overview > Interval to poll repeatedly, or OnDemand for manual runs. SFTP has no internal timer and cannot itself initiate Push runs.";
        if (FindCatalogModule(fetch.ModuleId)?.SupportsPushTrigger == true)
        {
            var guidance = string.Equals(_triggerMode, "Push", StringComparison.OrdinalIgnoreCase)
                ? ReferenceEquals(fetch, PushTriggerFetch)
                    ? "This fetch is the flow's Push event source. Saving and enabling the flow registers it before any execution; no manual bootstrap run is needed."
                    : "Only the first push-capable fetch registers as this flow's trigger. This fetch executes when the flow runs, but its own events will not independently start this flow."
                : "This fetch can initiate Push flows, but this flow is not in Push mode. Its events will not automatically start this flow; choose Push in Flow overview for event-driven execution.";
            if (string.Equals(fetch.ModuleId, "file-system-watcher-fetch", StringComparison.OrdinalIgnoreCase))
                guidance += " FileSystemWatcher queues events and drains settled files, rather than rescanning the directory each run. In OnDemand/Interval the first execution initializes the watcher and later executions drain it; settleSeconds can make the first run empty. Use file-system-fetch with Interval for periodic directory scans.";
            return guidance;
        }
        return "This fetch executes when the flow runs; it does not register a Push event source. Scheduling belongs to the flow, not this module.";
    }

    private static string StageDescription(NodeStage stage) => stage switch
    {
        NodeStage.Fetch => "Acquires raw or already-structured content once per execution. Parse nodes interpret its output; it can also consume other parse nodes' finished output as lookup input.",
        NodeStage.Parse => "Interprets or validates fetched content and establishes a named source for joins/lookups. Choose the parser for the fetch's output format, not its transport: SQL lookup emits JSON, so use JSON parse. Each linked fetch is parsed separately, then its results are merged.",
        NodeStage.Augment => "Join, decision, and mapping steps executed once every source has been fetched and parsed.",
        NodeStage.Render => "Prepares outbound content for one delivery route, once; every deliver node of the route receives the same rendered output.",
        _ => "Dispatches the rendered payload of its delivery route. A route can have several deliver nodes, each running independently."
    };

    private string NodeHeading(FlowNode node)
        => string.IsNullOrWhiteSpace(node.Name) ? DefaultNodeHeading(node) : node.Name;

    private string DefaultNodeHeading(FlowNode node) => node.Stage switch
    {
        NodeStage.Fetch => string.IsNullOrWhiteSpace(node.GraphId) ? "Fetch" : $"Fetch - {node.GraphId}",
        NodeStage.Parse => string.IsNullOrWhiteSpace(node.GraphId) ? "Parse" : $"Parse - {node.GraphId}",
        NodeStage.Augment => IsJoinNode(node) ? $"Join sources {AugmentNodes.IndexOf(node) + 1}" : $"Augment {AugmentNodes.IndexOf(node) + 1}",
        NodeStage.Render => $"Render {RouteNumber(node)}",
        _ => DeliverHeading(node)
    };

    /// <summary>"Deliver 2" for a route's only deliver node; "Deliver 2.1", "Deliver 2.2", ... once a route has several.</summary>
    private string DeliverHeading(FlowNode node)
    {
        var route = FindRoute(node.RouteId);
        if (route is null || route.Delivers.Count < 2)
        {
            return $"Deliver {RouteNumber(node)}";
        }

        return $"Deliver {RouteNumber(node)}.{IndexOfNode(route.Delivers, node) + 1}";
    }

    private RouteView? FindRoute(string routeId)
        => Routes.FirstOrDefault(route => string.Equals(route.RouteId, routeId, StringComparison.Ordinal));

    private static int IndexOfNode(IReadOnlyList<FlowNode> nodes, FlowNode node)
    {
        for (var index = 0; index < nodes.Count; index++)
        {
            if (ReferenceEquals(nodes[index], node))
            {
                return index;
            }
        }

        return -1;
    }

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
        => new(node.ModuleId, CreateSettingsCopy(node.Settings), Name: string.IsNullOrWhiteSpace(node.Name) ? null : node.Name.Trim());

    /// <summary>
    /// The deliver node's carried-through compensation step, or null when it has none or it names no module (e.g. the
    /// empty placeholder a BizTalk atomic-scope draft starts with).
    /// </summary>
    private static FlowStepRequestViewModel? ToCompensationRequest(FlowNode deliver)
        => deliver.Compensation is { } compensation && !string.IsNullOrWhiteSpace(compensation.Module)
            ? compensation with { Settings = CreateSettingsCopy(compensation.Settings) }
            : null;

    private static string? NormalizeAtomicScope(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

    private static string CreateDefaultFlowId(string detectedFormat)
    {
        var characters = detectedFormat
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

        /// <summary>Optional user-defined display name for this node instance. Purely cosmetic.</summary>
        public string? Name { get; set; }

        public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public int Order { get; set; }

        public string RouteId { get; init; } = string.Empty;

        /// <summary>The fetch or parse node id in the flow's source graph (unused for other stages).</summary>
        public string GraphId { get; set; } = string.Empty;

        /// <summary>Fetch nodes only: ids of the parse nodes whose finished output is merged and fed into this fetch.</summary>
        public List<string> InputParseIds { get; } = [];

        /// <summary>Parse nodes only: ids of the fetch nodes whose raw output this parse node parses.</summary>
        public List<string> FetchNodeIds { get; } = [];

        /// <summary>
        /// Render nodes only: the optional atomic-transaction scope of this node's delivery route, applying to every
        /// deliver node sharing its <see cref="RouteId"/>.
        /// </summary>
        public string? AtomicScope { get; set; }

        /// <summary>
        /// Deliver nodes only: the optional compensation step that undoes this deliver action inside an atomic scope.
        /// Carried through load/save unchanged; it isn't edited on the canvas.
        /// </summary>
        public FlowStepRequestViewModel? Compensation { get; set; }

        public double X { get; set; }

        public double Y { get; set; }
    }

    private sealed record FlowEdge(FlowNode From, FlowNode To, bool IsDependency);

    /// <summary>
    /// One delivery route on the canvas: the render node and every deliver node sharing <paramref name="RouteId"/>.
    /// <paramref name="Delivers"/> is ordered by <see cref="FlowNode.Order"/>, then canvas creation order.
    /// </summary>
    private sealed record RouteView(string RouteId, int Order, FlowNode? Render, IReadOnlyList<FlowNode> Delivers);

    private enum JoinMappingField
    {
        SourceKind,
        SourcePath,
        TargetField,
        Condition,
        LiteralValue,
        TransformModule
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

    // Property names mirror Mulse.Modules' WorkflowFieldMappingDefinition / WorkflowTransformInputDefinition so the
    // join/map augment can deserialize exactly what the Designer writes into the 'mappingJson' setting.
    private sealed record JoinMappingDto
    {
        public string? SourceKind { get; init; }

        public string? SourcePath { get; init; }

        public string? TargetField { get; init; }

        public string? Condition { get; init; }

        public string? LiteralValue { get; init; }

        public string? TransformModule { get; init; }

        public TransformInputDto[]? TransformInputs { get; init; }

        public Dictionary<string, string>? TransformSettings { get; init; }
    }

    private sealed record TransformInputDto
    {
        public string? SourceKind { get; init; }

        public string? SourcePath { get; init; }

        public string? LiteralValue { get; init; }
    }

    private sealed class FieldMappingRow
    {
        public string SourceKind { get; set; } = "Left";

        public string SourcePath { get; set; } = string.Empty;

        public string Condition { get; set; } = "Always";

        public string TargetField { get; set; } = string.Empty;

        public string LiteralValue { get; set; } = string.Empty;

        /// <summary>Transform rows only: the transform module id to invoke.</summary>
        public string? TransformModule { get; set; }

        /// <summary>Transform rows only: the ordered inputs handed to the transform module.</summary>
        public List<TransformInputRow> TransformInputs { get; } = [];

        /// <summary>Transform rows only: settings passed to the transform module.</summary>
        public Dictionary<string, string> TransformSettings { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class TransformInputRow
    {
        public string SourceKind { get; set; } = "Left";

        public string SourcePath { get; set; } = string.Empty;

        public string LiteralValue { get; set; } = string.Empty;
    }
}
