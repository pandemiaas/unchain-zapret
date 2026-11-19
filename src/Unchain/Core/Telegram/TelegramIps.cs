using System.Net;
using System.Net.Sockets;

namespace Unchain.Core.Telegram;

public static class TelegramIps
{
    static readonly (byte[] Net, int Bits)[] Ranges = new[]
    {
        "91.108.4.0/22", "91.108.8.0/22", "91.108.12.0/22", "91.108.16.0/22", "91.108.20.0/22",
        "91.108.56.0/22", "91.105.192.0/23", "149.154.160.0/20", "185.76.151.0/24", "95.161.64.0/20",
        "2001:b28:f23d::/48", "2001:b28:f23f::/48", "2001:b28:f23c::/48", "2001:67c:4e8::/48", "2a0a:f280::/32",
    }.Select(Parse).ToArray();

    static (byte[], int) Parse(string cidr)
    {
        var parts = cidr.Split('/');
        return (IPAddress.Parse(parts[0]).GetAddressBytes(), int.Parse(parts[1]));
    }

    public static bool IsTelegram(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        foreach (var (net, bits) in Ranges)
        {
            if (net.Length != b.Length) continue;
            if (Matches(b, net, bits)) return true;
        }
        return false;
    }

    static bool Matches(byte[] a, byte[] net, int bits)
    {
        var full = bits / 8;
        for (var i = 0; i < full; i++) if (a[i] != net[i]) return false;
        var rest = bits % 8;
        if (rest == 0) return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (a[full] & mask) == (net[full] & mask);
    }

    /// <summary>Запасной способ узнать DC, если из init-пакета не вышло.</summary>
    public static int? DcFor(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork) return null;
        var b = ip.GetAddressBytes();
        return (b[0], b[1], b[2]) switch
        {
            (149, 154, 175) => b[3] >= 100 ? 3 : 1,
            (149, 154, 167) => b[3] is >= 91 and <= 99 ? 4 : 2,
            (149, 154, 171) => 5,
            (91, 108, 56) => 5,
            (91, 108, 4) => 4,
            _ => null,
        };
    }
}
