using System.Security.Cryptography;
using System.Text;
using Beam.Core.Security;
using Xunit;

namespace Beam.Core.Tests;

public class SessionCipherTests
{
    private static byte[] RandomKey()
    {
        var key = new byte[SessionCipher.KeySizeBytes];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    [Fact]
    public void EncryptThenDecrypt_RoundTripsPlaintext()
    {
        using var cipher = new SessionCipher(RandomKey());
        var plaintext = Encoding.UTF8.GetBytes("olá, mundo cifrado 🔐 — ClipBridge");

        var package = cipher.Encrypt(plaintext);
        var decrypted = cipher.Decrypt(package);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_ProducesDifferentCiphertexts_ForSamePlaintext()
    {
        using var cipher = new SessionCipher(RandomKey());
        var plaintext = Encoding.UTF8.GetBytes("mesma entrada");

        var a = cipher.Encrypt(plaintext);
        var b = cipher.Encrypt(plaintext);

        // Nonce aleatório por operação → pacotes distintos.
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Decrypt_TamperedPackage_Throws()
    {
        using var cipher = new SessionCipher(RandomKey());
        var package = cipher.Encrypt(Encoding.UTF8.GetBytes("dados íntegros"));

        package[^1] ^= 0xFF; // adultera o último byte do ciphertext

        Assert.Throws<AuthenticationTagMismatchException>(() => cipher.Decrypt(package));
    }

    [Fact]
    public void Constructor_WithWrongKeySize_Throws()
    {
        var shortKey = new byte[16];
        Assert.Throws<ArgumentException>(() => new SessionCipher(shortKey));
    }
}
