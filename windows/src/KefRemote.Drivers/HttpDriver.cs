using System.Net;
using KefRemote.Core;

namespace KefRemote.Drivers;

/// <summary>Shared HTTP plumbing for LAN drivers: no proxy, no redirects, short timeout, bounded responses.</summary>
public abstract class HttpDriver : IDisposable
{
    protected readonly HttpClient Http;
    protected readonly string Host;
    readonly HttpMessageHandler handler;
    HttpClient? longPoll;

    protected HttpDriver(DriverOptions options, HttpMessageHandler? handler, TimeSpan? timeout = null)
    {
        Host = HostAddress.ParseLocal(options.Host);
        this.handler = handler ?? CreateHandler();
        Http = new HttpClient(this.handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromMilliseconds(1500),
            MaxResponseContentBufferSize = 256 * 1024,
        };
    }

    /// <summary>
    /// A client for requests the device holds open (long polls). Shares the connection pool; each request sets its
    /// own deadline, which must allow for the hold.
    /// </summary>
    protected HttpClient LongPoll => longPoll ??= new HttpClient(handler, disposeHandler: false)
    {
        Timeout = Timeout.InfiniteTimeSpan,
        MaxResponseContentBufferSize = 256 * 1024,
    };

    public static HttpMessageHandler CreateHandler() =>
        new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };

    protected Task<string> SendAsync(HttpRequestMessage request, CancellationToken ct) => SendAsync(Http, request, ct);

    protected static async Task<string> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SpeakerException(Loc.T("인증 거부: 기기 암호와 PC 시간을 확인하세요", "Authentication refused: check the device password and the PC clock", "認証が拒否されました。機器のパスワードとPCの時刻を確認してください"));
        if (!response.IsSuccessStatusCode)
            throw new SpeakerException($"HTTP {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync(ct);
    }

    protected Task<string> GetAsync(string url, CancellationToken ct) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);

    public virtual void Dispose()
    {
        longPoll?.Dispose();
        Http.Dispose();
        GC.SuppressFinalize(this);
    }
}
