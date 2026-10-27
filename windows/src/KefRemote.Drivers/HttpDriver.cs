using System.Net;
using KefRemote.Core;

namespace KefRemote.Drivers;

/// <summary>Shared HTTP plumbing for LAN drivers: no proxy, no redirects, short timeout, bounded responses.</summary>
public abstract class HttpDriver : IDisposable
{
    protected readonly HttpClient Http;
    protected readonly string Host;

    protected HttpDriver(DriverOptions options, HttpMessageHandler? handler, TimeSpan? timeout = null)
    {
        Host = HostAddress.ParseLocal(options.Host);
        Http = new HttpClient(handler ?? CreateHandler(), disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromMilliseconds(1500),
            MaxResponseContentBufferSize = 256 * 1024,
        };
    }

    public static HttpMessageHandler CreateHandler(bool acceptSelfSignedLocalCertificates = false)
    {
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        if (acceptSelfSignedLocalCertificates)
        {
            // Players such as WiiM serve HTTPS with a self-signed certificate. Only private IPv4 hosts
            // are reachable through HostAddress.ParseLocal, so accepting it does not extend to the internet.
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
        return handler;
    }

    protected async Task<string> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SpeakerException("인증 거부: 기기 암호와 PC 시간을 확인하세요");
        if (!response.IsSuccessStatusCode)
            throw new SpeakerException($"HTTP {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync(ct);
    }

    protected Task<string> GetAsync(string url, CancellationToken ct) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);

    public virtual void Dispose()
    {
        Http.Dispose();
        GC.SuppressFinalize(this);
    }
}
