using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using KefRemote.Drivers.Kef;

namespace KefRemote.Tests;

/// <summary>
/// In-memory KEF W2 speaker answering getData/setData like the LSX II LT measured on real hardware (see docs/protocol/kef-w2.md).
/// Verifies the HMAC header and decrypts protected values with the configured password.
/// </summary>
sealed class FakeKefDevice
{
    public string AuthMode = "setData";
    public string ModelName = "SP4077", Release = "LSXIILT_V2186", Name = "Daehee LSX II LT";
    public string Source = "usb", Power = "powerOn";
    public int Volume = 32, MaximumVolume = 70;
    public bool Muted, VolumeLimit = true, ReturnLoginPage;
    public int Posts;
    public string PlayState = "playing";
    public long PlayTime = 49876;
    public readonly List<string> Controls = [];

    /// <summary>Event queue: replies to pollQueue in order, then [] (the hold expired).</summary>
    public readonly Queue<string> Events = [];
    public readonly List<string> Subscriptions = [];
    public int QueuesCreated;
    public bool QueueBroken;
    string? queueId;
    string lastSource = "usb";
    readonly string password;

    public StubHandler Handler { get; }

    public FakeKefDevice(string password = "")
    {
        this.password = password;
        Handler = new StubHandler(Respond);
    }

    HttpResponseMessage Respond(HttpRequestMessage request, string? body)
    {
        if (ReturnLoginPage) return StubHandler.Ok("<html>login</html>", "text/html");
        string url = request.RequestUri!.OriginalString;
        bool signed = request.Headers.TryGetValues("Authorization", out var values);
        if (signed && !Verify(request.Method.Method, url, body, values!.Single())) return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        if (url.Contains("/api/event/", StringComparison.Ordinal))
        {
            if (signed) return new HttpResponseMessage(HttpStatusCode.BadRequest);
            if (url.EndsWith("/api/event/modifyQueue", StringComparison.Ordinal))
            {
                foreach (var item in JsonNode.Parse(body!)!["subscribe"]!.AsArray()) Subscriptions.Add(item!["path"]!.GetValue<string>());
                queueId = "{caa6b0ae-4a71-4647-8bf7-c2036842e97" + ++QueuesCreated + "}";
                return StubHandler.Ok(JsonSerializer.Serialize(queueId));
            }
            string? id = HttpUtility.ParseQueryString(request.RequestUri!.Query)["queueId"];
            if (QueueBroken || id != queueId) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return StubHandler.Ok(Events.TryDequeue(out var e) ? e : "[]");
        }

        if (request.Method == HttpMethod.Get)
        {
            string path = HttpUtility.ParseQueryString(request.RequestUri.Query)["path"]!;
            if (AuthMode == "all" && !signed && path != "settings:/webserver/authMode") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return StubHandler.Ok(path switch
            {
                "settings:/webserver/authMode" => Typed("webserverAuthMode", AuthMode),
                "settings:/kef/host/modelName" => Typed("string_", ModelName),
                "settings:/releasetext" => Typed("string_", Release),
                "settings:/deviceName" => Typed("string_", Name),
                "settings:/kef/host/volumeLimit" => Typed("bool_", VolumeLimit),
                "settings:/kef/host/maximumVolume" => Typed("i32_", MaximumVolume),
                KefW2Driver.SourcePath => Typed("kefPhysicalSource", Source),
                KefW2Driver.VolumePath => Typed("i32_", Volume),
                KefW2Driver.MutePath => Typed("bool_", Muted),
                KefW2Driver.PowerPath => Typed("kefSpeakerStatus", Power),
                KefW2Protocol.PlayerDataPath => PlayerData.Replace("\"state\":\"playing\"", $"\"state\":\"{PlayState}\""),
                KefW2Protocol.PlayTimePath => Typed("i64_", PlayTime),
                _ => """{"error":{"message":"unknown path"}}""",
            });
        }

        Posts++;
        if (AuthMode != "none" && !signed) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var o = JsonNode.Parse(body!)!.AsObject();
        JsonNode value = o["value"]!;
        if (AuthMode != "none")
        {
            var salt = Convert.FromBase64String(values!.Single().Split(' ')[1].Split('.')[1]);
            value = JsonNode.Parse(KefW2Protocol.Decrypt(value.GetValue<string>(), KefW2Protocol.DeriveKey(salt, password)))!;
        }
        if (o["path"]!.GetValue<string>() == KefW2Protocol.PlayerControlPath)
        {
            if (o["role"]!.GetValue<string>() != "activate") return StubHandler.Ok("""{"error":{"message":"read only"}}""");
            string control = value["control"]!.GetValue<string>();
            Controls.Add(control);
            if (control == "pause") PlayState = PlayState == "playing" ? "paused" : "playing";
            return StubHandler.Ok("");
        }
        string type = value["type"]!.GetValue<string>();
        switch (o["path"]!.GetValue<string>())
        {
            case KefW2Driver.VolumePath: Volume = value[type]!.GetValue<int>(); break;
            case KefW2Driver.MutePath: Muted = value[type]!.GetValue<bool>(); break;
            case KefW2Driver.SourcePath:
                // W2 firmware: "standby" and "powerOn" written to the input setting switch power.
                string next = value[type]!.GetValue<string>();
                if (next == "standby") { lastSource = Source; Source = "standby"; Power = "standby"; }
                else if (next == "powerOn") { Source = lastSource; Power = "powerOn"; }
                else { Source = next; Power = "powerOn"; }
                break;
            default: return StubHandler.Ok("""{"error":{"message":"read only"}}""");
        }
        return StubHandler.Ok("");
    }

