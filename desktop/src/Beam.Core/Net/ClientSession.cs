using System.Net.WebSockets;
using Beam.Core.Security;

namespace Beam.Core.Net;

/// <summary>Conexão WebSocket ativa, com cifra de sessão após o pareamento.</summary>
internal sealed class ClientSession : IDisposable
{
    public ClientSession(WebSocket socket) => Socket = socket;

    public WebSocket Socket { get; }

    public SessionCipher? Cipher { get; set; }

    public bool IsSecure { get; set; }

    /// <summary>
    /// Chave de retomada derivada no <c>pair.request</c>, ainda não confirmada:
    /// só vira vínculo persistido quando o código de pareamento for aceito.
    /// </summary>
    public byte[]? PendingResumeKey { get; set; }

    /// <summary>Vínculo em uso nesta conexão, para renovar a validade ao concluir.</summary>
    public string? TrustedDeviceId { get; set; }

    /// <summary>Prova esperada do celular no <c>session.resume.confirm</c>.</summary>
    public byte[]? ExpectedClientProof { get; set; }

    public BlobReceiver BlobReceiver { get; } = new();

    public MemoryStream BinaryAccumulator { get; } = new();

    public MemoryStream TextAccumulator { get; } = new();

    public void Dispose()
    {
        if (PendingResumeKey is not null)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(PendingResumeKey);
            PendingResumeKey = null;
        }

        Cipher?.Dispose();
        BinaryAccumulator.Dispose();
        TextAccumulator.Dispose();
    }
}
