using System.Globalization;
using Mulse.CompatibilityPack.Mapping;

namespace Mulse.CompatibilityPack;

// Custom functions: reusable, parameterized formulas. An imported BizTalk script becomes a stub (no body) that fails closed
// until a formula is supplied; implemented functions are inlined so the generated XSLT only contains catalog calls.
public sealed partial class XmlMappingEngine
{
    private const int MaxExpandedNodes = 512;
    private const int MaxExpandedDepth = 64;

    private static Dictionary<string, XmlCustomFunctionDefinition> ReadCustomFunctions(XmlMappingDefinition map, bool allowStubs)
    {
        var custom = new Dictionary<string, XmlCustomFunctionDefinition>(StringComparer.Ordinal);
        if (map.CustomFunctions is null || map.CustomFunctions.Count > XmlMappingFunctionCatalog.MaxCustomFunctions || (map.Version == 1 && map.CustomFunctions.Count > 0))
            throw new ArgumentException($"Custom functions require version 2 and at most {XmlMappingFunctionCatalog.MaxCustomFunctions} entries.");
        foreach (var function in map.CustomFunctions)
        {
            if (function is null || !XmlMappingFunctionCatalog.IsIdentifier(function.Name) || Enum.TryParse<XmlMappingFunction>(function.Name, out _)
                || !custom.TryAdd(function.Name, function))
                throw new ArgumentException($"Custom function '{function?.Name}' needs a unique name (letters, digits, underscore) that is not a built-in function name.");
            if (function.Parameters is null || function.Parameters.Count > XmlMappingFunctionCatalog.MaxCustomParameters
                || function.Parameters.Any(parameter => parameter is null || !XmlMappingFunctionCatalog.IsIdentifier(parameter.Name)
                    || (parameter.Description?.Length ?? 0) > XmlMappingFunctionCatalog.MaxDescriptionLength)
                || function.Parameters.Select(parameter => parameter.Name).Distinct(StringComparer.Ordinal).Count() != function.Parameters.Count)
                throw new ArgumentException($"Custom function '{function.Name}' has invalid or duplicate parameters (at most {XmlMappingFunctionCatalog.MaxCustomParameters}).");
            if ((function.Description?.Length ?? 0) > XmlMappingFunctionCatalog.MaxDescriptionLength
                || (function.OriginalSource?.Length ?? 0) > XmlMappingFunctionCatalog.MaxOriginalSourceLength
                || (function.OriginalSourceLanguage?.Length ?? 0) > 32
                || function.Usages is null || function.Usages.Count > XmlMappingFunctionCatalog.MaxUsages
                || function.Usages.Any(usage => usage is null || usage.Length > XmlMappingFunctionCatalog.MaxUsageLength))
                throw new ArgumentException($"Custom function '{function.Name}' has documentation that exceeds the allowed size.");
        }
        foreach (var function in custom.Values)
        {
            var body = function.Body;
            if (body is null)
                continue;

            var parameters = function.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
            var nodes = 0;
            ValidateExpression(body, function.Name, 1, ref nodes, [], custom, parameters);
            // Expanding with placeholder arguments finds cycles and runaway growth before any map uses the function.
            var placeholders = function.Parameters.ToDictionary(parameter => parameter.Name,
                _ => new XmlValueExpression { Kind = XmlValueExpressionKind.Constant, Constant = string.Empty }, StringComparer.Ordinal);
            var expanded = 0;
            Expand(body, placeholders, custom, [function.Name], ref expanded, 1, allowStubs: true, function.Name);
        }
        // A stub may stand for something no call site shows (for example a target record built by imported XSLT), so any stub blocks a real run.
        if (!allowStubs && custom.Values.FirstOrDefault(function => function.Body is null) is { } pending)
            throw new ArgumentException($"Custom function '{pending.Name}' is an unimplemented stub. Implement it, or delete it once the calls or target record it stands for are mapped natively, before this map can run.");
        return custom;
    }

