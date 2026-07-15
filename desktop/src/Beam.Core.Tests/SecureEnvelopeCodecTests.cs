using Beam.Core.Protocol;
using Beam.Core.Security;
using Xunit;

namespace Beam.Core.Tests;

public class SecureEnvelopeCodecTests
{
    [Fact]
    public void EncryptDecrypt_RoundTripsClipboardPayload()
    {
        using var cipher = new SessionCipher(new byte[SessionCipher.KeySizeBytes]);
        var original = Envelope.Create(
            MessageType.ClipboardText,
            new ClipboardTextPayload("conteúdo secreto"));

        var wire = SecureEnvelopeCodec.Encrypt(original, cipher);
        Assert.True(SecureEnvelopeCodec.TryDecrypt(wire, cipher, out var decrypted));

        Assert.Equal(MessageType.ClipboardText, decrypted.Type);
        Assert.Equal(original.Id, decrypted.Id);

        var payload = decrypted.PayloadAs<ClipboardTextPayload>();
        Assert.NotNull(payload);
        Assert.Equal("conteúdo secreto", payload!.Text);
    }

    [Fact]
    public void PairRequest_StaysCleartext()
    {
        using var cipher = new SessionCipher(new byte[SessionCipher.KeySizeBytes]);
        using var mobile = new X25519KeyAgreement();
        var request = Envelope.Create(
            MessageType.PairRequest,
            new PairRequestPayload(
                Convert.ToBase64String(mobile.PublicKey.Span),
                Convert.ToBase64String(new byte[32])));

        var wire = SecureEnvelopeCodec.Encrypt(request, cipher);

        Assert.Equal(request.Payload.GetRawText(), wire.Payload.GetRawText());
        Assert.True(SecureEnvelopeCodec.TryDecrypt(wire, cipher, out var decrypted));
        Assert.NotNull(decrypted.PayloadAs<PairRequestPayload>());
    }

    [Fact]
    public void TryDecrypt_RejectsTamperedCiphertext()
    {
        using var cipher = new SessionCipher(new byte[SessionCipher.KeySizeBytes]);
        var wire = SecureEnvelopeCodec.Encrypt(
            Envelope.Create(MessageType.Ping, new { }),
            cipher);

        var tampered = wire with
        {
            Payload = System.Text.Json.JsonSerializer.SerializeToElement(
                new EncryptedPayload("AAAA"),
                MessageSerializer.Options),
        };

        Assert.False(SecureEnvelopeCodec.TryDecrypt(tampered, cipher, out _));
    }
}
