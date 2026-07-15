using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Beam.Core.Security;

namespace Beam.Core.Protocol;

/// <summary>
/// Cifra/decifra o payload de envelopes no estado SECURE. Metadados (v, type, id, ts)
/// permanecem em claro; o campo payload trafega como <c>{ "ct": "..." }</c>.
/// </summary>
public static class SecureEnvelopeCodec
{
    private static readonly HashSet<string> CleartextTypes =
    [
        MessageType.PairRequest,
        MessageType.PairResponse,
        MessageType.PairConfirm,
    ];

    public static bool RequiresEncryption(string type) => !CleartextTypes.Contains(type);

    public static Envelope Encrypt(Envelope plaintext, SessionCipher cipher)
    {
        if (!RequiresEncryption(plaintext.Type) ||
            plaintext.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return plaintext;
        }

        var payloadJson = plaintext.Payload.GetRawText();
        var package = cipher.Encrypt(Encoding.UTF8.GetBytes(payloadJson), AssociatedData(plaintext));
        return plaintext with
        {
            Payload = JsonSerializer.SerializeToElement(
                new EncryptedPayload(Convert.ToBase64String(package)),
                MessageSerializer.Options),
        };
    }

    public static bool TryDecrypt(Envelope wire, SessionCipher cipher, out Envelope plaintext)
    {
        plaintext = wire;

        if (!RequiresEncryption(wire.Type))
        {
            return true;
        }

        var encrypted = wire.PayloadAs<EncryptedPayload>();
        if (encrypted is null || string.IsNullOrEmpty(encrypted.Ct))
        {
            return false;
        }

        try
        {
            var package = Convert.FromBase64String(encrypted.Ct);
            var payloadJson = Encoding.UTF8.GetString(cipher.Decrypt(package, AssociatedData(wire)));
            using var document = JsonDocument.Parse(payloadJson);
            plaintext = wire with { Payload = document.RootElement.Clone() };
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static byte[] AssociatedData(Envelope envelope) =>
        Encoding.UTF8.GetBytes($"{envelope.Type}|{envelope.Id}");
}
