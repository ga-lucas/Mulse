using System.Globalization;
using System.Text.Json.Serialization;

namespace Mulse.Modules.ControlFlow;

[JsonConverter(typeof(JsonStringEnumConverter<NativeScalarType>))]
public enum NativeScalarType { String, Int32, Boolean, Null }

[JsonConverter(typeof(JsonStringEnumConverter<NativeExpressionKind>))]
public enum NativeExpressionKind { Literal, Variable, Not, And, Or, Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual, Add, Subtract, Multiply, Divide, Remainder, Negate, MessageValue }

public sealed record NativeScalar(NativeScalarType Type, string Value)
{
    public object? Read() => Type switch
    {
        NativeScalarType.String when Value is { Length: <= 4096 } => Value,
        NativeScalarType.Int32 when int.TryParse(Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) => number,
        NativeScalarType.Boolean when Value is "true" or "false" => Value == "true",
        NativeScalarType.Null when Value == "" => null,
        _ => throw new ArgumentException("Native scalar requires an exact String, Int32, Boolean or null value.")
    };
    public static NativeScalar From(object? value) => value switch
    {
        string text => new(NativeScalarType.String, text), int number => new(NativeScalarType.Int32, number.ToString(CultureInfo.InvariantCulture)),
        bool boolean => new(NativeScalarType.Boolean, boolean ? "true" : "false"), null => new(NativeScalarType.Null, ""),
        _ => throw new InvalidOperationException("Native expression produced an unsupported scalar.")
    };
}

public sealed class NativeExpression
{
    public NativeExpressionKind Kind { get; set; }
    public NativeScalar? Literal { get; set; }
    public string? Variable { get; set; }
    public List<NativeExpression> Operands { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public NativeMessageFact? Fact { get; set; }

    public static void Validate(NativeExpression expression)
    {
        var seen = new HashSet<NativeExpression>();
        void Visit(NativeExpression node, int depth)
        {
            if (node is null || depth > 32 || !seen.Add(node) || seen.Count > 256 || !Enum.IsDefined(node.Kind) || node.Operands is null)
                throw new ArgumentException("Native expressions require an acyclic tree of at most 256 values and 32 levels.");
            var arity = node.Kind is NativeExpressionKind.Literal or NativeExpressionKind.Variable or NativeExpressionKind.MessageValue ? 0
                : node.Kind is NativeExpressionKind.Not or NativeExpressionKind.Negate ? 1 : 2;
            if (node.Operands.Count != arity || (node.Kind == NativeExpressionKind.Literal) != (node.Literal is not null)
                || (node.Kind == NativeExpressionKind.Variable) != (node.Variable is not null)
                || (node.Kind == NativeExpressionKind.MessageValue) != (node.Fact is not null))
                throw new ArgumentException("Native expression operands/literal/variable/fact do not match the selected operation.");
            node.Fact?.Validate();
            if (node.Literal is { } scalar)
            {
                if (scalar.Value is null || scalar.Value.Length > 4096) throw new ArgumentException("Native literal exceeds 4096 characters.");
                _ = scalar.Read();
            }
            if (node.Variable is { } variable && !ValidName(variable)) throw new ArgumentException("Native expression requires a bounded variable identifier.");
            foreach (var operand in node.Operands) Visit(operand, depth + 1);
        }
        Visit(expression, 0);
    }

    public static bool ValidName(string name) => name.Length is > 0 and <= 128 && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    public NativeScalar Evaluate(IReadOnlyDictionary<string, NativeScalar> variables) => Evaluate(variables, null);

    public NativeScalar Evaluate(IReadOnlyDictionary<string, NativeScalar> variables, Func<NativeMessageFact, NativeScalar>? facts)
    {
        object? Run(NativeExpression node)
        {
            if (node.Kind == NativeExpressionKind.MessageValue) return (facts ?? throw new InvalidOperationException("Native message facts require an explicit message resolver."))(node.Fact!).Read();
            if (node.Kind == NativeExpressionKind.Literal) return node.Literal!.Read();
            if (node.Kind == NativeExpressionKind.Variable) return variables.TryGetValue(node.Variable!, out var scalar)
                ? scalar.Read() : throw new InvalidOperationException("Native variable is uninitialized: " + node.Variable);
            var left = Run(node.Operands[0]);
            if (node.Kind == NativeExpressionKind.Not) return !Boolean(left);
            if (node.Kind == NativeExpressionKind.Negate) return checked(-Integer(left));
            if (node.Kind == NativeExpressionKind.And) return Boolean(left) && Boolean(Run(node.Operands[1]));
            if (node.Kind == NativeExpressionKind.Or) return Boolean(left) || Boolean(Run(node.Operands[1]));
            var right = Run(node.Operands[1]);
            return node.Kind switch
            {
                NativeExpressionKind.Equal => Equal(left, right), NativeExpressionKind.NotEqual => !Equal(left, right),
                NativeExpressionKind.Less => Integer(left) < Integer(right), NativeExpressionKind.LessOrEqual => Integer(left) <= Integer(right),
                NativeExpressionKind.Greater => Integer(left) > Integer(right), NativeExpressionKind.GreaterOrEqual => Integer(left) >= Integer(right),
                NativeExpressionKind.Add when left is string || right is string => Format(left) + Format(right),
                NativeExpressionKind.Add => checked(Integer(left) + Integer(right)), NativeExpressionKind.Subtract => checked(Integer(left) - Integer(right)),
                NativeExpressionKind.Multiply => checked(Integer(left) * Integer(right)), NativeExpressionKind.Divide => Integer(left) / Integer(right),
                NativeExpressionKind.Remainder => Integer(left) % Integer(right),
                _ => throw new InvalidOperationException("Unsupported native expression operation.")
            };
        }
        return NativeScalar.From(Run(this));
    }
    public static bool Boolean(object? value) => value is bool result ? result : throw new InvalidOperationException("Native predicate requires Boolean operands; no coercion is performed.");
    private static int Integer(object? value) => value is int result ? result : throw new InvalidOperationException("Native arithmetic requires Int32 operands.");
    private static bool Equal(object? left, object? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.GetType() != right.GetType()) throw new InvalidOperationException("Native equality requires matching scalar types.");
        return left.Equals(right);
    }
    private static string Format(object? value) => value switch
    {
        null => "", string text => text, bool boolean => boolean ? "True" : "False", int number => number.ToString(CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException("Native string conversion requires scalar operands.")
    };
}
