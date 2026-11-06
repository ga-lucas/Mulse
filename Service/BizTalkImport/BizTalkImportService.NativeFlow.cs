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

    // Settings mirror the module's documented defaults; the importer cannot know the BizTalk host's zone/culture, so they stay visible and editable.
    private static readonly IReadOnlyDictionary<string, NativeSourceUdf> KnownUdfs = new Dictionary<string, NativeSourceUdf>(StringComparer.Ordinal)
    {
        ["FilenameMaker.MakeFilename"] = new("filename-template-transform", new Dictionary<string, string> { ["timeZone"] = "UTC", ["culture"] = "Invariant" }, 1)
    };
    private static readonly System.Text.RegularExpressions.Regex NsoftwareTransportPattern = new(@"^nsoftware\.S?FTP( \d{4})?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static bool ContainsTransformCall(IEnumerable<NativeFlowNode> nodes)
        => nodes.Any(node => node.Kind == NativeFlowNodeKind.InvokeTransform || ContainsTransformCall(node.Children) || ContainsTransformCall(node.Otherwise));

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
            var scopeScalarDeclarations = FlattenNativeSource(source.Roots).Where(node => node.ShapeType == "VariableDeclaration" && parents.GetValueOrDefault(node.Id)?.ShapeType == "Scope").ToArray();
            var scalarTypes = variableDeclarations.Concat(scopeScalarDeclarations).Where(node => declarationCounts[node.Name] == 1 && node.Properties.GetValueOrDefault("Type") is "System.String" or "System.Int32" or "System.Boolean")
                .GroupBy(node => node.Name, StringComparer.Ordinal).Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single().Properties["Type"] switch
                { "System.Int32" => NativeScalarType.Int32, "System.Boolean" => NativeScalarType.Boolean, _ => NativeScalarType.String }, StringComparer.Ordinal);
            var dynamicPorts = FlattenNativeSource(source.Roots).Where(node => node.ShapeType == "PortDeclaration" && NativeExpression.ValidName(node.Name)
                    && node.Children.Any(child => child.ShapeType == "PhysicalBindingAttribute" && child.Properties.GetValueOrDefault("IsDynamic") == "True"))
                .GroupBy(node => node.Name, StringComparer.Ordinal).Where(group => group.Count() == 1).Select(group => group.Key)
                .Where(port => !scalarTypes.ContainsKey(NativeSourceExpressionReader.DynamicPortVariable(port, "Address")) && !scalarTypes.ContainsKey(NativeSourceExpressionReader.DynamicPortVariable(port, "TransportType")))
                .ToHashSet(StringComparer.Ordinal);
            var hiddenDeclarations = new List<NativeFlowNode>();
            void DeclareHidden(string name)
            {
                if (hiddenDeclarations.Any(declaration => declaration.Variable == name)) return;
                hiddenDeclarations.Add(new() { Id = "generated-" + name, Name = name + " (generated)", Kind = NativeFlowNodeKind.DeclareVariable, Variable = name,
                    VariableType = NativeScalarType.String, Expression = new() { Literal = NativeScalar.From("") } });
            }
            foreach (var port in dynamicPorts.Order(StringComparer.Ordinal))
                foreach (var property in new[] { "Address", "TransportType" })
                {
                    var variable = NativeSourceExpressionReader.DynamicPortVariable(port, property);
                    scalarTypes[variable] = NativeScalarType.String; DeclareHidden(variable);
                }
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
                transformTypes, mapChoices, KnownUdfs, dynamicPorts);
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
            void Warn(string text) { if (!warnings.Contains(text)) warnings.Add(text); }
            var activeExceptionNames = new HashSet<string>(StringComparer.Ordinal);
            NativeFlowNode Block(OdxNode node, string reason, List<NativeFlowNode>? children = null)
            {
                diagnostics.Add(new("BT-NATIVE-UNRESOLVED", MigrationDiagnosticSeverity.Blocking, paths[0],
                    $"{node.ShapeType} '{node.Name}' at line {node.SourceLine}: {reason}", "Implement/review the explicit native blocker; no source statement is executed or silently omitted."));
                return new() { Id = "source-" + node.Id, Name = node.Name.Length == 0 ? node.ShapeType : node.Name,
                    Kind = NativeFlowNodeKind.Blocked, BlockReason = reason, Children = children ?? [] };
            }
            NativeFlowNode DynamicSend(OdxNode node, NativeFlowNode translated, string port, string message, SendPortAnalysis send)
            {
                if (send.IsTwoWay) return Block(node, $"Dynamic send port '{send.Name}' is two-way; native request/response over a runtime-selected transport is not inferred.");
                NativeExpression Variable(string name) => new() { Kind = NativeExpressionKind.Variable, Variable = name };
                var addressVariable = NativeSourceExpressionReader.DynamicPortVariable(port, "Address");
                var transportVariable = NativeSourceExpressionReader.DynamicPortVariable(port, "TransportType");
                NativeFlowNode Part(string suffix, string format, string part, string? defaultPort = null)
                {
                    var target = NativeSourceExpressionReader.DynamicPortVariable(port, format == "Mail" ? "MailAddress" : part);
                    DeclareHidden(target);
                    var partSettings = new Dictionary<string, string> { ["format"] = format, ["part"] = part };
                    if (defaultPort is not null) partSettings["defaultPort"] = defaultPort;
                    return new() { Id = translated.Id + suffix, Name = "Address " + part, Kind = NativeFlowNodeKind.InvokeTransform, Module = "address-part-transform",
                        Variable = target, TransformInputs = [Variable(addressVariable)], Settings = partSettings };
                }
                NativeFlowNode Deliver(string suffix, string module, Dictionary<string, string> staticSettings, Dictionary<string, NativeExpression> computed)
                    => new() { Id = translated.Id + suffix, Name = "Deliver (" + module + ")", Kind = NativeFlowNodeKind.Deliver, Module = module, MessageName = message,
                        Settings = new(staticSettings, StringComparer.OrdinalIgnoreCase), SettingExpressions = computed };
                string Requirement(string suffix, string key, string kind, string description)
                    => AddRequirement(requirements, candidate.SuggestedFlowId, $"deliveries[{translated.Id + suffix}].delivers[0].deliver.settings.{key}", kind, description, appliedToDraft: true);
                var allowed = send.DynamicTransportTypes;
                var branches = new List<(string Transport, List<NativeFlowNode> Nodes)>();
                bool Allowed(string transport) => allowed.Count == 0 || allowed.Contains(transport, StringComparer.OrdinalIgnoreCase);
                if (Allowed("FILE"))
                    branches.Add(("FILE", [Part("-file-dir", "Path", "Directory"), Part("-file-name", "Path", "FileName"),
                        Deliver("-file", "file-system-deliver", new() { ["namingMode"] = "Fixed", ["overwriteExisting"] = "false" },
                            new() { ["path"] = Variable(NativeSourceExpressionReader.DynamicPortVariable(port, "Directory")), ["fileName"] = Variable(NativeSourceExpressionReader.DynamicPortVariable(port, "FileName")) })]));
                if (Allowed("FTP"))
                    branches.Add(("FTP", [Part("-ftp-host", "Url", "Host"), Part("-ftp-port", "Url", "Port", "21"), Part("-ftp-dir", "Url", "Directory"), Part("-ftp-name", "Url", "FileName"),
                        Deliver("-ftp", "ftp-deliver", new()
                        {
                            ["username"] = Requirement("-ftp", "username", "Secret", $"FTP user name for dynamic send port '{send.Name}'."),
                            ["password"] = Requirement("-ftp", "password", "Secret", $"FTP password for dynamic send port '{send.Name}' (BizTalk never exports passwords)."),
                            ["encryptionMode"] = "Auto", ["passive"] = "true", ["acceptAnyCertificate"] = "false", ["overwrite"] = "false", ["append"] = "false"
                        }, new()
                        {
                            ["host"] = Variable(NativeSourceExpressionReader.DynamicPortVariable(port, "Host")), ["port"] = Variable(NativeSourceExpressionReader.DynamicPortVariable(port, "Port")),
                            ["remotePath"] = Variable(NativeSourceExpressionReader.DynamicPortVariable(port, "Directory")), ["remoteFileName"] = Variable(NativeSourceExpressionReader.DynamicPortVariable(port, "FileName"))
                        })]));
                if (Allowed("HTTP"))
                    branches.Add(("HTTP", [Deliver("-http", "http-deliver", new() { ["method"] = "POST" }, new() { ["url"] = Variable(addressVariable) })]));
                if (Allowed("SMTP"))
                    branches.Add(("SMTP", [Part("-smtp-to", "Mail", "MailAddress"),
                        Deliver("-smtp", "smtp-email-deliver", new()
                        {
                            ["host"] = Requirement("-smtp", "host", "Config", $"SMTP host for dynamic send port '{send.Name}'."),
                            ["port"] = "25",
                            ["from"] = Requirement("-smtp", "from", "Config", $"SMTP sender address for dynamic send port '{send.Name}'."),
                            ["subject"] = $"Imported BizTalk delivery {send.Name}", ["attachPayload"] = "true"
                        }, new() { ["to"] = Variable(NativeSourceExpressionReader.DynamicPortVariable(port, "MailAddress")) })]));
                if (branches.Count == 0) return Block(node, $"Dynamic send port '{send.Name}' allows no transport with a native adapter ({string.Join(", ", allowed)}).");
                var supported = string.Join(", ", branches.Select(branch => branch.Transport));
                List<NativeFlowNode> chain = [new() { Id = translated.Id + "-unsupported", Name = "No native adapter", Kind = NativeFlowNodeKind.Terminate,
                    TerminateMessage = $"Dynamic send port '{port}' has no native adapter for the assigned transport type; native adapters exist for: {supported}." }];
                foreach (var (transport, nodes) in Enumerable.Reverse(branches))
                    chain = [new() { Id = translated.Id + "-when-" + transport.ToLowerInvariant(), Name = transport, Kind = NativeFlowNodeKind.If,
                        Expression = new() { Kind = NativeExpressionKind.Equal, Operands = [Variable(transportVariable), new() { Literal = NativeScalar.From(transport) }] },
                        Children = nodes, Otherwise = chain }];
                translated.Kind = NativeFlowNodeKind.Sequence; translated.Children = chain;
                Warn($"Dynamic send port '{port}' is translated to an explicit native transport dispatch on the assigned TransportType (exact, case-sensitive FILE/FTP/HTTP/SMTP, limited to the binding's dynamic handlers). Address parsing uses the 'address-part-transform' UDF (Compatibility Pack): FILE splits the last separator, FTP expects ftp://host[:port]/dir/file (default port 21), SMTP strips mailto:. Adapter properties assigned on the outbound message (for example FTP.*, SMTP.* or nsoftware.* values such as TLS mode, subject, sender or host) are NOT translated and stay visible as blockers; the deliver nodes carry reviewed static defaults (FTP encryptionMode=Auto, passive, no overwrite; FILE overwriteExisting=false; SMTP host/from/subject as configuration). A FTP computed remote file name is still expanded as a template by ftp-deliver. Any other or unassigned transport type terminates the flow explicitly.");
                var unsupportedAllowed = allowed.Where(transport => !branches.Any(branch => branch.Transport.Equals(transport, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (unsupportedAllowed.Length > 0)
                    Warn($"Dynamic send port '{send.Name}' also allows transports with no native adapter ({string.Join(", ", unsupportedAllowed)}); those values terminate the flow.");
                if (!string.IsNullOrWhiteSpace(send.TransmitPipeline))
                {
                    translated.Id += "-delivery";
                    return Block(node, $"Send port '{send.Name}' uses transmit pipeline '{send.TransmitPipeline}'. Review its full component order and configuration; configure named-message augment/render nodes on a separate output message, then point the dynamic deliveries at that output. No serializer, component equivalence or pass-through is inferred.", [translated]);
                }
                return translated;
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
                if (node.ShapeType == "Scope")
                {
                    var catches = node.Children.Where(child => child.ShapeType == "Catch").ToArray();
                    var body = node.Children.Where(child => child.ShapeType != "Catch").Select(Convert).ToList();
                    Warn("Source scopes are translated as plain native sequences (or a native Try when the scope has a catch). Scope/transaction timeout, retry, batching, isolation, atomic rollback, synchronization and compensation are not enforced; review each scope's intended failure behavior.");
                    if (catches.Length == 0) return new() { Id = "source-" + node.Id, Name = node.Name, Children = body };
                    var handlerNode = catches[0];
                    var exceptionName = handlerNode.Properties.GetValueOrDefault("ExceptionName", "");
                    var messageVariable = exceptionName + "_Message";
                    var detailsVariable = exceptionName + "_StackTrace";
                    var unsupported = catches.Length != 1 ? "Scope has several catch handlers; native import does not guess handler order or exception-type matching."
                        : handlerNode.Properties.GetValueOrDefault("ExceptionType") != "System.Exception" || handlerNode.Properties.GetValueOrDefault("IsFaultMessage", "False") != "False"
                            ? "Only an untyped System.Exception catch is translated; typed, fault-message and general-exception ordering semantics require an explicit native design."
                        : !NativeExpression.ValidName(exceptionName) || !NativeExpression.ValidName(detailsVariable)
                            ? "Catch exception name is not a valid native variable prefix."
                        : new[] { exceptionName, messageVariable, detailsVariable }.Any(name => scalarTypes.ContainsKey(name) || messages.Contains(name))
                            ? "Catch exception name collides with a declared variable or message."
                        : !activeExceptionNames.Add(exceptionName) ? "Nested catch handlers reuse the active exception name; rename one explicitly." : null;
                    if (unsupported is not null)
                        return Block(node, unsupported, body.Concat(node.Children.Where(child => child.ShapeType == "Catch").Select(Convert)).ToList());
                    List<NativeFlowNode> handler;
                    string[] aliases = [exceptionName + ".Message", messageVariable, exceptionName + ".StackTrace", detailsVariable];
                    foreach (var alias in aliases) scalarTypes[alias] = NativeScalarType.String;
                    try { handler = handlerNode.Children.Select(Convert).ToList(); }
                    finally { foreach (var alias in aliases) scalarTypes.Remove(alias); activeExceptionNames.Remove(exceptionName); }
                    Warn("Native Try handlers run only for faults raised by the guarded body. Terminate, failed Deliver/Augment/Render effects (left ambiguous for reconciliation), persistence faults, and faults crossing a ForEach, Receive or Wait are not caught.");
                    if (handlerNode.Children.Any(child => child.Properties.Values.Any(value => value.Contains(exceptionName + ".StackTrace", StringComparison.Ordinal))))
                        Warn("Source exception StackTrace reads are mapped to the native exception type name and message, not a CLR stack trace. Any output that embeds it (for example a result description) will differ from the legacy text.");
                    return new() { Id = "source-" + node.Id, Name = node.Name, Kind = NativeFlowNodeKind.Try, Children = body,
                        Otherwise = [new() { Id = "source-" + handlerNode.Id, Name = handlerNode.Name, Children = handler }],
                        ExceptionMessageVariable = messageVariable, ExceptionDetailsVariable = detailsVariable };
                }
                var children = node.Children.Select(Convert).ToList();
                var translated = new NativeFlowNode { Id = "source-" + node.Id, Name = node.Name.Length == 0 ? node.ShapeType : node.Name, Children = children };
                switch (node.ShapeType)
                {
                    case "ServiceDeclaration": case "ServiceBody": case "Construct":
                        return translated;
                    case "Module": case "PortType": case "OperationDeclaration": case "PortDeclaration":
                    case "PhysicalBindingAttribute": case "LogicalBindingAttribute": case "PropertyRef":
                    case "CorrelationType": case "PartDeclaration": case "MultipartMessageType":
                        // Contract/wiring declarations carry no execution semantics; wiring is resolved from bindings (Send) or channel names (Receive).
                        translated.Name = node.ShapeType + ": " + translated.Name;
                        return translated;
                    case "Terminate":
                        if (children.Count != 0) return Block(node, "Terminate contains unsupported nested behavior.", children);
                        var rawTermination = node.Properties.GetValueOrDefault("ErrorMessage", "").Trim();
                        try
                        {
                            var expression = rawTermination.EndsWith(';') ? rawTermination[..^1].TrimEnd() : rawTermination;
                            var value = new NativeSourceExpressionReader(expression, scalarTypes, messages, messageCatalog, contextCatalog: contextCatalog, configuration: configuration).ReadCondition();
                            if (value.Kind != NativeExpressionKind.Literal || value.Literal is not { Type: NativeScalarType.String } literal
                                || string.IsNullOrWhiteSpace(literal.Value) || literal.Value.Length > 2000)
                                throw new NotSupportedException("Terminate requires a non-empty literal string of at most 2000 characters; dynamic or missing messages require explicit native configuration.");
                            translated.Kind = NativeFlowNodeKind.Terminate;
                            translated.TerminateMessage = literal.Value;
                            return translated;
                        }
                        catch (Exception exception) when (exception is NotSupportedException or JsonException)
                        {
                            var evidence = rawTermination.Length > 1024 ? rawTermination[..1024] + " [truncated]" : rawTermination;
                            return Block(node, "Terminate message could not be preserved: " + exception.Message + " Source expression: " + evidence, children);
                        }
                    case "Delay":
                        {
                            var rawTimeout = node.Properties.GetValueOrDefault("Timeout", "").Trim();
                            var timeout = rawTimeout.EndsWith(';') ? rawTimeout[..^1].TrimEnd() : rawTimeout;
                            var seconds = ReadConstantTimeoutSeconds(timeout);
                            if (children.Count != 0 || seconds is null or <= 0 or > 31622400)
                            {
                                var evidence = rawTimeout.Length > 1024 ? rawTimeout[..1024] + " [truncated]" : rawTimeout;
                                return Block(node, "Source delay timeout is not a supported positive whole-second TimeSpan within the native wait limit, or contains nested behavior; configure it explicitly (source expression: " + evidence + ").", children);
                            }
                            translated.Kind = NativeFlowNodeKind.Wait; translated.DurationSeconds = seconds.Value;
                            return translated;
                        }
                    case "MessageRef" when parents.GetValueOrDefault(node.Id)?.ShapeType is "OperationDeclaration" or "PartDeclaration":
                        translated.Name = "Operation message: " + node.Properties.GetValueOrDefault("Ref", node.Name);
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
                            if (!variableDeclarations.Concat(scopeScalarDeclarations).Any(declaration => declaration.Id == node.Id) || !scalarTypes.TryGetValue(node.Name, out var type)
                                || node.Children.Count != 0 || node.Properties.GetValueOrDefault("UseDefaultConstructor", "False") != "False"
                                || node.Properties.GetValueOrDefault("ParamDirection", "In") != "In")
                                throw new NotSupportedException("Only unique service-level or scope-level scalar variables with explicit or language-default initializers are supported; other constructors are not inferred.");
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
                            if (ContainsTransformCall(translated.Children))
                                Warn("Source helper FilenameMaker.MakeFilename is imported as the pure 'filename-template-transform' UDF (Compatibility Pack) with assumed settings timeZone=UTC and culture=Invariant. The BizTalk host's time zone and culture are unknown: review both in the node editor. The transform is not journaled, runs again after a crash before the next checkpoint, and its code version is not pinned.");
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
                        if (physical[0].IsDynamic && dynamicPorts.Contains(port)) return DynamicSend(node, translated, port, outboundMessage, physical[0]);
                        var vendorTransport = NsoftwareTransportPattern.Match(physical[0].TransportType);
                        if (!physical[0].TransportType.Equals("FILE", StringComparison.OrdinalIgnoreCase)
                            && !vendorTransport.Success
                            && !physical[0].TransportType.Equals("HTTP", StringComparison.OrdinalIgnoreCase)
                            && !physical[0].TransportType.Equals("FTP", StringComparison.OrdinalIgnoreCase)
                            && !physical[0].TransportType.Equals("SFTP", StringComparison.OrdinalIgnoreCase)
                            && !physical[0].TransportType.Equals("SMTP", StringComparison.OrdinalIgnoreCase))
                            return Block(node, $"Bound transport '{physical[0].TransportType}' requires an explicit native protocol adapter; address/name heuristics are not execution wiring.");
                        var deliverWarnings = new List<string>();
                        var settings = CreateDeliverSettings(physical[0], candidate.SuggestedFlowId, node.Id, requirements, "", includeConfigPlaceholders: true, deliverWarnings);
                        foreach (var deliverWarning in deliverWarnings) Warn(deliverWarning);
                        translated.Kind = NativeFlowNodeKind.Deliver; translated.Module = settings.Module;
                        translated.Settings = new(settings.Settings, StringComparer.OrdinalIgnoreCase); translated.MessageName = outboundMessage;
                        if (physical[0].IsTwoWay) translated.Settings["expectsResponse"] = "true";
                        if (physical[0].RetryCount > 0 || !string.IsNullOrWhiteSpace(physical[0].TransmitPipeline))
                        {
                            translated.Id += "-delivery";
                            var reason = physical[0].RetryCount > 0
                                ? "Physical send retry semantics require reviewed native effect reconciliation; no automatic resend is inferred. " : "";
                            if (!string.IsNullOrWhiteSpace(physical[0].TransmitPipeline))
                                reason += $"Send port '{physical[0].Name}' uses transmit pipeline '{physical[0].TransmitPipeline}'. Review its full component order and configuration; configure named-message augment/render nodes on a separate output message, then point the child delivery at that output. No serializer, component equivalence or pass-through is inferred.";
                            return Block(node, reason, [translated]);
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
                    case "LongRunningTransaction": case "AtomicTransaction": case "TransactionAttribute":
                        if (children.Count != 0) return Block(node, $"{node.ShapeType} contains unsupported nested behavior.", children);
                        var transactionSettings = string.Join("; ", node.Properties.Where(pair => pair.Key is not ("Name" or "Signal" or "ReportToAnalyst")).OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
                        translated.Name = node.ShapeType + " (not enforced)" + (transactionSettings.Length == 0 ? "" : ": " + transactionSettings);
                        Warn("Source transaction declarations (atomic/long-running type and transaction attributes such as batch, retry, timeout and isolation) are kept as named evidence only; none is enforced natively. Review whether retry, rollback or timeout behavior must be rebuilt explicitly.");
                        return translated;
                    default:
                        return Block(node, $"Source {node.ShapeType} semantics are not translated by the native importer.", children);
                }
            }
            var definition = new NativeFlowDefinition { Durable = true, Nodes = source.Roots.Select(Convert).ToList() };
            definition.Nodes.InsertRange(0, hiddenDeclarations);
            if (configuration is not null)
                warnings.Add("Explicit host configuration and pure getter source files are native test snapshots, not proof of deployed assembly identity or inherited machine configuration. Referenced getter results are pinned as editable scalar literals; absent keys retain the proven empty-string return. No configuration file or helper assembly is read at execution time.");
            if (definition.AllNodes().Any(node => node.Expression?.RequiresInstance == true || node.NamespaceExpression?.RequiresInstance == true))
                warnings.Add("Source instance identity reads use the stable new native instance GUID, not a legacy BizTalk instance ID. This identity survives waits and restart; review any external identity contract before activation.");
            var json = JsonSerializer.Serialize(definition, NativeJson);
            _ = NativeFlowDefinition.Read(json, allowBlocked: true);
            if (definition.AllNodes().Any(node => node.Expression?.UsesOrdinalSearch == true || node.NamespaceExpression?.UsesOrdinalSearch == true || node.SelectionExpression?.UsesOrdinalSearch == true))
                warnings.Add("Source string IndexOf/LastIndexOf calls default to culture-sensitive comparison in the legacy runtime; native flows search ordinally. Only non-empty printable-ASCII constant search text was imported, and results can differ only for text containing combining or ignorable characters. Review any such inputs.");
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

    private static int? ReadConstantTimeoutSeconds(string expression)
    {
        if (expression.Length > 4096) return null;
        const System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.CultureInvariant;
        var timeout = TimeSpan.FromMilliseconds(100);
        var ticks = System.Text.RegularExpressions.Regex.Match(expression, @"\A(?:new\s+(?:System\.)?TimeSpan|(?:System\.)?TimeSpan\.FromTicks)\s*\(\s*(?<ticks>[0-9]{1,19})[lL]?\s*\)\z", options, timeout);
        if (ticks.Success)
        {
            if (!long.TryParse(ticks.Groups["ticks"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
                || value % TimeSpan.TicksPerSecond != 0) return null;
            return (int)Math.Min(int.MaxValue, value / TimeSpan.TicksPerSecond);
        }
        var ctor = System.Text.RegularExpressions.Regex.Match(expression, @"^new\s+(?:System\.)?TimeSpan\s*\(\s*(?<args>[0-9]{1,9}(?:\s*,\s*[0-9]{1,9}){2,4})\s*\)$", options, timeout);
        long seconds;
        if (ctor.Success)
        {
            var args = ctor.Groups["args"].Value.Split(',').Select(value => long.Parse(value.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var offset = args.Length == 3 ? 0 : 1;
            var milliseconds = args.Length == 5 ? args[4] : 0;
            if (milliseconds % 1000 != 0) return null;
            seconds = (offset == 1 ? args[0] * 86400 : 0) + args[offset] * 3600 + args[offset + 1] * 60 + args[offset + 2] + milliseconds / 1000;
        }
        else
        {
            var factory = System.Text.RegularExpressions.Regex.Match(expression, @"^(?:System\.)?TimeSpan\.From(Seconds|Minutes|Hours|Days|Milliseconds)\s*\(\s*([0-9]{1,9})\s*\)$", options, timeout);
            if (!factory.Success) return null;
            var number = long.Parse(factory.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (factory.Groups[1].Value == "Milliseconds")
            {
                if (number % 1000 != 0) return null;
                seconds = number / 1000;
            }
            else
            {
                var multiplier = factory.Groups[1].Value switch { "Minutes" => 60L, "Hours" => 3600L, "Days" => 86400L, _ => 1L };
                seconds = number * multiplier;
            }
        }
        return (int)Math.Min(int.MaxValue, seconds);
    }

    private static IEnumerable<OdxNode> FlattenNativeSource(IEnumerable<OdxNode> nodes)
    {
        foreach (var node in nodes) { yield return node; foreach (var child in FlattenNativeSource(node.Children)) yield return child; }
    }
}
