using System.Text.Json;
using System.Text.Json.Nodes;
using KefRemote.Core;
using KefRemote.Drivers.Kef;
using Xunit;

namespace KefRemote.Tests;

/// <summary>Checks the C# implementation against spec/test-vectors/kef-w2.json, shared with the macOS app.</summary>
public class ProtocolVectorTests
{
    static readonly JsonElement V = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "kef-w2.json"))).RootElement;

    static byte[] Hex(JsonElement e, string name) => Convert.FromHexString(e.GetProperty(name).GetString()!);
    static string S(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    [Fact]
    public void KeyDerivation()
    {
        foreach (var c in V.GetProperty("keyDerivation").EnumerateArray())
            Assert.Equal(Hex(c, "keyHex"), KefW2Protocol.DeriveKey(Hex(c, "saltHex"), S(c, "password")));
    }

    [Fact]
    public void EncryptionRoundTrip()
    {
        foreach (var c in V.GetProperty("encryption").EnumerateArray())
        {
            Assert.Equal(S(c, "envelopeBase64"), KefW2Protocol.Encrypt(S(c, "plaintext"), Hex(c, "keyHex"), Hex(c, "ivHex")));
            Assert.Equal(S(c, "plaintext"), KefW2Protocol.Decrypt(S(c, "envelopeBase64"), Hex(c, "keyHex")));
        }
    }

    [Fact]
    public void AuthorizationHeader()
    {
        foreach (var c in V.GetProperty("authorization").EnumerateArray())
        {
            string? body = c.GetProperty("body").ValueKind == JsonValueKind.Null ? null : S(c, "body");
            Assert.Equal(S(c, "header"), KefW2Protocol.AuthorizationHeader(S(c, "method"), S(c, "url"), body, Hex(c, "saltHex"), Hex(c, "keyHex"), c.GetProperty("timestampMs").GetInt64()));
        }
    }

    [Fact]
    public void GetDataUrl()
    {
        foreach (var c in V.GetProperty("getDataUrl").EnumerateArray())
            Assert.Equal(S(c, "url"), KefW2Protocol.GetDataUrl(S(c, "host"), S(c, "path")));
    }

    [Fact]
    public async Task SecureSetDataRequestMatchesVector()
    {
        foreach (var c in V.GetProperty("secureSetData").EnumerateArray())
        {
            var device = new FakeKefDevice(S(c, "password"));
            var random = new Queue<byte[]>([Hex(c, "saltHex"), Hex(c, "ivHex")]);
            using var driver = new KefW2Driver(new DriverOptions(S(c, "host"), S(c, "password")), device.Handler)
            {
                RandomBytes = _ => random.Dequeue(),
                UnixTimeMs = () => c.GetProperty("timestampMs").GetInt64(),
            };
            await driver.ConnectAsync();
            var typed = (JsonObject)JsonNode.Parse(c.GetProperty("typedValue").GetRawText())!;
            Assert.Equal("i32_", typed["type"]!.GetValue<string>());
            await driver.SetVolumeAsync(typed["i32_"]!.GetValue<int>());

            var (request, body) = device.Handler.Requests.Last(r => r.Request.Method == HttpMethod.Post);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(S(c, "body"), body);
            Assert.Equal(S(c, "header"), request.Headers.GetValues("Authorization").Single());
        }
    }

    [Fact]
    public void ResponseParsing()
    {
        foreach (var c in V.GetProperty("responseParsing").EnumerateArray())
        {
            string json = S(c, "json");
            Func<object> read = S(c, "kind") switch
            {
                "int" => () => KefW2Protocol.ReadInt(json),
                "bool" => () => KefW2Protocol.ReadBool(json),
                _ => () => KefW2Protocol.ReadString(json),
            };
            if (c.TryGetProperty("error", out _))
            {
                Assert.ThrowsAny<Exception>(read);
                continue;
            }
            var expect = c.GetProperty("expect");
            object expected = expect.ValueKind switch
            {
                JsonValueKind.Number => expect.GetInt32(),
                JsonValueKind.String => expect.GetString()!,
                _ => expect.GetBoolean(),
            };
            Assert.Equal(expected, read());
        }
    }
}
