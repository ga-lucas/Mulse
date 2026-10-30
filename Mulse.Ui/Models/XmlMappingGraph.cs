namespace Mulse.Ui.Models;

public sealed record XmlMappingGraphNode(string Path, string Key, int Depth, XmlSchemaSelectionViewModel? Selection)
{
    public bool CanConnect => Selection is { Field: { Scalar: true, Repeating: false, UnsupportedReason: null } };
}

public static class XmlMappingGraph
{
    public static string Key(string? path, IReadOnlyDictionary<string, string> namespaces)
        => string.Join("/", (path ?? string.Empty).Split('/').Select(segment =>
        {
            var attribute = segment.StartsWith('@');
            var name = attribute ? segment[1..] : segment;
            var colon = name.IndexOf(':');
            var expanded = colon > 0 && namespaces.TryGetValue(name[..colon], out var uri)
                ? "{" + Uri.EscapeDataString(uri) + "}" + name[(colon + 1)..] : "{}" + name;
            return (attribute ? "@" : "") + expanded;
        }));

    public static IReadOnlyList<XmlMappingGraphNode> Nodes(XmlMappingViewModel map, XmlSchemaTreeViewModel? tree, bool source)
    {
        var nodes = new Dictionary<string, XmlMappingGraphNode>(StringComparer.Ordinal);
        void Add(string path, IReadOnlyDictionary<string, string> namespaces, XmlSchemaSelectionViewModel? selection)
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < segments.Length; index++)
            {
                var prefix = "/" + string.Join("/", segments.Take(index + 1));
                var key = Key(prefix, namespaces);
                var candidate = new XmlMappingGraphNode(prefix, key, index, index == segments.Length - 1 ? selection : null);
                if (!nodes.TryGetValue(key, out var existing) || candidate.Selection is not null && existing.Selection is null)
                    nodes[key] = candidate;
            }
        }
        if (tree is not null)
            foreach (var root in tree.Roots)
            {
                Add(root.Path, tree.Namespaces, null);
                foreach (var field in root.Fields) Add(field.Path, tree.Namespaces, new(root, field, tree.Namespaces));
            }
        foreach (var field in map.Fields)
        {
            var path = source ? field.Constant is null ? field.SourcePath : null : "/" + map.TargetRoot + "/" + field.TargetPath;
            if (!string.IsNullOrWhiteSpace(path)) Add(path, map.Namespaces, null);
        }
        var children = nodes.Values.ToLookup(node =>
        {
            var parent = node.Key[..node.Key.LastIndexOf('/')];
            return nodes.ContainsKey(parent) ? parent : string.Empty;
        }, StringComparer.Ordinal);
        var ordered = new List<XmlMappingGraphNode>(nodes.Count);
        var pending = new Stack<XmlMappingGraphNode>(children[string.Empty].Reverse());
        while (pending.TryPop(out var node))
        {
            ordered.Add(node);
            foreach (var child in children[node.Key].Reverse()) pending.Push(child);
        }
        return ordered;
    }
}
