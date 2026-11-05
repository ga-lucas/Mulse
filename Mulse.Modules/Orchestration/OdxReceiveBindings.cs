using System.Globalization;
using System.Text.Json.Nodes;

namespace Mulse.Modules.Orchestration;

/// <summary>Compiles exact receive filters and lexically scoped receive/send correlation sets.</summary>
public static class OdxReceiveBindings
{
    public static IReadOnlyList<string> Bind(OdxProgram program, OdxNode entry, OdxDurableBindings bindings, IReadOnlyList<OdxProgram>? referencedPrograms = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(bindings);
        if (referencedPrograms?.Count > 32) throw new ArgumentException("At most 32 referenced source models are supported.");
        var all = ModelNodes(program);
        var selected = Flatten([entry]).ToArray();
        if (entry.ShapeType != "ServiceDeclaration" || !all.Any(node => ReferenceEquals(node, entry))) throw new ArgumentException("Receive translation requires a service in its source program.");
        var typesByName = new[] { program }.Concat(referencedPrograms ?? []).SelectMany(model => QualifiedTypes(model)).ToLookup(type => type.Name, StringComparer.Ordinal);
        var receives = selected.Where(node => node.ShapeType == "Receive").ToArray();
        if (receives.Any(node => node.Properties.ContainsKey("Activate")))
        {
            var first = entry.Children.SingleOrDefault(node => node.ShapeType == "ServiceBody")?.Children.FirstOrDefault();
            if (first?.ShapeType != "Receive" || first.Properties.GetValueOrDefault("Activate") != "True"
                || receives.Any(node => !ReferenceEquals(node, first) && node.Properties.GetValueOrDefault("Activate") != "False"))
                throw new NotSupportedException("Source activation requires one initial activating receive followed by non-activating receives; no activation flags are ignored.");
        }
        var references = new Dictionary<int, List<CorrelationUse>>();
        var declarations = selected.Where(node => node.ShapeType == "CorrelationDeclaration").ToArray();
        foreach (var declaration in declarations)
        {
            var owner = selected.SingleOrDefault(node => node.Id == declaration.ParentId);
            if (owner?.ShapeType is not ("ServiceDeclaration" or "Scope") || declaration.Properties.GetValueOrDefault("ParamDirection", "In") != "In")
                throw new NotSupportedException($"Correlation {declaration.Id} requires a local service/scope lifetime; correlation parameters are not translated.");
            var lifetime = Flatten([owner]).Select(node => node.Id).ToHashSet();
            var typeName = Required(declaration, "Type");
            var types = typesByName[typeName].ToArray();
            if (types.Length != 1) throw new NotSupportedException($"Correlation type '{typeName}' must resolve to exactly one source definition.");
            var properties = types[0].Node.Children;
            if (properties.Count is < 1 or > 128 || properties.Any(property => property.ShapeType != "PropertyRef" || property.Children.Count != 0))
                throw new NotSupportedException($"Correlation type '{typeName}' requires 1-128 plain PropertyRef entries.");
            var keys = properties.Select(property => Required(property, "Ref")).ToArray();
            if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length) throw new ArgumentException("Duplicate correlation property in " + typeName);
            if (declaration.Children.Count == 0 || declaration.Children.Any(node => node.ShapeType != "StatementRef" || node.Children.Count != 0))
                throw new NotSupportedException($"Correlation {declaration.Id} requires explicit StatementRef metadata.");
            var uses = declaration.Children.Select(reference =>
            {
                var target = selected.Where(node => node.SourceId == Required(reference, "Ref")).ToArray();
                var initializes = Required(reference, "Initializes") switch { "True" => true, "False" => false, _ => throw new NotSupportedException("Invalid correlation Initializes value.") };
                if (target.Length != 1 || target[0].ShapeType is not ("Receive" or "Send") || !lifetime.Contains(target[0].Id))
                    throw new NotSupportedException($"Correlation {declaration.Id} references an unresolved statement or one outside its lexical receive/send lifetime.");
                return new CorrelationUse(declaration.Id, target[0].Id, initializes, keys);
            }).ToArray();
            if (uses.Count(use => use.Initializes) != 1 || uses.Select(use => use.NodeId).Distinct().Count() != uses.Length)
                throw new NotSupportedException($"Correlation {declaration.Id} requires exactly one initializer and distinct receive/send uses.");
            foreach (var use in uses)
            {
                if (!references.TryGetValue(use.NodeId, out var existing)) references.Add(use.NodeId, existing = []);
                existing.Add(use);
            }
            var key = StateKey(declaration.Id);
            bindings.Execution.Activities[declaration.Id] = (_, frame, token) => { token.ThrowIfCancellationRequested(); frame.Variables[key] = null; return Task.CompletedTask; };
            bindings.FrameLocalActivities.Add(declaration.Id);
        }
        foreach (var receive in selected.Where(node => node.ShapeType == "Receive"))
        {
            var filter = CompileFilter(receive);
            bindings.ReceiveFilters[receive.Id] = filter;
            var port = Required(receive, "PortName"); var message = Required(receive, "MessageName");
            var uses = references.GetValueOrDefault(receive.Id) ?? [];
            bindings.Receives[receive.Id] = (_, frame) =>
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var use in uses.Where(use => !use.Initializes))
                {
                    if (frame.Variables.GetValueOrDefault(StateKey(use.DeclarationId)) is not JsonObject saved)
                        throw new InvalidOperationException($"Correlation {use.DeclarationId} is followed before initialization.");
                    foreach (var key in use.Keys)
                    {
                        var value = saved[key]?.GetValue<string>() ?? throw new InvalidOperationException("Missing pinned correlation property: " + key);
                        if (values.TryGetValue(key, out var previous) && previous != value) throw new InvalidOperationException("Conflicting correlation sets for property " + key);
                        values[key] = value;
                    }
                }
                return new(port, message, values)
                {
                    RequiredMetadata = uses.SelectMany(use => use.Keys).Distinct(StringComparer.Ordinal).ToArray(),
                    OperationName = receive.Properties.GetValueOrDefault("OperationName"), OperationMessageName = receive.Properties.GetValueOrDefault("OperationMessageName")
                };
            };
            bindings.AcceptedReceives[receive.Id] = (_, frame, token) =>
            {
                token.ThrowIfCancellationRequested();
                var payload = frame.Messages[message].Payloads.Single();
                PinAndValidate(uses, frame, payload);
                return Task.CompletedTask;
            };
        }
        foreach (var send in selected.Where(node => node.ShapeType == "Send" && references.ContainsKey(node.Id)))
        {
            var message = Required(send, "MessageName");
            var uses = references[send.Id];
            bindings.PreparingSends[send.Id] = (_, frame, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (!frame.Messages.TryGetValue(message, out var batch) || batch.Count != 1)
                    throw new InvalidOperationException($"Correlated send {send.Id} requires exactly one initialized named message.");
                PinAndValidate(uses, frame, batch.Payloads[0]);
                return Task.CompletedTask;
            };
        }
        var firstReceive = entry.Children.SingleOrDefault(node => node.ShapeType == "ServiceBody")?.Children.FirstOrDefault();
        var initializers = references.Values.SelectMany(uses => uses).Where(use => use.Initializes).ToArray();
        return firstReceive is { ShapeType: "Receive" } && initializers.Length > 0 && initializers.All(use => use.NodeId == firstReceive.Id)
            ? initializers.SelectMany(use => use.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() : [];
    }

    public static Func<IntegrationPayload, bool> CompileFilter(OdxNode receive)
    {
        if (receive.ShapeType != "Receive" || receive.Children.Count > 128 || receive.Children.Any(node => node.ShapeType != "DNFPredicate" || node.Children.Count != 0))
            throw new NotSupportedException($"Receive {receive.Id} contains unsupported filter metadata.");
        var predicates = receive.Children.Select(node =>
        {
            var operation = node.Properties.GetValueOrDefault("Operator");
            if (node.Properties.GetValueOrDefault("Grouping") != "AND" || operation is not ("Equals" or "Exists"))
                throw new NotSupportedException($"Receive filter {node.Id} requires source AND with Equals/Exists semantics; no predicate is discarded.");
            var key = Required(node, "LHS");
            if (operation == "Exists")
            {
                if (node.Properties.ContainsKey("RHS")) throw new NotSupportedException("Exists predicates cannot contain an ignored RHS.");
                return (Func<IntegrationPayload, bool>)(payload => TryExactMetadata(payload, key, out _));
            }
            var source = Required(node, "RHS");
            if (source.Length > 4096) throw new NotSupportedException("Receive literal exceeds 4096 characters.");
            JsonNode? value;
            try { value = JsonNode.Parse(source); }
            catch (System.Text.Json.JsonException exception) { throw new NotSupportedException("Receive RHS must be a JSON-compatible string/Boolean/Int32 literal.", exception); }
            if (value is not JsonValue literal) throw new NotSupportedException("Receive RHS requires a non-null scalar literal.");
            if (literal.TryGetValue<string>(out var text)) return (Func<IntegrationPayload, bool>)(payload => TryExactMetadata(payload, key, out var actual) && actual == text);
            if (literal.TryGetValue<bool>(out var boolean)) return payload => TryExactMetadata(payload, key, out var actual) && TryBoolean(actual, out var parsed) && parsed == boolean;
            if (literal.TryGetValue<int>(out var number)) return payload => TryExactMetadata(payload, key, out var actual)
                && int.TryParse(actual, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) && parsed == number;
            throw new NotSupportedException("Receive RHS supports only string/Boolean/Int32 literals.");
        }).ToArray();
        return payload => predicates.All(predicate => predicate(payload));
    }

    private static void PinAndValidate(IEnumerable<CorrelationUse> uses, OdxExecutionFrame frame, IntegrationPayload payload)
    {
        var snapshots = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var use in uses)
        {
            var key = StateKey(use.DeclarationId);
            if (!frame.Variables.TryGetValue(key, out var current))
                throw new InvalidOperationException($"Correlation {use.DeclarationId} is outside its initialized scope lifetime.");
            if (use.Initializes)
            {
                if (current is not null) throw new InvalidOperationException($"Correlation {use.DeclarationId} is already initialized.");
                var snapshot = new JsonObject();
                foreach (var property in use.Keys) snapshot[property] = ExactMetadata(payload, property);
                snapshots.Add(key, snapshot);
            }
            else
            {
                if (current is not JsonObject saved) throw new InvalidOperationException($"Correlation {use.DeclarationId} is followed before initialization.");
                foreach (var property in use.Keys)
                    if (saved[property]?.GetValue<string>() != ExactMetadata(payload, property))
                        throw new InvalidOperationException($"Correlation {use.DeclarationId} does not match promoted property {property}.");
            }
        }
        foreach (var snapshot in snapshots) frame.Variables[snapshot.Key] = snapshot.Value;
    }

    internal static void ExitScope(OdxNode scope, OdxExecutionFrame frame)
    {
        foreach (var declaration in scope.Children.Where(node => node.ShapeType == "CorrelationDeclaration"))
            frame.Variables.Remove(StateKey(declaration.Id));
    }

    private static bool TryBoolean(string value, out bool parsed)
    {
        if (value is "true" or "1" or "True") { parsed = true; return true; }
        if (value is "false" or "0" or "False") { parsed = false; return true; }
        parsed = false; return false;
    }
    internal static bool TryExactMetadata(IntegrationPayload payload, string key, out string value)
    {
        foreach (var pair in payload.Metadata) if (pair.Key == key) { value = pair.Value; return true; }
        value = ""; return false;
    }
    private static string ExactMetadata(IntegrationPayload payload, string key) => TryExactMetadata(payload, key, out var value)
        ? value : throw new InvalidOperationException("Receive lacks promoted correlation property: " + key);
    private static string StateKey(int declaration) => "$odx.sourceCorrelation." + declaration.ToString(CultureInfo.InvariantCulture);
    private static string Required(OdxNode node, string key) => node.Properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new NotSupportedException($"ODX node {node.Id} requires {key}.");
    private static OdxNode[] ModelNodes(OdxProgram program)
    {
        if (program is null || program.Version != 1) throw new ArgumentException("Receive translation requires version-1 source models.");
        var nodes = Flatten(program.Roots).Take(10001).ToArray();
        if (nodes.Length > 10000 || nodes.Select(node => node.Id).Distinct().Count() != nodes.Length) throw new ArgumentException("Receive source model exceeds 10000 unique nodes.");
        return nodes;
    }
    private static IEnumerable<(string Name, OdxNode Node)> QualifiedTypes(OdxProgram program)
        => ModelNodes(program).Where(node => node.ShapeType == "Module").SelectMany(module => module.Children.Where(node => node.ShapeType == "CorrelationType").Select(node => (module.Name + "." + node.Name, node)));
    private static IEnumerable<OdxNode> Flatten(IEnumerable<OdxNode> roots, int depth = 1)
    {
        foreach (var node in roots)
        {
            if (depth > 64) throw new ArgumentException("ODX receive metadata exceeds 64 levels.");
            yield return node;
            foreach (var child in Flatten(node.Children, depth + 1)) yield return child;
        }
    }
    private sealed record CorrelationUse(int DeclarationId, int NodeId, bool Initializes, string[] Keys);
}
