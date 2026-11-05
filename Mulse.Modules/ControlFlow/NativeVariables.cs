namespace Mulse.Modules.ControlFlow;

internal static class NativeVariables
{
    public static void Apply(NativeFlowNode node, Dictionary<string, NativeScalar> variables, Dictionary<string, NativeScalarType> types,
        Func<NativeMessageFact, NativeScalar>? facts = null, string? instanceId = null)
    {
        var name = node.Variable!;
        var value = node.Expression!.Evaluate(variables, facts, instanceId);
        _ = value.Read();
        if (node.Kind == NativeFlowNodeKind.DeclareVariable)
        {
            if (types.ContainsKey(name) || types.Count >= 128) throw new InvalidOperationException("Native variable declaration is duplicated or exceeds 128 variables: " + name);
            RequireType(node.VariableType!.Value, value);
            types.Add(name, node.VariableType.Value); variables.Add(name, value);
        }
        else
        {
            if (!types.TryGetValue(name, out var type)) throw new InvalidOperationException("Native assignment targets an undeclared variable: " + name);
            RequireType(type, value); variables[name] = value;
        }
    }

    private static void RequireType(NativeScalarType type, NativeScalar value)
    {
        if (type != value.Type && !(type == NativeScalarType.String && value.Type == NativeScalarType.Null))
            throw new InvalidOperationException("Native assignment does not match the declared scalar type.");
    }
}
