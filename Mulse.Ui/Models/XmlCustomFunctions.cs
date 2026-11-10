using System.Text;

namespace Mulse.Ui.Models;

/// <summary>Editor rules for custom functions (mirrors the engine) and the copy/paste AI workflow for implementing stubs.</summary>
public static class XmlCustomFunctions
{
    public const string CannotExpress = "CANNOT_EXPRESS";

    public static bool IsStub(XmlCustomFunctionViewModel function) => function.Body is null;

    public static XmlFormulaContext ContextFor(IEnumerable<XmlCustomFunctionViewModel>? functions, IEnumerable<string>? parameters = null)
        => new((functions ?? []).Where(function => function is not null && !string.IsNullOrEmpty(function.Name))
                .GroupBy(function => function.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Parameters?.Count ?? 0, StringComparer.Ordinal),
            parameters is null ? null : new HashSet<string>(parameters, StringComparer.Ordinal));

    public static XmlFormulaContext ContextForBody(IEnumerable<XmlCustomFunctionViewModel> functions, XmlCustomFunctionViewModel function)
        => ContextFor(functions, (function.Parameters ?? []).Select(parameter => parameter.Name));

    /// <summary>Structural check of the whole set, as the engine does; null means the editor can show it.</summary>
    public static string? Validate(IReadOnlyList<XmlCustomFunctionViewModel>? functions)
    {
        if (functions is null || functions.Count > XmlFormula.MaxCustomFunctions) return $"A mapping supports at most {XmlFormula.MaxCustomFunctions} custom functions.";
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var function in functions)
        {
            if (function is null || !XmlFormula.IsIdentifier(function.Name) || XmlFormula.Functions.ContainsKey(function.Name) || !names.Add(function.Name))
                return $"Custom function '{function?.Name}' needs a unique name (letters, digits, underscore) that is not a built-in function name.";
            if (function.Parameters is null || function.Parameters.Count > XmlFormula.MaxCustomParameters
                || function.Parameters.Any(parameter => parameter is null || !XmlFormula.IsIdentifier(parameter.Name) || (parameter.Description?.Length ?? 0) > XmlFormula.MaxDescriptionLength)
                || function.Parameters.Select(parameter => parameter.Name).Distinct(StringComparer.Ordinal).Count() != function.Parameters.Count)
                return $"Custom function '{function.Name}' has invalid or duplicate parameters (at most {XmlFormula.MaxCustomParameters}).";
            if ((function.Description?.Length ?? 0) > XmlFormula.MaxDescriptionLength || (function.OriginalSource?.Length ?? 0) > XmlFormula.MaxOriginalSourceLength
                || (function.OriginalSourceLanguage?.Length ?? 0) > 32 || function.Usages is null || function.Usages.Count > XmlFormula.MaxUsages
                || function.Usages.Any(usage => usage is null || usage.Length > XmlFormula.MaxUsageLength))
                return $"Custom function '{function.Name}' has documentation that exceeds the allowed size.";
        }
        foreach (var function in functions.Where(function => function.Body is not null))
            if (XmlFormula.Validate(function.Body, ContextForBody(functions, function)) is { } error) return $"Custom function '{function.Name}': {error}";
        return null;
    }

    public static bool IsReferenced(XmlMappingViewModel map, string name)
        => map.Fields.Any(field => (field.Expression is not null && Calls(field.Expression, name)) || (field.Condition is not null && Calls(field.Condition, name)))
            || map.RecordConditions.Any(record => record.Condition is not null && Calls(record.Condition, name))
            || map.CustomFunctions.Any(other => other.Name != name && other.Body is not null && Calls(other.Body, name));

    private static bool Calls(XmlValueExpressionViewModel expression, string name)
        => (expression.Kind == "Custom" && expression.Name == name) || (expression.Arguments ?? []).Any(argument => Calls(argument, name));

    public static string Signature(XmlCustomFunctionViewModel function)
        => $"{function.Name}({string.Join(", ", (function.Parameters ?? []).Select(parameter => "$" + parameter.Name))})";

    /// <summary>Text to give any AI assistant (Copilot, ChatGPT, ...) so it can write the body. The assistant never sees map data, only the stub documentation.</summary>
    public static string BuildPrompt(XmlCustomFunctionViewModel function, IReadOnlyList<XmlCustomFunctionViewModel> others)
    {
        var text = new StringBuilder();
        text.AppendLine("You are helping migrate a BizTalk map to Mulse. Write the body of one Mulse custom function as a single formula expression.");
        text.AppendLine();
        text.AppendLine($"Function: {Signature(function)}");
        if (!string.IsNullOrWhiteSpace(function.Description))
        {
            text.AppendLine("Description:");
            text.AppendLine(function.Description.Trim());
        }
        if (function.Parameters is { Count: > 0 })
        {
            text.AppendLine("Parameters (all values are text):");
            foreach (var parameter in function.Parameters)
                text.AppendLine($"- ${parameter.Name}" + (string.IsNullOrWhiteSpace(parameter.Description) ? string.Empty : $": {parameter.Description.Trim()}"));
        }
        if (!string.IsNullOrWhiteSpace(function.OriginalSource))
        {
            text.AppendLine();
            text.AppendLine($"Original {function.OriginalSourceLanguage ?? "source"} (reference only; reproduce its behavior, do not copy the syntax):");
            text.AppendLine("```");
            text.AppendLine(function.OriginalSource.Trim());
            text.AppendLine("```");
        }
        if (function.Usages is { Count: > 0 })
        {
            text.AppendLine();
            text.AppendLine("Where it is used in the map:");
            foreach (var usage in function.Usages.Take(8)) text.AppendLine($"- {usage}");
        }
        text.AppendLine();
        text.AppendLine("Formula rules:");
        text.AppendLine("- Reply with exactly one formula expression and nothing else (no explanation, no code block).");
        text.AppendLine("- Read an argument as $name, for example $value. You cannot read the message or any other data.");
        text.AppendLine("- Text constants are 'single quoted' (write '' for a quote). Bare numbers are constants. Every value is text.");
        text.AppendLine("- Logical results are the text true or false. Conditional(condition, whenTrue, whenFalse) evaluates both branches.");
        text.AppendLine($"- Use at most {XmlFormula.MaxNodes} nodes and {XmlFormula.MaxDepth} levels. Use only these functions:");
        foreach (var summary in XmlFormula.Functions.Keys.Where(XmlFormula.Summaries.ContainsKey).Select(key => XmlFormula.Summaries[key]))
            text.AppendLine($"  {summary}");
        var callable = others.Where(other => other.Body is not null && other.Name != function.Name).ToArray();
        if (callable.Length > 0)
        {
            text.AppendLine("- You may also call these already-implemented custom functions:");
            foreach (var other in callable) text.AppendLine($"  {other.Name}({string.Join(", ", other.Parameters.Select(parameter => parameter.Name))})");
        }
        text.AppendLine("- Example: Conditional(StringContains($a, '-'), StringUpperCase($a), StringConcat($a, '!'))");
        if (function.OriginalSourceLanguage is not null && function.OriginalSourceLanguage.StartsWith("Xslt", StringComparison.OrdinalIgnoreCase))
            text.AppendLine($"- The original is XSLT. Reproduce only the single text value it gives the target field. If it builds a whole record or repeating structure, reply exactly: {CannotExpress}: record structure must be mapped natively");
        text.AppendLine($"- If the behavior cannot be expressed with these functions, reply exactly: {CannotExpress}: <short reason>");
        return text.ToString().TrimEnd();
    }

    /// <summary>Takes an AI reply
    public static string? ExtractFormula(string? reply, out string? error)
    {
        error = null;
        var text = (reply ?? string.Empty).Trim();
        if (text.Length == 0) { error = "Paste the assistant's reply first."; return null; }
        if (text.Contains(CannotExpress, StringComparison.Ordinal))
        {
            error = "The assistant reported that it cannot express this function: " + text[(text.IndexOf(CannotExpress, StringComparison.Ordinal) + CannotExpress.Length)..].TrimStart(':', ' ').Trim();
            return null;
        }
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var fence = lines.FindIndex(line => line.TrimStart().StartsWith("```", StringComparison.Ordinal));
        if (fence >= 0)
        {
            var close = lines.FindIndex(fence + 1, line => line.TrimStart().StartsWith("```", StringComparison.Ordinal));
            lines = lines.Skip(fence + 1).Take((close < 0 ? lines.Count : close) - fence - 1).ToList();
        }
        text = string.Join("\n", lines).Trim();
        foreach (var prefix in new[] { "Body:", "Formula:", "Expression:" })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { text = text[prefix.Length..].Trim(); break; }
        if (text.Length >= 2 && text[0] == '`' && text[^1] == '`') text = text[1..^1].Trim();
        return text;
    }
}
