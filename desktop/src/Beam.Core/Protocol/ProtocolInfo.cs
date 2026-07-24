namespace Beam.Core.Protocol;

/// <summary>
/// Constantes globais do protocolo Beam.
/// </summary>
public static class ProtocolInfo
{
    /// <summary>Versão atual do protocolo de aplicação.</summary>
    public const int Version = 1;

    /// <summary>Porta TCP padrão do servidor WebSocket (configurável).</summary>
    public const int DefaultPort = 8787;

    /// <summary>Porta UDP usada para descobrir servidores na LAN.</summary>
    public const int DiscoveryPort = 8788;

    /// <summary>Porta UDP onde o celular escuta os anúncios em broadcast do desktop.</summary>
    public const int AnnouncePort = 8789;

    /// <summary>Prefixo do anúncio de descoberta.</summary>
    public const string AnnouncePrefix = "clipbridge.announce.v1:";

    /// <summary>
    /// Terminador do anúncio. Sem ele, um datagrama cortado no meio dos dígitos
    /// (8787 → 878) seria lido como uma porta válida porém errada, e o celular
    /// tentaria conectar num lugar onde ninguém escuta.
    /// </summary>
    public const string AnnounceSuffix = ";";

    /// <summary>Anúncio completo emitido pelo desktop para a porta informada.</summary>
    public static string BuildAnnounce(int port) => $"{AnnouncePrefix}{port}{AnnounceSuffix}";

    /// <summary>Tamanho padrão de cada chunk de blob binário (64 KiB).</summary>
    public const int DefaultChunkSize = 64 * 1024;

    /// <summary>Limite máximo de um blob transferido (50 MiB).</summary>
    public const long MaxBlobBytes = 50L * 1024 * 1024;
}
