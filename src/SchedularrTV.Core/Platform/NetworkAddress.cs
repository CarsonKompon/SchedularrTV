using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SchedularrTV.Core.Platform;

/// <summary>
/// This computer's address on the local network, for links other devices follow (channel logos and guide pictures in
/// the M3U and XMLTV a TV reads). "localhost" there means the TV itself, so those links can never work.
/// </summary>
public static class NetworkAddress
{
    /// <summary>
    /// The IPv4 address of the network connection with a default gateway (the one other devices on the LAN reach this
    /// computer by), preferring wired and Wi-Fi over VPNs and virtual adapters; null when there isn't one.
    /// </summary>
    public static string? LanIPv4()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                    && nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(nic => (Nic: nic, Properties: nic.GetIPProperties()))
                .Where(pair => pair.Properties.GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)))
                .OrderBy(pair => Rank(pair.Nic))
                .SelectMany(pair => pair.Properties.UnicastAddresses
                    .Where(unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(unicast.Address)
                        && !unicast.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    .Select(unicast => unicast.Address.ToString()))
                .FirstOrDefault();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    // Real adapters before the virtual ones (Hyper-V, WSL, VirtualBox, VPN clients), which often have gateways too.
    private static int Rank(NetworkInterface nic)
    {
        string name = $"{nic.Name} {nic.Description}";
        bool virtualAdapter = new[] { "virtual", "vethernet", "hyper-v", "vmware", "virtualbox", "wsl", "vpn", "tap", "tun", "zerotier", "tailscale", "wireguard" }
            .Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
        return (virtualAdapter ? 10 : 0) + nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => 0,
            NetworkInterfaceType.Wireless80211 => 1,
            _ => 2
        };
    }

    /// <summary>Whether <paramref name="host"/> names this computer only to itself (localhost, 127.x, ::1).</summary>
    public static bool IsLoopbackHost(string? host) =>
        !string.IsNullOrWhiteSpace(host)
        && (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? address) && IPAddress.IsLoopback(address)));

    /// <summary>
    /// <paramref name="url"/> with a loopback host (as an upload made from a browser on this computer records it) moved
    /// to <paramref name="reachableBase"/>'s host and port; anything else is returned as it is.
    /// </summary>
    public static string? Reachable(string? url, string? reachableBase)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(reachableBase)
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? link) || !IsLoopbackHost(link.Host)
            || !Uri.TryCreate(reachableBase, UriKind.Absolute, out Uri? target) || IsLoopbackHost(target.Host))
        {
            return url;
        }

        return new UriBuilder(link) { Scheme = target.Scheme, Host = target.Host, Port = target.Port }.Uri.AbsoluteUri;
    }
}
