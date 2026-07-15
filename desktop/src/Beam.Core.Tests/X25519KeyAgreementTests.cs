using Beam.Core.Security;
using Xunit;

namespace Beam.Core.Tests;

public class X25519KeyAgreementTests
{
    private static readonly byte[] Salt = "clipbridge-pairing-salt"u8.ToArray();
    private static readonly byte[] Info = "clipbridge-v1-session"u8.ToArray();

    [Fact]
    public void DeriveSessionKey_BetweenPeers_ProducesSameAes256Key()
    {
        using var desktop = new X25519KeyAgreement();
        using var mobile = new X25519KeyAgreement();

        var desktopKey = desktop.DeriveSessionKey(mobile.PublicKey.Span, Salt, Info);
        var mobileKey = mobile.DeriveSessionKey(desktop.PublicKey.Span, Salt, Info);

        Assert.Equal(SessionCipher.KeySizeBytes, desktopKey.Length);
        Assert.Equal(desktopKey, mobileKey);
    }

    [Fact]
    public void DeriveSessionKey_IsUsableBySessionCipher()
    {
        using var desktop = new X25519KeyAgreement();
        using var mobile = new X25519KeyAgreement();
        var desktopKey = desktop.DeriveSessionKey(mobile.PublicKey.Span, Salt, Info);
        var mobileKey = mobile.DeriveSessionKey(desktop.PublicKey.Span, Salt, Info);
        using var encryptor = new SessionCipher(desktopKey);
        using var decryptor = new SessionCipher(mobileKey);

        var package = encryptor.Encrypt("mensagem de sessão"u8);

        Assert.Equal("mensagem de sessão"u8.ToArray(), decryptor.Decrypt(package));
    }

    [Fact]
    public void DeriveSessionKey_WithInvalidPublicKey_Throws()
    {
        using var agreement = new X25519KeyAgreement();

        Assert.Throws<ArgumentException>(() => agreement.DeriveSessionKey(new byte[31], Salt, Info));
    }

    [Fact]
    public void Fingerprint_IsStableAndReadable()
    {
        using var agreement = new X25519KeyAgreement();

        Assert.Matches("^[0-9A-F]{12}$", agreement.Fingerprint);
        Assert.Equal(agreement.Fingerprint, agreement.Fingerprint);
    }
}