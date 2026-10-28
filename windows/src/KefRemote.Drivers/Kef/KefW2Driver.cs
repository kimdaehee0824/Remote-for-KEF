using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KefRemote.Core;

namespace KefRemote.Drivers.Kef;

/// <summary>
/// KEF W2 platform (LSX II, LSX II LT, LS50 Wireless II, LS60, XIO) over the local HTTP API.
/// Supports authMode none, setData and all with the HMAC_SHA256_AES256 scheme.
/// </summary>
public sealed class KefW2Driver : HttpDriver, ISpeakerDriver
{
    public const string SourcePath = "settings:/kef/play/physicalSource";
    public const string VolumePath = "player:volume";
    public const string MutePath = "settings:/mediaPlayer/mute";
    public const string PowerPath = "settings:/kef/host/speakerStatus";

    readonly string password;

    public static readonly DriverDescriptor Descriptor = new(
        "kef-w2",
        "KEF (W2 플랫폼)",
        "LSX II LT (검증), LSX II, LS50 Wireless II, LS60, XIO",
        DriverStatus.Verified,
        UsesPassword: true,
        o => new KefW2Driver(o));

    public KefW2Driver(DriverOptions options, HttpMessageHandler? handler = null) : base(options, handler)
    {
        password = options.Password ?? "";
    }

    public string AuthMode { get; private set; } = "unknown";
    public KefModel? Model { get; private set; }

    /// <summary>Test hook for salt/IV generation.</summary>
    public Func<int, byte[]> RandomBytes { get; init; } = RandomNumberGenerator.GetBytes;

