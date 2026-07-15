namespace Beam.Core.Discovery;

/// <summary>
/// Anúncio/resposta do serviço na LAN (atualmente UDP na porta 8788; mDNS planejado),
/// para que o celular descubra o desktop sem digitar IP.
/// </summary>
public interface IDiscoveryService : IDisposable
{
    /// <summary>Começa a anunciar o serviço na porta informada.</summary>
    void Advertise(int port, string instanceName);

    /// <summary>Para o anúncio.</summary>
    void Stop();

    bool IsAdvertising { get; }
}