    bool Verify(string method, string url, string? body, string header)
    {
        var parts = header.Split(' ')[1].Split('.');
        var salt = Convert.FromBase64String(parts[1]);
        var key = KefW2Protocol.DeriveKey(salt, password);
        return header == KefW2Protocol.AuthorizationHeader(method, url, body, salt, key, long.Parse(parts[2]));
    }

    /// <summary>player:player/data captured from LSX II LT 2.1.86 playing Spotify Connect (2026-11-01).</summary>
    public const string PlayerData = """[{"controls":{"next_":true,"playMode":{"repeatOne":false,"shuffleRepeatAll":false,"repeatAll":false,"shuffle":false,"shuffleRepeatOne":false},"seekTime":true,"previous":true,"pause":true},"trackRoles":{"containerType":"none","audioType":"audioBroadcast","doNotTrack":true,"icon":"https://i.scdn.co/image/ab67616d0000b2730e62daaea2c7b94053e1c142","mediaData":{"activeResource":{"mimeType":"audio/unknown","quality":{"spotifyHifi":true}},"resources":[{"mimeType":"audio/unknown","quality":{"spotifyHifi":true}}],"metaData":{"album":"Bird's Eye","playbackSourceId":"spotify:track:736PP5LTtREkDgktNmX3Gu","trackId":"spotify:track:4WFgvKVfEhb3IUAFGrutTR","playbackSource":"","serviceID":"spotify","albumId":"spotify:album:1uE3dRPe3SrGdNhd1nWlSa","contentPlayContextPath":"spotify:/ui/context","playLogicPath":"spotify:playlogic","artist":"Ravyn Lenae","albumCoverUri":"spotify:image:ab67616d0000b2730e62daaea2c7b94053e1c142","artistId":"spotify:artist:5RTLRtXjbXI2lSXc6jxlAz","live":true}},"type":"audio","title":"Love Me Not"},"state":"playing","mediaRoles":{"containerType":"none","audioType":"audioBroadcast","doNotTrack":true,"mediaData":{"resources":[{"uri":"?preset=%7B%22trackUri%22:%22spotify:track:4WFgvKVfEhb3IUAFGrutTR%22,%22title%22:%22Spotify%20content%22,%22playMode%22:%22normal%22,%22srcUri%22:%22spotify:track:736PP5LTtREkDgktNmX3Gu%22%7D","mimeType":"audio/unknown"}],"metaData":{"serviceID":"spotify","contentPlayContextPath":"spotify:/ui/context","playLogicPath":"spotify:playlogic","live":true}},"type":"audio","title":"Spotify content"},"status":{"duration":213466},"contextPath":"spotify:/ui/context","playId":{"timestamp":428572132,"systemMemberId":"lsxlite-c44a124b-f1ad-4fbb-a349-aabd30556079"}}]""";

    static string Typed(string type, object value) =>
        "[" + new JsonObject { ["type"] = type, [type] = JsonSerializer.SerializeToNode(value) }.ToJsonString() + "]";
}
