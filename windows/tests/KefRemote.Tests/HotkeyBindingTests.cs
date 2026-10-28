using System.Text.Json;
using KefRemote.Desktop;
using Xunit;

namespace KefRemote.Tests;

public class HotkeyBindingTests
{
    [Fact]
    public void Empty_settings_mean_defaults()
    {
        var bindings = HotkeyBinding.Normalize(null);
        Assert.Equal(3, bindings.Count);
        Assert.Equal("Ctrl + Alt + ↑", bindings.Single(b => b.Action == HotkeyAction.VolumeUp).ToString());
        Assert.Equal("Ctrl + Alt + ↓", bindings.Single(b => b.Action == HotkeyAction.VolumeDown).ToString());
        Assert.Equal("Ctrl + Alt + M", bindings.Single(b => b.Action == HotkeyAction.ToggleMute).ToString());
    }

    [Fact]
    public void Explicitly_cleared_binding_stays_off()
    {
        var bindings = HotkeyBinding.Normalize([new HotkeyBinding(HotkeyAction.ToggleMute, HotkeyModifiers.None, 0)]);
        var mute = bindings.Single(b => b.Action == HotkeyAction.ToggleMute);
        Assert.False(mute.IsAssigned);
        Assert.Equal("없음", mute.ToString());
        Assert.True(bindings.Single(b => b.Action == HotkeyAction.VolumeUp).IsAssigned);
    }

    [Theory]
    [InlineData(HotkeyModifiers.None, VirtualKeys.Up, false)]
    [InlineData(HotkeyModifiers.Win, VirtualKeys.Up, false)]
    [InlineData(HotkeyModifiers.Shift, VirtualKeys.M, false)]
    [InlineData(HotkeyModifiers.Shift, VirtualKeys.F1 + 4, true)]
    [InlineData(HotkeyModifiers.Control, VirtualKeys.Control, false)]
    [InlineData(HotkeyModifiers.None, VirtualKeys.F13, true)]
    [InlineData(HotkeyModifiers.None, VirtualKeys.VolumeUp, true)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKeys.PageUp, true)]
    public void Validate(HotkeyModifiers mods, int key, bool valid) =>
        Assert.Equal(valid, new HotkeyBinding(HotkeyAction.VolumeUp, mods, key).Validate() == null);

    [Fact]
    public void Duplicate_chords_are_rejected()
    {
        var set = HotkeyBinding.Defaults();
        set[1] = new HotkeyBinding(HotkeyAction.VolumeDown, HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKeys.Up);
        Assert.NotNull(HotkeyBinding.ValidateSet(set));
        Assert.Null(HotkeyBinding.ValidateSet(HotkeyBinding.Defaults()));
    }

    [Fact]
    public void Json_round_trip_uses_readable_action_names()
    {
        var json = JsonSerializer.Serialize(HotkeyBinding.Defaults());
        Assert.Contains("\"VolumeUp\"", json);
        Assert.DoesNotContain("IsAssigned", json);
        var back = JsonSerializer.Deserialize<List<HotkeyBinding>>(json)!;
        Assert.Equal(HotkeyBinding.Defaults().Select(b => b.ToString()), back.Select(b => b.ToString()));
    }

    [Theory]
    [InlineData(VirtualKeys.F13, "F13")]
    [InlineData(0x31, "1")]
    [InlineData(VirtualKeys.NumPad0 + 7, "Num 7")]
    [InlineData(VirtualKeys.VolumeUp, "볼륨 올림 키")]
    public void Key_names(int vk, string name) => Assert.Equal(name, VirtualKeys.Name(vk));
}
