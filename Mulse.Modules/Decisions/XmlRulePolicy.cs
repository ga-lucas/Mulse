using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace Mulse.Modules.Decisions;

public sealed record XmlRuleField
{
    public string Name { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string Type { get; set; } = "string";
    public string Value { get; set; } = "";
}

public sealed record XmlFieldRule
{
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
    public int Priority { get; set; }
    public bool ConstantCondition { get; set; } = true;
    public List<XmlRuleField> Conditions { get; set; } = [];
    public List<XmlRuleField> Assignments { get; set; } = [];
}

public sealed record XmlRulePolicy
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string RootName { get; set; } = "";
    public string RootNamespace { get; set; } = "";
    public List<XmlFieldRule> Rules { get; set; } = [];
}

public sealed record XmlRuleResult(string OutputXml, IReadOnlyList<string> MatchedRules)
{
    public IReadOnlyList<XmlRuleField> Assignments { get; init; } = [];
}

/// <summary>Single-pass XML field rules, not a forward-chaining implementation of the BizTalk rules engine.</summary>
public static class XmlRuleRuntime
{
    public const int MaxPolicyCharacters = 2_000_000;
    public const int MaxXmlCharacters = 1_000_000;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static XmlRulePolicy ReadPolicy(string json)
    {
        if (json is null || json.Length > MaxPolicyCharacters)
            throw new ArgumentException("XML rule policy exceeds 2000000 characters.");
        XmlRulePolicy policy;
        try { policy = JsonSerializer.Deserialize<XmlRulePolicy>(json, Options) ?? throw new ArgumentException("An XML rule policy is required."); }
        catch (JsonException ex) { throw new ArgumentException("Invalid XML rule policy JSON.", ex); }
        Validate(policy);
        return policy;
    }

    public static void Validate(XmlRulePolicy policy)
    {
        if (string.IsNullOrWhiteSpace(policy.Name) || string.IsNullOrWhiteSpace(policy.Version))
            throw new ArgumentException("An XML rule policy requires an explicit name and version.");
        Name(policy.RootName);
        if (policy.RootNamespace is null || policy.Rules is null || policy.Rules.Count is 0 or > 512)
            throw new ArgumentException("An XML rule policy requires a root namespace and 1-512 rules.");
        var reads = new HashSet<XName>();
        var writes = new HashSet<XName>();
        foreach (var rule in policy.Rules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Name) || rule.Conditions is null || rule.Assignments is null
                || rule.Conditions.Count > 32 || rule.Assignments.Count is 0 or > 64)
                throw new ArgumentException("Each XML rule requires a name, at most 32 equality conditions, and 1-64 assignments.");
            foreach (var field in rule.Conditions.Concat(rule.Assignments))
            {
                if (field is null || field.Namespace is null || field.Value is null)
                    throw new ArgumentException("XML rule fields, namespaces, and values cannot be null.");
                Name(field.Name);
                if (field.Type is not ("string" or "boolean"))
                    throw new ArgumentException("XML rules support only string and boolean fields.");
                if (field.Type == "boolean") Boolean(field.Value);
            }
            if (!rule.Active || !rule.ConstantCondition) continue;
            reads.UnionWith(rule.Conditions.Select(Key));
            writes.UnionWith(rule.Assignments.Select(Key));
        }
        if (reads.Overlaps(writes))
            throw new ArgumentException("XML rules cannot assign fields used by active conditions; forward chaining requires a separate implementation.");
    }

    public static XmlRuleResult Evaluate(XmlRulePolicy policy, string xml, CancellationToken cancellationToken = default)
    {
        Validate(policy);
        if (xml is null || xml.Length > MaxXmlCharacters)
            throw new ArgumentException("XML rule input exceeds 1000000 characters.");
        cancellationToken.ThrowIfCancellationRequested();
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlCharacters
        });
        XDocument document;
        try { document = XDocument.Load(reader, LoadOptions.PreserveWhitespace); }
        catch (XmlException ex) { throw new ArgumentException("Invalid or unsafe XML rule input.", ex); }
        var root = document.Root!;
        if (root.Name != XName.Get(policy.RootName, policy.RootNamespace))
            throw new ArgumentException("XML rule input root does not match the policy root name and namespace.");
        var children = root.Elements().ToArray();
        if (children.Length > 4096) throw new ArgumentException("XML rule facts exceed 4096 direct-child elements.");
        var elements = children.GroupBy(element => element.Name).ToDictionary(group => group.Key, group => group.ToArray());
        var matched = new List<string>();
        var assignments = new Dictionary<XName, (string Value, string Type)>();
        foreach (var rule in policy.Rules.Where(rule => rule.Active).OrderByDescending(rule => rule.Priority))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!rule.ConstantCondition || !rule.Conditions.All(Matches)) continue;
            matched.Add(rule.Name);
            foreach (var field in rule.Assignments)
            {
                var value = field.Type == "boolean" ? XmlConvert.ToString(Boolean(field.Value)) : field.Value;
                if (assignments.TryGetValue(Key(field), out var prior) && (prior.Value != value || prior.Type != field.Type))
                    throw new ArgumentException("Matching XML rules assign conflicting values or types to the same field. Disambiguate the conditions.");
                assignments[Key(field)] = (value, field.Type);
            }
        }
        if (matched.Count == 0)
            throw new ArgumentException("No XML rule matched the supplied facts. Configure a matching rule explicitly.");
        foreach (var (key, assignment) in assignments)
        {
            var targets = elements.GetValueOrDefault(key) ?? [];
            if (targets.Length != 1 || targets[0].HasElements || targets[0].Attributes().Any(attribute => !attribute.IsNamespaceDeclaration))
                throw new ArgumentException("XML rule assignments require exactly one existing simple element without data attributes.");
            targets[0].Value = assignment.Value;
        }
        var output = document.ToString(SaveOptions.DisableFormatting);
        if (output.Length > MaxXmlCharacters) throw new ArgumentException("XML rule output exceeds 1000000 characters.");
        return new(output, matched)
        {
            Assignments = assignments.Select(entry => new XmlRuleField
            {
                Name = entry.Key.LocalName, Namespace = entry.Key.NamespaceName, Type = entry.Value.Type, Value = entry.Value.Value
            }).ToArray()
        };

        bool Matches(XmlRuleField field)
        {
            var values = elements.GetValueOrDefault(Key(field)) ?? [];
            if (values.Length != 1 || values[0].HasElements || values[0].Attributes().Any(attribute => !attribute.IsNamespaceDeclaration))
                throw new ArgumentException("XML rule conditions require exactly one existing simple element without data attributes.");
            return field.Type == "boolean"
                ? Boolean(values[0].Value) == Boolean(field.Value)
                : string.Equals(values[0].Value, field.Value, StringComparison.Ordinal);
        }
    }

    private static XName Key(XmlRuleField field) => XName.Get(field.Name, field.Namespace);
    private static void Name(string name)
    {
        try { XmlConvert.VerifyNCName(name); }
        catch (Exception ex) when (ex is XmlException or ArgumentException)
        { throw new ArgumentException("XML rule names must be nonempty XML local names.", ex); }
    }
    private static bool Boolean(string value)
    {
        try { return XmlConvert.ToBoolean(value.Trim()); }
        catch (FormatException ex) { throw new ArgumentException("XML rule boolean values must be true, false, 1, or 0.", ex); }
    }
}
