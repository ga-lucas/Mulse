using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Mulse.Modules.Decisions;
using Service.Models;

namespace Service.BizTalkImport;

public static class BizTalkRulePolicyAnalyzer
{
    public const int MaxExportCharacters = 32_000_000;
    private static readonly XNamespace Brl = "http://schemas.microsoft.com/businessruleslanguage/2002";
    private static readonly Regex FieldPath = new(
        "^\\*\\[local-name\\(\\)='(?<name>[^']+)' and namespace-uri\\(\\)='(?<ns>[^']*)'\\]$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static IReadOnlyList<BizTalkRulePolicyResponse> Analyze(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = System.IO.Path.GetFullPath(path);
        using var reader = XmlReader.Create(path, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxExportCharacters
        });
        XDocument document;
        try { document = XDocument.Load(reader, LoadOptions.PreserveWhitespace); }
        catch (XmlException ex) { throw new ArgumentException("Invalid or oversized BizTalk rules XML export.", ex); }
        if (document.Root?.Name != Brl + "brl")
            throw new ArgumentException("Expected a BizTalk BRL export with the businessruleslanguage/2002 namespace.");
        Only(document.Root, "ruleset", "vocabulary");
        var rulesets = document.Root.Elements(Brl + "ruleset").ToArray();
        if (rulesets.Length is 0 or > 256)
            throw new ArgumentException("A rules export must contain 1-256 policy versions.");
        var results = new List<BizTalkRulePolicyResponse>();
        var identities = new HashSet<(string, int, int)>();
        foreach (var ruleset in rulesets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Required(ruleset.Attribute("name")?.Value, "Policy name");
            var version = Single(ruleset, "version");
            var major = Number(version.Attribute("major")?.Value);
            var minor = Number(version.Attribute("minor")?.Value);
            if (!identities.Add((name, major, minor)))
                throw new ArgumentException("The rules export contains a duplicate policy name/version.");
            var rules = ruleset.Elements(Brl + "rule").ToArray();
            var facts = ruleset.Element(Brl + "bindings")?.Elements().ToArray() ?? [];
            var issues = new List<string>();
            string? proposal = null;
            try
            {
                var policy = Convert(ruleset, name, major, minor, rules, facts, cancellationToken);
                XmlRuleRuntime.Validate(policy);
                proposal = JsonSerializer.Serialize(policy, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (proposal.Length > XmlRuleRuntime.MaxPolicyCharacters)
                    throw new ArgumentException("Native policy proposal exceeds the policy size limit.");
                issues.Add("Native single-pass proposal only: typed equality conditions and literal XML assignments. Deployed version, BRE agenda, priorities-based conflict resolution, and orchestration wiring are not inferred. Preview and review before using.");
                if (rules.Sum(rule => rule.Element(Brl + "then")!.Elements().Count()) > policy.Rules.Sum(rule => rule.Assignments.Count))
                    issues.Add("Repeated same-typed literal setters within one rule retain the final source action's value. This does not resolve conflicts between separate matching rules.");
                var conflicts = CountPotentialConflicts(policy, cancellationToken);
                if (conflicts > 0)
                    issues.Add($"{conflicts} active rule pair(s) have potentially overlapping conditions and conflicting assignments. Preview/runtime reject conflicting matches; disambiguate client rules explicitly instead of assuming first-match behavior.");
            }
            catch (ArgumentException ex)
            {
                proposal = null;
                issues.Add("Native proposal unavailable: " + ex.Message);
            }
            results.Add(new(path, name, major, minor, rules.Length,
                rules.Count(rule => rule.Attribute("active")?.Value is "true" or "1"),
                facts.Select(fact => fact.Attribute("doctype")?.Value ?? fact.Name.LocalName).Distinct(StringComparer.Ordinal).ToArray(),
                proposal, issues));
        }
        return results.OrderBy(result => result.Name, StringComparer.Ordinal)
            .ThenByDescending(result => result.MajorVersion).ThenByDescending(result => result.MinorVersion).ToArray();
    }

