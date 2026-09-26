using System;
using System.Net;

namespace Jellyfin.Plugin.Common;

/// <summary>
/// Whether a service address is on this machine or the local network. One definition for every plugin: it decides
/// where a key may be sent over plain HTTP, and which services are treated as free.
/// </summary>
internal static class NetworkAddress
{
    /// <summary>
    /// Whether an address is this machine or the local network.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns><c>true</c> for loopback, private (10/8, 172.16/12, 192.168/16), link-local (169.254/16, fe80::/10) and
    /// unique-local (fc00::/7) addresses, <c>localhost</c> and <c>.local</c> names.</returns>
    public static bool IsLocal(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.IsAbsoluteUri)
        {
            return false;
        }

        if (address.IsLoopback || address.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(address.Host.Trim('[', ']'), out var ip))
        {
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal)
        {
            return true;
        }

        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254));
    }

    /// <summary>
    /// Whether an address, as entered, is this machine or the local network.
    /// </summary>
    /// <param name="address">The address text.</param>
    /// <returns><c>true</c> if it is an absolute address that is local.</returns>
    public static bool IsLocal(string? address)
        => Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) && IsLocal(uri);
}
