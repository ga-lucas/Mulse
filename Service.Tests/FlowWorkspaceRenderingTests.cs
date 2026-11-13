using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Mulse.Ui.Components.Pages;
using Mulse.Ui.Models;
using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class FlowWorkspaceRenderingTests
{
    [Fact]
    public async Task Dashboard_RendersFiltersAndAllFlowsWithoutPaginationOrModuleAdministration()
    {
        var html = await RenderAsync<Home>();
        Assert.Contains("Search flows", html);
        Assert.Contains("All categories", html);
        Assert.Contains("Uncategorized", html);
        Assert.Contains("Sales", html);
        Assert.Contains("All statuses", html);
        Assert.Contains("All triggers", html);
        Assert.Contains("All modules", html);
        Assert.Contains("Unresolved configuration only", html);
        Assert.Contains("12 of 12 flow(s) match", html);
        Assert.DoesNotContain("Flow list pages", html);
        Assert.Equal(12, html.Split("class=\"flow-card dashboard-flow ").Length - 1);
        Assert.Equal(12, html.Split("aria-label=\"Pipeline stages\"").Length - 1);
        for (var index = 1; index <= 12; index++)
        {
            Assert.Contains($"href=\"/designer/flow-{index:00}\"", html);
            Assert.Contains($"href=\"/history/flow-{index:00}\"", html);
        }
        Assert.Equal(12, html.Split(">History</a>").Length - 1);
        Assert.Contains("stage-augment", html);
        Assert.Contains("stage-render", html);
        Assert.Contains("stage-deliver", html);
        Assert.Contains("is-disabled", html);
        Assert.Contains("href=\"/designer/flow-01\"", html);
        Assert.Contains("Categorize", html);
        Assert.Contains("Duplicate", html);
        Assert.Contains("Delete", html);
        Assert.Contains("Configure", html);
        Assert.Contains("Run flow", html);
        Assert.Contains("Customer orders 1", html);
        Assert.Contains("Flow ID:</strong> flow-01", html);
        Assert.DoesNotContain("Runtime module packages", html);
        Assert.DoesNotContain("Reload package", html);
    }

    [Fact]
    public async Task ImportedFlows_ShowReportsAndDisableEnableRunControls()
    {
        var dashboard = await RenderAsync<Home>(migration: true);
        Assert.Contains("verified translation support", dashboard);
        Assert.Contains("execution blocked", dashboard);
        Assert.Matches("<button[^>]*disabled[^>]*>\\s*Enable", dashboard);
        Assert.Matches("<button[^>]*disabled[^>]*>\\s*Run flow", dashboard);
        Assert.Contains("Configure", dashboard);
        var designer = await RenderAsync<Designer>(flowId: "join-test", migration: true);
        Assert.Contains("You can still edit and save a disabled draft", designer);
        Assert.Contains("execution blocked", designer);
        Assert.Matches("<input[^>]*type=\"checkbox\"[^>]*disabled[^>]*>\\s*<span[^>]*>Enabled</span>", designer);
    }

    [Fact]
    public async Task Modules_RendersOnlyCatalogWithLinkToPackages()
    {
        var html = await RenderAsync<Mulse.Ui.Components.Pages.Modules>();
        Assert.Contains("Search modules", html);
        Assert.Contains("Module kind", html);
        Assert.Contains("file-fetch", html);
        Assert.Contains("Source package", html);
        Assert.Contains("All packages", html);
        Assert.Contains("builtin.core", html);
        Assert.Contains("BuiltIn", html);
        Assert.Contains("C:\\runtime\\Mulse.Modules.dll", html);
        Assert.Contains("href=\"/module-packages#package-builtin.core\"", html);
        Assert.Contains("href=\"/module-packages\"", html);
        Assert.DoesNotContain("Runtime module packages", html);
        Assert.DoesNotContain("Reload package", html);
        Assert.DoesNotContain("Your flows", html);
    }

    [Fact]
    public async Task ModulePackages_RendersPackageManagementWithoutCatalog()
    {
        var html = await RenderAsync<ModulePackages>();
        Assert.Contains("Runtime module packages", html);
        Assert.Contains("No module packages were found.", html);
        Assert.Contains("Refresh packages", html);
        Assert.Contains("appear unloaded", html);
        Assert.DoesNotContain("Load a package", html);
        Assert.DoesNotContain("Server assembly path", html);
        Assert.Contains("trust", html);
        Assert.Contains("href=\"/modules\"", html);
        Assert.DoesNotContain("Search modules", html);
        Assert.DoesNotContain("Your flows", html);
    }

    [Fact]
    public async Task ModulePackages_GroupsModulesUnderTheirOwningPackage()
    {
        var html = await RenderAsync<ModulePackages>(packages: true);
        var coreStart = html.IndexOf("id=\"package-builtin.core\"", StringComparison.Ordinal);
        var pluginStart = html.IndexOf("id=\"package-custom.plugin\"", StringComparison.Ordinal);
        var offlineStart = html.IndexOf("id=\"package-offline\"", StringComparison.Ordinal);
        Assert.True(coreStart >= 0 && pluginStart > coreStart && offlineStart > pluginStart);
        var core = html[coreStart..pluginStart];
        var plugin = html[pluginStart..offlineStart];
        var offline = html[offlineStart..];
        Assert.Contains("file-fetch", core);
        Assert.DoesNotContain("plugin-parse", core);
        Assert.Contains("plugin-parse", plugin);
        Assert.DoesNotContain("file-fetch", plugin);
        Assert.Contains("<details><summary>Show/hide modules (1)</summary>", core);
        Assert.Contains("<details><summary>Show/hide modules (1)</summary>", plugin);
        Assert.DoesNotMatch("<details[^>]*\\bopen\\b", html);
        Assert.Contains("href=\"/modules?package=custom.plugin\"", plugin);
        Assert.Contains("Module details are unavailable while this package is unloaded", offline);
        Assert.DoesNotContain("plugin-parse", offline);
    }

    [Fact]
    public async Task Menu_SeparatesModulesFromConfigurationWithoutDesignerLink()
    {
        var html = await RenderAsync<Mulse.Ui.Components.Layout.MainLayout>();
        Assert.DoesNotContain("href=\"/designer\"", html);
        Assert.Contains("href=\"/\"", html);
        var start = html.IndexOf("<nav class=\"workspace-nav\" aria-labelledby=\"configuration-nav-label\"", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var group = html[start..html.IndexOf("</nav>", start, StringComparison.Ordinal)];
        Assert.DoesNotContain("href=\"/modules\"", group);
        Assert.DoesNotContain("href=\"/module-packages\"", group);
        Assert.Contains("href=\"/config-values\"", group);
        Assert.Contains("href=\"/backup-restore\"", group);
        Assert.Contains("href=\"/storage\"", group);
        var moduleStart = html.IndexOf("<nav class=\"workspace-nav\" aria-labelledby=\"modules-nav-label\"", StringComparison.Ordinal);
        Assert.True(moduleStart >= 0);
        var modules = html[moduleStart..html.IndexOf("</nav>", moduleStart, StringComparison.Ordinal)];
        Assert.Contains("href=\"/modules\"", modules);
        Assert.Contains("href=\"/module-packages\"", modules);
        Assert.DoesNotContain("href=\"/config-values\"", modules);
        Assert.DoesNotContain("href=\"/backup-restore\"", modules);
        Assert.DoesNotContain("href=\"/storage\"", modules);
    }

    [Theory]
    [InlineData("Sqlite", "SQLite")]
    [InlineData("SqlServer", "SQL Server")]
    [InlineData("PostgreSql", "PostgreSQL")]
    [InlineData("Json", "JSON file (legacy)")]
    public async Task Storage_ShowsEffectiveBackendAndSafeConfigurationGuidance(string provider, string displayName)
    {
        var html = await RenderAsync<Storage>(storageProvider: provider);
        Assert.Contains($"<dt>Engine</dt><dd>{displayName}</dd>", html);
        Assert.Contains("<dt>Payload encryption</dt><dd>Off</dd>", html);
        Assert.Contains("ConnectionStrings__Mulse", html);
        Assert.Contains("MULSE_STATE_ENCRYPTION_KEY", html);
        Assert.Contains("Service\\appsettings.json", html);
        Assert.Contains("no live engine switch", html);
        Assert.Contains("Storage migration wizard", html);
        if (provider != "Json") Assert.Contains("excludes pending execution state and revision history", html);
        Assert.Contains("Changes require a Service restart", html);
        Assert.Contains("Pause and drain flows", html);
        Assert.Contains("<dt>Active executions</dt><dd>2</dd>", html);
        Assert.Contains("<dt>Active inbound admissions</dt><dd>1</dd>", html);
        Assert.Contains("Paused is not database-offline", html);
        Assert.Contains("Processing__ShutdownDrainSeconds", html);
        Assert.Contains("Persistence__History__MaxPendingMessages", html);
        if (provider != "Json")
        {
            Assert.Contains("Recovery attention required", html);
            Assert.Contains("Inbox capacity is at least 90% used", html);
            Assert.Contains("<dt>Unfinished inbox / capacity</dt><dd>9000 / 10000</dd>", html);
        }
        else Assert.DoesNotContain("aria-label=\"Durable work backlog\"", html);
        if (provider == "Json") Assert.DoesNotContain("<select", html);
        else
        {
            Assert.Contains("id=\"migration-provider\"", html);
            Assert.Contains("id=\"migration-connection\" type=\"password\"", html);
            Assert.Contains("Preview and validate target", html);
            Assert.Contains("Stop the Service", html);
        }
        if (provider is "Sqlite" or "Json") Assert.Contains("C:\\workspace\\data", html);
        else Assert.DoesNotContain("Data file on the Service machine", html);
        Assert.Contains(provider == "Sqlite" ? "<dt>Apply migrations at startup</dt><dd>Yes</dd>"
            : provider == "Json" ? "<dt>Apply migrations at startup</dt><dd>Not applicable</dd>"
            : "<dt>Apply migrations at startup</dt><dd>No</dd>", html);
    }

    [Fact]
    public async Task Storage_ShowsApiFailureRatherThanDefaultEngine()
    {
        var html = await RenderAsync<Storage>(storageFailure: true);
        Assert.Contains("Unable to load storage settings", html);
        Assert.Contains("Storage diagnostics unavailable", html);
        Assert.DoesNotContain("aria-label=\"Storage summary\"", html);
    }

    [Theory]
    [InlineData(8999, false)]
    [InlineData(9000, true)]
    [InlineData(10000, true)]
    public async Task Storage_CapacityWarningStartsAtExactlyNinetyPercent(int unfinished, bool warning)
    {
        var html = await RenderAsync<Storage>(unfinished: unfinished);
        Assert.Equal(warning, html.Contains("Inbox capacity is at least 90% used", StringComparison.Ordinal));
        Assert.Contains($"<dt>Unfinished inbox / capacity</dt><dd>{unfinished} / 10000</dd>", html);
    }

    [Theory]
    [InlineData("Paused", "Resume processing", false)]
    [InlineData("Draining", "Resume processing", false)]
    [InlineData("Faulted", "Pause and drain flows", true)]
    [InlineData("Stopping", "Pause and drain flows", true)]
    public async Task Storage_ReflectsMaintenanceAndDisablesUnsafeTransitions(string state, string action, bool disabled)
    {
        var html = await RenderAsync<Storage>(processingState: state);
        Assert.Contains($"<dt>State</dt><dd>{state}</dd>", html);
        Assert.Matches(disabled ? $"<button[^>]*disabled[^>]*>\\s*{action}" : $"<button class=\"primary-button\">\\s*{action}", html);
        if (state == "Faulted") Assert.Contains("Restart required", html);
    }

    [Fact]
    public async Task BackupRestore_RendersEncryptedBackupPreviewModesAndTypedConfirmations()
    {
        var html = await RenderAsync<BackupRestore>();
        Assert.Contains("Download encrypted backup", html);
        Assert.Contains("type=\"password\"", html);
        Assert.Contains("Preview restore", html);
        Assert.Contains("value=\"merge\"", html);
        Assert.Contains("value=\"replace\"", html);
        Assert.Contains("start disabled", html);
        Assert.Contains("DELETE ALL FLOWS", html);
        Assert.Contains("Running executions are not cancelled", html);
        Assert.Contains("disabled", html);
    }

    [Fact]
    public async Task Dashboard_ShowsMissingDependenciesAndBlocksEnableAndRun()
    {
        var html = await RenderAsync<Home>(missingModules: true);
        Assert.Contains("Unknown or unloaded modules", html);
        Assert.Contains("Fetch: missing-plugin", html);
        Assert.Contains("href=\"/module-packages\"", html);
        Assert.Contains("Resolve missing modules and config/secret references before enabling this flow", html);
        Assert.Contains("Resolve missing modules and config/secret references before running this flow", html);
        Assert.Matches("<button[^>]*disabled[^>]*>\\s*Enable\\s*</button>", html);
        Assert.Matches("<button[^>]*disabled[^>]*>\\s*Run flow\\s*</button>", html);
    }

    [Fact]
    public async Task ConfigValues_MasksValuesAndDisablesEncryptionWithoutEnvironmentKey()
    {
        var html = await RenderAsync<Mulse.Ui.Components.Pages.ConfigValues>();
        Assert.Contains("Show config/secret values", html);
        Assert.Contains("Runtime state encryption", html);
        Assert.Contains("MULSE_STATE_ENCRYPTION_KEY", html);
        Assert.Matches("role=\"switch\"[^>]*disabled", html);
        Assert.Matches("type=\"password\"[^>]*aria-label=\"Value for db-host\"", html);
        Assert.Matches("id=\"new-value\"[^>]*type=\"password\"", html);
        Assert.Contains("not access control", html);
    }

    [Theory]
    [InlineData("Password", "literal-value", "password")]
    [InlineData("Number", "{{config:count}}", "text")]
    [InlineData("Select", "{{config:mode}}", "text")]
    [InlineData("Boolean", "{{config:enabled}}", "text")]
    public async Task ReferenceInput_PreservesTokensAndMasksPasswordSettings(string kind, string value, string type)
    {
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new StaticRenderJsRuntime()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<Mulse.Ui.Components.Shared.ReferenceInput>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["InputKind"] = kind, ["Value"] = value, ["Label"] = "Test setting", ["References"] = new[] { "count" }
            }));
            return rendered.ToHtmlString();
        });
        Assert.Contains($"type=\"{type}\"", html);
        Assert.Contains("role=\"combobox\"", html);
        Assert.Contains("data-reference-editor", html);
        Assert.Contains("Insert stored reference...", html);
        Assert.Contains(value, html);
        if (kind == "Password") Assert.Contains("Show value", html);
    }

    [Fact]
    public async Task Designer_ShowsIndependentNameAndIdAndRightAlignedProperties()
    {
        var html = await RenderAsync<Designer>();
        Assert.Contains("for=\"flow-name\"", html);
        Assert.Contains("fixed after creation", html);
        Assert.Contains("class=\"designer-properties dock-panel\"", html);
        Assert.Contains("class=\"designer-left-dock\"", html);
        Assert.Contains("role=\"separator\"", html);
        Assert.Contains("aria-orientation=\"horizontal\"", html);
        Assert.Contains("aria-controls=\"designer-toolbox designer-overview\"", html);
        Assert.DoesNotContain("floating-panel-title\">Toolbox", html);
        Assert.DoesNotContain("floating-panel-title\">Flow overview", html);
        Assert.DoesNotContain("floating-panel-title\">Properties", html);
        Assert.Contains("Create flow", html);
        Assert.DoesNotContain(">History</a>", html);
        Assert.Contains("80%", html);
        Assert.Contains("data-node-type=\"render\"", html);
        Assert.Contains("data-node-type=\"parse\"", html);
        Assert.Contains("Run on startup (Interval only)", html);
    }

    [Fact]
    public async Task Designer_RendersNativeRemoveButtonOnEveryNode()
    {
        var html = await RenderAsync<Designer>(flowId: "remove-test");
        Assert.Matches("href=\"/history/remove-test\"[^>]*>History</a>", html);
        Assert.Equal(5, html.Split("class=\"node-remove\"").Length - 1);
        Assert.Equal(10, html.Split("<foreignObject").Length - 1);
        Assert.Equal(2, html.Split("class=\"node-port input-port\"").Length - 1);
        Assert.Equal(2, html.Split("class=\"node-port output-port\"").Length - 1);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(html, "data-node-id=\"[^\"]+\" tabindex=\"0\" role=\"group\"").Count);
        Assert.Contains("drag between the handles", html);
        Assert.Contains("<svg", html);
        Assert.Matches("<marker id=\"flow-arrow\"[^>]*viewBox=\"0 0 10 10\"[^>]*>\\s*<path[^>]*class=\"edge-arrow\"", html);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(html, "<button[^>]*type=\"button\"[^>]*class=\"node-remove\"[^>]*aria-label=\"Remove ").Count);
        Assert.Contains("Remove only this node", html);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(WebUtility.HtmlDecode(html), "<span[^>]*aria-hidden=\"true\"[^>]*>\\s*\\u00d7\\s*</span>").Count);
        Assert.Contains("Dashed arrows supply lookup inputs", html);
        Assert.Contains("not matching rows", html);
        Assert.Contains("Batch flow:", html);
        Assert.Contains("data-lane-members=", html);
        Assert.Contains("class=\"source-lane-handle\"", html);
        Assert.Contains("height=\"22\" rx=\"8\" ry=\"8\"", html);
        Assert.Contains("<foreignObject class=\"source-lane-label\"", html);
        Assert.Contains("class=\"source-lane-caption\" title=\"Source: parsed\"", html);
        Assert.Contains("Drag this header", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Designer_DistinguishesLookupDependenciesBatchMergesAndRealJoins(bool join)
    {
        var html = WebUtility.HtmlDecode(await RenderAsync<Designer>(flowId: join ? "join-test" : "lookup-test"));
        Assert.Contains("data-edge-kind=\"dependency\"", html);
        Assert.Contains("Lookup input:", html);
        Assert.Contains("This does not join rows.", html);
        Assert.Contains("USES \u00d72", html);
        Assert.Matches("class=\"split-badge\"[^>]*y=\"110\"", html);
        Assert.DoesNotContain("SPLITS", html);
        if (join)
        {
            Assert.Contains("JOIN BY KEY", html);
            Assert.Contains("Join steps", html);
            Assert.Contains("Configure join", html);
            Assert.Contains("Remove join", html);
            Assert.DoesNotContain("BATCH x2", html);
        }
        else
        {
            Assert.Contains("BATCH x2", html);
            Assert.DoesNotContain("JOIN BY KEY", html);
        }
    }

    [Fact]
    public async Task Designer_GraphEditingAndTriggerGuidanceSurviveRenders()
    {
        using var httpClient = new HttpClient(new WorkspaceHandler(false, false)) { BaseAddress = new Uri("http://localhost/") };
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<NavigationManager>(new TestNavigationManager())
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new StaticRenderJsRuntime())
            .AddSingleton<IMulseApiClient>(new MulseApiClient(httpClient)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Designer? designer = null;
            var rendered = await renderer.RenderComponentAsync<DesignerHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(DesignerHost.Capture)] = (Action<Designer>)(value => designer = value)
            }));
            Assert.NotNull(designer);
            var nodes = (System.Collections.IList)typeof(Designer).GetField("_nodes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(designer)!;
            static string Value(object node, string property) => node.GetType().GetProperty(property)!.GetValue(node)!.ToString()!;
            object Find(string stage) => nodes.Cast<object>().First(node => Value(node, "Stage") == stage);
            void Invoke(string method, params object?[] arguments) => typeof(Designer).GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(designer, arguments);
            async Task Refresh() => await designer.OnViewChanged(0, 0, 1600, 800);

            designer.OnDockSplitChanged(61);
            await Refresh();
            Assert.Contains("aria-valuenow=\"61\"", rendered.ToHtmlString());
            Invoke("ResizeDockByKey", new KeyboardEventArgs { Key = "ArrowDown" });
            await Refresh();
            Assert.Contains("aria-valuenow=\"66\"", rendered.ToHtmlString());
            Invoke("ResizeDockByKey", new KeyboardEventArgs { Key = "Home" });
            Invoke("ResizeDockByKey", new KeyboardEventArgs { Key = "ArrowUp" });
            await Refresh();
            Assert.Contains("aria-valuenow=\"20\"", rendered.ToHtmlString());
            Invoke("ResizeDockByKey", new KeyboardEventArgs { Key = "End" });
            Invoke("ResizeDockByKey", new KeyboardEventArgs { Key = "ArrowDown" });
            Invoke("ResizeDockByKey", new KeyboardEventArgs { Key = "Tab" });
            await Refresh();
            Assert.Contains("aria-valuenow=\"80\"", rendered.ToHtmlString());
            designer.OnDockSplitChanged(-500);
            await Refresh();
            Assert.Contains("aria-valuenow=\"20\"", rendered.ToHtmlString());
            Assert.Throws<ArgumentOutOfRangeException>(() => designer.OnDockSplitChanged(double.NaN));

            var fetchId = Value(Find("Fetch"), "Id");
            var parseId = Value(Find("Parse"), "Id");
            await designer.OnNodesMoved([new(fetchId, -120, -160), new(parseId, 150, -160)]);
            await Refresh();
            Assert.Contains("transform=\"translate(-120, -160)\"", rendered.ToHtmlString());
            Assert.Contains("transform=\"translate(150, -160)\"", rendered.ToHtmlString());
            Assert.Contains("class=\"source-lane-body\" x=\"-138\" y=\"-198\"", rendered.ToHtmlString());
            Assert.DoesNotContain("canvas-background", rendered.ToHtmlString());

            var join = Find("Augment");
            var render = Find("Render");
            var deliver = Find("Deliver");
            Invoke("SelectNode", join);
            await Refresh();
            Assert.Contains("Join inputs", rendered.ToHtmlString());
            Assert.Contains("Remove join", rendered.ToHtmlString());
            Invoke("RemoveCanvasNode", join);
            await Refresh();
            Assert.DoesNotContain("JOIN BY KEY", rendered.ToHtmlString());
            Assert.Contains(render, nodes.Cast<object>());
            Assert.Contains(deliver, nodes.Cast<object>());

            Invoke("RemoveCanvasNode", render);
            Invoke("SelectNode", deliver);
            await Refresh();
            Assert.Contains("Add render to this route", rendered.ToHtmlString());
            Assert.Contains("Step retry policy", rendered.ToHtmlString());
            Assert.Contains("Override flow retry policy", rendered.ToHtmlString());
            Assert.Contains("Delivery compensation", rendered.ToHtmlString());
            Assert.Contains("Compensation module", rendered.ToHtmlString());
            Assert.Contains("not a distributed transaction", rendered.ToHtmlString());
            Assert.DoesNotContain("configure it through the flows API", rendered.ToHtmlString());
            Invoke("AddRenderToRoute", Value(deliver, "RouteId"), null);
            await Refresh();
            Assert.DoesNotContain("Add render to this route", rendered.ToHtmlString());
            Assert.Equal(Value(deliver, "RouteId"), Value(Find("Render"), "RouteId"));
            Assert.Contains(deliver, nodes.Cast<object>());
            Assert.Contains("transform=\"translate(-120, -160)\"", rendered.ToHtmlString());

            var renderNode = Find("Render");
            renderNode.GetType().GetProperty("ModuleId")!.SetValue(renderNode, "render-pipeline");
            Invoke("SelectNode", renderNode);
            await Refresh();
            Assert.Contains("Ordered renderer stages", rendered.ToHtmlString());
            Assert.Contains("Add renderer stage", rendered.ToHtmlString());
            var renderSettings = (Dictionary<string, string>)renderNode.GetType().GetProperty("Settings")!.GetValue(renderNode)!;
            renderSettings["stagesJson"] = "[{\"module\":\"json-render\",\"settings\":{}}]";
            await Refresh();
            Assert.Contains("Move stage earlier", rendered.ToHtmlString());
            Assert.Contains("Move stage later", rendered.ToHtmlString());

            var fetch = Find("Fetch");
            var removedParser = Find("Parse");
            Invoke("RemoveCanvasNode", removedParser);
            Invoke("SelectNode", fetch);
            await Refresh();
            Assert.Contains("Add parser for this fetch", rendered.ToHtmlString());
            var count = nodes.Count;
            Invoke("AddParseForFetch", fetch, null);
            await Refresh();
            Assert.Equal(count + 1, nodes.Count);
            Assert.DoesNotContain(removedParser, nodes.Cast<object>());
            Assert.Contains("SQL lookup emits JSON, so use JSON parse", rendered.ToHtmlString());
            Assert.Contains("keeping the fetch", rendered.ToHtmlString());

            void SetField(string name, object value) => typeof(Designer).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(designer, value);
            fetch.GetType().GetProperty("ModuleId")!.SetValue(fetch, "sftp-fetch");
            SetField("_triggerMode", "Push");
            Invoke("SelectNode", fetch);
            await Refresh();
            Assert.Contains("Push requires a loaded push-capable fetch module", rendered.ToHtmlString());
            Assert.Contains("SFTP has no internal timer", rendered.ToHtmlString());
            SetField("_modules", new ModuleViewModel[]
            {
                new("file-system-watcher-fetch", "Watcher", "Fetch", "File events", [], [], [], [], SupportsPushTrigger: true)
            });
            fetch.GetType().GetProperty("ModuleId")!.SetValue(fetch, "file-system-watcher-fetch");
            await Refresh();
            Assert.DoesNotContain("Push requires a loaded push-capable fetch module", rendered.ToHtmlString());
            Assert.Contains("no manual bootstrap run is needed", rendered.ToHtmlString());
            SetField("_triggerMode", "Interval");
            await Refresh();
            Assert.Contains("File system watcher events will not start this flow", rendered.ToHtmlString());
            Assert.Contains("The flow owns the schedule", rendered.ToHtmlString());
        });
    }

    private sealed class DesignerHost : ComponentBase
    {
        [Parameter]
        public Action<Designer>? Capture { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<Designer>(0);
            builder.AddAttribute(1, nameof(Designer.FlowId), "join-test");
            builder.AddComponentReferenceCapture(2, value => Capture?.Invoke((Designer)value));
            builder.CloseComponent();
        }
    }

    [Fact]
    public async Task RightAlignedPanel_KeepsDraggedPositionAcrossRendersAndCollapse()
    {
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new StaticRenderJsRuntime()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Mulse.Ui.Components.Shared.FloatingPanel? panel = null;
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(PanelHost.Capture)] = (Action<Mulse.Ui.Components.Shared.FloatingPanel>)(value => panel = value)
            });
            var rendered = await renderer.RenderComponentAsync<PanelHost>(parameters);
            Assert.Contains("--panel-x:calc(100% - var(--panel-width, 360px) - 1rem)", rendered.ToHtmlString());
            Assert.NotNull(panel);
            panel.OnMoved(320, 80);
            Assert.Contains("--panel-x:320px; --panel-y:80px;", rendered.ToHtmlString());
            await panel.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(panel.IsCollapsed)] = true }));
            await panel.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(panel.IsCollapsed)] = false }));
            Assert.Contains("--panel-x:320px; --panel-y:80px;", rendered.ToHtmlString());
            Assert.DoesNotContain("--panel-x:calc", rendered.ToHtmlString());
        });
    }

    private sealed class PanelHost : ComponentBase
    {
        [Parameter]
        public Action<Mulse.Ui.Components.Shared.FloatingPanel>? Capture { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<Mulse.Ui.Components.Shared.FloatingPanel>(0);
            builder.AddAttribute(1, "Title", "Properties");
            builder.AddAttribute(2, "AlignRight", true);
            builder.AddAttribute(3, "Width", 360d);
            builder.AddComponentReferenceCapture(4, value => Capture?.Invoke((Mulse.Ui.Components.Shared.FloatingPanel)value));
            builder.CloseComponent();
        }
    }

    private static async Task<string> RenderAsync<TComponent>(bool missingModules = false, bool packages = false, string? flowId = null, bool migration = false,
        string storageProvider = "Sqlite", bool storageFailure = false, string processingState = "Running", int unfinished = 9000) where TComponent : IComponent
    {
        using var httpClient = new HttpClient(new WorkspaceHandler(missingModules, packages, migration, storageProvider, storageFailure, processingState, unfinished)) { BaseAddress = new Uri("http://localhost") };
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<NavigationManager>(new TestNavigationManager())
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new StaticRenderJsRuntime())
            .AddSingleton<IMulseApiClient>(new MulseApiClient(httpClient)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = flowId is null ? ParameterView.Empty
                : ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Designer.FlowId)] = flowId });
            var component = await renderer.RenderComponentAsync<TComponent>(parameters);
            return component.ToHtmlString();
        });
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");
    }

    private sealed class StaticRenderJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => throw new NotSupportedException("Static rendering must not invoke JavaScript.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => throw new NotSupportedException("Static rendering must not invoke JavaScript.");
    }

    private sealed class WorkspaceHandler(bool missingModules, bool packages, bool migration = false,
        string storageProvider = "Sqlite", bool storageFailure = false, string processingState = "Running", int unfinished = 9000) : HttpMessageHandler
    {
        private FlowMigrationReportViewModel? Report => migration ? new("BizTalk", @"C:\migration\Orders.btproj", "Orders", [], [], []) : null;

        private static FlowViewModel LookupFlow(bool join) => new(
            join ? "join-test" : "lookup-test", false, "OnDemand", null, false,
            ["input", "lookup"], ["primary", "reference"], ["file-fetch", "sql-server-lookup-fetch"], ["excel-parse", "json-parse"], [], ["json-render"], ["file-system-deliver"],
            [new("input", new("file-fetch", new Dictionary<string, string>()), []), new("lookup", new("sql-server-lookup-fetch", new Dictionary<string, string>()), ["primary"])],
            [new("primary", new("excel-parse", new Dictionary<string, string>()), ["input"]), new("reference", new("json-parse", new Dictionary<string, string>()), ["lookup"])],
            join ? [new("multi-source-join-map-augment", new Dictionary<string, string> { ["leftSourceId"] = "primary", ["rightSourceId"] = "reference" })] : [],
            [new(new("json-render", new Dictionary<string, string>()), [new(new("file-system-deliver", new Dictionary<string, string>()))])]);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (storageFailure && request.RequestUri!.AbsolutePath == "/api/workspace/storage")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = JsonContent.Create(new Microsoft.AspNetCore.Mvc.ProblemDetails { Detail = "Storage diagnostics unavailable" })
                });
            object payload = request.RequestUri!.AbsolutePath switch
            {
                "/api/flows/lookup-test" => LookupFlow(false),
                "/api/flows/join-test" => LookupFlow(true) with { Migration = Report },
                "/api/flows/remove-test" => new FlowViewModel(
                    "remove-test", false, "OnDemand", null, false, ["input"], ["parsed"], ["file-fetch"], ["json-parse"], ["augment"], ["render"], ["deliver"],
                    [new("input", new("file-fetch", new Dictionary<string, string>()), [])],
                    [new("parsed", new("json-parse", new Dictionary<string, string>()), ["input"])],
                    [new("augment", new Dictionary<string, string>())],
                    [new(new("render", new Dictionary<string, string>()), [new(new("deliver", new Dictionary<string, string>()))])]),
                "/api/flows" => Enumerable.Range(1, 12).Select(index => new FlowViewModel(
                    $"flow-{index:00}", false, "OnDemand", null, false, ["input"], ["parsed"], ["file-fetch"], ["json-parse"], [], [], [], [], [], [], [], Category: "Sales", MissingModules: missingModules ? ["Fetch: missing-plugin"] : [], Name: $"Customer orders {index}", Migration: Report)).ToArray(),
                "/api/modules" => new[]
                {
                    new ModuleViewModel("file-fetch", "File fetch", "Fetch", "Reads files", [], [], [], [], "builtin.core", "BuiltIn", "C:\\runtime\\Mulse.Modules.dll"),
                    new ModuleViewModel("plugin-parse", "Plugin parse", "Parse", "Parses plugin input", [], [], [], [], "custom.plugin", "Managed", "C:\\plugins\\custom.dll")
                },
                "/api/module-packages" => packages ? new[]
                {
                    new ModulePackageViewModel("builtin.core", "C:\\runtime\\Mulse.Modules.dll", "BuiltIn", true, DateTimeOffset.MinValue, 1),
                    new ModulePackageViewModel("custom.plugin", "C:\\plugins\\custom.dll", "Managed", true, DateTimeOffset.MinValue, 1),
                    new ModulePackageViewModel("offline", "C:\\plugins\\offline.dll", "AutoDiscovered", false, DateTimeOffset.MinValue, 0)
                } : Array.Empty<ModulePackageViewModel>(),
                "/api/config-values/usages" => Array.Empty<ConfigReferenceUsageViewModel>(),
                "/api/config-values/references" => new[] { "db-host" },
                "/api/config-values" => new[] { new ConfigValueViewModel("db-host", "example.internal") },
                "/api/workspace/encryption" => new RuntimeEncryptionViewModel(false, false, "MULSE_STATE_ENCRYPTION_KEY", "Set the environment variable and restart the Service."),
                "/api/workspace/storage/migration" => new StorageMigrationStatusViewModel(storageProvider != "Json", null, null, null),
                "/api/workspace/processing" => new ProcessingStatusViewModel(processingState, processingState is "Paused" or "Draining", 2, 1,
                    processingState == "Stopping", processingState == "Faulted" ? "Restart required" : null),
                "/api/workspace/processing/backlog" => storageProvider == "Json"
                    ? new ProcessingBacklogViewModel(false, null, null, null, null, null, null, null, null)
                    : new ProcessingBacklogViewModel(true, unfinished, unfinished - 10, 9, 1, 1, 2, 1, 10000),
                "/api/workspace/storage" => new RuntimeStorageViewModel(storageProvider, storageProvider != "Json",
                    storageProvider is "Sqlite" or "Json" ? "C:\\workspace\\data" : null,
                    storageProvider == "Sqlite", storageProvider == "Json" ? null : "primary", "MULSE_STATE_ENCRYPTION_KEY"),
                _ => throw new InvalidOperationException($"Unexpected API call: {request.RequestUri}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload, payload.GetType()) });
        }
    }
}
