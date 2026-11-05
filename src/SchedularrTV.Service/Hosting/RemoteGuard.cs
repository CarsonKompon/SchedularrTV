using System.Net;
using System.Net.NetworkInformation;

namespace SchedularrTV.Service;

/// <summary>
/// What other devices may fetch while "Allow other devices" is off: only the pictures the channel list and guide link
/// to, which a TV elsewhere on the network has to be able to load. Everything else answers this computer only.
/// </summary>
public static class RemoteGuard
{
    /// <summary>Paths of the pictures players fetch: uploaded channel logos, and local shows' and movies' artwork.</summary>
    public static readonly string[] PublicPictures = ["/logos/", "/api/library/artwork/"];

    public static bool Allows(IPAddress? remote, string method, PathString path)
    {
        if (remote is null || IsThisComputer(remote))
        {
            return true;
        }

        return (HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
            && path.HasValue
            && PublicPictures.Any(prefix => path.Value!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            && !path.Value!.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>Loopback, or one of this computer's own addresses (a browser here using the network address).</summary>
    public static bool IsThisComputer(IPAddress remote)
    {
        IPAddress address = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Any(unicast => unicast.Address.Equals(address));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }
}
