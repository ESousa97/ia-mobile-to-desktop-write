using System.Text.Json;
using System.Text.Json.Serialization;

namespace Beam.Core.Protocol;

/// <summary>
/// Envelope comum a todas as mensagens do protocolo. O <see cref="Payload"/>
/// carrega o corpo específico do tipo e, na sessão segura, trafega cifrado.
/// </summary>
public sealed record Envelope
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = ProtocolInfo.Version;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("ts")]
    public long Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }

    /// <summary>Cria um envelope tipado serializando o payload informado.</summary>
    public static Envelope Create<T>(string type, T payload)
    {
        var element = JsonSerializer.SerializeToElement(payload, MessageSerializer.Options);
        return new Envelope { Type = type, Payload = element };
    }

    /// <summary>Desserializa o payload para o tipo esperado.</summary>
    public T? PayloadAs<T>() =>
        Payload.ValueKind == JsonValueKind.Undefined
            ? default
            : Payload.Deserialize<T>(MessageSerializer.Options);
}
