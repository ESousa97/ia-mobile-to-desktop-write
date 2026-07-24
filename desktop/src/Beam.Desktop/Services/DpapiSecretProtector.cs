using System.Security.Cryptography;
using Beam.Core.Security;

namespace Beam.Desktop.Services;

/// <summary>
/// Proteção em repouso via DPAPI com escopo do usuário atual: o arquivo de
/// dispositivos confiáveis só é legível pela conta Windows que o gravou, nesta
/// máquina. Copiá-lo para outro PC ou outra conta não dá acesso às chaves.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "Beam.TrustedDevices.v1"u8.ToArray();

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext) =>
        ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
}
