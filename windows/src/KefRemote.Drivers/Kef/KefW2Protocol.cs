using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KefRemote.Core;

namespace KefRemote.Drivers.Kef;

/// <summary>
/// Pure functions of the KEF W2 local HTTP API (docs/protocol/kef-w2.md).
/// Kept free of I/O so they can be checked against spec/test-vectors/kef-w2.json.
/// </summary>
public static class KefW2Protocol
{
    public const string AuthScheme = "HMAC_SHA256_AES256";
    public const string UserB64 = "dXNlcg==";

    public static string GetDataUrl(string host, string path) =>
        $"http://{host}/api/getData?path={Uri.EscapeDataString(path)}&roles=value";

    public static string SetDataUrl(string host) => $"http://{host}/api/setData";

    /// <summary>{"type":T,"T":value} serialized without whitespace.</summary>
    public static JsonObject Typed(string type, JsonNode? value) => new() { ["type"] = type, [type] = value };

    // Without the default HTML escaping, so a base64 '+' goes out as '+' and the bytes match the macOS app and
    // spec/test-vectors (the speaker reads "\u002B" as well, but the signed body should be one canonical form).
    static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Serialize(JsonNode node) => node.ToJsonString(Compact);

    /// <summary>key = SHA256(salt || UTF8(password)).</summary>
    public static byte[] DeriveKey(ReadOnlySpan<byte> salt, string password)
    {
        var pw = Encoding.UTF8.GetBytes(password);
        var input = new byte[salt.Length + pw.Length];
        salt.CopyTo(input);
        pw.CopyTo(input.AsSpan(salt.Length));
        return SHA256.HashData(input);
    }

