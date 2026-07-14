using ClipBridge.Core.Protocol;
using Xunit;

namespace ClipBridge.Core.Tests;

public class EnvelopeTests
{
    [Fact]
    public void Create_And_Serialize_RoundTripsTextPayload()
    {
        var envelope = Envelope.Create(
            MessageType.ClipboardText,
            new ClipboardTextPayload("conteúdo copiado"));

        var json = MessageSerializer.Serialize(envelope);
        var parsed = MessageSerializer.Deserialize(json);

        Assert.NotNull(parsed);
        Assert.Equal(MessageType.ClipboardText, parsed!.Type);
        Assert.Equal(ProtocolInfo.Version, parsed.Version);

        var payload = parsed.PayloadAs<ClipboardTextPayload>();
        Assert.NotNull(payload);
        Assert.Equal("conteúdo copiado", payload!.Text);
    }

    [Fact]
    public void Envelope_HasStableIdAndTimestamp()
    {
        var envelope = Envelope.Create(MessageType.Ping, new { });

        Assert.False(string.IsNullOrWhiteSpace(envelope.Id));
        Assert.True(envelope.Timestamp > 0);
    }
}
