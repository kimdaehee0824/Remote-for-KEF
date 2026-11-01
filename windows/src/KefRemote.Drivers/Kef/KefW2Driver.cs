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

    /// <summary>Seconds the speaker may hold a pollQueue request open waiting for a change.</summary>
    public const int ChangeHoldSeconds = 10;

    static readonly HashSet<string> StatePaths = [SourcePath, PowerPath];
    static readonly HashSet<string> DevicePaths = ["settings:/kef/host/maximumVolume", "settings:/kef/host/volumeLimit", "settings:/deviceName"];

    readonly string password;
    string? queueId;

    public static readonly DriverDescriptor Descriptor = new(
        "kef-w2",
        Loc.T("KEF (W2 플랫폼)", "KEF (W2 platform)", "KEF（W2プラットフォーム）"),
        Loc.T("LSX II LT (검증), LSX II, LS50 Wireless II, LS60, XIO", "LSX II LT (verified), LSX II, LS50 Wireless II, LS60, XIO", "LSX II LT（検証済み）、LSX II、LS50 Wireless II、LS60、XIO"),
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
        DriverCapabilities.Playback | DriverCapabilities.Changes |
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
        if (auth is not ("none" or "setData" or "all")) throw new SpeakerException(Loc.T("지원하지 않는 인증 방식: ", "Unsupported authentication mode: ", "対応していない認証方式: ") + auth);
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
        if (first != second) throw new SpeakerException(Loc.T("입력 전환 중입니다", "The input is switching", "入力を切り替え中です"));

        // The input setting changes before the power status both ways (measured on LSX II LT): "standby" about
        // 0.4 s earlier when turning off, the input about 2 s earlier when waking. Waking therefore reads as
        // standby with an input (see SpeakerController.IsWaking).
        string status = KefW2Protocol.ReadString(power.Result);
        var powerState = second == "standby" ? PowerState.Standby
            : status switch { "powerOn" => PowerState.On, "standby" => PowerState.Standby, _ => PowerState.Unknown };
        return new SpeakerState
        {
            Power = powerState,
            // Only an input of this model counts, as on macOS.
            SourceId = Model?.Sources.Contains(second) == true ? second : null,
            Volume = KefW2Protocol.ReadInt(volume.Result),
            Muted = KefW2Protocol.ReadBool(mute.Result),
            Valid = true,
            // Woken with "powerOn", the input setting can keep that value until the input is switched (seen for
            // minutes on LSX II LT). The speaker wakes on the input it had before standby.
            OnPreviousInput = second == "powerOn",
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
        if (Model == null || !Model.Sources.Contains(sourceId)) throw new SpeakerException(SpeakerController.UnsupportedInput + sourceId);
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

    /// <summary>Streaming inputs carry track information. Physical inputs only report their own name.</summary>
    public bool SourceHasPlayback(string? sourceId) => sourceId is "wifi" or "bluetooth";

    public async Task<PlaybackInfo> ReadPlaybackAsync(CancellationToken ct = default)
    {
        var data = GetDataAsync(KefW2Protocol.PlayerDataPath, ct);
        var time = OptionalAsync(GetDataAsync(KefW2Protocol.PlayTimePath, ct));
        return KefW2Protocol.ParsePlayback(await data, await time);

        // The position is optional: without it the card just shows no progress.
        static async Task<string?> OptionalAsync(Task<string> read)
        {
            try { return await read; }
            catch (SpeakerException) { return null; }
        }
    }

    public Task SendPlaybackAsync(PlaybackCommand command, CancellationToken ct = default) =>
        SetDataAsync(KefW2Protocol.PlayerControlPath, KefW2Protocol.ControlValue(command), ct, role: "activate");

    /// <summary>
    /// Long-polls the speaker's event queue, subscribing first if needed. The event endpoints are not signed in any
    /// auth mode (measured on LSX II LT 2.1.86 in setData mode). Any failure drops the queue so the next call
    /// subscribes again.
    /// </summary>
    public async Task<DeviceChanges> WaitForChangesAsync(CancellationToken ct = default)
    {
        try
        {
            if (queueId == null)
            {
                using var subscribe = new HttpRequestMessage(HttpMethod.Post, KefW2Protocol.ModifyQueueUrl(Host))
                {
                    Content = new StringContent(KefW2Protocol.ModifyQueueBody(
                        [SourcePath, PowerPath, VolumePath, MutePath, KefW2Protocol.PlayerDataPath, .. DevicePaths]), Encoding.UTF8, "application/json"),
                };
                queueId = KefW2Protocol.ReadQueueId(await SendAsync(subscribe, ct));
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(ChangeHoldSeconds + 3));
            using var poll = new HttpRequestMessage(HttpMethod.Get, KefW2Protocol.PollQueueUrl(Host, queueId, ChangeHoldSeconds));
            string text = await SendAsync(LongPoll, poll, deadline.Token);
            if (string.IsNullOrWhiteSpace(text)) return DeviceChanges.None;
            return KefW2Protocol.ParseChanges(text, VolumePath, MutePath, StatePaths, DevicePaths);
        }
        catch
        {
            queueId = null;
            throw;
        }
    }

    Task<string> GetDataAsync(string path, CancellationToken ct) =>
        RequestAsync("GET", KefW2Protocol.GetDataUrl(Host, path), null, AuthMode == "all", ct);

    async Task SetDataAsync(string path, JsonObject typed, CancellationToken ct, string role = "value")
    {
        if (AuthMode is not ("none" or "setData" or "all")) throw new SpeakerException(Loc.T("인증 방식 미확인: 제어 중지", "Authentication mode unknown; control stopped", "認証方式を確認できないため、操作を停止しました"));
        await RequestAsync("POST", KefW2Protocol.SetDataUrl(Host), new Payload(path, typed, role), AuthMode != "none", ct);
    }

    sealed record Payload(string Path, JsonObject Typed, string Role);

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
                ? KefW2Protocol.SecureSetBody(payload.Path, payload.Typed, key!, RandomBytes(16), payload.Role)
                : KefW2Protocol.PlainSetBody(payload.Path, payload.Typed, payload.Role);
        }

        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (secure) request.Headers.TryAddWithoutValidation("Authorization", KefW2Protocol.AuthorizationHeader(method, url, body, salt!, key!, UnixTimeMs()));

        string text = await SendAsync(request, ct);
        if (text.TrimStart().StartsWith('<')) throw new SpeakerException(Loc.T("JSON 대신 로그인 페이지가 반환되었습니다. 기기 웹페이지 암호를 확인하세요", "The speaker returned a login page instead of JSON. Check the device web page password", "JSONではなくログインページが返されました。機器のWebページのパスワードを確認してください"));
        if (string.IsNullOrWhiteSpace(text))
        {
            if (method == "GET") throw new SpeakerException(Loc.T("빈 API 응답", "Empty API reply", "APIの応答が空です"));
            return "";
        }
        if (method == "POST" && text.Contains("\"error\"", StringComparison.Ordinal))
        {
            var node = JsonNode.Parse(text);
            if (node is JsonObject o && o["error"] is { } err && err.ToJsonString() is not ("null" or "false"))
                throw new SpeakerException(Loc.T("KEF API가 명령을 거부했습니다", "The KEF API rejected the command", "KEF APIがコマンドを拒否しました"));
        }
        return text;
    }
}
