using System.Net;
using System.Text;
using KefRemote.Core;

namespace KefRemote.Tests;

/// <summary>In-memory HTTP endpoint. Records every request with its body.</summary>
sealed class StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    public readonly List<(HttpRequestMessage Request, string? Body)> Requests = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string? body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
        lock (Requests) Requests.Add((request, body));
        return respond(request, body);
    }

    public static HttpResponseMessage Ok(string text, string type = "application/json") =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, type) };
}

/// <summary>Scriptable driver used by controller tests.</summary>
sealed class FakeDriver : ISpeakerDriver
{
    public int Volume = 20, Writes, Reads;
    public bool Muted, Broken, IgnoreWrites, ResetMuteOnSource;
    public string Source = "usb";
    public PowerState Power = PowerState.On;
    public Dictionary<string, int> VolumePerSource = [];
    public TaskCompletionSource<bool>? Gate;
    public DeviceInfo Info = new("Test", "Fake", "1");

    public DriverCapabilities Capabilities { get; set; } = DriverCapabilities.Volume | DriverCapabilities.Mute | DriverCapabilities.Source;
    public IReadOnlyList<SourceOption> Sources { get; } = [new("usb", "USB"), new("wifi", "Wi-Fi")];

    public Task<DeviceInfo> ConnectAsync(CancellationToken ct = default) =>
        Broken ? throw new HttpRequestException("offline") : Task.FromResult(Info);

    public async Task<SpeakerState> ReadAsync(CancellationToken ct = default)
    {
        Reads++;
        AdvancePower();
        if (Gate != null) await Gate.Task;
        if (Broken) throw new HttpRequestException("offline");
        return new SpeakerState { Power = Power, SourceId = Power == PowerState.Standby ? null : Source, Volume = Volume, Muted = Muted, Valid = true };
    }

    public readonly List<int> VolumeWrites = [];
    public Func<int, int, int?>? Overshoot;
    /// <summary>Reads that report the old power state after a power write, like a speaker that takes a while.</summary>
    public int PowerLagReads;
    PowerState? pendingPower;

    public Task SetVolumeAsync(int volume, CancellationToken ct = default)
    {
        Writes++;
        VolumeWrites.Add(volume);
        if (!IgnoreWrites) Volume = volume;
        return Task.CompletedTask;
    }

    public Task SetPowerAsync(bool on, CancellationToken ct = default)
    {
        Writes++;
        if (IgnoreWrites) return Task.CompletedTask;
        if (PowerLagReads > 0) pendingPower = on ? PowerState.On : PowerState.Standby;
        else Power = on ? PowerState.On : PowerState.Standby;
        return Task.CompletedTask;
    }

    public int? ApproachOvershoot(int current, int target) => Overshoot?.Invoke(current, target);

    void AdvancePower()
    {
        if (pendingPower is not PowerState next) return;
        if (PowerLagReads-- > 0) return;
        Power = next;
        pendingPower = null;
    }
    public Task SetMuteAsync(bool muted, CancellationToken ct = default) { Writes++; if (!IgnoreWrites) Muted = muted; return Task.CompletedTask; }

    public Task SetSourceAsync(string sourceId, CancellationToken ct = default)
    {
        Writes++;
        VolumePerSource[Source] = Volume;
        Source = sourceId;
        if (VolumePerSource.TryGetValue(sourceId, out int v)) Volume = v;
        if (ResetMuteOnSource) Muted = false;
        return Task.CompletedTask;
    }

    public void Dispose() { }
}
