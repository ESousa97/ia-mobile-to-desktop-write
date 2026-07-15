using System.Security.Cryptography;
using Beam.Core.Protocol;
using Beam.Core.Security;
using Xunit;

namespace Beam.Core.Tests;

public class PairingCoordinatorTests
{
    [Fact]
    public void Invitation_ContainsCodeAndExpiry()
    {
        using var pairing = new PairingCoordinator();

        Assert.Matches("^[0-9]{6}$", pairing.Invitation.PairingCode);
        Assert.True(pairing.Invitation.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void TryConfirmCode_AcceptsValidCodeOnlyOnce()
    {
        using var pairing = new PairingCoordinator();

        Assert.False(pairing.TryConfirmCode("000000"));
        Assert.True(pairing.TryConfirmCode(pairing.Invitation.PairingCode));
        Assert.False(pairing.TryConfirmCode(pairing.Invitation.PairingCode));
    }

    [Fact]
    public void TryConfirmCode_RejectsMalformedCode()
    {
        using var pairing = new PairingCoordinator();

        Assert.False(pairing.TryConfirmCode("12ab56"));
        Assert.False(pairing.TryConfirmCode("12345"));
    }

    [Fact]
    public void TryConfirmCode_LocksOutAfterMaxFailedAttempts()
    {
        using var pairing = new PairingCoordinator();
        var validCode = pairing.Invitation.PairingCode;

        for (var attempt = 0; attempt < PairingCoordinator.MaxFailedAttempts - 1; attempt++)
        {
            Assert.False(pairing.TryConfirmCode("000000"));
        }

        Assert.False(pairing.TryConfirmCode("000000"));
        Assert.False(pairing.TryConfirmCode(validCode));
    }

    [Fact]
    public void DeriveSessionKey_UsesPairRequestNonceAsHkdfSalt()
    {
        using var pairing = new PairingCoordinator();
        using var mobile = new X25519KeyAgreement();
        var nonce = RandomNumberGenerator.GetBytes(32);
        var request = new PairRequestPayload(Convert.ToBase64String(mobile.PublicKey.Span), Convert.ToBase64String(nonce));

        var desktopKey = pairing.DeriveSessionKey(request);
        var mobileKey = mobile.DeriveSessionKey(
            Convert.FromBase64String(pairing.CreateResponse().PubKey),
            nonce,
            PairingCoordinator.SessionKeyInfo);

        Assert.Equal(desktopKey, mobileKey);
    }

    [Fact]
    public void DeriveSessionKey_RejectsNonceWithUnexpectedLength()
    {
        using var pairing = new PairingCoordinator();
        using var mobile = new X25519KeyAgreement();
        var request = new PairRequestPayload(Convert.ToBase64String(mobile.PublicKey.Span), Convert.ToBase64String(new byte[16]));

        Assert.Throws<ArgumentException>(() => pairing.DeriveSessionKey(request));
    }
}
