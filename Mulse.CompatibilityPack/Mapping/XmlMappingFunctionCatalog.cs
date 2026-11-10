namespace Mulse.CompatibilityPack.Mapping;

/// <summary>Argument counts and limits shared by the engine, the converter and the editor.</summary>
public static class XmlMappingFunctionCatalog
{
    public const int MaxDepth = 16;
    public const int MaxNodes = 128;
    public const int MaxConstantLength = 4096;
    public const int MaxCustomFunctions = 64;
    public const int MaxCustomParameters = 16;
    public const int MaxOriginalSourceLength = 16384;
    public const int MaxDescriptionLength = 4096;
    public const int MaxUsages = 64;
    public const int MaxUsageLength = 1024;

    public static (int Min, int Max) Arity(XmlMappingFunction function) => function switch
    {
        XmlMappingFunction.DateCurrentDate or XmlMappingFunction.DateCurrentDateTime or XmlMappingFunction.NewGuid => (0, 0),
        XmlMappingFunction.StringSize or XmlMappingFunction.StringUpperCase or XmlMappingFunction.MathInt
            or XmlMappingFunction.LogicalExistence or XmlMappingFunction.LogicalNot or XmlMappingFunction.StringLowerCase
            or XmlMappingFunction.StringTrim or XmlMappingFunction.DateNow or XmlMappingFunction.MathAbs
            or XmlMappingFunction.StringTrimLeft or XmlMappingFunction.StringTrimRight => (1, 1),
        XmlMappingFunction.StringConcat => (2, 32),
        XmlMappingFunction.LogicalAnd or XmlMappingFunction.LogicalOr or XmlMappingFunction.MathAdd
            or XmlMappingFunction.MathSubtract or XmlMappingFunction.MathMultiply => (2, 16),
        XmlMappingFunction.StringLeft or XmlMappingFunction.StringRight or XmlMappingFunction.MathDivide
            or XmlMappingFunction.LogicalGt or XmlMappingFunction.LogicalEq or XmlMappingFunction.LogicalNe
            or XmlMappingFunction.DateValid or XmlMappingFunction.StringContains or XmlMappingFunction.LogicalGte
            or XmlMappingFunction.LogicalLt or XmlMappingFunction.LogicalLte => (2, 2),
        XmlMappingFunction.DateReformat or XmlMappingFunction.Conditional or XmlMappingFunction.StringReplace
            or XmlMappingFunction.StringSubstring => (3, 3),
        _ => throw new ArgumentOutOfRangeException(nameof(function))
    };

    /// <summary>The BizTalk functoid ID a function corresponds to (see <see cref="HelperVerified"/>), or null for a Mulse-native function.</summary>
    public static int? Fid(XmlMappingFunction function) => function switch
    {
        XmlMappingFunction.StringLeft => 102, XmlMappingFunction.StringRight => 104, XmlMappingFunction.StringSize => 105,
        XmlMappingFunction.StringConcat => 107, XmlMappingFunction.StringUpperCase => 110, XmlMappingFunction.MathInt => 112,
        XmlMappingFunction.MathDivide => 121, XmlMappingFunction.DateCurrentDate => 123, XmlMappingFunction.DateCurrentDateTime => 125,
        XmlMappingFunction.LogicalGt => 311, XmlMappingFunction.LogicalEq => 315, XmlMappingFunction.LogicalNe => 316,
        XmlMappingFunction.LogicalAnd => 321, XmlMappingFunction.LogicalExistence => 701,
        XmlMappingFunction.StringLowerCase => 103, XmlMappingFunction.StringTrimLeft => 108, XmlMappingFunction.StringTrimRight => 109,
        XmlMappingFunction.MathAbs => 111, XmlMappingFunction.MathAdd => 118, XmlMappingFunction.MathSubtract => 119,
        XmlMappingFunction.MathMultiply => 120, XmlMappingFunction.LogicalGte => 312, XmlMappingFunction.LogicalLt => 313,
        XmlMappingFunction.LogicalLte => 314, XmlMappingFunction.LogicalOr => 320, XmlMappingFunction.LogicalNot => 705,
        _ => null
    };

    /// <summary>True when the function's behavior was compared with a helper body BizTalk compiled into a real map; false when it was ported from the functoid's documented behavior.</summary>
    public static bool HelperVerified(XmlMappingFunction function)
        => Fid(function) is { } fid && (fid is 102 or 104 or 105 or 107 or 110 or 111 or 112 or 121 or 123 or 125 or 311 or 315 or 316 or 321 or 701);

