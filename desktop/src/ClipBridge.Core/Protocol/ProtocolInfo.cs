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

    /// <summary>Tipo de serviço mDNS/NSD anunciado na LAN.</summary>
    public const string ServiceType = "_clipbridge._tcp";

    /// <summary>Tamanho padrão de cada chunk de blob binário (64 KiB).</summary>
    public const int DefaultChunkSize = 64 * 1024;
}
