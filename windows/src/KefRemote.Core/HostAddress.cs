using System.Net;
using System.Net.Sockets;

namespace KefRemote.Core;

public static class HostAddress
{
    /// <summary>
    /// Accepts only private/link-local IPv4 addresses so the app never talks to a public host.
    /// </summary>
    public static string ParseLocal(string? text)
    {
        if (!IPAddress.TryParse(text?.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || !IsLocal(ip))
            throw new SpeakerException("사설 네트워크의 IPv4 주소를 입력하세요 (예: 192.168.0.14)");
        return ip.ToString();
    }

    public static bool IsLocal(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10
            || b[0] == 192 && b[1] == 168
            || b[0] == 172 && b[1] >= 16 && b[1] <= 31
            || b[0] == 127
            || b[0] == 169 && b[1] == 254);
    }
}
