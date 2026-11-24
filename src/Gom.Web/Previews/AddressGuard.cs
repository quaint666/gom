using System.Net;
using System.Net.Sockets;

namespace Gom.Web.Previews;

/// <summary>
/// Which addresses a preview fetch may connect to: only public ones of the internet. Anything of this machine, of a private or
/// shared network, of a cloud's metadata service, or special (multicast, documentation, translation) is refused.
/// </summary>
public static class AddressGuard
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork) return PublicV4(address.GetAddressBytes());
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return PublicV6(address.GetAddressBytes());
        return false;
    }

    private static bool PublicV4(byte[] b)
    {
        var (a, c) = (b[0], b[1]);
        if (a == 0 || a == 10 || a == 127) return false;                       // "this" network, private, loopback
        if (a == 100 && c is >= 64 and <= 127) return false;                    // shared address space (carrier-grade NAT)
        if (a == 169 && c == 254) return false;                                // link-local, including the cloud metadata address
        if (a == 172 && c is >= 16 and <= 31) return false;                     // private
        if (a == 192 && c == 0 && b[2] is 0 or 2) return false;                 // protocol assignments, documentation
        if (a == 192 && c == 88 && b[2] == 99) return false;                    // 6to4 relay
        if (a == 192 && c == 168) return false;                                 // private
        if (a == 198 && c is 18 or 19) return false;                           // benchmarking
        if (a == 198 && c == 51 && b[2] == 100) return false;                   // documentation
        if (a == 203 && c == 0 && b[2] == 113) return false;                    // documentation
        if (a >= 224) return false;                                             // multicast, reserved, broadcast
        return true;
    }

    private static bool PublicV6(byte[] b)
    {
        // Only global unicast (2000::/3) is a candidate; everything else (loopback, unique-local, link-local, multicast, mapped, NAT64...) is not.
        if ((b[0] & 0xE0) != 0x20) return false;
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false;   // 2001:db8::/32 documentation
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return false;   // 2001::/32 Teredo
        if (b[0] == 0x20 && b[1] == 0x02) return false;                                    // 2002::/16 6to4, which carries an IPv4 address inside
        return true;
    }
}
