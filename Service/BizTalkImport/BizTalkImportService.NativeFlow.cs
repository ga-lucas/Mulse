using System.Text.Json;
using System.Text.Json.Serialization;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Decisions;
using Mulse.Modules.Orchestration;
using Mulse.Modules.Pipeline;
using Service.Models;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService
{
    private static readonly JsonSerializerOptions NativeJson = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 128, Converters = { new JsonStringEnumConverter() }
    };

    private static BizTalkFlowCandidateResponse AttachNativeFlow(BizTalkFlowCandidateResponse candidate,
        IReadOnlyList<BindingFileAnalysis> bindings, IReadOnlyList<BizTalkRulePolicyResponse> policies, BizTalkSchemaSourceCatalog schemas,
        IReadOnlyDictionary<string, string>? policyTargets, NativeSourceConfigurationCatalog? configuration, string? mapChoices)
    {
        var report = candidate.DraftFlow.Migration;
        var paths = report?.Artifacts.Where(artifact => artifact.Kind == "Orchestration").Select(artifact => artifact.SourcePath).ToArray() ?? [];
        if (report is null || paths.Length != 1) return candidate;
        var diagnostics = report.Diagnostics.ToList();
        try
        {
            var source = OdxProgramReader.ReadFile(paths[0]);
            var services = FlattenNativeSource(source.Roots).Where(node => node.ShapeType == "ServiceDeclaration").ToArray();
            if (services.Length != 1) throw new ArgumentException("Native translation requires exactly one source service declaration.");
            var owners = bindings.SelectMany(binding => binding.Services
                .Where(service => service.AssemblyName == candidate.SourceProject && (service.ServiceName == report.SourceOrchestration
                    || service.ServiceName.EndsWith("." + report.SourceOrchestration, StringComparison.Ordinal)))
                .Select(service => (Binding: binding, Service: service))).ToArray();
            var requirements = candidate.DraftFlow.ConfigurationRequirements.ToList();
            var warnings = candidate.DraftFlow.Warnings.ToList();
            var variableDeclarations = services[0].Children.Where(node => node.ShapeType == "VariableDeclaration").ToArray();
            var messageDeclarations = services[0].Children.Where(node => node.ShapeType == "MessageDeclaration").ToArray();
            var documents = variableDeclarations.Concat(messageDeclarations).Where(node => node.Properties.GetValueOrDefault("Type") == "System.Xml.XmlDocument")
                .Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
            var messages = messageDeclarations.Select(node => node.Name).Concat(documents)
                .Concat(FlattenNativeSource(source.Roots).Where(node => node.ShapeType is "Receive" or "Send").Select(node => node.Properties.GetValueOrDefault("MessageName", "")))
                .Where(name => name.Length > 0).ToHashSet(StringComparer.Ordinal);
            var messageCatalog = NativeSourceMessageCatalog.Read(messageDeclarations, schemas);
            var declarationCounts = FlattenNativeSource(source.Roots).Where(node => node.ShapeType is "MessageDeclaration" or "VariableDeclaration")
                .GroupBy(node => node.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var contextCatalog = NativeSourceContextCatalog.Read(schemas, messageDeclarations.Where(node => declarationCounts[node.Name] == 1)
                .Select(node => node.Name).ToHashSet(StringComparer.Ordinal));
            var parents = FlattenNativeSource(source.Roots).SelectMany(parent => parent.Children.Select(child => (child.Id, Parent: parent)))
                .ToDictionary(pair => pair.Id, pair => pair.Parent);
            var elementDeclarations = FlattenNativeSource(source.Roots).Where(node => node.ShapeType == "VariableDeclaration")
                .GroupBy(node => node.Name, StringComparer.Ordinal).Where(group => group.Count() == 1).Select(group => group.Single())
                .Where(node => node.Properties.GetValueOrDefault("Type") == "System.Xml.XmlElement"
                    && parents.GetValueOrDefault(node.Id)?.ShapeType is "ServiceDeclaration" or "Scope"
                    && node.Children.Count == 0 && string.IsNullOrWhiteSpace(node.Properties.GetValueOrDefault("InitialValue"))
                    && node.Properties.GetValueOrDefault("UseDefaultConstructor", "False") == "False"
                    && node.Properties.GetValueOrDefault("ParamDirection", "In") == "In").ToArray();
            IReadOnlySet<string> ElementSymbols(OdxNode statement)
            {
                var ancestors = new HashSet<int>();
                var current = statement;
                while (parents.TryGetValue(current.Id, out var parent)) { ancestors.Add(parent.Id); current = parent; }
                return elementDeclarations.Where(declaration => ancestors.Contains(parents[declaration.Id].Id)).Select(declaration => declaration.Name).ToHashSet(StringComparer.Ordinal);
            }
            var scalarTypes = variableDeclarations.Where(node => declarationCounts[node.Name] == 1 && node.Properties.GetValueOrDefault("Type") is "System.String" or "System.Int32" or "System.Boolean")
                .GroupBy(node => node.Name, StringComparer.Ordinal).Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single().Properties["Type"] switch
                { "System.Int32" => NativeScalarType.Int32, "System.Boolean" => NativeScalarType.Boolean, _ => NativeScalarType.String }, StringComparer.Ordinal);
            var transformTypes = variableDeclarations.Where(node => declarationCounts[node.Name] == 1 && NativeExpression.ValidName(node.Name)
                && node.Properties.GetValueOrDefault("Type") == "System.Type" && node.Children.Count == 0
                && string.IsNullOrWhiteSpace(node.Properties.GetValueOrDefault("InitialValue"))
                && node.Properties.GetValueOrDefault("UseDefaultConstructor", "False") == "False"
                && node.Properties.GetValueOrDefault("ParamDirection", "In") == "In").Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
            var transformMessages = messageDeclarations.Where(node => declarationCounts[node.Name] == 1 && NativeExpression.ValidName(node.Name))
                .Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
            var transformCatalog = NativeSourceMessageCatalog.Read(messageDeclarations.Where(node => transformMessages.Contains(node.Name)), schemas);
            NativeSourceExpressionReader Assignments(OdxNode node, bool transformsOnly = false) => new(node.Properties.GetValueOrDefault("Expression", ""), scalarTypes,
                transformsOnly ? transformMessages : messages, transformsOnly ? transformCatalog : messageCatalog, documents, ElementSymbols(node), contextCatalog, configuration,
                transformTypes, mapChoices);
            var erasableTransformTypes = new HashSet<string>(StringComparer.Ordinal);
            if (mapChoices is not null && Mulse.Modules.Mapping.RenderDispatchDefinition.Read(mapChoices, allowEmpty: true).Count > 0)
            {
                foreach (var symbol in transformTypes)
                {
                    var token = new System.Text.RegularExpressions.Regex(@"(?<![A-Za-z_0-9])" + System.Text.RegularExpressions.Regex.Escape(symbol) + @"(?![A-Za-z_0-9])",
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                    var uses = FlattenNativeSource(source.Roots).Where(node => !(node.ShapeType == "VariableDeclaration" && node.Name == symbol))
                        .SelectMany(node => node.Properties.Where(property => token.IsMatch(property.Value)).Select(property => (Node: node, Property: property))).ToArray();
                    try
                    {
                        if (uses.Length == 0 || uses.Any(use => use.Property.Key != "Expression" || use.Node.ShapeType is not ("Expression" or "MessageAssignment" or "VariableAssignment")
                            || use.Node.Children.Count != 0)) continue;
                        foreach (var use in uses) _ = Assignments(use.Node, transformsOnly: true).ReadAssignments("proof");
                        erasableTransformTypes.Add(symbol);
                    }
                    catch (NotSupportedException) { /* The declaration remains an explicit blocker when any use is unproven. */ }
                }
            }
            NativeFlowNode Block(OdxNode node, string reason, List<NativeFlowNode>? children = null)
            {
                diagnostics.Add(new("BT-NATIVE-UNRESOLVED", MigrationDiagnosticSeverity.Blocking, paths[0],
                    $"{node.ShapeType} '{node.Name}' at line {node.SourceLine}: {reason}", "Implement/review the explicit native blocker; no source statement is executed or silently omitted."));
                return new() { Id = "source-" + node.Id, Name = node.Name.Length == 0 ? node.ShapeType : node.Name,
                    Kind = NativeFlowNodeKind.Blocked, BlockReason = reason, Children = children ?? [] };
            }
            NativeFlowNode Convert(OdxNode node)
            {
                if (node.ShapeType == "Decision")
                {
                    try
                    {
                        if (node.Children.Count == 0 || node.Children.Any(branch => branch.ShapeType != "DecisionBranch"))
                            throw new NotSupportedException("Decision requires ordered source branches.");
                        var predicates = node.Children.Select((branch, index) => string.IsNullOrWhiteSpace(branch.Properties.GetValueOrDefault("Expression"))
                            ? index == node.Children.Count - 1 ? null : throw new NotSupportedException("Only the final decision branch may be an else branch.")
                            : new NativeSourceExpressionReader(branch.Properties["Expression"], scalarTypes, messages, messageCatalog, contextCatalog: contextCatalog, configuration: configuration).ReadCondition()).ToArray();
                        List<NativeFlowNode> remaining = [];
                        for (var index = node.Children.Count - 1; index >= 0; index--)
                        {
                            var branch = node.Children[index];
                            var native = new NativeFlowNode { Id = "source-" + branch.Id, Name = branch.Name, Children = branch.Children.Select(Convert).ToList() };
                            if (predicates[index] is { } predicate) { native.Kind = NativeFlowNodeKind.If; native.Expression = predicate; native.Otherwise = remaining; }
                            remaining = [native];
                        }
                        return new() { Id = "source-" + node.Id, Name = node.Name, Children = remaining };
                    }
                    catch (NotSupportedException exception) { return Block(node, exception.Message, node.Children.Select(Convert).ToList()); }
                }
                var selectedVersion = policyTargets?.FirstOrDefault(pair => pair.Key == node.Properties.GetValueOrDefault("PolicyName")).Value;
                if (node.ShapeType == "CallRules" && selectedVersion is not null)
                {
                    try
                    {
                        var policyName = node.Properties.GetValueOrDefault("PolicyName", "");
                        var versions = policies.Where(policy => policy.Name == policyName && $"{policy.MajorVersion}.{policy.MinorVersion}" == selectedVersion).ToArray();
                        if (versions.Length != 1 || versions[0].ProposedPolicyJson is not { } proposal)
                            throw new NotSupportedException("Selected policy target has no unique complete native proposal in the supplied exports.");
                        if (node.Properties.GetValueOrDefault("PolicyVersion", "-1") is { } sourceVersion && sourceVersion != "-1" && sourceVersion != selectedVersion)
                            throw new NotSupportedException("Explicit native policy target conflicts with the source call's pinned version.");
                        if (node.Children.Count != 1 || node.Children[0].ShapeType != "RulesParameterRef" || node.Children[0].Children.Count != 0)
                            throw new NotSupportedException("Native policy call requires exactly one explicit XML message parameter with no nested behavior.");
                        var parameter = node.Children[0];
                        var message = parameter.Properties.GetValueOrDefault("Reference", "");
                        var declaration = messageDeclarations.Where(item => item.Name == message).ToArray();
                        if (declaration.Length != 1 || declarationCounts.GetValueOrDefault(message) != 1
                            || versions[0].FactTypes.Count != 1 || declaration[0].Properties.GetValueOrDefault("Type") != versions[0].FactTypes[0]
                            || parameter.Properties.GetValueOrDefault("Alias") != versions[0].FactTypes[0])
                            throw new NotSupportedException("Policy fact alias requires the exact unshadowed service-message declaration and exported XML fact type.");
                        var policy = XmlRuleRuntime.ReadPolicy(proposal);
                        if (BizTalkRulePolicyAnalyzer.CountPotentialConflicts(policy, default) != 0)
                            throw new NotSupportedException("Selected policy has potentially conflicting matches; native import does not guess BRE agenda/priority conflict resolution.");
                        messageCatalog.ValidatePolicyMessage(message, policy);
                        warnings.Add($"Policy '{policyName}' {selectedVersion} is an explicitly selected native test target, not proof of the deployed version. Only proven conflict-free single-pass XML fact behavior is compiled; full flow activation remains disabled.");
                        return new() { Id = "source-" + node.Id, Name = node.Name, Children = [
                            new() { Id = "source-" + parameter.Id, Name = parameter.Name },
                            new() { Id = "source-" + node.Id + "-rules", Name = policyName + " " + selectedVersion + " (native test target)", Kind = NativeFlowNodeKind.ApplyXmlRules, MessageName = message, PolicyJson = proposal }
                        ] };
                    }
                    catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                    { return Block(node, exception.Message, node.Children.Select(Convert).ToList()); }
                }
                var children = node.Children.Select(Convert).ToList();
                var translated = new NativeFlowNode { Id = "source-" + node.Id, Name = node.Name.Length == 0 ? node.ShapeType : node.Name, Children = children };
                switch (node.ShapeType)
                {
                    case "ServiceDeclaration": case "ServiceBody": case "Construct":
                        return translated;
                    case "MessageRef":
                        if (node.Children.Count == 0 && parents.GetValueOrDefault(node.Id)?.ShapeType == "Construct"
                            && messages.Contains(node.Properties.GetValueOrDefault("Ref", "")))
                        {
                            translated.Name = "Construct message: " + node.Properties["Ref"];
                            return translated;
                        }
                        return Block(node, "Message reference is not an exact declared Construct output; operation/type references require explicit contract translation.", children);
                    case "VariableDeclaration":
                        try
                        {
                            if (elementDeclarations.Any(declaration => declaration.Id == node.Id)) return translated;
                            if (erasableTransformTypes.Contains(node.Name) && variableDeclarations.Any(declaration => declaration.Id == node.Id))
                            {
                                translated.Name = "Native renderer identity binding: " + node.Name;
                                return translated;
                            }
                            if (variableDeclarations.Any(declaration => declaration.Id == node.Id) && documents.Contains(node.Name)
                                && variableDeclarations.Count(declaration => declaration.Name == node.Name) == 1 && node.Children.Count == 0
                                && string.IsNullOrWhiteSpace(node.Properties.GetValueOrDefault("InitialValue"))
                                && node.Properties.GetValueOrDefault("ParamDirection", "In") == "In") return translated;
                            if (!variableDeclarations.Any(declaration => declaration.Id == node.Id) || !scalarTypes.TryGetValue(node.Name, out var type)
                                || node.Children.Count != 0 || node.Properties.GetValueOrDefault("UseDefaultConstructor", "False") != "False"
                                || node.Properties.GetValueOrDefault("ParamDirection", "In") != "In")
                                throw new NotSupportedException("Only unique service-level scalar variables with explicit or language-default initializers are supported; other constructors/scoped variables are not inferred.");
                            translated.Kind = NativeFlowNodeKind.DeclareVariable; translated.Variable = node.Name; translated.VariableType = type;
                            var initializer = node.Properties.GetValueOrDefault("InitialValue");
                            translated.Expression = string.IsNullOrWhiteSpace(initializer)
                                ? new() { Literal = type switch { NativeScalarType.Int32 => NativeScalar.From(0), NativeScalarType.Boolean => NativeScalar.From(false), _ => NativeScalar.From(null) } }
                                : new NativeSourceExpressionReader(initializer, scalarTypes, messages, messageCatalog, contextCatalog: contextCatalog, configuration: configuration).ReadCondition();
                            return translated;
                        }
                        catch (NotSupportedException exception) { return Block(node, exception.Message, children); }
                    case "VariableAssignment":
                    case "Expression":
                    case "MessageAssignment":
                        try
                        {
                            if (children.Count > 0) throw new NotSupportedException("Source statements contain unsupported nested metadata.");
                            var expression = node.Properties.GetValueOrDefault("Expression", "");
                            var hasTransform = expression.Contains("System.Type.GetType", StringComparison.Ordinal) || expression.Contains("transform", StringComparison.Ordinal);
                            translated.Children = Assignments(node, transformsOnly: hasTransform).ReadAssignments(translated.Id).ToList();
                            if (translated.Children.Any(child => child.SelectionExpression is not null))
                                warnings.Add($"Transform '{node.Name}' uses explicitly configured exact native renderer replacements with ordered named arguments; no CLR map is loaded and source parity is not implied. Full flow activation remains disabled.");
                            return translated;
                        }
                        catch (NotSupportedException exception) { return Block(node, exception.Message, children); }
                    case "While": case "Loop":
                        try
                        {
                            translated.Kind = NativeFlowNodeKind.While;
                            translated.Expression = new NativeSourceExpressionReader(node.Properties.GetValueOrDefault("Expression", ""), scalarTypes, messages, messageCatalog, contextCatalog: contextCatalog, configuration: configuration).ReadCondition();
                            warnings.Add($"Source loop '{node.Name}' uses a native safety bound of {translated.MaxIterations} iterations. Review the bound in the visual editor; exceeding it fails explicitly.");
                            return translated;
                        }
                        catch (NotSupportedException exception) { return Block(node, exception.Message, children); }
                    case "MessageDeclaration":
                        if (node.Properties.GetValueOrDefault("InitialValue") is { Length: > 0 }) return Block(node, "Message initialization requires an explicit native payload/fact definition.", children);
                        return translated;
                    case "Receive":
                        if (node.Properties.Any(pair => pair.Key.Contains("Correlation", StringComparison.Ordinal) || pair.Key.Contains("Filter", StringComparison.Ordinal))
                            || children.Count != 0)
                            return Block(node, "Source receive filters/correlation declarations require explicit native channel and promoted-metadata bindings.", children);
                        var message = node.Properties.GetValueOrDefault("MessageName", "");
                        var channel = node.Properties.GetValueOrDefault("PortName", "");
                        if (message.Length == 0 || channel.Length == 0) return Block(node, "Receive has no exact message/port identity.");
                        translated.Kind = NativeFlowNodeKind.Receive; translated.MessageName = message; translated.Channel = channel;
                        return translated;
                    case "Send":
                        if (children.Count > 0) return Block(node, "Send contains unsupported nested behavior.", children);
                        var outboundMessage = node.Properties.GetValueOrDefault("MessageName", "");
                        if (outboundMessage.Length == 0) return Block(node, "Send has no named message.");
                        if (node.Properties.GetValueOrDefault("OperationMessageName") == "Response")
                        {
                            translated.Kind = NativeFlowNodeKind.Reply; translated.MessageName = outboundMessage; return translated;
                        }
                        var port = node.Properties.GetValueOrDefault("PortName", "");
                        var matches = owners.SelectMany(owner => owner.Service.LogicalPorts.Where(logical => logical.Name == port)
                            .SelectMany(logical => logical.SendPortNames.Select(name => (owner.Binding, Name: name)))).ToArray();
                        if (matches.Length != 1) return Block(node, "Outbound logical port has no unique explicit physical binding. Direct subscriptions/dynamic routes are not guessed.");
                        var physical = matches[0].Binding.SendPorts.Where(send => send.Name == matches[0].Name).ToArray();
                        if (physical.Length != 1) return Block(node, "Bound physical send port is missing or ambiguous.");
                        if (!physical[0].TransportType.Equals("FILE", StringComparison.OrdinalIgnoreCase)
                            && !physical[0].TransportType.Equals("HTTP", StringComparison.OrdinalIgnoreCase))
                            return Block(node, "This exact bound transport requires an explicit native protocol adapter; address/name heuristics are not execution wiring.");
                        var settings = CreateDeliverSettings(physical[0], candidate.SuggestedFlowId, node.Id, requirements, "", includeConfigPlaceholders: true);
                        translated.Kind = NativeFlowNodeKind.Deliver; translated.Module = settings.Module;
                        translated.Settings = new(settings.Settings, StringComparer.OrdinalIgnoreCase); translated.MessageName = outboundMessage;
                        if (physical[0].IsTwoWay) translated.Settings["expectsResponse"] = "true";
                        if (physical[0].RetryCount > 0 || !string.IsNullOrWhiteSpace(physical[0].TransmitPipeline))
                        {
                            translated.Id += "-delivery";
                            return Block(node, physical[0].RetryCount > 0
                                ? "Physical send retry semantics require reviewed native effect reconciliation; no automatic resend is inferred."
                                : "The bound transmit pipeline requires explicit ordered native components; no serializer or pass-through is substituted.", [translated]);
                        }
                        return translated;
                    case "CallRules":
                        var name = node.Properties.GetValueOrDefault("PolicyName", "");
                        var versions = policies.Where(policy => policy.Name == name).OrderByDescending(policy => policy.MajorVersion).ThenByDescending(policy => policy.MinorVersion).ToArray();
                        var latest = versions.FirstOrDefault();
                        if (latest?.ProposedPolicyJson is { } proposal)
                            children.Add(new() { Id = translated.Id + "-policy", Name = name + " " + latest.MajorVersion + "." + latest.MinorVersion + " (exported latest; review)",
                                Kind = NativeFlowNodeKind.Augment, Module = "xml-rules-augment", Settings = new() { ["policyJson"] = proposal, ["factMode"] = "Metadata", ["metadataPrefix"] = "rules." } });
                        return Block(node, latest is null ? "Called policy is absent from supplied exports." :
                            "Policy proposal is attached, but CallRules input/output fact bindings and source version resolution require review. Latest exported version is not deployed proof.", children);
                    default:
                        return Block(node, $"Source {node.ShapeType} semantics are not translated by the native importer.", children);
                }
            }
            var definition = new NativeFlowDefinition { Durable = true, Nodes = source.Roots.Select(Convert).ToList() };
            if (configuration is not null)
                warnings.Add("Explicit host configuration and pure getter source files are native test snapshots, not proof of deployed assembly identity or inherited machine configuration. Referenced getter results are pinned as editable scalar literals; absent keys retain the proven empty-string return. No configuration file or helper assembly is read at execution time.");
            if (definition.AllNodes().Any(node => node.Expression?.RequiresInstance == true || node.NamespaceExpression?.RequiresInstance == true))
                warnings.Add("Source instance identity reads use the stable new native instance GUID, not a legacy BizTalk instance ID. This identity survives waits and restart; review any external identity contract before activation.");
            var json = JsonSerializer.Serialize(definition, NativeJson);
            _ = NativeFlowDefinition.Read(json, allowBlocked: true);
            warnings.Add("Native source structure is preserved with explicit blockers; no ODX interpreter or heuristic routing/decision augments execute it. Configure stable message/instance IDs and channel metadata before native activation.");
            warnings.Add("Declared source context properties use exact case-sensitive {namespace}property metadata keys with schema-proven String/Int32/Boolean types. Incoming promotion/context adapters and MessageBox subscriptions are not inferred from body fields; missing keys fail. Review these inlet contracts before activation.");
            return candidate with
            {
                DraftFlow = candidate.DraftFlow with
                {
                    Augments = [new(NativeFlowDefinition.ModuleId, new Dictionary<string, string> { ["flowJson"] = json })],
                    Deliveries = [],
                    ConfigurationRequirements = requirements, Warnings = warnings,
                    Migration = report with { Diagnostics = diagnostics }
                },
                ScaffoldedModules = candidate.ScaffoldedModules.Where(module => !module.ModuleId.StartsWith("odx-translated-", StringComparison.Ordinal))
                    .Concat([new BizTalkScaffoldedModuleResponse(candidate.SuggestedFlowId + "-native.json", candidate.SuggestedFlowId + "-native", "Native control-flow draft", json)]).ToArray()
            };
        }
        catch (Exception exception) when (exception is ArgumentException or System.Xml.XmlException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            diagnostics.Add(new("BT-NATIVE-TRANSLATION-FAILED", MigrationDiagnosticSeverity.Blocking, paths[0], "Native draft could not be generated: " + exception.Message,
                "Correct the bounded source/definition; no partial native translation is available."));
            var failed = JsonSerializer.Serialize(new NativeFlowDefinition { Durable = true, Nodes =
                [new() { Id = "translation-failed", Kind = NativeFlowNodeKind.Blocked, BlockReason = "Native source translation failed; inspect migration diagnostics before implementing the flow." }] }, NativeJson);
            return candidate with
            {
                DraftFlow = candidate.DraftFlow with { Augments = [new(NativeFlowDefinition.ModuleId, new Dictionary<string, string> { ["flowJson"] = failed })],
                    Deliveries = [], Migration = report with { Diagnostics = diagnostics } },
                ScaffoldedModules = candidate.ScaffoldedModules.Where(module => !module.ModuleId.StartsWith("odx-translated-", StringComparison.Ordinal)).ToArray()
            };
        }
    }

    private static IEnumerable<OdxNode> FlattenNativeSource(IEnumerable<OdxNode> nodes)
    {
        foreach (var node in nodes) { yield return node; foreach (var child in FlattenNativeSource(node.Children)) yield return child; }
    }
}
