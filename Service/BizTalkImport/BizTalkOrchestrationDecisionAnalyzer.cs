using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Mulse.Modules;
using Service.Models;

namespace Service.BizTalkImport;

/// <summary>
/// Best-effort translation of BizTalk orchestration (.odx) Decision/DecisionBranch shapes into either
/// declarative <c>decision-augment</c> rules (for simple field comparisons) or a scaffolded starter C# module
/// (for anything more complex: method calls, correlation/convoy/transaction/loop control flow). This never
/// blocks or fails import: any parsing/classification issue for a given orchestration silently degrades to an
/// empty result, and <see cref="BizTalkImportService"/> falls back to its pre-existing placeholder behavior.
/// </summary>
/// <remarks>
/// .odx files are not valid XML on their own (they interleave designer metadata with generated C# guarded by
/// <c>#if __DESIGNER_DATA</c> / <c>#endif</c> preprocessor directives), but the designer metadata itself -
/// from the embedded <c>&lt;?xml ... ?&gt;</c> declaration through the closing <c>&lt;/om:MetaModel&gt;</c> -
/// is well-formed XML using the <c>http://schemas.microsoft.com/BizTalk/2003/DesignerData</c> namespace. This
/// analyzer extracts and parses just that region.
/// </remarks>
internal static class BizTalkOrchestrationDecisionAnalyzer
{
    private static readonly XNamespace OmNamespace = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
    private const string DesignerDataStartMarker = "<?xml";
    private const string MetaModelEndTag = "</om:MetaModel>";

    /// <summary>
    /// Orchestration designer shape types (beyond Decision/DecisionBranch) that signal control-flow complexity
    /// no declarative rule can express: convoys/racing receives, parallel branches, correlation sets and
    /// declarations, atomic transaction scopes, and loops.
    /// </summary>
    private static readonly IReadOnlyList<string> ComplexControlFlowShapeTypes =
        ["Listen", "Parallel", "CorrelationDeclaration", "CorrelationType", "AtomicTransaction", "Loop"];

