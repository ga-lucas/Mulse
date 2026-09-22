using System.Text.Json;
using Mulse.Modules;
using Service.Models;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService
{
    private static BizTalkDraftStepResponse CreateFetchStep(
        string flowId,
        IReadOnlyList<BindingFileAnalysis> bindings,
        List<BizTalkSettingRequirementResponse> requirements,
        List<string> draftWarnings)
    {
        var portLocation = bindings
            .SelectMany(static binding => binding.ReceivePorts)
            .SelectMany(port => port.ReceiveLocations.Select(location => (Port: port, Location: location)))
            .FirstOrDefault();
        if (portLocation.Location is null)
        {
            var placeholderPath = AddRequirement(requirements, flowId, "fetch.settings.path", "Config", "Choose the staging repository path used when no BizTalk receive location could be imported.", appliedToDraft: true);
            draftWarnings.Add("No BizTalk receive location was matched to this project, so the draft uses a repository inbox placeholder for fetch.");
            AddArchiveAfterProcessingWarning(draftWarnings);
            return new BizTalkDraftStepResponse(
                "document-repository-fetch",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["path"] = placeholderPath,
                    ["searchPattern"] = "*.*",
                    ["includeMetadataSidecars"] = "true",
                    ["afterProcessing"] = nameof(FetchPostProcessingAction.MoveToArchive)
                });
        }

        var fetchStep = CreateFetchSettings(portLocation.Location, flowId, includeConfigPlaceholders: true, requirements, "fetch.settings");
        if (fetchStep.Settings.ContainsKey("afterProcessing"))
        {
            AddArchiveAfterProcessingWarning(draftWarnings);
        }

        if (portLocation.Port.IsTwoWay)
        {
            var twoWaySettings = new Dictionary<string, string>(fetchStep.Settings, StringComparer.OrdinalIgnoreCase)
            {
                ["expectsResponse"] = "true"
            };
            fetchStep = fetchStep with { Settings = twoWaySettings };
            draftWarnings.Add($"Receive port '{portLocation.Port.Name}' is a two-way (request-response) service. Mulse's fetch modules are currently fire-and-forget, so this draft cannot yet return a synchronous reply to the caller - plan a response-capable fetch module (or an inline synchronous augment) before enabling this flow.");
        }

        return fetchStep;
    }

    private static BizTalkDraftStepResponse CreateParseStep(
        string flowId,
        BizTalkProjectResponse project,
        List<BizTalkSettingRequirementResponse> requirements,
        List<string> draftWarnings)
    {
        var module = SelectParseModule(project);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.Equals(module, "xml-envelope-debatch-parse", StringComparison.OrdinalIgnoreCase))
        {
            settings["bodyXPath"] = AddRequirement(requirements, flowId, "parse.settings.bodyXPath", "Config", "Enter the XPath selecting each repeated business-message element, based on the imported pipeline's EnvelopeSpecNames schema.", appliedToDraft: true);
            draftWarnings.Add("This project's receive pipeline configures an envelope schema (EnvelopeSpecNames), so the draft uses an envelope-debatching parse step. Set bodyXPath to match the real envelope shape before enabling the flow.");
        }
        else if (string.Equals(module, "xml-xsd-parse", StringComparison.OrdinalIgnoreCase))
        {
            var schemaPaths = AllArtifacts(project)
                .Where(static artifact => string.Equals(artifact.Kind, "Schema", StringComparison.OrdinalIgnoreCase))
                .Select(static artifact => artifact.FullPath)
                .ToArray();
            if (schemaPaths.Length > 0)
            {
                settings["schemaPaths"] = string.Join(";", schemaPaths);
            }
        }
        else if (string.Equals(module, "flat-file-parse", StringComparison.OrdinalIgnoreCase))
        {
            settings["delimiter"] = InferFlatFileDelimiter(project);
            settings["hasHeader"] = "true";
        }

        if (string.Equals(module, "json-parse", StringComparison.OrdinalIgnoreCase))
        {
            draftWarnings.Add("JSON parsing was selected as a fallback. Replace it if the inbound document is XML, HL7, or flat-file content.");
        }

        return new BizTalkDraftStepResponse(module, settings);
    }

    private static IReadOnlyList<BizTalkDraftStepResponse> CreateAugments(
        string flowId,
        BizTalkProjectResponse project,
        IReadOnlyList<BizTalkCustomAssemblyResponse> relatedAssemblies,
        int deliveryRouteCount,
        List<BizTalkSettingRequirementResponse> requirements,
        List<string> draftWarnings)
    {
        var augments = new List<BizTalkDraftStepResponse>();

        var needsPromotion = relatedAssemblies.Any(assembly => assembly.Name.Contains("promot", StringComparison.OrdinalIgnoreCase)
            || assembly.Name.Contains("routing", StringComparison.OrdinalIgnoreCase)
            || assembly.Name.Contains("property", StringComparison.OrdinalIgnoreCase));
        if (needsPromotion)
        {
            augments.Add(new BizTalkDraftStepResponse(
                "metadata-promotion-augment",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["promotionsJson"] = "[{\"metadataKey\":\"documentType\",\"selector\":\"$.documentType\"}]"
                }));
            draftWarnings.Add("Metadata promotion was preloaded from custom assembly heuristics. Review JSONPath or XPath selectors before using the imported flow.");
        }

        if (deliveryRouteCount > 1)
        {
            var defaultRoute = $"route-1";
            augments.Add(new BizTalkDraftStepResponse(
                "route-selection-augment",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["routeRulesJson"] = $"[{{\"metadataKey\":\"documentType\",\"equals\":\"TODO\",\"route\":\"{defaultRoute}\"}}]",
                    ["defaultRoute"] = defaultRoute,
                    ["targetMetadataKey"] = "targetRoute"
                }));
            draftWarnings.Add("Multiple BizTalk send ports were imported. The generated route-selection augment uses placeholder routing rules and must be reviewed before enabling the flow.");
        }

        if (project.MapCount > 1)
        {
            draftWarnings.Add("The project contains multiple BizTalk maps. Review which generated delivery route should use each converted map or XSLT artifact.");
        }

        AddControlFlowAugments(flowId, project, augments, requirements, draftWarnings);

        return augments;
    }

    private static readonly IReadOnlyList<string> StatefulOrchestrationShapeTypes =
    [
        "Listen", "CorrelationDeclaration", "CorrelationType", "Loop", "AtomicTransaction"
    ];

    /// <summary>
    /// Scaffolds draft augment steps from detected orchestration control-flow signals (see
    /// <see cref="DetectOrchestrationControlFlowSignalsByOrchestration"/>) instead of only surfacing them as
    /// open questions. Decision/branch shapes become a <c>decision-augment</c> step, pre-populated with real
    /// rules auto-translated from the orchestration's simple branch expressions (see
    /// <see cref="BizTalkOrchestrationDecisionAnalyzer"/>) and falling back to a config placeholder only when no
    /// branch could be translated. Convoy, correlation, loop, and atomic-transaction shapes become a placeholder
    /// <c>stateful-orchestration-augment</c> step; the matching scaffolded C# module carries the details that
    /// can't be expressed declaratively.
    /// </summary>
    private static void AddControlFlowAugments(
        string flowId,
        BizTalkProjectResponse project,
        List<BizTalkDraftStepResponse> augments,
        List<BizTalkSettingRequirementResponse> requirements,
        List<string> draftWarnings)
    {
        if (project.OrchestrationControlFlowSignals.Count == 0)
        {
            return;
        }

        var signalsByShape = project.OrchestrationControlFlowSignals.ToDictionary(static signal => signal.ShapeType, StringComparer.Ordinal);

        if (signalsByShape.TryGetValue("Decision", out var decisionSignal))
        {
            var generatedDecisionJson = BizTalkOrchestrationDecisionAnalyzer.MergeDecisionJson(project.OrchestrationDecisionAnalyses);
            var complexBranchCount = project.OrchestrationDecisionAnalyses.Sum(static analysis => analysis.ComplexBranches.Count);

            string decisionJson;
            if (generatedDecisionJson is not null)
            {
                decisionJson = generatedDecisionJson;
                draftWarnings.Add($"Detected {decisionSignal.Count} {decisionSignal.Description} in the source orchestration. Branch conditions simple enough to translate were auto-converted into real decision-augment rules that tag the matched branch via metadata ('biztalk:<decision>' = '<branch>'); they do not reproduce each branch's side effects, so review them and wire up the downstream behavior before enabling the flow.");
                if (complexBranchCount > 0)
                {
                    draftWarnings.Add($"{complexBranchCount} decision branch(es) were too complex to auto-translate (method calls, non-equality operators, or assignments). Their original BizTalk expressions are quoted in the scaffolded C# module for this orchestration.");
                }
            }
            else
            {
                decisionJson = AddRequirement(requirements, flowId, "augments.decision.decisionJson", "Config", $"Detected {decisionSignal.Count} {decisionSignal.Description} in the orchestration. Replace the placeholder decision rules with the real branch conditions before enabling the flow.", appliedToDraft: true);
                draftWarnings.Add($"Detected {decisionSignal.Count} {decisionSignal.Description} in the source orchestration, but none of the branch expressions were simple enough to auto-translate. A decision-augment step was scaffolded with placeholder rules; replace them with the real branch conditions.");
            }

            augments.Add(new BizTalkDraftStepResponse(
                "decision-augment",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["decisionJson"] = decisionJson,
                    ["stopAfterFirstMatch"] = "true"
                }));
        }

        var statefulSignals = StatefulOrchestrationShapeTypes
            .Select(shapeType => signalsByShape.GetValueOrDefault(shapeType))
            .Where(static signal => signal is not null)
            .Select(static signal => signal!)
            .ToArray();
        if (statefulSignals.Length > 0)
        {
            var correlationPath = AddRequirement(requirements, flowId, "augments.stateful.correlationPath", "Config", "Set the metadata key or JSONPath that carries the correlation value used by the orchestration's correlation set(s).", appliedToDraft: true);
            augments.Add(new BizTalkDraftStepResponse(
                "stateful-orchestration-augment",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mode"] = "ResumeOrWait",
                    ["correlationSource"] = "Metadata",
                    ["correlationPath"] = correlationPath
                }));
            var summary = string.Join(", ", statefulSignals.Select(signal => $"{signal.Count} {signal.Description}"));
            draftWarnings.Add($"Detected {summary} in the source orchestration. A stateful-orchestration-augment step was scaffolded to model convoy/correlation waits; configure the real correlation source, bookmarks, and state capture before enabling the flow.");
        }
    }

    private static IReadOnlyList<BizTalkDraftDeliveryRouteResponse> CreateDeliveryRoutes(
        string flowId,
        BizTalkProjectResponse project,
        IReadOnlyList<BindingFileAnalysis> bindings,
        List<BizTalkSettingRequirementResponse> requirements,
        List<string> draftWarnings)
    {
        var sendPorts = ConsolidateSendPorts(bindings.SelectMany(static binding => binding.SendPorts).ToArray(), draftWarnings);
        if (sendPorts.Count == 0)
        {
            var placeholderPath = AddRequirement(requirements, flowId, "deliveries[0].deliver.settings.path", "Config", "Choose the staging output repository path because no BizTalk send port was imported for this project.", appliedToDraft: true);
            draftWarnings.Add("No BizTalk send port was matched to this project, so the draft uses a repository outbox placeholder.");
            return
            [
                new BizTalkDraftDeliveryRouteResponse(
                    CreateRenderStepForProject(flowId, project, sendPort: null, routeIndex: 0, requirements, draftWarnings),
                    new BizTalkDraftStepResponse(
                        "document-repository-store",
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["path"] = placeholderPath,
                            ["writeMetadataSidecar"] = "true"
                        }))
            ];
        }

        var routes = new List<BizTalkDraftDeliveryRouteResponse>(sendPorts.Count);
        for (var index = 0; index < sendPorts.Count; index++)
        {
            routes.Add(CreateRouteForSendPort(flowId, project, sendPorts[index], index, requirements, draftWarnings));
        }

        return ApplyAtomicScopeIfDetected(flowId, project, routes, requirements, draftWarnings);
    }

    /// <summary>
    /// Tags all delivery routes with a shared atomic scope, and adds a compensation-module requirement for each,
    /// when the source orchestration used a BizTalk <c>AtomicTransaction</c> scope and there is more than one
    /// delivery route to roll back between. With a single route there is nothing to compensate, so tagging is
    /// skipped. This closes the loop between <see cref="DetectOrchestrationControlFlowSignalsByOrchestration"/> and the
    /// <see cref="Mulse.Modules.Pipeline.DeliveryRouteDefinition.AtomicScope"/>/<see cref="Mulse.Modules.Pipeline.DeliveryRouteDefinition.Compensation"/>
    /// runtime model: the draft flow is pre-wired for all-or-nothing delivery, and the operator only needs to
    /// supply real compensation module settings before enabling it.
    /// </summary>
    private static IReadOnlyList<BizTalkDraftDeliveryRouteResponse> ApplyAtomicScopeIfDetected(
        string flowId,
        BizTalkProjectResponse project,
        List<BizTalkDraftDeliveryRouteResponse> routes,
        List<BizTalkSettingRequirementResponse> requirements,
        List<string> draftWarnings)
    {
        var hasAtomicTransaction = project.OrchestrationControlFlowSignals
            .Any(static signal => string.Equals(signal.ShapeType, "AtomicTransaction", StringComparison.Ordinal));
        if (!hasAtomicTransaction || routes.Count < 2)
        {
            return routes;
        }

        const string atomicScope = "atomic-scope-1";
        var scopedRoutes = new List<BizTalkDraftDeliveryRouteResponse>(routes.Count);
        for (var index = 0; index < routes.Count; index++)
        {
            AddRequirement(requirements, flowId, $"deliveries[{index}].compensation.module", "Config", $"This delivery route participates in a migrated atomic transaction scope. Configure a compensation deliver module (e.g. a cancel/undo call) for deliveries[{index}], or leave unset to only log a warning if rollback is needed.", appliedToDraft: false);            scopedRoutes.Add(routes[index] with
            {
                AtomicScope = atomicScope,
                Compensation = new BizTalkDraftStepResponse(string.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            });
        }

        draftWarnings.Add($"The source orchestration used an AtomicTransaction scope, so all {routes.Count} delivery routes were tagged with atomic scope '{atomicScope}'. Configure a real compensation deliver module for each route before enabling the flow, or rollback will only be logged rather than executed.");
        return scopedRoutes;
    }

    private static BizTalkDraftDeliveryRouteResponse CreateRouteForSendPort(
        string referencePrefix,
        BizTalkProjectResponse project,
        SendPortAnalysis sendPort,
        int routeIndex,
        List<BizTalkSettingRequirementResponse>? requirements,
        List<string> draftWarnings)
    {
        var renderStep = CreateRenderStepForProject(referencePrefix, project, sendPort, routeIndex, requirements, draftWarnings);
        var deliverSettings = CreateDeliverSettings(sendPort, referencePrefix, routeIndex, requirements, renderStep.Module, includeConfigPlaceholders: requirements is not null);

        if (deliverSettings.Retry is { } importedRetry)
        {
            draftWarnings.Add($"Send port '{sendPort.Name}' had a native BizTalk retry configuration (retry {sendPort.RetryCount} time(s), {sendPort.RetryIntervalMinutes} minute(s) apart) - the deliver step was migrated with an equivalent retry policy ({importedRetry.MaxAttempts} max attempts, {importedRetry.Delay} delay). Review before enabling the flow.");
        }

        if (sendPort.IsTwoWay)
        {
            var twoWaySettings = new Dictionary<string, string>(deliverSettings.Settings, StringComparer.OrdinalIgnoreCase)
            {
                ["expectsResponse"] = "true"
            };
            deliverSettings = deliverSettings with { Settings = twoWaySettings };
            draftWarnings.Add($"Send port '{sendPort.Name}' is a two-way (solicit-response) WCF port. Mulse's deliver modules are currently fire-and-forget, so the synchronous reply from '{sendPort.Address}' is not captured yet - plan a response-capturing deliver module (or a follow-up augment step) before enabling this route.");
        }

        if (routeIndex > 0)
        {
            var conditionalSettings = new Dictionary<string, string>(renderStep.Settings, StringComparer.OrdinalIgnoreCase)
            {
                ["conditionJson"] = $"[{{\"source\":\"Metadata\",\"path\":\"targetRoute\",\"operator\":\"Equals\",\"value\":\"route-{routeIndex + 1}\"}}]"
            };
            renderStep = renderStep with { Settings = conditionalSettings };
        }

        return new BizTalkDraftDeliveryRouteResponse(renderStep, deliverSettings);
    }

    private static BizTalkDraftStepResponse CreateRenderStepForProject(
        string referencePrefix,
        BizTalkProjectResponse project,
        SendPortAnalysis? sendPort,
        int routeIndex,
        List<BizTalkSettingRequirementResponse>? requirements,
        List<string> draftWarnings)
    {
        var module = SelectRenderModule(project, sendPort);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var settingPrefix = $"deliveries[{routeIndex}].render.settings";

        switch (module)
        {
            case "xslt-render":
                var generatedStylesheet = project.MapAnalyses.FirstOrDefault(static analysis => analysis.GeneratedXsltSourceCode is not null);
                settings["xsltPath"] = AddRequirement(
                    requirements,
                    referencePrefix,
                    $"{settingPrefix}.xsltPath",
                    "Config",
                    generatedStylesheet is null
                        ? "Convert the BizTalk map or BTM asset into an XSLT file and update this path before enabling the flow."
                        : $"A starter XSLT stylesheet ('{generatedStylesheet.ScaffoldedModuleFileName}') was auto-generated from map '{generatedStylesheet.MapName}' ({generatedStylesheet.DirectLinkCount} direct link(s), {generatedStylesheet.ConstantValueCount} constant(s), {generatedStylesheet.FunctoidInvolvedLinkCount} functoid-involved link(s) left untranslated). Download it from the scaffolded-module endpoints, save it, then set this path to the saved file.",
                    appliedToDraft: true);
                settings["outputExtension"] = ".xml";
                break;
            case "xml-render":
                settings["rootElement"] = NormalizeArtifactName(project.Name);
                settings["itemElement"] = "Item";
                break;
            case "flat-file-render":
                settings["delimiter"] = InferFlatFileDelimiter(project);
                settings["includeHeader"] = "true";
                break;
            case "soap-envelope-render":
                var soapVersion = ResolveSoapVersion(sendPort);
                settings["soapVersion"] = soapVersion;
                var actionMappings = ParseSoapActionMappings(sendPort);
                if (actionMappings.Count > 0)
                {
                    settings["actionMappingsJson"] = JsonSerializer.Serialize(actionMappings);
                    if (actionMappings.Count > 1)
                    {
                        draftWarnings.Add($"Send port '{sendPort?.Name}' exposes multiple SOAP actions. Verify the payload root elements align with the imported action mappings before enabling the flow.");
                    }
                    else
                    {
                        settings["fallbackAction"] = actionMappings.Values.First();
                    }
                }
                else
                {
                    draftWarnings.Add($"Send port '{sendPort?.Name}' looks SOAP-based, but no static action mapping could be imported. Review the SOAP action and body shape before enabling the flow.");
                }
                break;
        }

        return new BizTalkDraftStepResponse(module, settings);
    }

    private static BizTalkDraftStepResponse CreateFetchSettings(
        ReceiveLocationAnalysis location,
        string referencePrefix,
        bool includeConfigPlaceholders,
        List<BizTalkSettingRequirementResponse>? requirements = null,
        string settingPathPrefix = "fetch.settings")
    {
        var module = SelectFetchModule(location.TransportType, location.Address);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var address = location.Address;

        switch (module)
        {
            case "sftp-fetch":
                if (Uri.TryCreate(address, UriKind.Absolute, out var sftpUri))
                {
                    settings["host"] = ResolveConfigValue(requirements, referencePrefix, $"{settingPathPrefix}.host", sftpUri.Host, "Remote SFTP host imported from BizTalk.", includeConfigPlaceholders);
                    settings["port"] = sftpUri.IsDefaultPort ? "22" : sftpUri.Port.ToString();
                    settings["remotePath"] = string.IsNullOrWhiteSpace(sftpUri.AbsolutePath) ? "/" : sftpUri.AbsolutePath;
                    var userName = string.IsNullOrWhiteSpace(sftpUri.UserInfo) ? null : sftpUri.UserInfo.Split(':')[0];
                    if (!string.IsNullOrWhiteSpace(userName))
                    {
                        settings["username"] = AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.username", "Secret", "SFTP username imported from BizTalk should be moved to protected configuration.", appliedToDraft: true, actualValue: userName);
                    }
                }
                else
                {
                    settings["host"] = AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.host", "Config", "Enter the SFTP host because BizTalk did not expose a parseable address.", appliedToDraft: true);
                    settings["remotePath"] = "/";
                }

                settings.TryAdd("port", "22");
                settings.TryAdd("username", AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.username", "Secret", "Set the SFTP username in protected configuration before enabling the flow.", appliedToDraft: true));
                settings["password"] = AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.password", "Secret", "Set the SFTP password in protected configuration before enabling the flow.", appliedToDraft: true);
                settings["searchPattern"] = "*.*";
                settings.TryAdd("deleteAfterRead", "true");
                break;
            case "file-system-fetch":
                settings["path"] = ResolveConfigValue(requirements, referencePrefix, $"{settingPathPrefix}.path", address, "File-system fetch path imported from BizTalk should usually be externalized to configuration.", includeConfigPlaceholders);
                settings["searchPattern"] = "*.*";
                settings["afterProcessing"] = nameof(FetchPostProcessingAction.MoveToArchive);
                break;
            case "http-inbound-fetch":
                settings["route"] = ResolveConfigValue(requirements, referencePrefix, $"{settingPathPrefix}.route", CreateSlug(location.Name), "Inbound route segment imported from a BizTalk WCF/HTTP receive location. Exposed at /api/inbound/{route}; the original endpoint must be repointed here during cutover.", includeConfigPlaceholders);
                break;
            default:
                settings["path"] = AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.path", "Config", "Choose the staging repository path used to replace this BizTalk receive transport.", appliedToDraft: includeConfigPlaceholders, actualValue: address);
                settings["searchPattern"] = "*.*";
                settings["includeMetadataSidecars"] = "true";
                settings["afterProcessing"] = nameof(FetchPostProcessingAction.MoveToArchive);
                break;
        }

        return new BizTalkDraftStepResponse(module, settings);
    }

    /// <summary>
    /// BizTalk receive locations always remove (or move) a file after successful pickup, specifically to
    /// avoid re-processing it on the next poll. Imported fetch steps default to the same behavior
    /// (archiving to a sibling '&lt;path&gt;.processed' folder) rather than silently leaving files in place,
    /// which would otherwise deliver duplicate payloads on every subsequent run of a timer-triggered flow.
    /// </summary>
    private static void AddArchiveAfterProcessingWarning(List<string> draftWarnings)
    {
        draftWarnings.Add("The fetch step defaults to archiving files after a successful run (afterProcessing=MoveToArchive) so the same file isn't re-delivered on every run, matching BizTalk's receive-location behavior. Change 'afterProcessing' to 'None' if you need files left in place for manual inspection.");
    }

    private static BizTalkDraftStepResponse CreateDeliverSettings(
        SendPortAnalysis sendPort,
        string referencePrefix,
        int routeIndex,
        List<BizTalkSettingRequirementResponse>? requirements,
        string renderModule,
        bool includeConfigPlaceholders)
    {
        var module = SelectDeliverModule(sendPort.TransportType, sendPort.Address);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var settingPathPrefix = $"deliveries[{routeIndex}].deliver.settings";

        switch (module)
        {
            case "http-deliver":
                settings["url"] = ResolveConfigValue(requirements, referencePrefix, $"{settingPathPrefix}.url", sendPort.Address, "HTTP or WCF endpoint imported from BizTalk should usually be externalized to configuration.", includeConfigPlaceholders);
                settings["method"] = "POST";
                if (!string.Equals(renderModule, "soap-envelope-render", StringComparison.OrdinalIgnoreCase))
                {
                    settings["contentType"] = ResolveContentType(renderModule);
                }
                break;
            case "file-system-deliver":
                settings["path"] = ResolveConfigValue(requirements, referencePrefix, $"{settingPathPrefix}.path", sendPort.Address, "File-system destination imported from BizTalk should usually be externalized to configuration.", includeConfigPlaceholders);
                settings["extension"] = ResolveExtension(renderModule);
                break;
            case "smtp-email-deliver":
                var host = TryGetTransportProperty(sendPort.TransportProperties, "SMTPServer") ?? ExtractHost(sendPort.Address);
                settings["host"] = string.IsNullOrWhiteSpace(host)
                    ? AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.host", "Config", "Enter the SMTP host for the migrated send port.", appliedToDraft: includeConfigPlaceholders)
                    : ResolveConfigValue(requirements, referencePrefix, $"{settingPathPrefix}.host", host, "SMTP host imported from BizTalk should usually be externalized to configuration.", includeConfigPlaceholders);
                settings["port"] = TryGetTransportProperty(sendPort.TransportProperties, "SMTPServerPort") ?? "25";
                settings["from"] = AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.from", "Config", "Set the SMTP sender address for the migrated send port.", appliedToDraft: includeConfigPlaceholders);
                settings["to"] = AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.to", "Config", "Set the SMTP recipient addresses for the migrated send port.", appliedToDraft: includeConfigPlaceholders);
                settings["subject"] = $"Imported BizTalk delivery {sendPort.Name}";
                settings["attachPayload"] = "true";
                break;
            default:
                settings["path"] = AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.path", "Config", "Choose a repository output path for this migrated send port.", appliedToDraft: includeConfigPlaceholders, actualValue: sendPort.Address);
                settings["writeMetadataSidecar"] = "true";
                break;
        }

        return new BizTalkDraftStepResponse(module, settings, CreateRetryPolicy(sendPort));
    }

    /// <summary>
    /// Translates a BizTalk send port's native retry configuration (<c>RetryCount</c>/<c>RetryInterval</c>,
    /// read straight from the binding file) into an equivalent <see cref="RetryPolicyDefinition"/> for the
    /// migrated deliver step, so imported flows keep the source system's retry intent instead of silently
    /// defaulting to no retries. BizTalk's RetryCount is the number of retries *after* the first attempt and
    /// RetryInterval is in minutes; a RetryCount of 0 means BizTalk itself was configured with no retries, so
    /// no policy (null, i.e. exactly one attempt) is generated in that case.
    /// </summary>
    private static RetryPolicyDefinition? CreateRetryPolicy(SendPortAnalysis sendPort)
    {
        if (sendPort.RetryCount <= 0)
        {
            return null;
        }

        return new RetryPolicyDefinition
        {
            MaxAttempts = sendPort.RetryCount + 1,
            Delay = TimeSpan.FromMinutes(Math.Max(sendPort.RetryIntervalMinutes, 0)),
            Backoff = RetryBackoffKind.Fixed
        };
    }
}
