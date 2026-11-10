using System.Text;

namespace Mulse.Ui.Models;

/// <summary>Editor view of a native mapping value expression. Kind and Function mirror the engine's string-serialized enums.</summary>
public sealed class XmlValueExpressionViewModel
{
    public string Kind { get; set; } = "Source";
    public string? SourcePath { get; set; }
    public string? Constant { get; set; }
    public string? Function { get; set; }
    /// <summary>Custom function called (Kind Custom) or parameter read (Kind Parameter).</summary>
    public string? Name { get; set; }
    public List<XmlValueExpressionViewModel> Arguments { get; set; } = [];
}

/// <summary>What a formula may call: the custom functions of the map (name to argument count) and, inside a custom function body, its parameters.</summary>
public sealed record XmlFormulaContext(IReadOnlyDictionary<string, int> CustomFunctions, IReadOnlySet<string>? Parameters = null)
{
    public static XmlFormulaContext Empty { get; } = new(new Dictionary<string, int>(StringComparer.Ordinal));
}

/// <summary>
/// Text form of a mapping formula: <c>StringConcat(/s:Order/s:Id, '-', StringUpperCase(/s:Order/s:Name))</c>.
/// Sources are absolute named paths, constants are single-quoted (a doubled quote is a quote) or bare numbers.
/// The function list and limits mirror the engine; a test keeps them in step.
/// </summary>
public static class XmlFormula
{
    public const int MaxDepth = 16;
    public const int MaxNodes = 128;
    public const int MaxConstantLength = 4096;

    public static readonly IReadOnlyDictionary<string, (int Min, int Max)> Functions = new Dictionary<string, (int Min, int Max)>(StringComparer.Ordinal)
    {
        ["StringLeft"] = (2, 2), ["StringRight"] = (2, 2), ["StringSize"] = (1, 1), ["StringConcat"] = (2, 32), ["StringUpperCase"] = (1, 1),
        ["MathInt"] = (1, 1), ["MathDivide"] = (2, 2), ["DateCurrentDate"] = (0, 0), ["DateCurrentDateTime"] = (0, 0),
        ["LogicalGt"] = (2, 2), ["LogicalEq"] = (2, 2), ["LogicalNe"] = (2, 2), ["LogicalAnd"] = (2, 16), ["LogicalExistence"] = (1, 1),
        ["NewGuid"] = (0, 0), ["DateNow"] = (1, 1), ["DateReformat"] = (3, 3), ["DateValid"] = (2, 2), ["Conditional"] = (3, 3),
        ["LogicalOr"] = (2, 16), ["LogicalNot"] = (1, 1), ["StringLowerCase"] = (1, 1), ["StringTrim"] = (1, 1),
        ["StringReplace"] = (3, 3), ["StringSubstring"] = (3, 3), ["StringContains"] = (2, 2)
    };