    private static void ValidateExpression(XmlValueExpression? expression, string target, int depth, ref int nodes, List<string> sourcePaths,
        IReadOnlyDictionary<string, XmlCustomFunctionDefinition> custom, ISet<string>? parameters)
    {
        if (expression is null || depth > XmlMappingFunctionCatalog.MaxDepth || ++nodes > XmlMappingFunctionCatalog.MaxNodes)
            throw new ArgumentException($"Expression for '{target}' is missing a node or exceeds {XmlMappingFunctionCatalog.MaxDepth} levels / {XmlMappingFunctionCatalog.MaxNodes} nodes.");
        if (!Enum.IsDefined(expression.Kind) || expression.Arguments is null)
            throw new ArgumentException($"Expression for '{target}' has an invalid node.");
        switch (expression.Kind)
        {
            case XmlValueExpressionKind.Source:
                if (parameters is not null || string.IsNullOrEmpty(expression.SourcePath) || expression.Constant is not null || expression.Function is not null
                    || expression.Name is not null || expression.Arguments.Count > 0)
                    throw new ArgumentException($"Expression for '{target}' has a source node that is not just a source path (custom function bodies may only use their parameters).");
                sourcePaths.Add(expression.SourcePath);
                break;
            case XmlValueExpressionKind.Constant:
                if (expression.Constant is null || expression.Constant.Length > XmlMappingFunctionCatalog.MaxConstantLength
                    || expression.SourcePath is not null || expression.Function is not null || expression.Name is not null || expression.Arguments.Count > 0)
                    throw new ArgumentException($"Expression for '{target}' has an invalid constant (at most {XmlMappingFunctionCatalog.MaxConstantLength} characters, no other settings).");
                break;
            case XmlValueExpressionKind.Parameter:
                if (parameters is null || expression.Name is null || !parameters.Contains(expression.Name) || expression.SourcePath is not null
                    || expression.Constant is not null || expression.Function is not null || expression.Arguments.Count > 0)
                    throw new ArgumentException($"Expression for '{target}' reads a parameter that is not declared (parameters are only valid inside a custom function body).");
                break;
            case XmlValueExpressionKind.Custom:
                if (expression.Name is null || !custom.TryGetValue(expression.Name, out var called) || expression.SourcePath is not null
                    || expression.Constant is not null || expression.Function is not null)
                    throw new ArgumentException($"Expression for '{target}' calls an unknown custom function '{expression.Name}'.");
                if (expression.Arguments.Count != called.Parameters.Count)
                    throw new ArgumentException($"Custom function {called.Name} in '{target}' takes {called.Parameters.Count} argument(s), not {expression.Arguments.Count}.");
                foreach (var argument in expression.Arguments) ValidateExpression(argument, target, depth + 1, ref nodes, sourcePaths, custom, parameters);
                break;
            default:
                if (expression.Function is null || !Enum.IsDefined(expression.Function.Value) || expression.SourcePath is not null || expression.Constant is not null || expression.Name is not null)
                    throw new ArgumentException($"Expression for '{target}' has a function node without a supported function.");
                var function = expression.Function.Value;
                var (min, max) = XmlMappingFunctionCatalog.Arity(function);
                if (expression.Arguments.Count < min || expression.Arguments.Count > max)
                    throw new ArgumentException($"Function {function} in '{target}' takes {(min == max ? min.ToString(CultureInfo.InvariantCulture) : $"{min} to {max}")} argument(s), not {expression.Arguments.Count}.");
                foreach (var argument in expression.Arguments) ValidateExpression(argument, target, depth + 1, ref nodes, sourcePaths, custom, parameters);
                break;
        }
    }

    private static int Count(XmlValueExpression expression) => 1 + expression.Arguments.Sum(Count);

    // Inlines implemented custom functions (arguments are substituted for parameters). An argument read by several parameter
    // references is evaluated at each read, so a NewGuid argument yields a different value per read.
    private static XmlValueExpression Expand(XmlValueExpression expression, IReadOnlyDictionary<string, XmlValueExpression>? bindings,
        IReadOnlyDictionary<string, XmlCustomFunctionDefinition> custom, HashSet<string> active, ref int nodes, int depth, bool allowStubs, string target)
    {
        if (depth > MaxExpandedDepth || ++nodes > MaxExpandedNodes)
            throw new ArgumentException($"Expression for '{target}' grows beyond {MaxExpandedNodes} nodes or {MaxExpandedDepth} levels after custom functions are expanded.");
        switch (expression.Kind)
        {
            case XmlValueExpressionKind.Source:
            case XmlValueExpressionKind.Constant:
                return expression;
            case XmlValueExpressionKind.Parameter:
                var bound = bindings![expression.Name!];
                nodes += Count(bound) - 1;
                if (nodes > MaxExpandedNodes) throw new ArgumentException($"Expression for '{target}' grows beyond {MaxExpandedNodes} nodes after custom functions are expanded.");
                return bound;
        }
        var arguments = new List<XmlValueExpression>(expression.Arguments.Count);
        foreach (var argument in expression.Arguments)
            arguments.Add(Expand(argument, bindings, custom, active, ref nodes, depth + 1, allowStubs, target));
        if (expression.Kind == XmlValueExpressionKind.Function) return expression with { Arguments = arguments };
        var function = custom[expression.Name!];
        if (function.Body is null)
        {
            if (!allowStubs)
                throw new ArgumentException($"Custom function '{function.Name}' is an unimplemented stub. Implement it (or replace its calls) before this map can run.");
            return expression with { Arguments = arguments };
        }
        if (!active.Add(function.Name))
            throw new ArgumentException($"Custom function '{function.Name}' calls itself through a cycle.");
        try
        {
            var inner = function.Parameters.Zip(arguments).ToDictionary(pair => pair.First.Name, pair => pair.Second, StringComparer.Ordinal);
            return Expand(function.Body, inner, custom, active, ref nodes, depth + 1, allowStubs, target);
        }
        finally { active.Remove(function.Name); }
    }
}
