using System.Net;
using Beam.Core.Net;
using Xunit;

namespace Beam.Core.Tests;

public class NetworkInfoTests
{
    [Fact]
    public void GetBroadcastAddresses_IncludesGlobalBroadcast()
    {
        Assert.Contains(IPAddress.Broadcast, NetworkInfo.GetBroadcastAddresses());
    }

    [Fact]
    public void GetLanAddresses_SkipsLoopbackAndApipa()
    {
        foreach (var address in NetworkInfo.GetLanAddresses())
        {
            var octets = address.Address.GetAddressBytes();
            Assert.NotEqual(127, octets[0]);
            Assert.False(octets[0] == 169 && octets[1] == 254);
        }
    }

    /// <summary>
    /// O broadcast dirigido é o que roteadores Wi-Fi realmente entregam, então
    /// precisa cair na mesma sub-rede do endereço da interface.
    /// </summary>
    [Fact]
    public void GetLanAddresses_BroadcastSharesNetworkPrefix()
    {
        foreach (var address in NetworkInfo.GetLanAddresses())
        {
            var host = address.Address.GetAddressBytes();
            var broadcast = address.Broadcast.GetAddressBytes();
            for (var i = 0; i < 4; i++)
            {
                // Cada octeto do broadcast é o do host com os bits de host em 1.
                Assert.Equal(host[i] | broadcast[i], broadcast[i]);
            }
        }
    }

    [Fact]
    public void GetLanAddresses_PutsWirelessFirst()
    {
        var addresses = NetworkInfo.GetLanAddresses();
        var lastWireless = addresses.ToList().FindLastIndex(static address => address.IsWireless);
        if (lastWireless < 0)
        {
            return; // Máquina sem Wi-Fi (CI): nada a ordenar.
        }

        Assert.All(addresses.Take(lastWireless + 1), static address => Assert.True(address.IsWireless));
    }
}
