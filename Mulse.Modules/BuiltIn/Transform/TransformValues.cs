using System.Globalization;
using System.Text.Json.Nodes;

namespace Mulse.Modules.BuiltIn.Transform;

/// <summary>
/// Shared input-coercion and arity helpers for the built-in <see cref="ITransformModule"/> implementations.
/// Numbers are handled as <see cref="decimal"/> so that typical integration arithmetic (prices, quantities,
/// totals) is exact in base 10 - e.g. <c>0.1 + 0.2</c> yields <c>0.3</c> rather than a binary floating-point
/// approximation.
/// </summary>
internal static class TransformValues
{
    /// <summary>Returns the scalar text of <paramref name="node"/> (see <see cref="JsonPayloadNavigator.ExtractScalarText"/>), or <c>null</c>.</summary>
    public static string? ToText(JsonNode? node) => JsonPayloadNavigator.ExtractScalarText(node);

    /// <summary>
    /// Interprets <paramref name="node"/> as a number: a JSON number, or a JSON string containing an
    /// invariant-culture number. Returns <c>null</c> for anything else (null, booleans, objects, arrays,
    /// non-numeric text, or numbers outside the <see cref="decimal"/> range).
    /// </summary>
    public static decimal? ToNumber(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<decimal>(out var number))
        {
            return number;
        }

        if (value.TryGetValue<string>(out var text)
            && decimal.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    public static JsonNode CreateNumber(decimal value) => JsonValue.Create(value);

    public static JsonNode CreateText(string value) => JsonValue.Create(value);

    public static void RequireExactly(IReadOnlyList<JsonNode?> inputs, int expected, string moduleId)
    {
        if (inputs.Count != expected)
        {
            throw new ArgumentException(
                $"Module '{moduleId}' requires exactly {expected} input{(expected == 1 ? string.Empty : "s")} but received {inputs.Count}.",
                nameof(inputs));
        }
    }

    public static void RequireAtLeast(IReadOnlyList<JsonNode?> inputs, int minimum, string moduleId)
    {
        if (inputs.Count < minimum)
        {
            throw new ArgumentException(
                $"Module '{moduleId}' requires at least {minimum} input{(minimum == 1 ? string.Empty : "s")} but received {inputs.Count}.",
                nameof(inputs));
        }
    }

    /// <summary>Reads a required integer setting, throwing in the same style as <see cref="ModuleSettingReader"/>.</summary>
    public static int GetRequiredInt32(IReadOnlyDictionary<string, string> settings, string settingName, string moduleId)
    {
        var value = ModuleSettingReader.GetRequired(settings, settingName, moduleId);
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new ArgumentException($"Module '{moduleId}' has an invalid integer value for '{settingName}'.", settingName);
    }

    /// <summary>Reads an optional integer setting, returning <c>null</c> when it is absent or blank.</summary>
    public static int? GetOptionalInt32(IReadOnlyDictionary<string, string> settings, string settingName, string moduleId)
    {
        var value = ModuleSettingReader.GetOptional(settings, settingName);
        if (value is null)
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new ArgumentException($"Module '{moduleId}' has an invalid integer value for '{settingName}'.", settingName);
    }
}