    /// <summary>Test hook for the signed timestamp.</summary>
    public Func<long> UnixTimeMs { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public DriverCapabilities Capabilities =>
        DriverCapabilities.Volume | DriverCapabilities.Mute | DriverCapabilities.Power |
        (Model?.Sources.Count > 1 ? DriverCapabilities.Source : 0);

    public IReadOnlyList<SourceOption> Sources => Model == null ? [] : KefModels.SourceOptions(Model);

    public async Task<DeviceInfo> ConnectAsync(CancellationToken ct = default)
    {
        string auth;
        try
        {
            auth = KefW2Protocol.ReadString(await RequestAsync("GET", KefW2Protocol.GetDataUrl(Host, "settings:/webserver/authMode"), null, false, ct));
        }
        catch (SpeakerException) when (password.Length > 0)
        {
            // authMode "all" protects reads too.
            auth = KefW2Protocol.ReadString(await RequestAsync("GET", KefW2Protocol.GetDataUrl(Host, "settings:/webserver/authMode"), null, true, ct));
        }
        if (auth is not ("none" or "setData" or "all")) throw new SpeakerException("지원하지 않는 인증 방식: " + auth);
        AuthMode = auth;

        string modelName = KefW2Protocol.ReadString(await GetDataAsync("settings:/kef/host/modelName", ct));
        string release = KefW2Protocol.ReadString(await GetDataAsync("settings:/releasetext", ct));
        string name = KefW2Protocol.ReadString(await GetDataAsync("settings:/deviceName", ct));
        Model = KefModels.Resolve(modelName, release);

        int? deviceLimit = null;
        try
        {
            if (KefW2Protocol.ReadBool(await GetDataAsync("settings:/kef/host/volumeLimit", ct)))
            {
                int max = KefW2Protocol.ReadInt(await GetDataAsync("settings:/kef/host/maximumVolume", ct));
                if (max is >= 0 and <= 100) deviceLimit = max;
            }
        }
        catch (SpeakerException)
        {
            // Optional. The app limit still applies.
        }
        return new DeviceInfo(name, Model.DisplayName, release, deviceLimit);
    }

    public async Task<SpeakerState> ReadAsync(CancellationToken ct = default)
    {
        var source = GetDataAsync(SourcePath, ct);
        var volume = GetDataAsync(VolumePath, ct);
        var mute = GetDataAsync(MutePath, ct);
        var power = GetDataAsync(PowerPath, ct);
        await Task.WhenAll(source, volume, mute, power);

        string first = KefW2Protocol.ReadString(source.Result);
        string second = KefW2Protocol.ReadString(await GetDataAsync(SourcePath, ct));
        if (first != second) throw new SpeakerException("입력 전환 중입니다");

        string status = KefW2Protocol.ReadString(power.Result);
        var powerState = status switch { "powerOn" => PowerState.On, "standby" => PowerState.Standby, _ => PowerState.Unknown };
        return new SpeakerState
        {
            Power = powerState,
            SourceId = second == "standby" ? null : second,
            Volume = KefW2Protocol.ReadInt(volume.Result),
            Muted = KefW2Protocol.ReadBool(mute.Result),
            Valid = true,
        };
    }

    public Task SetVolumeAsync(int volume, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(volume, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(volume, 100);
        return SetDataAsync(VolumePath, KefW2Protocol.Typed("i32_", volume), ct);
    }

    public Task SetMuteAsync(bool muted, CancellationToken ct = default) =>
        SetDataAsync(MutePath, KefW2Protocol.Typed("bool_", muted), ct);

    public Task SetSourceAsync(string sourceId, CancellationToken ct = default)
    {
        if (Model == null || !Model.Sources.Contains(sourceId)) throw new SpeakerException("지원하지 않는 입력: " + sourceId);
        return SetDataAsync(SourcePath, KefW2Protocol.Typed("kefPhysicalSource", sourceId), ct);
    }

    /// <summary>
    /// The W2 firmware switches power through the input setting: <c>standby</c> turns the speaker off and
    /// <c>powerOn</c> wakes it on the input it had before standby.
    /// </summary>
    public Task SetPowerAsync(bool on, CancellationToken ct = default) =>
        SetDataAsync(SourcePath, KefW2Protocol.Typed("kefPhysicalSource", on ? "powerOn" : "standby"), ct);

    public int? ApproachOvershoot(int current, int target) =>
        target > current ? KefVolumeCurve.Overshoot(target) : null;

    Task<string> GetDataAsync(string path, CancellationToken ct) =>
        RequestAsync("GET", KefW2Protocol.GetDataUrl(Host, path), null, AuthMode == "all", ct);

    async Task SetDataAsync(string path, JsonObject typed, CancellationToken ct)
    {
        if (AuthMode is not ("none" or "setData" or "all")) throw new SpeakerException("인증 방식 미확인: 제어 중지");
        await RequestAsync("POST", KefW2Protocol.SetDataUrl(Host), new Payload(path, typed), AuthMode != "none", ct);
    }

    sealed record Payload(string Path, JsonObject Typed);

    async Task<string> RequestAsync(string method, string url, Payload? payload, bool secure, CancellationToken ct)
    {
        string? body = null;
        byte[]? salt = null, key = null;
        if (secure)
        {
            salt = RandomBytes(6);
            key = KefW2Protocol.DeriveKey(salt, password);
        }
        if (payload != null)
        {
            body = secure
                ? KefW2Protocol.SecureSetBody(payload.Path, payload.Typed, key!, RandomBytes(16))
                : KefW2Protocol.PlainSetBody(payload.Path, payload.Typed);
        }

        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (secure) request.Headers.TryAddWithoutValidation("Authorization", KefW2Protocol.AuthorizationHeader(method, url, body, salt!, key!, UnixTimeMs()));

        string text = await SendAsync(request, ct);
        if (text.TrimStart().StartsWith('<')) throw new SpeakerException("JSON 대신 로그인 페이지가 반환되었습니다. 기기 웹페이지 암호를 확인하세요");
        if (string.IsNullOrWhiteSpace(text))
        {
            if (method == "GET") throw new SpeakerException("빈 API 응답");
            return "";
        }
        if (method == "POST" && text.Contains("\"error\"", StringComparison.Ordinal))
        {
            var node = JsonNode.Parse(text);
            if (node is JsonObject o && o["error"] is { } err && err.ToJsonString() is not ("null" or "false"))
                throw new SpeakerException("KEF API가 명령을 거부했습니다");
        }
        return text;
    }
}