    /// <summary>Base64(IV || AES-256-CBC-PKCS7(plaintext)).</summary>
    public static string Encrypt(string plaintext, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(plaintext), iv, PaddingMode.PKCS7);
        return Convert.ToBase64String([.. iv, .. cipher]);
    }

    public static string Decrypt(string envelope, byte[] key)
    {
        var raw = Convert.FromBase64String(envelope);
        using var aes = Aes.Create();
        aes.Key = key;
        return Encoding.UTF8.GetString(aes.DecryptCbc(raw.AsSpan(16), raw.AsSpan(0, 16), PaddingMode.PKCS7));
    }

    /// <summary>
    /// Authorization header. Signed message: user.{saltB64}.{timestamp}.{fullUrl}[.{actualBody}] (body only for POST).
    /// </summary>
    public static string AuthorizationHeader(string method, string url, string? body, byte[] salt, byte[] key, long timestampMs)
    {
        string saltB64 = Convert.ToBase64String(salt);
        string message = $"user.{saltB64}.{timestampMs}.{url}" + (method == "POST" ? "." + body : "");
        string signature = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message)));
        return $"{AuthScheme} {UserB64}.{saltB64}.{timestampMs}.{signature}";
    }

    /// <summary>The body actually sent for a protected setData: the typed value is replaced by its encrypted envelope.</summary>
    /// <remarks>
    /// <paramref name="role"/> is <c>value</c> for settings and <c>activate</c> for action paths such as
    /// <c>player:player/control</c>, whose value is a plain object like {"control":"pause"}.
    /// </remarks>
    public static string SecureSetBody(string path, JsonObject typedValue, byte[] key, byte[] iv, string role = "value") =>
        Serialize(new JsonObject
        {
            ["path"] = path,
            ["role"] = role,
            ["value"] = Encrypt(Serialize(typedValue), key, iv),
        });

    public static string PlainSetBody(string path, JsonObject typedValue, string role = "value") =>
        Serialize(new JsonObject { ["path"] = path, ["role"] = role, ["value"] = typedValue });

    public const string PlayerDataPath = "player:player/data";
    public const string PlayTimePath = "player:player/data/playTime";
    public const string PlayerControlPath = "player:player/control";

    /// <summary>
    /// Value for <see cref="PlayerControlPath"/>. <c>pause</c> toggles play and pause. A bare <c>play</c> is not a
    /// resume: it ends a Spotify Connect session, so it is never sent.
    /// </summary>
    public static JsonObject ControlValue(PlaybackCommand command) => new()
    {
        ["control"] = command switch
        {
            PlaybackCommand.TogglePause => "pause",
            PlaybackCommand.Next => "next",
            PlaybackCommand.Previous => "previous",
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        },
    };

    public static string ModifyQueueUrl(string host) => $"http://{host}/api/event/modifyQueue";

    public static string PollQueueUrl(string host, string queueId, int holdSeconds) =>
        $"http://{host}/api/event/pollQueue?queueId={Uri.EscapeDataString(queueId)}&timeout={holdSeconds}";

    /// <summary>{"subscribe":[{"path":P,"type":"itemWithValue"},…],"unsubscribe":[]}</summary>
    public static string ModifyQueueBody(IEnumerable<string> paths) =>
        Serialize(new JsonObject
        {
            ["subscribe"] = new JsonArray([.. paths.Select(p => (JsonNode)new JsonObject { ["path"] = p, ["type"] = "itemWithValue" })]),
            ["unsubscribe"] = new JsonArray(),
        });

    /// <summary>The queue id modifyQueue returns: a JSON string such as "{caa6b0ae-…}".</summary>
    public static string ReadQueueId(string json)
    {
        var e = Parse(json);
        if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } id) return id;
        throw new SpeakerException(Loc.T("이벤트 구독 응답이 올바르지 않습니다", "Unexpected event subscription reply", "イベント購読の応答が正しくありません"));
    }

    /// <summary>
    /// Sorts a pollQueue reply, [{"path":P,"itemType":"update","itemValue":{typed}},…], into what changed.
    /// The last volume and mute values win. A value that can't be read asks for a full read instead.
    /// </summary>
    public static DeviceChanges ParseChanges(string json, string volumePath, string mutePath, IReadOnlySet<string> statePaths, IReadOnlySet<string> devicePaths)
    {
        var root = Parse(json);
        if (root.ValueKind != JsonValueKind.Array) throw new SpeakerException(Loc.T("이벤트 응답이 올바르지 않습니다", "Unexpected event reply", "イベントの応答が正しくありません"));
        var changes = DeviceChanges.None;
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String) continue;
            string path = p.GetString()!;
            item.TryGetProperty("itemValue", out var value);
            if (path == volumePath)
            {
                changes = TryRead(value, ReadInt) is int v ? changes with { Volume = v } : changes with { State = true };
            }
            else if (path == mutePath)
            {
                changes = TryRead(value, ReadBool) is bool m ? changes with { Muted = m } : changes with { State = true };
            }
            else if (statePaths.Contains(path)) changes = changes with { State = true };
            else if (devicePaths.Contains(path)) changes = changes with { Device = true };
            else if (path == PlayerDataPath) changes = changes with { Playback = true };
        }
        return changes;
    }

    static T? TryRead<T>(JsonElement e, Func<JsonElement, T> read) where T : struct
    {
        if (e.ValueKind == JsonValueKind.Undefined) return null;
        try { return read(e); }
        catch (SpeakerException) { return null; }
    }

    /// <summary>
    /// player:player/data (an array of one untyped object) and player:player/data/playTime (i64_ ms, -1 for none).
    /// Physical inputs report the input name as the title and no controls; callers filter those by input.
    /// </summary>
    public static PlaybackInfo ParsePlayback(string dataJson, string? playTimeJson)
    {
        var root = Parse(dataJson);
        if (root.ValueKind == JsonValueKind.Array)
            root = root.GetArrayLength() == 1 ? root[0] : default;
        if (root.ValueKind != JsonValueKind.Object) return PlaybackInfo.None;
        if (root.TryGetProperty("error", out var err) && err.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
            throw new SpeakerException(Loc.T("KEF API 오류", "KEF API error", "KEF APIエラー"));

        var track = Child(root, "trackRoles");
        var meta = Child(Child(track, "mediaData"), "metaData");
        var controls = Child(root, "controls");
        long position = -1;
        if (playTimeJson != null)
        {
            try
            {
                var t = Unwrap(Parse(playTimeJson));
                if (t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out long ms) && ms >= 0) position = ms;
            }
            catch (SpeakerException)
            {
            }
        }
        string? icon = Text(track, "icon");
        return new PlaybackInfo
        {
            Status = Text(root, "state") switch
            {
                "playing" => PlaybackStatus.Playing,
                "paused" => PlaybackStatus.Paused,
                _ => PlaybackStatus.Stopped,
            },
            Title = Text(track, "title") ?? "",
            Artist = Text(meta, "artist") ?? Text(meta, "albumArtist") ?? "",
            Album = Text(meta, "album") ?? "",
            ArtworkUrl = icon != null && (icon.StartsWith("http://", StringComparison.Ordinal) || icon.StartsWith("https://", StringComparison.Ordinal)) ? icon : null,
            Service = ServiceName(Text(meta, "serviceID")),
            DurationMs = Child(root, "status").ValueKind == JsonValueKind.Object && Child(root, "status").TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt64(out long dur) && dur > 0 ? dur : 0,
            PositionMs = position,
            CanTogglePause = Flag(controls, "pause"),
            CanNext = Flag(controls, "next_") || Flag(controls, "next"),
            CanPrevious = Flag(controls, "previous"),
        };
    }

    /// <summary>Display name for a metaData.serviceID. Unknown ids are shown as sent.</summary>
    public static string? ServiceName(string? id) => id?.ToLowerInvariant() switch
    {
        null or "" => null,
        "spotify" => "Spotify",
        "airplay" or "airplay2" => "AirPlay",
        "googlecast" or "chromecast" or "cast" => "Chromecast",
        "tidal" or "tidalconnect" => "TIDAL",
        "qobuz" or "qobuzconnect" => "Qobuz",
        "deezer" => "Deezer",
        "amazon" or "amazonmusic" => "Amazon Music",
        "roon" or "roonready" => "Roon",
        "bluetooth" => "Bluetooth",
        "airable" or "radio" or "internetradio" or "tunein" => Loc.T("인터넷 라디오", "Internet radio", "インターネットラジオ"),
        "upnp" or "dlna" => "UPnP",
        _ => id,
    };

    static JsonElement Child(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var c) ? c : default;

    static string? Text(JsonElement e, string name) =>
        Child(e, name) is { ValueKind: JsonValueKind.String } v && v.GetString() is { Length: > 0 } s ? s : null;

    static bool Flag(JsonElement e, string name) => Child(e, name).ValueKind == JsonValueKind.True;

    /// <summary>
    /// Unwraps a getData response such as [{"type":"i32_","i32_":32}] and checks the declared type.
    /// Throws on errors, empty arrays and type mismatches instead of guessing.
    /// </summary>
    public static JsonElement Unwrap(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            if (e.GetArrayLength() != 1) throw new SpeakerException(Loc.T("예상하지 못한 KEF API 배열", "Unexpected KEF API array", "予期しないKEF APIの配列"));
            return Unwrap(e[0]);
        }
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("error", out var err) && err.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
                throw new SpeakerException(Loc.T("KEF API 오류", "KEF API error", "KEF APIエラー"));
            if (e.TryGetProperty("value", out var v)) return Unwrap(v);
            if (e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && e.TryGetProperty(t.GetString()!, out var inner))
                return inner;
        }
        return e;
    }

    public static string ReadString(string json) => ReadString(Parse(json));
    public static int ReadInt(string json) => ReadInt(Parse(json));
    public static bool ReadBool(string json) => ReadBool(Parse(json));

    public static string ReadString(JsonElement e)
    {
        var v = Unwrap(e);
        return v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new SpeakerException(Loc.T("문자열 응답이 아닙니다", "Not a string reply", "文字列の応答ではありません"));
    }

    public static int ReadInt(JsonElement e)
    {
        var v = Unwrap(e);
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : throw new SpeakerException(Loc.T("정수 응답이 아닙니다", "Not an integer reply", "整数の応答ではありません"));
    }

    public static bool ReadBool(JsonElement e)
    {
        var v = Unwrap(e);
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new SpeakerException(Loc.T("불리언 응답이 아닙니다", "Not a boolean reply", "真偽値の応答ではありません")),
        };
    }

    static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
