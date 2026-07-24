using System.Security.Cryptography;
using System.Text;
using Beam.Core.Protocol;

namespace Beam.Core.Security;

/// <summary>Código temporário exibido pelo desktop para autorizar o pareamento.</summary>
public sealed record PairingInvitation(string PairingCode, DateTimeOffset ExpiresAt);

/// <summary>
/// Mantém o material efêmero de um convite de pareamento e valida seu código
/// de uso único.
/// </summary>
public sealed class PairingCoordinator : IDisposable
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);
    public static readonly byte[] SessionKeyInfo = "clipbridge-v1-session"u8.ToArray();
    public const int MaxFailedAttempts = 5;

    private readonly X25519KeyAgreement _keyAgreement = new();
    private readonly byte[] _codeHash;
    private int _failedAttempts;
    private bool _consumed;
    private bool _disposed;

    public PairingInvitation Invitation { get; }

    public PairingCoordinator(TimeSpan? lifetime = null)
    {
        var effectiveLifetime = lifetime ?? DefaultLifetime;
        if (effectiveLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "A validade do convite deve ser positiva.");
        }

        var pairingCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _codeHash = SHA256.HashData(Encoding.ASCII.GetBytes(pairingCode));

        Invitation = new PairingInvitation(
            pairingCode,
            DateTimeOffset.UtcNow.Add(effectiveLifetime));
    }

    public PairResponsePayload CreateResponse() => new(Convert.ToBase64String(_keyAgreement.PublicKey.Span));

    public byte[] DeriveSessionKey(PairRequestPayload request) =>
        Derive(request, SessionKeyInfo);

    /// <summary>
    /// Chave de retomada do vínculo: mesmo segredo ECDH, info HKDF distinta —
    /// logo, independente da chave de sessão. É ela que permite reconectar sem
    /// código enquanto a confiança estiver válida.
    /// </summary>
    public byte[] DeriveResumeKey(PairRequestPayload request) =>
        Derive(request, ResumeHandshake.ResumeKeyInfo);

    private byte[] Derive(PairRequestPayload request, byte[] info)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        var peerPublicKey = Convert.FromBase64String(request.PubKey);
        var salt = Convert.FromBase64String(request.Nonce);
        if (salt.Length != 32)
        {
            throw new ArgumentException("O nonce de pareamento deve ter 32 bytes.", nameof(request));
        }

        return _keyAgreement.DeriveSessionKey(peerPublicKey, salt, info);
    }

    public bool TryConfirmCode(string? code)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_consumed || DateTimeOffset.UtcNow >= Invitation.ExpiresAt ||
            string.IsNullOrEmpty(code) || code.Length != 6 || !code.All(char.IsAsciiDigit))
        {
            return false;
        }

        var suppliedCodeHash = SHA256.HashData(Encoding.ASCII.GetBytes(code));
        var valid = CryptographicOperations.FixedTimeEquals(_codeHash, suppliedCodeHash);
        CryptographicOperations.ZeroMemory(suppliedCodeHash);
        if (valid)
        {
            _consumed = true;
            return true;
        }

        _failedAttempts++;
        if (_failedAttempts >= MaxFailedAttempts)
        {
            _consumed = true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _keyAgreement.Dispose();
        CryptographicOperations.ZeroMemory(_codeHash);
        _disposed = true;
    }
}
