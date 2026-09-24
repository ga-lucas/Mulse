namespace Mulse.Modules.Abstractions;

public enum ModuleKind
{
    Fetch,
    Parse,
    OrchestrationAugment,
    Render,
    Deliver,

    /// <summary>
    /// A value function (see <see cref="ITransformModule"/>) invoked from inside another module's execution, such
    /// as a field mapping row. Never placed on a flow graph as its own node.
    /// </summary>
    Transform
}
