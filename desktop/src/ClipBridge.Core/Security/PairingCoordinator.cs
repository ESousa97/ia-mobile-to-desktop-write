using System.Security.Cryptography;
using System.Text;
using ClipBridge.Core.Protocol;

namespace ClipBridge.Core.Security;

/// <summary>Convite temporário que é codificado no QR Code do desktop.</summary>
public sealed record PairingInvitation(
    string Host,
    int Port,
    string PublicKey,
    string Fingerprint,
    string Token,
    DateTimeOffset ExpiresAt)
{
    public string ToQrCodePayload() =>
        $"clipbridge://pair?host={Uri.EscapeDataString(Host)}&port={Port}&pubKey={Uri.EscapeDataString(PublicKey)}" +
        $"&fingerprint={Uri.EscapeDataString(Fingerprint)}&token={Uri.EscapeDataString(Token)}" +
        $"&expiresAt={ExpiresAt.ToUnixTimeMilliseconds()}";
}

/// <summary>
/// Mantém o material efêmero de um convite de pareamento e valida seu token
/// de uso único.
/// </summary>
public sealed class PairingCoordinator : IDisposable
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);
    public static readonly byte[] SessionKeyInfo = "clipbridge-v1-session"u8.ToArray();

    private readonly X25519KeyAgreement _keyAgreement = new();
    private readonly byte[] _tokenHash;
    private bool _consumed;
    private bool _disposed;

    public PairingInvitation Invitation { get; }

    public PairingCoordinator(string host, int port, TimeSpan? lifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);

        var effectiveLifetime = lifetime ?? DefaultLifetime;
        if (effectiveLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "A validade do convite deve ser positiva.");
        }

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes);
        _tokenHash = SHA256.HashData(tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);

        Invitation = new PairingInvitation(
            host,
            port,
            Convert.ToBase64String(_keyAgreement.PublicKey.Span),
            _keyAgreement.Fingerprint,
            token,
            DateTimeOffset.UtcNow.Add(effectiveLifetime));
    }

    public PairResponsePayload CreateResponse() =>
        new(Convert.ToBase64String(_keyAgreement.PublicKey.Span), _keyAgreement.Fingerprint);

    public byte[] DeriveSessionKey(PairRequestPayload request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        var peerPublicKey = Convert.FromBase64String(request.PubKey);
        var salt = Convert.FromBase64String(request.Nonce);
        if (salt.Length != 32)
        {
            throw new ArgumentException("O nonce de pareamento deve ter 32 bytes.", nameof(request));
        }

        return _keyAgreement.DeriveSessionKey(peerPublicKey, salt, SessionKeyInfo);
    }

    public bool TryConfirmToken(string token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_consumed || DateTimeOffset.UtcNow >= Invitation.ExpiresAt)
        {
            return false;
        }

        byte[] suppliedToken;
        try
        {
            suppliedToken = Convert.FromBase64String(token);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            var suppliedHash = SHA256.HashData(suppliedToken);
            var valid = CryptographicOperations.FixedTimeEquals(_tokenHash, suppliedHash);
            CryptographicOperations.ZeroMemory(suppliedHash);
            _consumed = valid;
            return valid;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(suppliedToken);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _keyAgreement.Dispose();
        CryptographicOperations.ZeroMemory(_tokenHash);
        _disposed = true;
    }
}