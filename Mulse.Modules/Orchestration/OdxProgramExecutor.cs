using Mulse.Modules.Execution;

namespace Mulse.Modules.Orchestration;

public sealed class OdxProgramExecutor
{
    public void Validate(OdxProgram program, OdxNode entry, OdxExecutionBindings bindings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ValidateBindings(program, entry, new(bindings.Activities), new(bindings.Conditions), new(bindings.Transactions), cancellationToken);
    }

    internal void ValidateContinuation(OdxProgram program, OdxNode entry, OdxExecutionBindings bindings, CancellationToken cancellationToken)
        => ValidateBindings(program, entry, new(bindings.Activities), new(bindings.Conditions), new(bindings.Transactions), cancellationToken, true);

    private static void ValidateBindings(OdxProgram program, OdxNode entry, Dictionary<int, OdxActivity> activities,
        Dictionary<int, OdxCondition> conditions, Dictionary<int, OdxTransactionScope> transactions, CancellationToken cancellationToken, bool continuation = false)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        if (program.Version != 1) throw new ArgumentException("Execution requires a version-1 ODX program.");
        var nodes = new Dictionary<int, OdxNode>();
        void Collect(OdxNode node, int? parent, int depth)
        {
            if (node is null || depth > 64 || nodes.Count >= 10000 || !nodes.TryAdd(node.Id, node) || node.ParentId != parent)
                throw new ArgumentException("ODX program has invalid/cyclic/duplicate nodes, parent links, or exceeds 10000 elements / 64 levels.");
            foreach (var child in node.Children) Collect(child, node.Id, depth + 1);
        }
        foreach (var root in program.Roots) Collect(root, null, 1);
        if (!nodes.TryGetValue(entry.Id, out var saved) || !ReferenceEquals(saved, entry)) throw new ArgumentException("Entry must belong to this ODX program.");
        if (entry.ShapeType != "ServiceDeclaration") throw new ArgumentException("Execute a complete ServiceDeclaration; executing a subtree could bypass transaction/declaration semantics.");
        void Validate(OdxNode node)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parentType = node.ParentId is { } parentId ? nodes[parentId].ShapeType : null;
            if ((node.ShapeType == "Catch" && parentType is not ("ServiceDeclaration" or "Scope"))
                || (node.ShapeType == "DecisionBranch" && parentType != "Decision")
                || (node.ShapeType == "ServiceBody" && parentType != "ServiceDeclaration")
                || (node.ShapeType == "ServiceDeclaration" && !ReferenceEquals(node, entry)))
                throw new ArgumentException($"ODX structural shape {node.Id} ({node.ShapeType}) has an invalid placement.");
            switch (node.ShapeType)
            {
                case "ServiceDeclaration":
                case "Scope":
                    if (node.ShapeType == "ServiceDeclaration" && node.Children.Count(child => child.ShapeType == "ServiceBody") != 1)
                        throw new ArgumentException($"ODX service {node.Id} requires exactly one ServiceBody.");
                    var markers = node.Children.Where(child => child.ShapeType is "AtomicTransaction" or "LongRunningTransaction").ToArray();
                    if (markers.Length > 1 || markers.Any(marker => marker.Children.Count > 0))
                        throw new ArgumentException($"ODX scope {node.Id} declares multiple transactions or unsupported transaction children.");
                    if (HasTransaction(node) && (!transactions.TryGetValue(node.Id, out var transaction) || transaction is null)) Missing(node, "transaction boundary");
                    foreach (var child in node.Children.Where(child => child.ShapeType is not ("AtomicTransaction" or "LongRunningTransaction"))) Validate(child);
                    break;
                case "Listen" when continuation:
                    if (node.Children.Count == 0 || node.Children.Any(child => child.ShapeType != "ListenBranch"))
                        throw new ArgumentException($"ODX listen {node.Id} requires ordered ListenBranch children.");
                    foreach (var child in node.Children) Validate(child);
                    break;
                case "ListenBranch" when continuation:
                    if (parentType != "Listen" || node.Children.Count == 0 || node.Children[0].ShapeType is not ("Receive" or "Delay"))
                        throw new ArgumentException($"ODX listen branch {node.Id} requires a first receive/delay trigger.");
                    foreach (var child in node.Children) Validate(child);
                    break;
                case "ServiceBody":
                case "Construct":
                case "DecisionBranch":
                case "Catch":
                    foreach (var child in node.Children) Validate(child);
                    if (node.ShapeType == "Catch" && (node.Properties.GetValueOrDefault("IsFaultMessage")?.Equals("True", StringComparison.OrdinalIgnoreCase) == true
                        || string.IsNullOrWhiteSpace(node.Properties.GetValueOrDefault("ExceptionType"))))
                        throw new NotSupportedException($"ODX catch {node.Id} requires an explicit CLR exception type; fault-message catches are not translated.");
                    break;
                case "Decision":
                    if (node.Children.Count == 0 || node.Children.Any(child => child.ShapeType != "DecisionBranch"))
                        throw new ArgumentException($"ODX decision {node.Id} requires ordered DecisionBranch children.");
                    for (var index = 0; index < node.Children.Count; index++)
                    {
                        var branch = node.Children[index];
                        if (HasExpression(branch)) { if (!conditions.TryGetValue(branch.Id, out var condition) || condition is null) Missing(branch, "condition"); }
                        else if (index != node.Children.Count - 1) throw new ArgumentException($"ODX decision {node.Id} has a non-final else branch.");
                        Validate(branch);
                    }
                    break;
                case "While":
                case "Loop":
                    if (!HasExpression(node)) throw new ArgumentException($"ODX loop {node.Id} requires an expression.");
                    if (!conditions.TryGetValue(node.Id, out var loopCondition) || loopCondition is null) Missing(node, "condition");
                    foreach (var child in node.Children) Validate(child);
                    break;
                default:
                    if (!activities.TryGetValue(node.Id, out var activity) || activity is null) Missing(node, "activity (including its child metadata/semantics)");
                    break;
            }
        }
        Validate(entry);
    }

    public async Task ExecuteAsync(OdxProgram program, OdxNode entry, OdxExecutionBindings bindings, OdxExecutionFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(bindings);
        if (!ReferenceEquals(program, frame.Program)) throw new ArgumentException("Execution requires the frame's ODX program.");
        var activities = new Dictionary<int, OdxActivity>(bindings.Activities);
        var conditions = new Dictionary<int, OdxCondition>(bindings.Conditions);
        var transactions = new Dictionary<int, OdxTransactionScope>(bindings.Transactions);
        ValidateBindings(program, entry, activities, conditions, transactions, cancellationToken);
        var steps = 0;
        void Tick()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++steps > 100000) throw new OdxExecutionLimitException("ODX execution exceeded 100000 control-flow operations.");
        }
        bool Suspended() => frame.Context.Disposition == FlowExecutionDisposition.Suspended;
        async Task Sequence(IEnumerable<OdxNode> children, CancellationToken token)
        {
            foreach (var child in children)
            {
                token.ThrowIfCancellationRequested();
                if (Suspended()) return;
                await Execute(child, token).ConfigureAwait(false);
            }
        }
        async Task ScopeBody(OdxNode scope, CancellationToken token)
        {
            var previous = frame.CurrentException;
            try
            {
                var body = scope.Children.Where(child => child.ShapeType is not ("Catch" or "AtomicTransaction" or "LongRunningTransaction"));
                if (scope.ShapeType == "ServiceDeclaration")
                    body = body.Where(child => child.ShapeType != "ServiceBody").Concat(body.Where(child => child.ShapeType == "ServiceBody"));
                await Sequence(body, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OdxExecutionLimitException) && !Suspended())
            {
                var handler = scope.Children.FirstOrDefault(child => child.ShapeType == "Catch" && Matches(exception, child.Properties.GetValueOrDefault("ExceptionType")!));
                if (handler is null) throw;
                frame.CurrentException = exception;
                await Sequence(handler.Children, token).ConfigureAwait(false);
            }
            finally { frame.CurrentException = previous; }
        }
        async Task Execute(OdxNode node, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Tick();
            switch (node.ShapeType)
            {
                case "ServiceDeclaration":
                case "Scope":
                    if (HasTransaction(node)) await transactions[node.Id](node, frame, ct => ScopeBody(node, ct), token).ConfigureAwait(false);
                    else await ScopeBody(node, token).ConfigureAwait(false);
                    break;
                case "ServiceBody":
                case "Construct":
                case "DecisionBranch":
                    await Sequence(node.Children, token).ConfigureAwait(false);
                    break;
                case "Decision":
                    foreach (var branch in node.Children)
                    {
                        if (Suspended()) return;
                        Tick();
                        if (!HasExpression(branch) || await conditions[branch.Id](branch, frame, token).ConfigureAwait(false))
                        {
                            await Sequence(branch.Children, token).ConfigureAwait(false);
                            break;
                        }
                    }
                    break;
                case "While":
                case "Loop":
                    var iteration = 0;
                    while (!Suspended())
                    {
                        Tick();
                        if (!await conditions[node.Id](node, frame, token).ConfigureAwait(false)) break;
                        if (++iteration > 10000) throw new OdxExecutionLimitException($"ODX loop {node.Id} exceeded 10000 iterations.");
                        await Sequence(node.Children, token).ConfigureAwait(false);
                    }
                    break;
                default:
                    await activities[node.Id](node, frame, token).ConfigureAwait(false);
                    break;
            }
        }
        await Execute(entry, cancellationToken).ConfigureAwait(false);
    }

    private static bool HasExpression(OdxNode node) => !string.IsNullOrWhiteSpace(node.Properties.GetValueOrDefault("Expression"));
    private static bool HasTransaction(OdxNode node) => node.Children.Any(child => child.ShapeType is "AtomicTransaction" or "LongRunningTransaction");
    private static bool Matches(Exception exception, string typeName)
    {
        for (var type = exception.GetType(); type is not null; type = type.BaseType)
            if (type.FullName == typeName) return true;
        return false;
    }
    private static void Missing(OdxNode node, string kind)
        => throw new NotSupportedException($"ODX shape {node.Id} ({node.ShapeType}, line {node.SourceLine}) has no verified {kind} binding. This orchestration has not been implemented completely; no activities were executed.");
}
