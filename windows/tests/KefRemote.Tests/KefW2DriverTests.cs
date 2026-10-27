using KefRemote.Core;
using KefRemote.Drivers.Kef;
using Xunit;

namespace KefRemote.Tests;

public class KefW2DriverTests
{
    static KefW2Driver Driver(FakeKefDevice device, string password = "") =>
        new(new DriverOptions("192.168.0.14", password), device.Handler);

    [Fact]
    public async Task IdentifiesLsxIILtFromReleaseText()
    {
        var device = new FakeKefDevice();
        using var driver = Driver(device);
        var info = await driver.ConnectAsync();
        Assert.Equal("Daehee LSX II LT", info.Name);
        Assert.Equal("LSX II LT", info.Model);
        Assert.Equal(70, info.DeviceVolumeLimit);
        Assert.Contains(driver.Sources, s => s.Id == "usb");
        Assert.Contains(driver.Sources, s => s.Id == "wifi");
        Assert.True(driver.Capabilities.HasFlag(DriverCapabilities.Source));
        Assert.Equal(0, device.Posts);
    }

    [Fact]
    public async Task ReadsState()
    {
        using var driver = Driver(new FakeKefDevice());
        await driver.ConnectAsync();
        var s = await driver.ReadAsync();
        Assert.Equal(("usb", 32, false, PowerState.On), (s.SourceId, s.Volume, s.Muted, s.Power));
    }

    [Fact]
    public async Task SignedWritesApplyWithPassword()
    {
        var device = new FakeKefDevice("secret");
        using var driver = Driver(device, "secret");
        await driver.ConnectAsync();
        await driver.SetVolumeAsync(25);
        await driver.SetMuteAsync(true);
        await driver.SetSourceAsync("wifi");
        Assert.Equal((25, true, "wifi"), (device.Volume, device.Muted, device.Source));
    }

    [Fact]
    public async Task WrongPasswordIsRejected()
    {
        var device = new FakeKefDevice("secret");
        using var driver = Driver(device, "wrong");
        await driver.ConnectAsync();
        await Assert.ThrowsAsync<SpeakerException>(() => driver.SetVolumeAsync(25));
        Assert.Equal(32, device.Volume);
    }

    [Fact]
    public async Task AuthModeAllSignsReads()
    {
        var device = new FakeKefDevice("pw") { AuthMode = "all" };
        using var driver = Driver(device, "pw");
        await driver.ConnectAsync();
        Assert.Equal(32, (await driver.ReadAsync()).Volume);
    }

    [Fact]
    public async Task LoginPageStopsInsteadOfGuessing()
    {
        using var driver = Driver(new FakeKefDevice { ReturnLoginPage = true });
        await Assert.ThrowsAsync<SpeakerException>(() => driver.ConnectAsync());
    }

    [Fact]
    public async Task UnknownSourceIsNotSent()
    {
        var device = new FakeKefDevice();
        using var driver = Driver(device);
        await driver.ConnectAsync();
        await Assert.ThrowsAsync<SpeakerException>(() => driver.SetSourceAsync("coaxial"));
        Assert.Equal(0, device.Posts);
    }

    [Theory]
    [InlineData("SP4077", "LSXIILT_V2186", "LSX II LT")]
    [InlineData("LS50WII", "LS50W2_V30", "LS50 Wireless II")]
    [InlineData("LS60W", "", "LS60 Wireless")]
    [InlineData("SP9999", "NEWMODEL_V10", "KEF NEWMODEL")]
    public void ResolvesModels(string modelName, string release, string expected) =>
        Assert.Equal(expected, KefModels.Resolve(modelName, release).DisplayName);

    [Fact]
    public void RejectsPublicAddress() =>
        Assert.Throws<SpeakerException>(() => new KefW2Driver(new DriverOptions("8.8.8.8")));
}
