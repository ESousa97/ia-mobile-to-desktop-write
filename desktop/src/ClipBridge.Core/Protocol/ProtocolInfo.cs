namespace ClipBridge.Core.Protocol;

/// <summary>
/// Constantes globais do protocolo ClipBridge.
/// </summary>
public static class ProtocolInfo
{
    /// <summary>Versão atual do protocolo de aplicação.</summary>
    public const int Version = 1;

    /// <summary>Porta TCP padrão do servidor WebSocket (configurável).</summary>
    public const int DefaultPort = 8787;

    /// <summary>Porta UDP usada para descobrir servidores na LAN.</summary>
    public const int DiscoveryPort = 8788;

    /// <summary>Tamanho padrão de cada chunk de blob binário (64 KiB).</summary>
    public const int DefaultChunkSize = 64 * 1024;
}
