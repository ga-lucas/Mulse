using System.Text.Json;
using System.Text.Json.Serialization;
using Mulse.Modules.ControlFlow;
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
        IReadOnlyList<BindingFileAnalysis> bindings, IReadOnlyList<BizTalkRulePolicyResponse> policies, BizTalkSchemaSourceCatalog schemas)
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
            var scalarTypes = variableDeclarations.Where(node => node.Properties.GetValueOrDefault("Type") is "System.String" or "System.Int32" or "System.Boolean")
                .GroupBy(node => node.Name, StringComparer.Ordinal).Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single().Properties["Type"] switch
                { "System.Int32" => NativeScalarType.Int32, "System.Boolean" => NativeScalarType.Boolean, _ => NativeScalarType.String }, StringComparer.Ordinal);
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
                            : new NativeSourceExpressionReader(branch.Properties["Expression"], scalarTypes, messages, messageCatalog).ReadCondition()).ToArray();
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
                                : new NativeSourceExpressionReader(initializer, scalarTypes, messages, messageCatalog).ReadCondition();
                            return translated;
                        }
                        catch (NotSupportedException exception) { return Block(node, exception.Message, children); }
                    case "VariableAssignment":
                    case "Expression":
                    case "MessageAssignment":
                        try
                        {
                            if (children.Count > 0) throw new NotSupportedException("Source statements contain unsupported nested metadata.");
                            translated.Children = new NativeSourceExpressionReader(node.Properties.GetValueOrDefault("Expression", ""), scalarTypes, messages, messageCatalog, documents, ElementSymbols(node))
                                .ReadAssignments(translated.Id).ToList();
                            return translated;
                        }
                        catch (NotSupportedException exception) { return Block(node, exception.Message, children); }
                    case "While": case "Loop":
                        try
                        {
                            translated.Kind = NativeFlowNodeKind.While;
                            translated.Expression = new NativeSourceExpressionReader(node.Properties.GetValueOrDefault("Expression", ""), scalarTypes, messages, messageCatalog).ReadCondition();
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
            var json = JsonSerializer.Serialize(definition, NativeJson);
            _ = NativeFlowDefinition.Read(json, allowBlocked: true);
            warnings.Add("Native source structure is preserved with explicit blockers; no ODX interpreter or heuristic routing/decision augments execute it. Configure stable message/instance IDs and channel metadata before native activation.");
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
