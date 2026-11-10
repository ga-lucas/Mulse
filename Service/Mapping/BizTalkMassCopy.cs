namespace Service.Mapping;

/// <summary>
/// Expands a BizTalk Mass Copy functoid (a verbatim copy of a source record's attributes and children into a target record) into ordinary native
/// field and record links. Only records whose declared structure is identical (same qualified names, same kinds, same order) are expanded, so the
/// result is plain, editable mapping rows rather than hidden behavior. Anything the schemas cannot prove equivalent is refused.
/// </summary>
internal static class BizTalkMassCopy
{
    private const int MaximumListed = 5;

    public static IReadOnlyList<(XmlSchemaField From, XmlSchemaField To)> Expand(
        int functoid, XmlSchemaRoot sourceRoot, XmlSchemaField from, IReadOnlyDictionary<string, string> sourceNamespaces,
        XmlSchemaRoot targetRoot, XmlSchemaField to, IReadOnlyDictionary<string, string> targetNamespaces)
    {
        if (from.Scalar || to.Scalar)
            throw Refuse(functoid, $"copies '{from.Path}' to '{to.Path}', but Mass Copy works on records, not single values");
        if (from.UnsupportedReason is not null)
            throw Refuse(functoid, $"copies '{from.Path}', which is unsupported: {from.UnsupportedReason}");
        if (from.SelfRepeating != to.SelfRepeating)
            throw Refuse(functoid, $"copies '{from.Path}' onto '{to.Path}', but only one of them repeats");

        var sourceChildren = Descendants(sourceRoot, from, sourceNamespaces);
        var targetChildren = Descendants(targetRoot, to, targetNamespaces);
        foreach (var (_, field) in sourceChildren)
            if (field.UnsupportedReason is not null || field.HasDefaultOrFixed)
                throw Refuse(functoid, $"would copy '{field.Path}' verbatim, which a native map cannot reproduce: {field.UnsupportedReason ?? "schema default/fixed values are not supported"}");

        var targetsByKey = targetChildren.ToDictionary(entry => entry.Key, entry => entry.Field, StringComparer.Ordinal);
        var unmatched = sourceChildren.Where(entry => !targetsByKey.ContainsKey(entry.Key)).Select(entry => entry.Field.Path).ToArray();
        if (unmatched.Length > 0)
            throw Refuse(functoid, $"would copy {unmatched.Length} source node(s) that target record '{to.Path}' does not declare ({List(unmatched)}); a native map would silently drop them");

        var pairs = new List<(XmlSchemaField From, XmlSchemaField To)> { (from, to) };
        var lastIndex = -1;
        var targetIndexes = targetChildren.Select((entry, index) => (entry.Key, index)).ToDictionary(entry => entry.Key, entry => entry.index, StringComparer.Ordinal);
        foreach (var (key, source) in sourceChildren)
        {
            var target = targetsByKey[key];
            if (source.Scalar != target.Scalar)
                throw Refuse(functoid, $"copies '{source.Path}', which is {(source.Scalar ? "a value" : "a record")}, onto '{target.Path}', which is {(target.Scalar ? "a value" : "a record")}");
            if (source.SelfRepeating != target.SelfRepeating)
                throw Refuse(functoid, $"copies '{source.Path}' onto '{target.Path}', but only one of them repeats");
            if (targetIndexes[key] < lastIndex)
                throw Refuse(functoid, $"copies '{source.Path}' in a different order than target record '{to.Path}' declares; Mass Copy keeps the source order, a native map uses the target order");
            lastIndex = targetIndexes[key];
            pairs.Add((source, target));
        }
        return pairs;
    }

    // Each descendant is keyed by its relative path with namespace prefixes replaced by namespace URIs: Mass Copy keeps qualified names, so only
    // an identical qualified name is the same node (the two schema trees assign their prefixes independently).
    private static List<(string Key, XmlSchemaField Field)> Descendants(XmlSchemaRoot root, XmlSchemaField record, IReadOnlyDictionary<string, string> namespaces)
    {
        var start = record.Path + "/";
        return root.Fields.Where(field => field.Path.StartsWith(start, StringComparison.Ordinal))
            .Select(field => (Key: string.Join("/", field.Path[start.Length..].Split('/').Select(segment => Qualified(segment, namespaces))), Field: field)).ToList();
    }

    private static string Qualified(string segment, IReadOnlyDictionary<string, string> namespaces)
    {
        var attribute = segment.StartsWith('@');
        var name = attribute ? segment[1..] : segment;
        var colon = name.IndexOf(':');
        if (colon > 0 && namespaces.TryGetValue(name[..colon], out var uri)) name = "{" + uri + "}" + name[(colon + 1)..];
        return attribute ? "@" + name : name;
    }

    private static string List(IReadOnlyList<string> paths)
        => string.Join(", ", paths.Take(MaximumListed)) + (paths.Count > MaximumListed ? ", ..." : string.Empty);

    private static ArgumentException Refuse(int functoid, string reason)
        => new($"Mass Copy functoid {functoid} {reason}. No partial native map was produced; map the fields natively instead.");
}
