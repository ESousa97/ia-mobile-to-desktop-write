using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace ClipBridge.Core.Security;

/// <summary>
/// Implementa X25519 com HKDF-SHA256 para derivar uma chave de sessão AES-256.
/// </summary>
public sealed class X25519KeyAgreement : IKeyAgreement, IDisposable
{
    public static readonly int PublicKeySizeBytes = X25519PublicKeyParameters.KeySize;

    private readonly X25519PrivateKeyParameters _privateKey;
    private readonly byte[] _publicKey;
    private bool _disposed;

    public X25519KeyAgreement()
    {
        _privateKey = new X25519PrivateKeyParameters(new SecureRandom());
        _publicKey = _privateKey.GeneratePublicKey().GetEncoded();
    }

    public ReadOnlyMemory<byte> PublicKey => _publicKey;

    public string Fingerprint => Convert.ToHexString(SHA256.HashData(_publicKey).AsSpan(0, 6));

    public byte[] DeriveSessionKey(ReadOnlySpan<byte> peerPublicKey, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (peerPublicKey.Length != PublicKeySizeBytes)
        {
            throw new ArgumentException($"A chave pública X25519 deve ter {PublicKeySizeBytes} bytes.", nameof(peerPublicKey));
        }

        var sharedSecret = new byte[X25519PrivateKeyParameters.SecretSize];
        try
        {
            var agreement = new X25519Agreement();
            agreement.Init(_privateKey);
            agreement.CalculateAgreement(new X25519PublicKeyParameters(peerPublicKey.ToArray(), 0), sharedSecret, 0);

            var hkdf = new HkdfBytesGenerator(new Sha256Digest());
            hkdf.Init(new HkdfParameters(sharedSecret, salt.ToArray(), info.ToArray()));
            var sessionKey = new byte[SessionCipher.KeySizeBytes];
            hkdf.GenerateBytes(sessionKey, 0, sessionKey.Length);
            return sessionKey;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_privateKey.GetEncoded());
        CryptographicOperations.ZeroMemory(_publicKey);
        _disposed = true;
    }
}