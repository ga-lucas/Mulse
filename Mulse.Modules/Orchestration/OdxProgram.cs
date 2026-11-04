namespace Mulse.Modules.Orchestration;

public sealed record OdxProgram(int Version, IReadOnlyList<OdxNode> Roots);

public sealed record OdxNode(int Id, int? ParentId, int SourceLine, string SourceId, string ShapeType,
    string ParentLink, IReadOnlyDictionary<string, string> Properties, IReadOnlyList<OdxNode> Children)
{
    public string Name => Properties.GetValueOrDefault("Name") ?? "";
}
