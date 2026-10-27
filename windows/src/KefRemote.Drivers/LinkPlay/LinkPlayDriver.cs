using System.Text.Json;
using KefRemote.Core;

namespace KefRemote.Drivers.LinkPlay;

/// <summary>
/// LinkPlay based players (WiiM, Arylic, Audio Pro and others) over httpapi.asp.
/// Newer firmware serves only HTTPS with a self-signed certificate, so HTTPS is tried first.
/// Experimental: not tested on hardware by this project. Volume and mute only.
/// </summary>
public sealed class LinkPlayDriver : HttpDriver, ISpeakerDriver
{
    public static readonly DriverDescriptor Descriptor = new(
        "linkplay",
        "LinkPlay (WiiM, Arylic, Audio Pro)",
        "LinkPlay 기반 스트리머",
        DriverStatus.Experimental,
        UsesPassword: false,
        o => new LinkPlayDriver(o));

    string scheme = "https";

    public LinkPlayDriver(DriverOptions options, HttpMessageHandler? handler = null)
        : base(options, handler ?? CreateHandler(acceptSelfSignedLocalCertificates: true)) { }

    public DriverCapabilities Capabilities => DriverCapabilities.Volume | DriverCapabilities.Mute;
    public IReadOnlyList<SourceOption> Sources => [];

    public async Task<DeviceInfo> ConnectAsync(CancellationToken ct = default)
    {
        JsonElement status;
        try
        {
            status = await CommandAsync("getStatusEx", ct);
        }
        catch (Exception e) when (e is HttpRequestException or SpeakerException && !ct.IsCancellationRequested)
        {
            scheme = "http";
            status = await CommandAsync("getStatusEx", ct);
        }
        return new DeviceInfo(
            Text(status, "DeviceName") ?? Host,
            Text(status, "project") ?? "LinkPlay",
            Text(status, "firmware") ?? "");
    }

    public async Task<SpeakerState> ReadAsync(CancellationToken ct = default)
    {
        var s = await CommandAsync("getPlayerStatus", ct);
        if (!int.TryParse(Text(s, "vol"), out int volume)) throw new SpeakerException("음량을 읽을 수 없습니다");
        return new SpeakerState { Power = PowerState.On, Volume = volume, Muted = Text(s, "mute") == "1", Valid = true };
    }

    public Task SetVolumeAsync(int volume, CancellationToken ct = default) =>
        RawAsync($"setPlayerCmd:vol:{Math.Clamp(volume, 0, 100)}", ct);

    public Task SetMuteAsync(bool muted, CancellationToken ct = default) =>
        RawAsync($"setPlayerCmd:mute:{(muted ? 1 : 0)}", ct);

    public Task SetSourceAsync(string sourceId, CancellationToken ct = default) => throw new NotSupportedException();

    async Task<JsonElement> CommandAsync(string command, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await RawAsync(command, ct));
        return doc.RootElement.Clone();
    }

    async Task<string> RawAsync(string command, CancellationToken ct)
    {
        string text = await GetAsync($"{scheme}://{Host}/httpapi.asp?command={command}", ct);
        if (text.Trim() == "unknown command") throw new SpeakerException("지원하지 않는 명령");
        return text;
    }

    static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;
}
