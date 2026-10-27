using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using KefRemote.Drivers.Kef;

namespace KefRemote.Tests;

/// <summary>
/// In-memory KEF W2 speaker answering getData/setData like the LSX II LT measured in the handoff.
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
        string type = value["type"]!.GetValue<string>();
        switch (o["path"]!.GetValue<string>())
        {
            case KefW2Driver.VolumePath: Volume = value[type]!.GetValue<int>(); break;
            case KefW2Driver.MutePath: Muted = value[type]!.GetValue<bool>(); break;
            case KefW2Driver.SourcePath: Source = value[type]!.GetValue<string>(); break;
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

    static string Typed(string type, object value) =>
        "[" + new JsonObject { ["type"] = type, [type] = JsonSerializer.SerializeToNode(value) }.ToJsonString() + "]";
}