    private static XmlRulePolicy Convert(XElement ruleset, string name, int major, int minor,
        XElement[] rules, XElement[] facts, CancellationToken cancellationToken)
    {
        Only(ruleset, "bindings", "configuration", "rule", "version");
        Single(ruleset, "bindings");
        var configuration = Single(ruleset, "configuration");
        if (configuration.HasElements || configuration.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration) || !string.IsNullOrWhiteSpace(configuration.Value))
            throw new ArgumentException("Custom policy configuration requires implementation; no configuration is silently ignored.");
        if (facts.Length != 1 || facts[0].Name != Brl + "xmldocument")
            throw new ArgumentException("Only one XML document fact per policy is supported; .NET/database/multiple facts require implementation.");
        var fact = facts[0];
        Only(fact, "selector", "schema");
        var reference = Required(fact.Attribute("ref")?.Value, "Fact reference");
        var selector = Single(fact, "selector").Value;
        if (!selector.StartsWith("/", StringComparison.Ordinal)) throw new ArgumentException("Only an absolute root fact selector is supported.");
        var (rootName, rootNamespace) = ParsePath(selector[1..]);
        var policy = new XmlRulePolicy { Name = name, Version = $"{major}.{minor}", RootName = rootName, RootNamespace = rootNamespace };
        foreach (var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Only(rule, "if", "then");
            var active = Required(rule.Attribute("active")?.Value, "Rule active flag");
            if (active is not ("true" or "false" or "1" or "0")) throw new ArgumentException("Unsupported active flag.");
            if (!int.TryParse(rule.Attribute("priority")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority))
                throw new ArgumentException("Rule priority must be an integer.");
            var converted = new XmlFieldRule { Name = Required(rule.Attribute("name")?.Value, "Rule name"), Active = active is "true" or "1", Priority = priority };
            var predicate = Single(rule, "if");
            Only(predicate, "and");
            var conjunction = Single(predicate, "and");
            Only(conjunction, "compare");
            foreach (var comparison in conjunction.Elements())
            {
                Only(comparison, "vocabularylink", "lhs", "rhs");
                if (comparison.Attribute("operator")?.Value != "equal") throw new ArgumentException("Only equality predicates are supported.");
                var lhs = Single(comparison, "lhs");
                var rhs = Single(comparison, "rhs");
                Only(rhs, "constant");
                if (lhs.Element(Brl + "constant") is not null)
                {
                    Only(lhs, "constant");
                    var left = Literal(Single(lhs, "constant"));
                    var right = Literal(Single(rhs, "constant"));
                    if (left.Type != right.Type) throw new ArgumentException("Mixed-type literal comparisons require implementation.");
                    converted.ConstantCondition &= left.Value == right.Value;
                    continue;
                }
                Only(lhs, "function");
                var field = Member(Single(lhs, "function"), reference, false);
                Only(field, "field", "fieldalias");
                converted.Conditions.Add(Value(field, Single(rhs, "constant")));
            }
            var actions = Single(rule, "then");
            Only(actions, "function");
            var functions = actions.Elements().ToArray();
            if (functions.Length is 0 or > 64) throw new ArgumentException("Each source rule requires 1-64 literal assignment actions.");
            foreach (var function in functions)
            {
                var field = Member(function, reference, true);
                Only(field, "field", "fieldalias", "argument");
                var argument = Single(field, "argument");
                Only(argument, "constant");
                var assignment = Value(field, Single(argument, "constant"));
                var previous = converted.Assignments.FindIndex(item => item.Name == assignment.Name && item.Namespace == assignment.Namespace);
                if (previous < 0) converted.Assignments.Add(assignment);
                else
                {
                    if (converted.Assignments[previous].Type != assignment.Type)
                        throw new ArgumentException("Repeated setters for one XML field require the same declared type.");
                    converted.Assignments[previous] = assignment;
                }
            }
            policy.Rules.Add(converted);
        }
        return policy;
    }

    private static XElement Member(XElement function, string reference, bool setter)
    {
        Only(function, "vocabularylink", "xmldocumentmember");
        var member = Single(function, "xmldocumentmember");
        if (member.Attribute("xmldocumentref")?.Value != reference
            || member.Attribute("sideeffects")?.Value != (setter ? "true" : "false"))
            throw new ArgumentException("XML member reference or side effects do not match a supported field read/assignment.");
        return member;
    }
    private static XmlRuleField Value(XElement member, XElement constant)
    {
        var type = member.Attribute("type")?.Value;
        if (type is not ("string" or "boolean")) throw new ArgumentException("Only string and boolean XML field types are supported.");
        Only(constant, type);
        var literal = Literal(constant);
        var field = Single(member, "field");
        Only(field);
        var (name, ns) = ParsePath(field.Value);
        return new() { Name = name, Namespace = ns, Type = type, Value = literal.Value };
    }
    private static (string Type, string Value) Literal(XElement constant)
    {
        Only(constant, "string", "boolean");
        var entries = constant.Elements().ToArray();
        if (entries.Length != 1 || entries[0].Name.Namespace != Brl || entries[0].Name.LocalName is not ("string" or "boolean") || entries[0].HasElements)
            throw new ArgumentException("Only one typed string/boolean literal is supported.");
        var type = entries[0].Name.LocalName;
        Only(entries[0]);
        try { return (type, type == "boolean" ? XmlConvert.ToString(XmlConvert.ToBoolean(entries[0].Value.Trim())) : entries[0].Value); }
        catch (FormatException ex) { throw new ArgumentException("Invalid boolean literal.", ex); }
    }

    internal static int CountPotentialConflicts(XmlRulePolicy policy, CancellationToken cancellationToken)
    {
        var rules = policy.Rules.Where(rule => rule.Active && rule.ConstantCondition).ToArray();
        var count = 0;
        for (var left = 0; left < rules.Length; left++)
        for (var right = left + 1; right < rules.Length; right++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var conditions = rules[left].Conditions.Concat(rules[right].Conditions).GroupBy(field => (field.Name, field.Namespace));
            if (conditions.Any(group => group.GroupBy(field => field.Type).Any(typed => typed.Select(field => field.Value).Distinct().Count() > 1))) continue;
            if (rules[left].Assignments.Concat(rules[right].Assignments).GroupBy(field => (field.Name, field.Namespace))
                .Any(group => group.Select(field => (field.Type, field.Value)).Distinct().Count() > 1)) count++;
        }
        return count;
    }

    private static (string Name, string Namespace) ParsePath(string path)
    {
        if (path.Length > 2048) throw new ArgumentException("XML field selector exceeds 2048 characters.");
        var match = FieldPath.Match(path);
        if (!match.Success) throw new ArgumentException("Only explicit local-name/namespace root selectors and direct-child fields are supported.");
        return (match.Groups["name"].Value, match.Groups["ns"].Value);
    }
    private static XElement Single(XElement parent, string name)
    {
        var children = parent.Elements(Brl + name).ToArray();
        return children.Length == 1 ? children[0] : throw new ArgumentException($"Expected exactly one BRL {name} element.");
    }
    private static void Only(XElement parent, params string[] children)
    {
        string[] attributes = parent.Name.LocalName switch
        {
            "ruleset" => ["name"],
            "rule" => ["name", "priority", "active"],
            "xmldocument" => ["ref", "doctype", "instances", "selectivity", "instance"],
            "xmldocumentmember" => ["xmldocumentref", "type", "sideeffects"],
            "compare" => ["operator"],
            _ => []
        };
        if (parent.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration && attribute.Name != XNamespace.Xml + "space"
                && (attribute.Name.NamespaceName.Length != 0 || !attributes.Contains(attribute.Name.LocalName, StringComparer.Ordinal)))
            || parent.Elements().Any(element => element.Name.Namespace != Brl || !children.Contains(element.Name.LocalName, StringComparer.Ordinal)))
            throw new ArgumentException($"Unsupported expression or attribute under BRL {parent.Name.LocalName}; no partial policy was generated.");
    }
    private static string Required(string? value, string label) => !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException(label + " is required.");
    private static int Number(string? value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        ? number : throw new ArgumentException("Policy version must contain nonnegative major and minor integers.");
}