    /// <summary>One-line semantics shown in the editor and given to whoever writes a replacement formula. Arguments are text.</summary>
    public static string Summary(XmlMappingFunction function) => function switch
    {
        XmlMappingFunction.StringLeft => "StringLeft(text, count): first count characters; empty unless count is a number above 0.",
        XmlMappingFunction.StringRight => "StringRight(text, count): last count characters; empty unless count is a number above 0.",
        XmlMappingFunction.StringSize => "StringSize(text): number of characters.",
        XmlMappingFunction.StringConcat => "StringConcat(a, b, ...): joins 2 to 32 values.",
        XmlMappingFunction.StringUpperCase => "StringUpperCase(text): upper case, invariant culture.",
        XmlMappingFunction.MathInt => "MathInt(number): rounds down to a whole number; empty when not numeric.",
        XmlMappingFunction.MathDivide => "MathDivide(a, b): a / b with 15 significant digits; empty when not numeric or b is 0.",
        XmlMappingFunction.DateCurrentDate => "DateCurrentDate(): host local date as yyyy-MM-dd.",
        XmlMappingFunction.DateCurrentDateTime => "DateCurrentDateTime(): host local date and time as yyyy-MM-ddTHH:mm:ss.",
        XmlMappingFunction.LogicalGt => "LogicalGt(a, b): true when a > b (numeric when both are numbers, otherwise ordinal text).",
        XmlMappingFunction.LogicalEq => "LogicalEq(a, b): true when equal (numeric when both are numbers, otherwise ordinal, case-sensitive text).",
        XmlMappingFunction.LogicalNe => "LogicalNe(a, b): true when different (same comparison as LogicalEq).",
        XmlMappingFunction.LogicalAnd => "LogicalAnd(a, b, ...): true when all 2 to 16 values are true (text 'true' ignoring case, or a number above 0).",
        XmlMappingFunction.LogicalExistence => "LogicalExistence(source): true when the source node exists.",
        XmlMappingFunction.NewGuid => "NewGuid(): a new random GUID in lower-case 'D' format (different on every call).",
        XmlMappingFunction.DateNow => "DateNow(format): host local date and time in a .NET custom format such as 'yyyyMMddHHmm' (invariant culture); an invalid format fails the map.",
        XmlMappingFunction.DateReformat => "DateReformat(text, outputFormat, inputFormats): parses text with en-US culture using the '|'-separated .NET inputFormats (empty inputFormats = any common date text) and writes it in outputFormat; empty when it does not parse.",
        XmlMappingFunction.DateValid => "DateValid(text, inputFormats): true when text parses with the '|'-separated .NET inputFormats (empty = any common date text), en-US culture.",
        XmlMappingFunction.Conditional => "Conditional(condition, whenTrue, whenFalse): whenTrue when condition is true (text 'true' ignoring case, or a number above 0), otherwise whenFalse. Both branches are always evaluated.",
        XmlMappingFunction.LogicalOr => "LogicalOr(a, b, ...): true when any of 2 to 16 values is true.",
        XmlMappingFunction.LogicalNot => "LogicalNot(a): true when a is not true.",
        XmlMappingFunction.StringLowerCase => "StringLowerCase(text): lower case, invariant culture.",
        XmlMappingFunction.StringTrim => "StringTrim(text): removes leading and trailing white space.",
        XmlMappingFunction.StringReplace => "StringReplace(text, find, replacement): replaces every ordinal, case-sensitive occurrence; unchanged when find is empty.",
        XmlMappingFunction.StringSubstring => "StringSubstring(text, start, length): zero-based start; empty when start is not a valid position or either number is not numeric; length is cut at the end of the text.",
        XmlMappingFunction.StringContains => "StringContains(text, search): true when text contains search (ordinal, case-sensitive).",
        XmlMappingFunction.MathAbs => "MathAbs(number): absolute value; empty when not numeric.",
        XmlMappingFunction.MathAdd => "MathAdd(a, b, ...): sum of 2 to 16 numbers with 15 significant digits; empty when any is not numeric.",
        XmlMappingFunction.MathSubtract => "MathSubtract(a, b, ...): a minus every following number (2 to 16 values); empty when any is not numeric.",
        XmlMappingFunction.MathMultiply => "MathMultiply(a, b, ...): product of 2 to 16 numbers with 15 significant digits; empty when any is not numeric.",
        XmlMappingFunction.LogicalGte => "LogicalGte(a, b): true when a >= b (numeric when both are numbers, otherwise ordinal text).",
        XmlMappingFunction.LogicalLt => "LogicalLt(a, b): true when a < b (numeric when both are numbers, otherwise ordinal text).",
        XmlMappingFunction.LogicalLte => "LogicalLte(a, b): true when a <= b (numeric when both are numbers, otherwise ordinal text).",
        XmlMappingFunction.StringTrimLeft => "StringTrimLeft(text): removes leading white space.",
        XmlMappingFunction.StringTrimRight => "StringTrimRight(text): removes trailing white space.",
        _ => throw new ArgumentOutOfRangeException(nameof(function))
    };

    /// <summary>Custom function and parameter names: letters, digits and underscore, not starting with a digit.</summary>
    public static bool IsIdentifier(string? name)
        => !string.IsNullOrEmpty(name) && name.Length <= 64 && (char.IsAsciiLetter(name[0]) || name[0] == '_')
            && name.All(static character => char.IsAsciiLetterOrDigit(character) || character == '_');

    /// <summary>Every absolute source path an expression reads.</summary>
    public static IEnumerable<string> SourcePaths(XmlValueExpression expression)
    {
        if (expression.Kind == XmlValueExpressionKind.Source && expression.SourcePath is not null) yield return expression.SourcePath;
        foreach (var argument in expression.Arguments ?? [])
            foreach (var path in SourcePaths(argument)) yield return path;
    }
}