    /// <summary>One-line semantics per function (mirrors the engine catalog; a test keeps them equal). Given to whoever writes a custom function body.</summary>
    public static readonly IReadOnlyDictionary<string, string> Summaries = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["StringLeft"] = "StringLeft(text, count): first count characters; empty unless count is a number above 0.",
        ["StringRight"] = "StringRight(text, count): last count characters; empty unless count is a number above 0.",
        ["StringSize"] = "StringSize(text): number of characters.",
        ["StringConcat"] = "StringConcat(a, b, ...): joins 2 to 32 values.",
        ["StringUpperCase"] = "StringUpperCase(text): upper case, invariant culture.",
        ["MathInt"] = "MathInt(number): rounds down to a whole number; empty when not numeric.",
        ["MathDivide"] = "MathDivide(a, b): a / b with 15 significant digits; empty when not numeric or b is 0.",
        ["DateCurrentDate"] = "DateCurrentDate(): host local date as yyyy-MM-dd.",
        ["DateCurrentDateTime"] = "DateCurrentDateTime(): host local date and time as yyyy-MM-ddTHH:mm:ss.",
        ["LogicalGt"] = "LogicalGt(a, b): true when a > b (numeric when both are numbers, otherwise ordinal text).",
        ["LogicalEq"] = "LogicalEq(a, b): true when equal (numeric when both are numbers, otherwise ordinal, case-sensitive text).",
        ["LogicalNe"] = "LogicalNe(a, b): true when different (same comparison as LogicalEq).",
        ["LogicalAnd"] = "LogicalAnd(a, b, ...): true when all 2 to 16 values are true (text 'true' ignoring case, or a number above 0).",
        ["LogicalExistence"] = "LogicalExistence(source): true when the source node exists.",
        ["NewGuid"] = "NewGuid(): a new random GUID in lower-case 'D' format (different on every call).",
        ["DateNow"] = "DateNow(format): host local date and time in a .NET custom format such as 'yyyyMMddHHmm' (invariant culture); an invalid format fails the map.",
        ["DateReformat"] = "DateReformat(text, outputFormat, inputFormats): parses text with en-US culture using the '|'-separated .NET inputFormats (empty inputFormats = any common date text) and writes it in outputFormat; empty when it does not parse.",
        ["DateValid"] = "DateValid(text, inputFormats): true when text parses with the '|'-separated .NET inputFormats (empty = any common date text), en-US culture.",
        ["Conditional"] = "Conditional(condition, whenTrue, whenFalse): whenTrue when condition is true (text 'true' ignoring case, or a number above 0), otherwise whenFalse. Both branches are always evaluated.",
        ["LogicalOr"] = "LogicalOr(a, b, ...): true when any of 2 to 16 values is true.",
        ["LogicalNot"] = "LogicalNot(a): true when a is not true.",
        ["StringLowerCase"] = "StringLowerCase(text): lower case, invariant culture.",
        ["StringTrim"] = "StringTrim(text): removes leading and trailing white space.",
        ["StringReplace"] = "StringReplace(text, find, replacement): replaces every ordinal, case-sensitive occurrence; unchanged when find is empty.",
        ["StringSubstring"] = "StringSubstring(text, start, length): zero-based start; empty when start is not a valid position or either number is not numeric; length is cut at the end of the text.",
        ["StringContains"] = "StringContains(text, search): true when text contains search (ordinal, case-sensitive)."
    };

    public const int MaxCustomFunctions = 64;
    public const int MaxCustomParameters = 16;
    public const int MaxOriginalSourceLength = 16384;
    public const int MaxDescriptionLength = 4096;
    public const int MaxUsages = 64;
    public const int MaxUsageLength = 1024;

    /// <summary>Custom function and parameter names: letters, digits and underscore, not starting with a digit (mirrors the engine).</summary>
    public static bool IsIdentifier(string? name)
        => !string.IsNullOrEmpty(name) && name.Length <= 64 && (char.IsAsciiLetter(name[0]) || name[0] == '_')
            && name.All(static character => char.IsAsciiLetterOrDigit(character) || character == '_');

    public static XmlValueExpressionViewModel Starter() => new()
    {
        Kind = "Function", Function = "StringConcat",
        Arguments = [new() { Kind = "Constant", Constant = string.Empty }, new() { Kind = "Constant", Constant = string.Empty }]
    };

    public static string Format(XmlValueExpressionViewModel expression) => expression.Kind switch
    {
        "Source" => expression.SourcePath ?? string.Empty,
        "Constant" => "'" + (expression.Constant ?? string.Empty).Replace("'", "''", StringComparison.Ordinal) + "'",
        "Parameter" => "$" + expression.Name,
        "Custom" => (expression.Name ?? string.Empty) + "(" + string.Join(", ", (expression.Arguments ?? []).Select(Format)) + ")",
        _ => (expression.Function ?? string.Empty) + "(" + string.Join(", ", (expression.Arguments ?? []).Select(Format)) + ")"
    };

    public static IEnumerable<string> SourcePaths(XmlValueExpressionViewModel? expression)
    {
        if (expression is null) yield break;
        if (expression.Kind == "Source" && !string.IsNullOrWhiteSpace(expression.SourcePath)) yield return expression.SourcePath;
        foreach (var argument in expression.Arguments ?? [])
            foreach (var path in SourcePaths(argument)) yield return path;
    }

    /// <summary>Structural check for an expression loaded from JSON; null means usable by the editor.</summary>
    public static string? Validate(XmlValueExpressionViewModel? expression, XmlFormulaContext? context = null)
    {
        var nodes = 0;
        return Check(expression, 1, ref nodes, context ?? XmlFormulaContext.Empty);
    }

    private static string? Check(XmlValueExpressionViewModel? expression, int depth, ref int nodes, XmlFormulaContext context)
    {
        if (expression is null) return "An expression node is empty.";
        if (depth > MaxDepth || ++nodes > MaxNodes) return $"The formula exceeds {MaxNodes} nodes or depth {MaxDepth}.";
        switch (expression.Kind)
        {
            case "Source":
                if (context.Parameters is not null) return "A custom function body can only use its parameters, not source paths.";
                return string.IsNullOrWhiteSpace(expression.SourcePath) || expression.SourcePath[0] != '/' ? "A formula source must be an absolute path starting with '/'." : null;
            case "Constant":
                return expression.Constant is null || expression.Constant.Length > MaxConstantLength ? $"A formula constant must be text of at most {MaxConstantLength} characters." : null;
            case "Parameter":
                return context.Parameters is null || expression.Name is null || !context.Parameters.Contains(expression.Name)
                    ? $"'${expression.Name}' is not a parameter of this function (parameters can only be read inside a custom function body)." : null;
            case "Custom":
                if (expression.Name is null || !context.CustomFunctions.TryGetValue(expression.Name, out var parameterCount)) return $"Unknown custom function '{expression.Name}'.";
                if (expression.Arguments is null || expression.Arguments.Count != parameterCount) return $"{expression.Name} takes {parameterCount} argument(s).";
                foreach (var argument in expression.Arguments)
                    if (Check(argument, depth + 1, ref nodes, context) is { } customError) return customError;
                return null;
            case "Function":
                if (expression.Function is null || !Functions.TryGetValue(expression.Function, out var arity)) return $"Unknown formula function '{expression.Function}'.";
                if (expression.Arguments is null || expression.Arguments.Count < arity.Min || expression.Arguments.Count > arity.Max)
                    return $"{expression.Function} takes {(arity.Min == arity.Max ? arity.Min.ToString() : arity.Min + " to " + arity.Max)} argument(s).";
                foreach (var argument in expression.Arguments)
                    if (Check(argument, depth + 1, ref nodes, context) is { } error) return error;
                return null;
            default:
                return $"Unknown formula node kind '{expression.Kind}'.";
        }
    }

    public static bool TryParse(string? text, out XmlValueExpressionViewModel? expression, out string? error, XmlFormulaContext? context = null)
    {
        expression = null;
        context ??= XmlFormulaContext.Empty;
        var parser = new Parser(text ?? string.Empty, context);
        try
        {
            var parsed = parser.ParseRoot();
            if (Validate(parsed, context) is { } invalid) { error = invalid; return false; }
            expression = parsed;
            error = null;
            return true;
        }
        catch (FormatException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private sealed class Parser(string text, XmlFormulaContext context)
    {
        private int _position;
        private int _depth;

        public XmlValueExpressionViewModel ParseRoot()
        {
            var expression = ParseExpression();
            SkipSpace();
            if (_position < text.Length) throw Error("Unexpected text after the formula");
            return expression;
        }

        private FormatException Error(string message) => new($"Formula error at position {_position + 1}: {message}.");

        private void SkipSpace()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position])) _position++;
        }

        private XmlValueExpressionViewModel ParseExpression()
        {
            SkipSpace();
            if (_position >= text.Length) throw Error("Expected a source path, constant or function");
            if (++_depth > MaxDepth) throw Error($"Formulas nest at most {MaxDepth} levels");
            try
            {
                var current = text[_position];
                if (current == '/') return ParseSource();
                if (current == '\'') return ParseConstant();
                if (char.IsAsciiDigit(current) || current == '-') return ParseNumber();
                if (current == '$') return ParseParameter();
                if (char.IsAsciiLetter(current) || current == '_') return ParseFunction();
                throw Error($"Unexpected character '{current}'");
            }
            finally
            {
                _depth--;
            }
        }

        private XmlValueExpressionViewModel ParseSource()
        {
            var start = _position;
            while (_position < text.Length && (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] is '/' or '@' or ':' or '_' or '-' or '.')) _position++;
            var path = text[start.._position];
            if (path.EndsWith('/') || path.Contains("//", StringComparison.Ordinal)) { _position = start; throw Error("A source path needs a name after every '/'"); }
            return new() { Kind = "Source", SourcePath = path };
        }

        private XmlValueExpressionViewModel ParseConstant()
        {
            var builder = new StringBuilder();
            _position++;
            while (true)
            {
                if (_position >= text.Length) throw Error("A text constant is missing its closing quote");
                var current = text[_position++];
                if (current != '\'') { builder.Append(current); continue; }
                if (_position < text.Length && text[_position] == '\'') { builder.Append('\''); _position++; continue; }
                break;
            }
            if (builder.Length > MaxConstantLength) throw Error($"A constant holds at most {MaxConstantLength} characters");
            return new() { Kind = "Constant", Constant = builder.ToString() };
        }

        private XmlValueExpressionViewModel ParseNumber()
        {
            var start = _position;
            if (text[_position] == '-') _position++;
            var digits = _position;
            while (_position < text.Length && char.IsAsciiDigit(text[_position])) _position++;
            if (_position == digits) { _position = start; throw Error("Expected a number after '-'"); }
            if (_position + 1 < text.Length && text[_position] == '.' && char.IsAsciiDigit(text[_position + 1]))
            {
                _position++;
                while (_position < text.Length && char.IsAsciiDigit(text[_position])) _position++;
            }
            return new() { Kind = "Constant", Constant = text[start.._position] };
        }

        private XmlValueExpressionViewModel ParseParameter()
        {
            var start = _position++;
            while (_position < text.Length && (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] == '_')) _position++;
            var name = text[(start + 1).._position];
            if (!IsIdentifier(name)) { _position = start; throw Error("Expected a parameter name after '$'"); }
            return new() { Kind = "Parameter", Name = name };
        }

        private XmlValueExpressionViewModel ParseFunction()
        {
            var start = _position;
            while (_position < text.Length && (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] == '_')) _position++;
            var name = text[start.._position];
            var isNative = Functions.ContainsKey(name);
            if (!isNative && !context.CustomFunctions.ContainsKey(name)) { _position = start; throw Error($"Unknown function '{name}'"); }
            SkipSpace();
            if (_position >= text.Length || text[_position] != '(') throw Error($"Expected '(' after {name}");
            _position++;
            var arguments = new List<XmlValueExpressionViewModel>();
            SkipSpace();
            if (_position < text.Length && text[_position] == ')') _position++;
            else
                while (true)
                {
                    arguments.Add(ParseExpression());
                    SkipSpace();
                    if (_position >= text.Length) throw Error($"{name} is missing its closing ')'");
                    var separator = text[_position++];
                    if (separator == ')') break;
                    if (separator != ',') { _position--; throw Error("Expected ',' or ')'"); }
                }
            return isNative
                ? new() { Kind = "Function", Function = name, Arguments = arguments }
                : new() { Kind = "Custom", Name = name, Arguments = arguments };
        }
    }
}
