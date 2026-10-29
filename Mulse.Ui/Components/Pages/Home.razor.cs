using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Mulse.Ui.Models;

namespace Mulse.Ui.Components.Pages;

public partial class Home
{
    private ElementReference _actionEditor;
    private bool _focusAction;
    private const int PageSize = 10;
    private string _search = string.Empty;
    private string _categoryFilter = string.Empty;
    private string _statusFilter = string.Empty;
    private string _triggerFilter = string.Empty;
    private string _moduleFilter = string.Empty;
    private bool _unresolvedOnly;
    private bool _descending;
    private int _page = 1;
    private FlowViewModel? _managedFlow;
    private string _flowAction = string.Empty;
    private string _newFlowId = string.Empty;
    private string _newCategory = string.Empty;

    private bool IsBusy => _isManagingFlow || _isRunningFlow || _isTogglingFlowEnabled;
    private string ActionTitle => _flowAction switch
    {
        "duplicate" => "Duplicate flow",
        "category" => "Save category",
        _ => "Delete flow"
    };

    private IEnumerable<string> Categories => _flows.Select(flow => flow.Category)
        .OfType<string>().Where(category => !string.IsNullOrWhiteSpace(category))
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<FlowViewModel> FilteredFlows => new FlowListFilter(
        _search, _statusFilter, _triggerFilter, _moduleFilter,
        _categoryFilter.StartsWith("category:", StringComparison.Ordinal) ? _categoryFilter[9..] : null,
        _categoryFilter == "none", _unresolvedOnly, _descending).Apply(_flows, _configUsages);

    private int PageCount => Math.Max(1, (FilteredFlows.Count + PageSize - 1) / PageSize);
    private int CurrentPage => Math.Clamp(_page, 1, PageCount);
    private IEnumerable<FlowViewModel> VisibleFlows => FilteredFlows.Skip((CurrentPage - 1) * PageSize).Take(PageSize);

    private void ResetPage() => _page = 1;
    private void PreviousPage() => _page = Math.Max(1, CurrentPage - 1);
    private void NextPage() => _page = Math.Min(PageCount, CurrentPage + 1);

    private void ClearFilters()
    {
        _search = _categoryFilter = _statusFilter = _triggerFilter = _moduleFilter = string.Empty;
        _unresolvedOnly = _descending = false;
        ResetPage();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusAction)
        {
            _focusAction = false;
            await _actionEditor.FocusAsync();
        }
    }

    private void BeginAction(FlowViewModel flow, string action)
    {
        _managedFlow = flow;
        _focusAction = true;
        _flowAction = action;
        _newCategory = flow.Category ?? string.Empty;
        _newFlowId = $"{flow.Id}-copy";
        var suffix = 2;
        while (_flows.Any(candidate => string.Equals(candidate.Id, _newFlowId, StringComparison.OrdinalIgnoreCase)))
        {
            _newFlowId = $"{flow.Id}-copy-{suffix++}";
        }
        _errorMessage = _lastRunMessage = null;
    }

    private void CancelAction() => _managedFlow = null;

    private async Task SaveActionAsync()
    {
        if (_managedFlow is not { } flow || IsBusy)
        {
            return;
        }

        _isManagingFlow = true;
        _errorMessage = _lastRunMessage = null;
        try
        {
            switch (_flowAction)
            {
                case "duplicate":
                    if (string.IsNullOrWhiteSpace(_newFlowId))
                    {
                        throw new ArgumentException("A new flow ID is required.");
                    }
                    var copy = await ApiClient.DuplicateFlowAsync(flow.Id, _newFlowId.Trim(), CancellationToken.None);
                    _lastRunMessage = $"Created disabled flow '{copy.Id}' from '{flow.Id}'. Clear filters if the copy is not visible.";
                    break;
                case "category":
                    var updated = await ApiClient.SetFlowCategoryAsync(flow.Id, _newCategory, CancellationToken.None);
                    _lastRunMessage = $"Flow '{updated.Id}' category is now '{updated.Category ?? "Uncategorized"}'.";
                    break;
                case "delete":
                    await ApiClient.DeleteFlowAsync(flow.Id, CancellationToken.None);
                    _lastRunMessage = $"Deleted flow '{flow.Id}'.";
                    break;
                default:
                    throw new InvalidOperationException("Unknown flow action.");
            }
            _managedFlow = null;
            await LoadAsync();
        }
        catch (Exception exception)
        {
            _errorMessage = $"Unable to {_flowAction} flow '{flow.Id}': {exception.Message}";
        }
        finally
        {
            _isManagingFlow = false;
        }
    }
}
