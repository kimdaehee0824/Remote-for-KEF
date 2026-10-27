using System.Text.Json;
using KefRemote.Core;

namespace KefRemote.Drivers.MusicCast;

/// <summary>
/// Yamaha MusicCast over the Yamaha Extended Control HTTP API (main zone).
/// Experimental: not tested on hardware by this project. The native volume scale
/// (0..max_volume) is mapped onto 0-100.
/// </summary>
public sealed class MusicCastDriver(DriverOptions options, HttpMessageHandler? handler = null)
    : HttpDriver(options, handler), ISpeakerDriver
{
    public static readonly DriverDescriptor Descriptor = new(
        "musiccast",
        "Yamaha MusicCast",
        "MusicCast 지원 리시버/스피커",
        DriverStatus.Experimental,
        UsesPassword: false,
        o => new MusicCastDriver(o));

    string Api => $"http://{Host}/YamahaExtendedControl/v1";
    int maxVolume = 100;
    SourceOption[] sources = [];

    public DriverCapabilities Capabilities =>
        DriverCapabilities.Volume | DriverCapabilities.Mute | DriverCapabilities.Power
        | (sources.Length > 1 ? DriverCapabilities.Source : 0);

    public IReadOnlyList<SourceOption> Sources => sources;

    public int VolumeTolerance => maxVolume >= 100 ? 0 : (int)Math.Ceiling(100.0 / maxVolume / 2);

    public async Task<DeviceInfo> ConnectAsync(CancellationToken ct = default)
    {
        var info = await CallAsync("system/getDeviceInfo", ct);
        var network = await CallAsync("system/getNetworkStatus", ct);
        var features = await CallAsync("system/getFeatures", ct);

        foreach (var zone in features.GetProperty("zone").EnumerateArray())
        {
            if (zone.GetProperty("id").GetString() != "main") continue;
            sources = zone.GetProperty("input_list").EnumerateArray()
                .Select(i => i.GetString()!)
                .Select(id => new SourceOption(id, id))
                .ToArray();
            foreach (var range in zone.TryGetProperty("range_step", out var r) ? r.EnumerateArray() : default)
                if (range.GetProperty("id").GetString() == "volume") maxVolume = Math.Max(1, range.GetProperty("max").GetInt32());
        }
        return new DeviceInfo(
            network.TryGetProperty("network_name", out var n) ? n.GetString() ?? Host : Host,
            "Yamaha " + info.GetProperty("model_name").GetString(),
            info.TryGetProperty("system_version", out var v) ? v.ToString() : "");
    }

    public async Task<SpeakerState> ReadAsync(CancellationToken ct = default)
    {
        var s = await CallAsync("main/getStatus", ct);
        if (s.TryGetProperty("max_volume", out var max)) maxVolume = Math.Max(1, max.GetInt32());
        return new SpeakerState
        {
            Power = s.GetProperty("power").GetString() switch { "on" => PowerState.On, "standby" => PowerState.Standby, _ => PowerState.Unknown },
            SourceId = s.GetProperty("input").GetString(),
            Volume = (int)Math.Round(s.GetProperty("volume").GetInt32() * 100.0 / maxVolume),
            Muted = s.GetProperty("mute").GetBoolean(),
            Valid = true,
        };
    }

    public Task SetVolumeAsync(int volume, CancellationToken ct = default) =>
        CallAsync($"main/setVolume?volume={(int)Math.Round(Math.Clamp(volume, 0, 100) * maxVolume / 100.0)}", ct);

    public Task SetMuteAsync(bool muted, CancellationToken ct = default) =>
        CallAsync($"main/setMute?enable={(muted ? "true" : "false")}", ct);

    public Task SetSourceAsync(string sourceId, CancellationToken ct = default) =>
        sources.Any(s => s.Id == sourceId)
            ? CallAsync($"main/setInput?input={Uri.EscapeDataString(sourceId)}", ct)
            : throw new SpeakerException("지원하지 않는 입력: " + sourceId);

    public Task SetPowerAsync(bool on, CancellationToken ct = default) =>
        CallAsync($"main/setPower?power={(on ? "on" : "standby")}", ct);

    async Task<JsonElement> CallAsync(string path, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await GetAsync($"{Api}/{path}", ct));
        var root = doc.RootElement.Clone();
        if (!root.TryGetProperty("response_code", out var code) || code.GetInt32() != 0)
            throw new SpeakerException("MusicCast 오류 응답");
        return root;
    }
}
