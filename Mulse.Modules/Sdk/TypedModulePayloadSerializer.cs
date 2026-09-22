using System.Text.Json;

namespace Mulse.Modules.Sdk;

/// <summary>
/// Shared JSON (de)serialization helpers used by every "Typed*Module" SDK base class
/// (<see cref="TypedFetchModule{TOut}"/>, <see cref="TypedParseModule{TOut}"/>,
/// <see cref="TypedAugmentModule{TIn,TOut}"/>, <see cref="TypedRenderModule{TIn}"/>,
/// <see cref="TypedDeliverModule{TIn}"/>) so every strongly-typed module kind fails the same way -
/// naming the offending payload and module id - when a payload doesn't match the CLR shape a module
/// expects, instead of each base class re-implementing its own <see cref="JsonSerializer"/> try/catch.
/// Public so module packages outside <c>Mulse.Modules</c> can build their own typed module base
/// classes (for unusual module kinds or payload conventions) on top of the same conventions.
/// </summary>
public static class TypedModulePayloadSerializer
{
    /// <summary>
    /// Deserializes <paramref name="payload"/>'s text content as <typeparamref name="T"/>. Throws
    /// <see cref="InvalidOperationException"/> naming <paramref name="moduleId"/> and the payload if the
    /// content isn't valid JSON, isn't shaped like <typeparamref name="T"/>, or deserializes to <c>null</c>.
    /// </summary>
    public static T Deserialize<T>(IntegrationPayload payload, string moduleId, JsonSerializerOptions options)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload.GetText(), options)
                ?? throw new InvalidOperationException(
                    $"Module '{moduleId}' deserialized payload '{payload.Name}' as null; a non-null {typeof(T).Name} is required.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Module '{moduleId}' requires payloads shaped like {typeof(T).Name}. Payload '{payload.Name}' failed to deserialize: {exception.Message}",
                exception);
        }
    }

    /// <summary>Serializes <paramref name="value"/> as UTF-8 JSON bytes, ready to wrap in an <see cref="IntegrationPayload"/>.</summary>
    public static byte[] SerializeToUtf8Bytes<T>(T value, JsonSerializerOptions options)
        => JsonSerializer.SerializeToUtf8Bytes(value, options);

    /// <summary>
    /// Serializes <paramref name="value"/> as JSON and wraps it in a new <c>application/json</c>
    /// <see cref="IntegrationPayload"/> named <paramref name="name"/>.
    /// </summary>
    public static IntegrationPayload ToJsonPayload<T>(
        T value,
        string name,
        JsonSerializerOptions options,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var json = SerializeToUtf8Bytes(value, options);
        return new IntegrationPayload(name, BinaryData.FromBytes(json), "application/json", metadata ?? new Dictionary<string, string>());
    }
}