    /// <summary>
    /// Matches one simple comparison conjunct, e.g. <c>msgBilling.ConfigurationInfo.TransportType != "MLLP"</c>
    /// or <c>msgBre.MapName != null</c>. Anything not matching this shape (method calls, arithmetic, relational
    /// operators other than equality, assignments) is treated as too complex to auto-translate.
    /// </summary>
    private static readonly Regex ConjunctPattern = new(
        """^(?<path>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+)\s*(?<op>==|!=)\s*(?<literal>null|true|false|"(?:[^"\\]|\\.)*")$""",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches a standalone <c>Regex.IsMatch(path, "pattern")</c> conjunct (optionally with a trailing
    /// <c>RegexOptions.*</c> argument), e.g. <c>Regex.IsMatch(msgBilling.AccountId, "^\d{6}$")</c>. This is the
    /// one method-call shape allowed through the otherwise-conservative "no parens" bail-out, since it maps
    /// cleanly onto the existing <see cref="DecisionComparisonOperator.RegexMatch"/> rule operator.
    /// </summary>
    private static readonly Regex RegexIsMatchPattern = new(
        """^!?Regex\.IsMatch\(\s*(?<path>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+)\s*,\s*"(?<pattern>(?:[^"\\]|\\.)*)"\s*(?:,\s*RegexOptions\.[A-Za-z]+(?:\s*\|\s*RegexOptions\.[A-Za-z]+)*\s*)?\)$""",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions DecisionJsonOptions = CreateDecisionJsonOptions();

    /// <summary>
    /// Analyzes a single .odx orchestration file. Never throws: returns an empty (all-null/empty) result if
    /// the file can't be read or its designer metadata can't be parsed.
    /// </summary>
    public static BizTalkOrchestrationDecisionAnalysisResponse Analyze(string odxFilePath, string orchestrationName, string moduleNamespaceHint)
    {
        var designerDocument = TryExtractDesignerDocument(odxFilePath);
        if (designerDocument is null)
        {
            return new BizTalkOrchestrationDecisionAnalysisResponse(orchestrationName, null, [], new Dictionary<string, int>(), null, null, null);
        }

        var branches = ExtractDecisionBranches(designerDocument);
        var controlFlowCounts = CountControlFlowShapes(designerDocument);

        var simpleBranches = branches.Where(static branch => branch.IsSimple).ToArray();
        var complexBranches = branches.Where(static branch => !branch.IsSimple).ToArray();

        var generatedDecisionJson = BuildDecisionJson(simpleBranches);

        string? scaffoldSource = null;
        string? scaffoldFileName = null;
        string? scaffoldModuleId = null;
        if (complexBranches.Length > 0 || controlFlowCounts.Count > 0)
        {
            var className = CreateClassName(orchestrationName);
            scaffoldModuleId = CreateModuleId(orchestrationName);
            scaffoldFileName = $"{className}.cs";
            scaffoldSource = BuildScaffoldModuleSource(moduleNamespaceHint, className, scaffoldModuleId, orchestrationName, complexBranches, controlFlowCounts);
        }

        return new BizTalkOrchestrationDecisionAnalysisResponse(
            orchestrationName,
            generatedDecisionJson,
            complexBranches
                .Select(static branch => new BizTalkComplexDecisionBranchResponse(branch.DecisionName, branch.BranchName, branch.Expression))
                .ToArray(),
            controlFlowCounts,
            scaffoldSource,
            scaffoldFileName,
            scaffoldModuleId);
    }

    /// <summary>
    /// Merges the <c>decisionJson</c> generated for each orchestration in a project into a single rule array
    /// suitable for one shared <c>decision-augment</c> step, in orchestration/decision/branch order.
    /// </summary>
    public static string? MergeDecisionJson(IReadOnlyList<BizTalkOrchestrationDecisionAnalysisResponse> analyses)
    {
        var rules = analyses
            .Where(static analysis => analysis.GeneratedDecisionJson is not null)
            .SelectMany(analysis => JsonSerializer.Deserialize<DecisionRuleDefinition[]>(analysis.GeneratedDecisionJson!, DecisionJsonOptions) ?? [])
            .ToArray();

        return rules.Length == 0 ? null : JsonSerializer.Serialize(rules, DecisionJsonOptions);
    }

    private static XDocument? TryExtractDesignerDocument(string odxFilePath)
    {
        string content;
        try
        {
            content = File.ReadAllText(odxFilePath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var xmlStart = content.IndexOf(DesignerDataStartMarker, StringComparison.Ordinal);
        if (xmlStart < 0)
        {
            return null;
        }

        var metaModelEndIndex = content.IndexOf(MetaModelEndTag, xmlStart, StringComparison.Ordinal);
        if (metaModelEndIndex < 0)
        {
            return null;
        }

        var sliceLength = metaModelEndIndex + MetaModelEndTag.Length - xmlStart;
        var designerXml = content.Substring(xmlStart, sliceLength);

        try
        {
            return XDocument.Parse(designerXml);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static IReadOnlyList<DecisionBranchInfo> ExtractDecisionBranches(XDocument designerDocument)
    {
        var results = new List<DecisionBranchInfo>();
        var decisions = designerDocument.Descendants(OmNamespace + "Element")
            .Where(static element => (string?)element.Attribute("Type") == "Decision");

        foreach (var decision in decisions)
        {
            var decisionName = GetPropertyValue(decision, "Name") ?? "Decision";
            var branches = decision.Elements(OmNamespace + "Element")
                .Where(static element => (string?)element.Attribute("Type") == "DecisionBranch");

            foreach (var branch in branches)
            {
                var branchName = GetPropertyValue(branch, "Name") ?? "Branch";
                var expression = GetPropertyValue(branch, "Expression");
                if (string.IsNullOrWhiteSpace(expression))
                {
                    // Implicit "Else" branches carry no expression of their own; nothing to translate.
                    continue;
                }

                var (isSimple, conditionGroups) = ClassifyExpression(expression);
                results.Add(new DecisionBranchInfo(decisionName, branchName, expression, isSimple, conditionGroups));
            }
        }

        return results;
    }

    private static string? GetPropertyValue(XElement element, string propertyName)
    {
        return element.Elements(OmNamespace + "Property")
            .FirstOrDefault(property => (string?)property.Attribute("Name") == propertyName)
            ?.Attribute("Value")?.Value;
    }

    private static Dictionary<string, int> CountControlFlowShapes(XDocument designerDocument)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var shapeType in ComplexControlFlowShapeTypes)
        {
            var count = designerDocument.Descendants(OmNamespace + "Element")
                .Count(element => (string?)element.Attribute("Type") == shapeType);
            if (count > 0)
            {
                counts[shapeType] = count;
            }
        }

        return counts;
    }

    /// <summary>
    /// Classifies a BizTalk decision branch expression as either a set of OR-groups of simple AND'd field
    /// comparisons (translatable to declarative rules), or complex (bails out entirely - no partial credit,
    /// since mixing translated and untranslated conjuncts could silently change the branch's real semantics).
    /// </summary>
    private static (bool IsSimple, IReadOnlyList<IReadOnlyList<DecisionConditionDefinition>> ConditionGroups) ClassifyExpression(string rawExpression)
    {
        var withoutComments = string.Join(
            '\n',
            rawExpression.Split('\n').Select(static line =>
            {
                var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
                return commentIndex >= 0 ? line[..commentIndex] : line;
            }));

        var normalized = withoutComments.Replace("\r", " ").Replace("\n", " ").Trim();
        if (normalized.Length == 0)
        {
            return (false, []);
        }

        // Conservative bail-out: relational/arithmetic operators, assignment, static member access (other than
        // the one allowed "Regex.IsMatch(...)" call shape handled per-conjunct below), and increment/decrement
        // all indicate logic beyond a simple field comparison.
        if (Regex.IsMatch(normalized, "[<>]=?")
            || Regex.IsMatch(normalized, @"(?<![=!<>])=(?!=)")
            || (normalized.Contains("System.", StringComparison.Ordinal) && !normalized.Contains("Regex.IsMatch", StringComparison.Ordinal))
            || normalized.Contains("++", StringComparison.Ordinal)
            || normalized.Contains("--", StringComparison.Ordinal))
        {
            return (false, []);
        }

        var orGroups = normalized.Split("||");
        var groups = new List<IReadOnlyList<DecisionConditionDefinition>>();

        foreach (var orGroup in orGroups)
        {
            var conjuncts = orGroup.Split("&&")
                .Select(static conjunct => conjunct.Trim())
                .Where(static conjunct => conjunct.Length > 0)
                .ToArray();
            if (conjuncts.Length == 0)
            {
                return (false, []);
            }

            var conditions = new List<DecisionConditionDefinition>();
            foreach (var conjunct in conjuncts)
            {
                var condition = ClassifyConjunct(conjunct);
                if (condition is null)
                {
                    return (false, []);
                }

                conditions.Add(condition);
            }

            groups.Add(conditions);
        }

        return (true, groups);
    }

    private static DecisionConditionDefinition? ClassifyConjunct(string conjunct)
    {
        var regexMatch = RegexIsMatchPattern.Match(conjunct);
        if (regexMatch.Success)
        {
            // A leading "!" negates the whole call (Regex.IsMatch returning false should pass the branch).
            // There's no "NotRegexMatch" operator today, so bail rather than silently invert the semantics.
            if (conjunct.TrimStart().StartsWith('!'))
            {
                return null;
            }

            var regexPath = regexMatch.Groups["path"].Value;
            var regexDotIndex = regexPath.IndexOf('.');
            if (regexDotIndex < 0)
            {
                return null;
            }

            return new DecisionConditionDefinition
            {
                Source = DecisionValueSourceKind.Payload,
                Path = "$." + regexPath[(regexDotIndex + 1)..],
                Operator = DecisionComparisonOperator.RegexMatch,
                Value = regexMatch.Groups["pattern"].Value,
            };
        }

        var match = ConjunctPattern.Match(conjunct);
        if (!match.Success)
        {
            return null;
        }

        var path = match.Groups["path"].Value;
        var dotIndex = path.IndexOf('.');
        if (dotIndex < 0)
        {
            return null;
        }

        // Drop the leading BizTalk message-variable segment (e.g. "msgBilling") - it has no meaning once the
        // payload is a plain JSON document, so only the remaining member-access chain becomes the JSON path.
        var jsonPath = "$." + path[(dotIndex + 1)..];
        var op = match.Groups["op"].Value;
        var literal = match.Groups["literal"].Value;

        return (literal, op) switch
        {
            ("null", "!=") => new DecisionConditionDefinition { Source = DecisionValueSourceKind.Payload, Path = jsonPath, Operator = DecisionComparisonOperator.Exists },
            ("true", "==") => new DecisionConditionDefinition { Source = DecisionValueSourceKind.Payload, Path = jsonPath, Operator = DecisionComparisonOperator.IsTrue },
            ("false", "==") => new DecisionConditionDefinition { Source = DecisionValueSourceKind.Payload, Path = jsonPath, Operator = DecisionComparisonOperator.IsFalse },
            ("true", "!=") => new DecisionConditionDefinition { Source = DecisionValueSourceKind.Payload, Path = jsonPath, Operator = DecisionComparisonOperator.IsFalse },
            ("false", "!=") => new DecisionConditionDefinition { Source = DecisionValueSourceKind.Payload, Path = jsonPath, Operator = DecisionComparisonOperator.IsTrue },
            ("null", "==") => null, // No "NotExists" operator exists today - bail rather than guess wrong.
            _ when literal.StartsWith('"') && literal.EndsWith('"') => new DecisionConditionDefinition
            {
                Source = DecisionValueSourceKind.Payload,
                Path = jsonPath,
                Operator = op == "==" ? DecisionComparisonOperator.Equals : DecisionComparisonOperator.NotEquals,
                Value = literal[1..^1],
            },
            _ => null,
        };
    }

    private static string? BuildDecisionJson(IReadOnlyList<DecisionBranchInfo> simpleBranches)
    {
        if (simpleBranches.Count == 0)
        {
            return null;
        }

        var rules = new List<DecisionRuleDefinition>();
        var seenRuleSignatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var branch in simpleBranches)
        {
            var decisionKey = CreateSlug(branch.DecisionName);
            var branchKey = CreateSlug(branch.BranchName);

            for (var groupIndex = 0; groupIndex < branch.ConditionGroups.Count; groupIndex++)
            {
                var suffix = branch.ConditionGroups.Count > 1 ? $"-{groupIndex + 1}" : string.Empty;
                var rule = new DecisionRuleDefinition
                {
                    Name = $"{decisionKey}-{branchKey}{suffix}",
                    Conditions = [.. branch.ConditionGroups[groupIndex]],
                    Actions =
                    [
                        new DecisionActionDefinition
                        {
                            Kind = DecisionActionKind.SetMetadata,
                            Target = $"biztalk:{decisionKey}",
                            ValueSource = DecisionValueSourceKind.Literal,
                            Value = branchKey,
                        },
                    ],
                };

                // The same simple condition can legitimately appear in several distinct Decision shapes across
                // one orchestration (e.g. the same field check repeated per delivery-route branch). Emitting an
                // identical rule (same conditions + actions) more than once is harmless at evaluation time but
                // noisy for reviewers, so keep only the first occurrence rather than silently dropping/merging
                // anything that isn't a byte-for-byte duplicate (which would risk altering real semantics).
                if (seenRuleSignatures.Add(BuildRuleSignature(rule)))
                {
                    rules.Add(rule);
                }
            }
        }

        return rules.Count == 0 ? null : JsonSerializer.Serialize(rules, DecisionJsonOptions);
    }

    /// <summary>
    /// Builds a stable signature for a rule's conditions and actions (excluding its generated <c>Name</c>) so
    /// exact duplicates can be detected regardless of naming.
    /// </summary>
    private static string BuildRuleSignature(DecisionRuleDefinition rule)
    {
        return JsonSerializer.Serialize(new { rule.Conditions, rule.Actions }, DecisionJsonOptions);
    }

    private static string BuildScaffoldModuleSource(
        string moduleNamespaceHint,
        string className,
        string moduleId,
        string orchestrationName,
        IReadOnlyList<DecisionBranchInfo> complexBranches,
        IReadOnlyDictionary<string, int> controlFlowCounts)
    {
        var builder = new StringBuilder();
        builder.AppendLine("using Mulse.Modules;");
        builder.AppendLine();
        builder.AppendLine($"namespace {moduleNamespaceHint};");
        builder.AppendLine();
        builder.AppendLine($"// Scaffolded from BizTalk orchestration '{orchestrationName}' by the Mulse BizTalk importer.");
        builder.AppendLine("// This is a starting point only: the TODOs below summarize the BizTalk logic the importer");
        builder.AppendLine("// could not confidently translate into a declarative decision-augment rule. Implement the");
        builder.AppendLine("// real behavior in TransformAsync, then register this module from an IModuleInstaller and");
        builder.AppendLine("// reference its module id from the imported flow's augment chain.");
        builder.AppendLine($"public sealed class {className} : IOrchestrationAugmentModule");
        builder.AppendLine("{");
        builder.AppendLine("    public ModuleDescriptor Descriptor { get; } = new(");
        builder.AppendLine($"        \"{moduleId}\",");
        builder.AppendLine($"        \"{orchestrationName} (migrated)\",");
        builder.AppendLine("        ModuleKind.OrchestrationAugment,");
        builder.AppendLine($"        \"Scaffolded replacement for BizTalk orchestration '{orchestrationName}'. Implement TransformAsync below.\");");
        builder.AppendLine();
        builder.AppendLine("    public Task<IntegrationBatch> AugmentAsync(");
        builder.AppendLine("        FlowExecutionContext context,");
        builder.AppendLine("        IntegrationBatch batch,");
        builder.AppendLine("        ModuleStepDefinition step,");
        builder.AppendLine("        CancellationToken cancellationToken)");
        builder.AppendLine("    {");
        builder.AppendLine("        cancellationToken.ThrowIfCancellationRequested();");
        builder.AppendLine();

        if (controlFlowCounts.Count > 0)
        {
            builder.AppendLine("        // TODO: the source orchestration used the following control-flow constructs, which");
            builder.AppendLine("        // require real implementation here (convoys, correlation, transactions, or loops):");
            foreach (var (shapeType, count) in controlFlowCounts)
            {
                builder.AppendLine($"        //   - {shapeType}: {count} occurrence(s)");
            }

            builder.AppendLine();
        }

        foreach (var branch in complexBranches)
        {
            builder.AppendLine($"        // TODO: BizTalk decision '{branch.DecisionName}' branch '{branch.BranchName}' could not be");
            builder.AppendLine("        // auto-translated. Original expression:");
            foreach (var line in branch.Expression.Replace("\r", string.Empty).Split('\n'))
            {
                builder.AppendLine($"        //   {line}");
            }

            builder.AppendLine();
        }

        builder.AppendLine("        // Placeholder: passes payloads through unchanged until the logic above is implemented.");
        builder.AppendLine("        return Task.FromResult(batch);");
        builder.AppendLine("    }");
        builder.AppendLine("}");

        return builder.ToString();
    }

    private static string CreateClassName(string orchestrationName)
    {
        // Preserve the orchestration name's existing PascalCase word boundaries (e.g. "BillingBatchDoubleBodyProcess")
        // instead of round-tripping through a lowercase slug, which would collapse them into a single unreadable word.
        var cleaned = Regex.Replace(orchestrationName.Trim(), "[^A-Za-z0-9]+", string.Empty);
        if (cleaned.Length == 0)
        {
            return "MigratedOrchestrationModule";
        }

        var name = char.ToUpperInvariant(cleaned[0]) + cleaned[1..];
        return $"{name}Module";
    }

    private static string CreateModuleId(string orchestrationName) => $"migrated-{CreateSlug(orchestrationName)}";

    private static string CreateSlug(string value)
    {
        // Insert separators at camel/Pascal-case word boundaries before lowercasing, so a name like
        // "BillingBatchDoubleBodyProcess" slugs to "billing-batch-double-body-process" instead of one run-on word.
        var spaced = Regex.Replace(value.Trim(), "(?<=[a-z0-9])(?=[A-Z])", "-");
        var lowered = spaced.ToLowerInvariant();
        var slug = Regex.Replace(lowered, "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "orchestration" : slug;
    }

    private static JsonSerializerOptions CreateDecisionJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record DecisionBranchInfo(
        string DecisionName,
        string BranchName,
        string Expression,
        bool IsSimple,
        IReadOnlyList<IReadOnlyList<DecisionConditionDefinition>> ConditionGroups);
}
