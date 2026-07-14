using System.Security.Cryptography;
using ClipBridge.Core.Protocol;
using ClipBridge.Core.Security;
using Xunit;

namespace ClipBridge.Core.Tests;

public class PairingCoordinatorTests
{
    [Fact]
    public void Invitation_ContainsEphemeralPublicKeyAndExpiry()
    {
        using var pairing = new PairingCoordinator("192.168.1.20", 8787);

        Assert.Equal("192.168.1.20", pairing.Invitation.Host);
        Assert.Equal(8787, pairing.Invitation.Port);
        Assert.Equal(X25519KeyAgreement.PublicKeySizeBytes, Convert.FromBase64String(pairing.Invitation.PublicKey).Length);
        Assert.True(pairing.Invitation.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Invitation_QrCodePayloadContainsAllPairingParameters()
    {
        using var pairing = new PairingCoordinator("desktop.local", 8787);

        var payload = pairing.Invitation.ToQrCodePayload();

        Assert.StartsWith("clipbridge://pair?", payload);
        Assert.Contains("host=desktop.local", payload);
        Assert.Contains("port=8787", payload);
        Assert.Contains("pubKey=", payload);
        Assert.Contains("fingerprint=", payload);
        Assert.Contains("token=", payload);
        Assert.Contains("expiresAt=", payload);
    }

    [Fact]
    public void TryConfirmToken_AcceptsValidTokenOnlyOnce()
    {
        using var pairing = new PairingCoordinator("desktop", 8787);

        Assert.False(pairing.TryConfirmToken(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        Assert.True(pairing.TryConfirmToken(pairing.Invitation.Token));
        Assert.False(pairing.TryConfirmToken(pairing.Invitation.Token));
    }

    [Fact]
    public void DeriveSessionKey_UsesPairRequestNonceAsHkdfSalt()
    {
        using var pairing = new PairingCoordinator("desktop", 8787);
        using var mobile = new X25519KeyAgreement();
        var nonce = RandomNumberGenerator.GetBytes(32);
        var request = new PairRequestPayload(Convert.ToBase64String(mobile.PublicKey.Span), Convert.ToBase64String(nonce));

        var desktopKey = pairing.DeriveSessionKey(request);
        var mobileKey = mobile.DeriveSessionKey(
            Convert.FromBase64String(pairing.Invitation.PublicKey),
            nonce,
            PairingCoordinator.SessionKeyInfo);

        Assert.Equal(desktopKey, mobileKey);
    }

    [Fact]
    public void DeriveSessionKey_RejectsNonceWithUnexpectedLength()
    {
        using var pairing = new PairingCoordinator("desktop", 8787);
        using var mobile = new X25519KeyAgreement();
        var request = new PairRequestPayload(Convert.ToBase64String(mobile.PublicKey.Span), Convert.ToBase64String(new byte[16]));

        Assert.Throws<ArgumentException>(() => pairing.DeriveSessionKey(request));
    }
}