using System.Text;

namespace Mulse.Ui.Models;

/// <summary>Editor view of a native mapping value expression. Kind and Function mirror the engine's string-serialized enums.</summary>
public sealed class XmlValueExpressionViewModel
{
    public string Kind { get; set; } = "Source";
    public string? SourcePath { get; set; }
    public string? Constant { get; set; }
    public string? Function { get; set; }
    public List<XmlValueExpressionViewModel> Arguments { get; set; } = [];
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
        ["LogicalGt"] = (2, 2), ["LogicalEq"] = (2, 2), ["LogicalNe"] = (2, 2), ["LogicalAnd"] = (2, 16), ["LogicalExistence"] = (1, 1)
    };

    public static XmlValueExpressionViewModel Starter() => new()
    {
        Kind = "Function", Function = "StringConcat",
        Arguments = [new() { Kind = "Constant", Constant = string.Empty }, new() { Kind = "Constant", Constant = string.Empty }]
    };

    public static string Format(XmlValueExpressionViewModel expression) => expression.Kind switch
    {
        "Source" => expression.SourcePath ?? string.Empty,
        "Constant" => "'" + (expression.Constant ?? string.Empty).Replace("'", "''", StringComparison.Ordinal) + "'",
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
    public static string? Validate(XmlValueExpressionViewModel? expression)
    {
        var nodes = 0;
        return Check(expression, 1, ref nodes);
    }

    private static string? Check(XmlValueExpressionViewModel? expression, int depth, ref int nodes)
    {
        if (expression is null) return "An expression node is empty.";
        if (depth > MaxDepth || ++nodes > MaxNodes) return $"The formula exceeds {MaxNodes} nodes or depth {MaxDepth}.";
        switch (expression.Kind)
        {
            case "Source":
                return string.IsNullOrWhiteSpace(expression.SourcePath) || expression.SourcePath[0] != '/' ? "A formula source must be an absolute path starting with '/'." : null;
            case "Constant":
                return expression.Constant is null || expression.Constant.Length > MaxConstantLength ? $"A formula constant must be text of at most {MaxConstantLength} characters." : null;
            case "Function":
                if (expression.Function is null || !Functions.TryGetValue(expression.Function, out var arity)) return $"Unknown formula function '{expression.Function}'.";
                if (expression.Arguments is null || expression.Arguments.Count < arity.Min || expression.Arguments.Count > arity.Max)
                    return $"{expression.Function} takes {(arity.Min == arity.Max ? arity.Min.ToString() : arity.Min + " to " + arity.Max)} argument(s).";
                foreach (var argument in expression.Arguments)
                    if (Check(argument, depth + 1, ref nodes) is { } error) return error;
                return null;
            default:
                return $"Unknown formula node kind '{expression.Kind}'.";
        }
    }

    public static bool TryParse(string? text, out XmlValueExpressionViewModel? expression, out string? error)
    {
        expression = null;
        var parser = new Parser(text ?? string.Empty);
        try
        {
            var parsed = parser.ParseRoot();
            if (Validate(parsed) is { } invalid) { error = invalid; return false; }
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

    private sealed class Parser(string text)
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
                if (char.IsAsciiLetter(current)) return ParseFunction();
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

        private XmlValueExpressionViewModel ParseFunction()
        {
            var start = _position;
            while (_position < text.Length && char.IsAsciiLetterOrDigit(text[_position])) _position++;
            var name = text[start.._position];
            if (!Functions.ContainsKey(name)) { _position = start; throw Error($"Unknown function '{name}'"); }
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
            return new() { Kind = "Function", Function = name, Arguments = arguments };
        }
    }
}
