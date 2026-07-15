using System.Text.Json;
using System.Text.Json.Serialization;

namespace Beam.Core.Protocol;

/// <summary>Configuração centralizada de (de)serialização JSON do protocolo.</summary>
public static class MessageSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize(Envelope envelope) =>
        JsonSerializer.Serialize(envelope, Options);

    public static Envelope? Deserialize(string json) =>
        JsonSerializer.Deserialize<Envelope>(json, Options);
}
