namespace Beam.Core.Security;

/// <summary>
/// Acordo de chaves do handshake de pareamento. A implementação de produção
/// usa X25519 (ECDH) com chaves efêmeras + HKDF-SHA256 para derivar a chave de
/// sessão de 32 bytes consumida por <see cref="SessionCipher"/>.
/// </summary>
/// <remarks>
/// Definido como interface para permitir troca de implementação e testes.
/// A implementação criptográfica completa faz parte do roadmap e deve passar
/// por revisão antes de uso em produção (ver <c>docs/SECURITY-DESIGN.md</c>).
/// </remarks>
public interface IKeyAgreement
{
    /// <summary>Chave pública efêmera deste lado, para enviar ao par.</summary>
    ReadOnlyMemory<byte> PublicKey { get; }

    /// <summary>
    /// Deriva a chave de sessão de 32 bytes a partir da chave pública do par.
    /// </summary>
    byte[] DeriveSessionKey(ReadOnlySpan<byte> peerPublicKey, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info);

    /// <summary>Fingerprint curto e legível da chave pública, para verificação anti-MITM.</summary>
    string Fingerprint { get; }
}
