using System.Globalization;
using System.Text.RegularExpressions;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Decisions;
using Mulse.Modules.Orchestration;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService
{
    private static readonly Regex QuotedStringLiteral = new("^\"([^\"\\\\]*)\"$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Int32Literal = new("^-?[0-9]{1,10}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Translates a Receive's DNF filter into native receive conditions. Only exact shapes are accepted: a conjunction (every grouping AND, or one predicate),
    /// or a pure disjunction of Equals predicates on one property (every grouping OR). Mixed groupings, other operators and any property without exact declared
    /// identity throw, so the Receive stays a visible Blocked node.
    /// </summary>
    private static List<DecisionConditionDefinition> TranslateReceiveFilter(IReadOnlyList<OdxNode> predicates, string message, NativeSourceContextCatalog catalog)
    {
        if (predicates.Count > 32) throw new NotSupportedException("Receive filter has more than 32 predicates.");
        var parsed = predicates.Select(predicate => (
            Left: predicate.Properties.GetValueOrDefault("LHS", "").Trim(),
            Right: predicate.Properties.GetValueOrDefault("RHS", "").Trim(),
            Operator: predicate.Properties.GetValueOrDefault("Operator", ""),
            Grouping: predicate.Properties.GetValueOrDefault("Grouping", ""))).ToArray();
        if (parsed.Any(item => item.Left.Length == 0 || item.Operator is not ("Equals" or "Exists") || item.Grouping is not ("AND" or "OR")
                || item.Operator == "Equals" && item.Right.Length == 0 || item.Operator == "Exists" && item.Right.Length != 0))
            throw new NotSupportedException("Receive filter predicates support only Equals (with a literal) and Exists over a named property, grouped AND/OR; other operators and operands are not translated.");
        var groupings = parsed.Select(item => item.Grouping).Distinct(StringComparer.Ordinal).ToArray();
        if (parsed.Length > 1 && groupings.Length != 1)
            throw new NotSupportedException("Receive filter mixes AND and OR groupings; the DNF grouping order is not proven, so no precedence is guessed.");

        var facts = parsed.Select(item => catalog.Resolve(message, item.Left)).ToArray();
        if (parsed.Length > 1 && groupings[0] == "OR")
        {
            if (parsed.Any(item => item.Operator != "Equals") || parsed.Select(item => item.Left).Distinct(StringComparer.Ordinal).Count() != 1)
                throw new NotSupportedException("An OR receive filter is translated only as alternatives of Equals on one property; independent disjunctive groups are not representable natively.");
            var values = parsed.Select((item, index) => LiteralValue(item.Right, facts[index].Kind)).ToArray();
            if (values.Any(value => value.Length == 0 || value.Contains(',') || value != value.Trim()))
                throw new NotSupportedException("OR alternatives must be non-empty values without commas or surrounding whitespace (the native 'In' list is comma-separated and trimmed).");
            return [new() { Source = DecisionValueSourceKind.Metadata, Path = facts[0].MetadataKey, Operator = DecisionComparisonOperator.In, Value = string.Join(',', values) }];
        }
        return parsed.Select((item, index) => new DecisionConditionDefinition
        {
            Source = DecisionValueSourceKind.Metadata, Path = facts[index].MetadataKey,
            Operator = item.Operator == "Exists" ? DecisionComparisonOperator.Exists : DecisionComparisonOperator.Equals,
            Value = item.Operator == "Exists" ? null : LiteralValue(item.Right, facts[index].Kind)
        }).ToList();
    }

    private static string LiteralValue(string literal, NativeMessageFactKind kind)
    {
        switch (kind)
        {
            case NativeMessageFactKind.Metadata when QuotedStringLiteral.Match(literal) is { Success: true } text:
                return text.Groups[1].Value;
            case NativeMessageFactKind.MetadataBoolean when literal is "true" or "false":
                return literal;
            case NativeMessageFactKind.MetadataInt32 when Int32Literal.IsMatch(literal) && int.TryParse(literal, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number):
                return number.ToString(CultureInfo.InvariantCulture);
            default:
                throw new NotSupportedException($"Filter literal '{literal}' does not exactly match the declared {kind} property type (strings are double-quoted without escapes; Boolean is true/false; Int32 is decimal).");
        }
    }
}
