namespace Mulse.Modules.Mapping;

/// <summary>Argument counts and limits shared by the engine, the converter and the editor.</summary>
public static class XmlMappingFunctionCatalog
{
    public const int MaxDepth = 16;
    public const int MaxNodes = 128;
    public const int MaxConstantLength = 4096;

    public static (int Min, int Max) Arity(XmlMappingFunction function) => function switch
    {
        XmlMappingFunction.DateCurrentDate or XmlMappingFunction.DateCurrentDateTime => (0, 0),
        XmlMappingFunction.StringSize or XmlMappingFunction.StringUpperCase or XmlMappingFunction.MathInt
            or XmlMappingFunction.LogicalExistence => (1, 1),
        XmlMappingFunction.StringConcat => (2, 32),
        XmlMappingFunction.LogicalAnd => (2, 16),
        XmlMappingFunction.StringLeft or XmlMappingFunction.StringRight or XmlMappingFunction.MathDivide
            or XmlMappingFunction.LogicalGt or XmlMappingFunction.LogicalEq or XmlMappingFunction.LogicalNe => (2, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(function))
    };

    /// <summary>Every absolute source path an expression reads.</summary>
    public static IEnumerable<string> SourcePaths(XmlValueExpression expression)
    {
        if (expression.Kind == XmlValueExpressionKind.Source && expression.SourcePath is not null) yield return expression.SourcePath;
        foreach (var argument in expression.Arguments ?? [])
            foreach (var path in SourcePaths(argument)) yield return path;
    }
}
