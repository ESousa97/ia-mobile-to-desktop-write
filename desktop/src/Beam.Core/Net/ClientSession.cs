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

    public BlobReceiver BlobReceiver { get; } = new();

    public MemoryStream BinaryAccumulator { get; } = new();

    public MemoryStream TextAccumulator { get; } = new();

    public void Dispose()
    {
        Cipher?.Dispose();
        BinaryAccumulator.Dispose();
        TextAccumulator.Dispose();
    }
}
