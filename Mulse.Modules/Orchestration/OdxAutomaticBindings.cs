namespace Mulse.Modules.Orchestration;

public sealed record OdxTranslationDiagnostic(int NodeId, int SourceLine, string ShapeType, string Status, string Detail);

public sealed class OdxAutomaticBindingResult(OdxProgram program, OdxExecutionBindings bindings, IReadOnlyList<OdxTranslationDiagnostic> diagnostics)
{
    public OdxExecutionBindings Bindings { get; } = bindings;
    public IReadOnlyList<OdxTranslationDiagnostic> Diagnostics { get; } = diagnostics;

    public void EnsureComplete(OdxNode entry, CancellationToken cancellationToken = default)
        => new OdxProgramExecutor().Validate(program, entry, Bindings, cancellationToken);

    public void EnsureComplete()
    {
        static IEnumerable<OdxNode> Services(IEnumerable<OdxNode> nodes)
            => nodes.SelectMany(node => node.ShapeType == "ServiceDeclaration" ? [node] : Services(node.Children));
        var services = Services(program.Roots).ToArray();
        if (services.Length != 1) throw new ArgumentException("Specify the entry ServiceDeclaration when a source model does not contain exactly one service.");
        EnsureComplete(services[0]);
    }
}

/// <summary>Automatically binds supported expressions while retaining explicit, source-addressable blockers.</summary>
public static class OdxAutomaticBindings
{
    public static OdxAutomaticBindingResult Compile(OdxProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (program.Version != 1) throw new ArgumentException("Automatic ODX binding requires a version-1 source model.");
        var bindings = new OdxExecutionBindings();
        var diagnostics = new List<OdxTranslationDiagnostic>();
        var seen = new HashSet<int>();
        void Visit(OdxNode node, int depth)
        {
            if (depth > 64 || seen.Count >= 10000 || !seen.Add(node.Id)) throw new ArgumentException("ODX translation requires unique nodes within 10000 elements / 64 levels.");
            var status = "Structural";
            var detail = "Structural control flow; execution validates placement and complete service bindings.";
            switch (node.ShapeType)
            {
                case "Module": case "ServiceDeclaration": case "ServiceBody": case "Scope": case "Construct": case "Decision": case "Catch":
                    break;
                case "DecisionBranch" when string.IsNullOrWhiteSpace(node.Properties.GetValueOrDefault("Expression")):
                    detail = "Final else branch; executor validates its position.";
                    break;
                case "DecisionBranch": case "While": case "Loop":
                    TryCompile(() => bindings.Conditions.Add(node.Id, OdxExpressionCompiler.CompileCondition(node.Properties.GetValueOrDefault("Expression") ?? "")));
                    break;
                case "VariableAssignment": case "MessageAssignment":
                    TryCompile(() => bindings.Activities.Add(node.Id, OdxExpressionCompiler.CompileStatements(node.Properties.GetValueOrDefault("Expression") ?? "")));
                    break;
                default:
                    status = "RequiresBinding";
                    detail = node.ShapeType is "AtomicTransaction" or "LongRunningTransaction"
                        ? "An explicit transaction boundary implementation is required on the containing scope."
                        : "An explicit activity/schema/transport/component binding is required; this node is not skipped.";
                    break;
            }
            diagnostics.Add(new(node.Id, node.SourceLine, node.ShapeType, status, detail));
            foreach (var child in node.Children) Visit(child, depth + 1);
            void TryCompile(Action compile)
            {
                try { compile(); status = "Translated"; detail = "Bounded typed expression compiled automatically; no source code or CLR assemblies are executed."; }
                catch (NotSupportedException exception) { status = "RequiresBinding"; detail = exception.Message; }
            }
        }
        foreach (var root in program.Roots) Visit(root, 1);
        return new(program, bindings, diagnostics);
    }
}
