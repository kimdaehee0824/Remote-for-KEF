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

    public static string Serialize(JsonNode node) => node.ToJsonString();

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
    public static string SecureSetBody(string path, JsonObject typedValue, byte[] key, byte[] iv) =>
        Serialize(new JsonObject
        {
            ["path"] = path,
            ["role"] = "value",
            ["value"] = Encrypt(Serialize(typedValue), key, iv),
        });

    public static string PlainSetBody(string path, JsonObject typedValue) =>
        Serialize(new JsonObject { ["path"] = path, ["role"] = "value", ["value"] = typedValue });

    /// <summary>
    /// Unwraps a getData response such as [{"type":"i32_","i32_":32}] and checks the declared type.
    /// Throws on errors, empty arrays and type mismatches instead of guessing.
    /// </summary>
    public static JsonElement Unwrap(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            if (e.GetArrayLength() != 1) throw new SpeakerException("예상하지 못한 KEF API 배열");
            return Unwrap(e[0]);
        }
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("error", out var err) && err.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
                throw new SpeakerException("KEF API 오류");
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
        return v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new SpeakerException("문자열 응답이 아닙니다");
    }

    public static int ReadInt(JsonElement e)
    {
        var v = Unwrap(e);
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : throw new SpeakerException("정수 응답이 아닙니다");
    }

    public static bool ReadBool(JsonElement e)
    {
        var v = Unwrap(e);
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new SpeakerException("불리언 응답이 아닙니다"),
        };
    }

    static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
