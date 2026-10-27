using System.Xml.Linq;
using KefRemote.Core;

namespace KefRemote.Drivers.BluOS;

/// <summary>
/// BluOS players (Bluesound, NAD, some others) over the documented HTTP API on port 11000.
/// Experimental: not tested on hardware by this project. Volume and mute only.
/// </summary>
public sealed class BluOsDriver(DriverOptions options, HttpMessageHandler? handler = null)
    : HttpDriver(options, handler), ISpeakerDriver
{
    public static readonly DriverDescriptor Descriptor = new(
        "bluos",
        "BluOS (Bluesound, NAD)",
        "BluOS 플레이어 전반",
        DriverStatus.Experimental,
        UsesPassword: false,
        o => new BluOsDriver(o));

    string Base => $"http://{Host}:11000";

    public DriverCapabilities Capabilities => DriverCapabilities.Volume | DriverCapabilities.Mute;
    public IReadOnlyList<SourceOption> Sources => [];

    public async Task<DeviceInfo> ConnectAsync(CancellationToken ct = default)
    {
        var sync = XElement.Parse(await GetAsync($"{Base}/SyncStatus", ct));
        if (sync.Name.LocalName != "SyncStatus") throw new SpeakerException("BluOS 응답이 아닙니다");
        string name = (string?)sync.Attribute("name") ?? Host;
        string model = $"{(string?)sync.Attribute("brand")} {(string?)sync.Attribute("modelName")}".Trim();
        return new DeviceInfo(name, model, (string?)sync.Attribute("schemaVersion") ?? "");
    }

    public async Task<SpeakerState> ReadAsync(CancellationToken ct = default)
    {
        var status = XElement.Parse(await GetAsync($"{Base}/Status", ct));
        if (!int.TryParse((string?)status.Element("volume"), out int volume) || volume < 0)
            throw new SpeakerException("고정 음량이거나 음량을 읽을 수 없습니다");
        return new SpeakerState
        {
            Power = PowerState.On,
            Volume = volume,
            Muted = (string?)status.Element("mute") == "1",
            Valid = true,
        };
    }

    public Task SetVolumeAsync(int volume, CancellationToken ct = default) =>
        GetAsync($"{Base}/Volume?level={Math.Clamp(volume, 0, 100)}", ct);

    public Task SetMuteAsync(bool muted, CancellationToken ct = default) =>
        GetAsync($"{Base}/Volume?mute={(muted ? 1 : 0)}", ct);

    public Task SetSourceAsync(string sourceId, CancellationToken ct = default) => throw new NotSupportedException();
}
