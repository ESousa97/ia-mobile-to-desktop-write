using System.Security.Cryptography;

namespace ClipBridge.Core.Security;

/// <summary>
/// Cifra de sessão AEAD (AES-256-GCM). Fornece confidencialidade e integridade
/// para os payloads do protocolo. O formato do pacote é:
/// <c>nonce(12) | tag(16) | ciphertext(n)</c>.
/// </summary>
/// <remarks>
/// A chave (32 bytes) é derivada do handshake X25519 via HKDF — ver
/// <see cref="IKeyAgreement"/> e <c>docs/SECURITY-DESIGN.md</c>. Esta classe
/// usa a implementação de <see cref="AesGcm"/> da BCL (revisada), evitando
/// criptografia "caseira".
/// </remarks>
public sealed class SessionCipher : IDisposable
{
    public const int KeySizeBytes = 32;   // AES-256
    public const int NonceSizeBytes = 12; // 96 bits (recomendado para GCM)
    public const int TagSizeBytes = 16;   // 128 bits

    private readonly AesGcm _aead;

    public SessionCipher(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySizeBytes)
        {
            throw new ArgumentException($"A chave deve ter {KeySizeBytes} bytes.", nameof(key));
        }

        _aead = new AesGcm(key, TagSizeBytes);
    }

    /// <summary>Cifra <paramref name="plaintext"/> e retorna o pacote autoportante.</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        var package = new byte[NonceSizeBytes + TagSizeBytes + plaintext.Length];
        var nonce = package.AsSpan(0, NonceSizeBytes);
        var tag = package.AsSpan(NonceSizeBytes, TagSizeBytes);
        var ciphertext = package.AsSpan(NonceSizeBytes + TagSizeBytes);

        RandomNumberGenerator.Fill(nonce);
        _aead.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return package;
    }

    /// <summary>Decifra um pacote produzido por <see cref="Encrypt"/>.</summary>
    /// <exception cref="CryptographicException">Se a autenticação (tag) falhar.</exception>
    public byte[] Decrypt(ReadOnlySpan<byte> package, ReadOnlySpan<byte> associatedData = default)
    {
        if (package.Length < NonceSizeBytes + TagSizeBytes)
        {
            throw new ArgumentException("Pacote cifrado muito curto.", nameof(package));
        }

        var nonce = package[..NonceSizeBytes];
        var tag = package.Slice(NonceSizeBytes, TagSizeBytes);
        var ciphertext = package[(NonceSizeBytes + TagSizeBytes)..];

        var plaintext = new byte[ciphertext.Length];
        _aead.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        return plaintext;
    }

    public void Dispose() => _aead.Dispose();
}
