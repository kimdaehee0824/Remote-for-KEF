using KefRemote.Core;
using KefRemote.Drivers.BluOS;
using KefRemote.Drivers.LinkPlay;
using KefRemote.Drivers.MusicCast;
using Xunit;

namespace KefRemote.Tests;

/// <summary>Protocol shape checks for drivers that were not tested on hardware.</summary>
public class ExperimentalDriverTests
{
    static readonly DriverOptions Options = new("192.168.0.20");

    [Fact]
    public async Task BluOsReadsAndWrites()
    {
        int volume = 15;
        var handler = new StubHandler((req, _) =>
        {
            string url = req.RequestUri!.PathAndQuery;
            if (url.StartsWith("/SyncStatus")) return StubHandler.Ok("""<SyncStatus name="Living" brand="Bluesound" modelName="NODE" schemaVersion="34"/>""", "text/xml");
            if (url.StartsWith("/Status")) return StubHandler.Ok($"<status><volume>{volume}</volume><mute>0</mute></status>", "text/xml");
            if (url.StartsWith("/Volume?level=")) volume = int.Parse(url["/Volume?level=".Length..]);
            return StubHandler.Ok("<volume/>", "text/xml");
        });
        using var d = new BluOsDriver(Options, handler);
        var info = await d.ConnectAsync();
        Assert.Equal(("Living", "Bluesound NODE"), (info.Name, info.Model));
        Assert.Equal(15, (await d.ReadAsync()).Volume);
        await d.SetVolumeAsync(22);
        Assert.Equal(22, (await d.ReadAsync()).Volume);
    }

    [Fact]
    public async Task MusicCastMapsNativeScale()
    {
        int raw = 80;
        string? lastSet = null;
        var handler = new StubHandler((req, _) =>
        {
            string url = req.RequestUri!.PathAndQuery;
            string json = url switch
            {
                _ when url.EndsWith("getDeviceInfo") => """{"response_code":0,"model_name":"WX-021","system_version":2.5}""",
                _ when url.EndsWith("getNetworkStatus") => """{"response_code":0,"network_name":"Kitchen"}""",
                _ when url.EndsWith("getFeatures") => """{"response_code":0,"zone":[{"id":"main","input_list":["net_radio","spotify","airplay"],"range_step":[{"id":"volume","min":0,"max":160,"step":1}]}]}""",
                _ when url.EndsWith("getStatus") => $$"""{"response_code":0,"power":"on","volume":{{raw}},"mute":false,"input":"spotify","max_volume":160}""",
                _ => """{"response_code":0}""",
            };
            if (url.Contains("setVolume")) { lastSet = url; raw = int.Parse(url[(url.IndexOf('=') + 1)..]); }
            return StubHandler.Ok(json);
        });
        using var d = new MusicCastDriver(Options, handler);
        await d.ConnectAsync();
        Assert.Equal(3, d.Sources.Count);
        var s = await d.ReadAsync();
        Assert.Equal((50, "spotify"), (s.Volume, s.SourceId));
        await d.SetVolumeAsync(25);
        Assert.EndsWith("setVolume?volume=40", lastSet);
        Assert.Equal(25, (await d.ReadAsync()).Volume);
    }

    [Fact]
    public async Task LinkPlayFallsBackToHttp()
    {
        var handler = new StubHandler((req, _) =>
        {
            if (req.RequestUri!.Scheme == "https") throw new HttpRequestException("tls");
            return StubHandler.Ok(req.RequestUri.Query.Contains("getStatusEx")
                ? """{"DeviceName":"WiiM Mini","project":"Muzo_Mini","firmware":"4.6"}"""
                : """{"vol":"35","mute":"1"}""");
        });
        using var d = new LinkPlayDriver(Options, handler);
        var info = await d.ConnectAsync();
        Assert.Equal("WiiM Mini", info.Name);
        var s = await d.ReadAsync();
        Assert.Equal((35, true), (s.Volume, s.Muted));
        Assert.All(handler.Requests.Skip(1), r => Assert.Equal("http", r.Request.RequestUri!.Scheme));
    }
}
