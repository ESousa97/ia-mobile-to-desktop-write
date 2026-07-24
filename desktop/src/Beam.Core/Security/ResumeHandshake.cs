using System.Security.Cryptography;

namespace Beam.Core.Security;

/// <summary>
/// Derivações do handshake de retomada — a reconexão automática que dispensa o
/// código de seis dígitos enquanto a confiança estiver válida.
/// </summary>
/// <remarks>
/// <para>
/// No pareamento por código, além da chave de sessão, ambos os lados derivam do
/// mesmo segredo ECDH uma <b>chave de retomada</b> de longa duração (info HKDF
/// distinta, portanto independente da chave de sessão). Ela é persistida dos dois
/// lados e expira 72h após a última conexão bem-sucedida.
/// </para>
/// <para>
/// Na reconexão, o celular envia <c>session.resume</c> com uma chave pública
/// X25519 efêmera nova e um nonce; o desktop responde com as suas. A chave de
/// sessão sai de <c>HKDF(ECDH ‖ chaveDeRetomada)</c>: o ECDH efêmero preserva o
/// sigilo futuro (uma retomada gravada hoje não é decifrável com a chave de
/// retomada vazada amanhã) e a chave de retomada autentica os dois lados. As
/// provas HMAC fecham o handshake em ambas as direções.
/// </para>
/// </remarks>
public static class ResumeHandshake
{
    /// <summary>Validade da confiança, renovada a cada conexão segura.</summary>
    public static readonly TimeSpan TrustLifetime = TimeSpan.FromHours(72);

    /// <summary>Tamanho dos nonces trocados no handshake de retomada.</summary>
    public const int NonceSizeBytes = 32;

    /// <summary>Info HKDF da chave de retomada (derivada no pareamento por código).</summary>
    public static readonly byte[] ResumeKeyInfo = "clipbridge-v1-resume"u8.ToArray();

    private static readonly byte[] EcdhInfo = "clipbridge-v1-resume-ecdh"u8.ToArray();
    private static readonly byte[] SessionInfo = "clipbridge-v1-resume-session"u8.ToArray();
    private static readonly byte[] DeviceIdInfo = "clipbridge-v1-device-id"u8.ToArray();
    private static readonly byte[] ServerProofLabel = "clipbridge-v1-resume-server"u8.ToArray();
    private static readonly byte[] ClientProofLabel = "clipbridge-v1-resume-client"u8.ToArray();

    /// <summary>
    /// Identificador público do vínculo, derivado da chave de retomada — os dois
    /// lados chegam ao mesmo valor sem trocá-lo, e ele não revela a chave.
    /// </summary>
    public static string ComputeDeviceId(ReadOnlySpan<byte> resumeKey) =>
        Convert.ToHexString(HMACSHA256.HashData(resumeKey, DeviceIdInfo).AsSpan(0, 8));

    /// <summary>Info HKDF do ECDH efêmero da retomada.</summary>
    public static ReadOnlySpan<byte> EcdhSessionInfo => EcdhInfo;

    /// <summary>Salt comum do handshake: os dois nonces, na ordem cliente‖servidor.</summary>
    public static byte[] BuildSalt(ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce)
    {
        var salt = new byte[clientNonce.Length + serverNonce.Length];
        clientNonce.CopyTo(salt);
        serverNonce.CopyTo(salt.AsSpan(clientNonce.Length));
        return salt;
    }

    /// <summary>
    /// Chave de sessão da retomada: o ECDH efêmero misturado com a chave de
    /// retomada. Sem a chave de retomada não se chega a ela, mesmo com o ECDH.
    /// </summary>
    public static byte[] DeriveSessionKey(
        ReadOnlySpan<byte> ecdhKey,
        ReadOnlySpan<byte> resumeKey,
        ReadOnlySpan<byte> salt)
    {
        var material = new byte[ecdhKey.Length + resumeKey.Length];
        try
        {
            ecdhKey.CopyTo(material);
            resumeKey.CopyTo(material.AsSpan(ecdhKey.Length));
            var sessionKey = new byte[SessionCipher.KeySizeBytes];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, material, sessionKey, salt, SessionInfo);
            return sessionKey;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    /// <summary>Prova de que o desktop conhece a chave de retomada.</summary>
    public static byte[] ServerProof(ReadOnlySpan<byte> resumeKey, ReadOnlySpan<byte> salt) =>
        Proof(resumeKey, ServerProofLabel, salt);

    /// <summary>Prova de que o celular conhece a chave de retomada.</summary>
    public static byte[] ClientProof(ReadOnlySpan<byte> resumeKey, ReadOnlySpan<byte> salt) =>
        Proof(resumeKey, ClientProofLabel, salt);

    private static byte[] Proof(ReadOnlySpan<byte> resumeKey, ReadOnlySpan<byte> label, ReadOnlySpan<byte> salt)
    {
        var message = new byte[label.Length + salt.Length];
        label.CopyTo(message);
        salt.CopyTo(message.AsSpan(label.Length));
        return HMACSHA256.HashData(resumeKey, message);
    }
}
