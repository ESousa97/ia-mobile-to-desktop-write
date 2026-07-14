namespace ClipBridge.Core.Discovery;

/// <summary>
/// Anúncio do serviço na LAN via mDNS/DNS-SD (<c>_clipbridge._tcp</c>), para que
/// o celular descubra o desktop sem digitar IP.
/// </summary>
public interface IDiscoveryService : IDisposable
{
    /// <summary>Começa a anunciar o serviço na porta informada.</summary>
    void Advertise(int port, string instanceName);

    /// <summary>Para o anúncio.</summary>
    void Stop();

    bool IsAdvertising { get; }
}
