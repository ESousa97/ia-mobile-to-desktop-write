using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Beam.Core.Net;

/// <summary>Endereço IPv4 de uma interface de rede ativa da máquina.</summary>
/// <param name="Address">IP da interface (ex.: 192.168.1.20).</param>
/// <param name="Broadcast">Broadcast dirigido da sub-rede (ex.: 192.168.1.255).</param>
/// <param name="InterfaceName">Nome amigável da interface (ex.: "Wi-Fi").</param>
/// <param name="IsWireless">Verdadeiro para interfaces 802.11.</param>
public readonly record struct LanAddress(
    IPAddress Address,
    IPAddress Broadcast,
    string InterfaceName,
    bool IsWireless);

/// <summary>
/// Enumera as interfaces IPv4 utilizáveis para a comunicação na LAN.
/// </summary>
/// <remarks>
/// Usado tanto para o anúncio em broadcast (o celular precisa receber pacotes
/// no broadcast dirigido — roteadores Wi-Fi costumam descartar 255.255.255.255)
/// quanto para exibir ao usuário o endereço de conexão manual.
/// </remarks>
public static class NetworkInfo
{
    /// <summary>
    /// Endereços das interfaces ativas, com Wi-Fi primeiro — é a rede em que o
    /// celular quase sempre está, então é o IP que interessa mostrar.
    /// </summary>
    public static IReadOnlyList<LanAddress> GetLanAddresses()
    {
        var addresses = new List<LanAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                    {
                        continue;
                    }

                    // 169.254.x.x (APIPA) indica interface sem DHCP: inalcançável pelo celular.
                    var octets = unicast.Address.GetAddressBytes();
                    if (octets[0] == 169 && octets[1] == 254)
                    {
                        continue;
                    }

                    addresses.Add(new LanAddress(
                        unicast.Address,
                        ComputeBroadcast(octets, unicast.IPv4Mask.GetAddressBytes()),
                        nic.Name,
                        nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Enumeração indisponível: o chamador cai no broadcast global.
        }

        return addresses
            .OrderByDescending(static address => address.IsWireless)
            .ToList();
    }

    /// <summary>
    /// Alvos de broadcast: o dirigido de cada interface mais o global. Roteadores
    /// Wi-Fi frequentemente só entregam o dirigido.
    /// </summary>
    public static IReadOnlyList<IPAddress> GetBroadcastAddresses()
    {
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var address in GetLanAddresses())
        {
            targets.Add(address.Broadcast);
        }

        return targets.ToList();
    }

    private static IPAddress ComputeBroadcast(byte[] address, byte[] mask)
    {
        var broadcast = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            broadcast[i] = (byte)(address[i] | ~mask[i]);
        }

        return new IPAddress(broadcast);
    }
}
